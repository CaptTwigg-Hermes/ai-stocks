using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiStocks.Core;
using AiStocks.MarketData;
using AiStocks.Persistence;
using AiStocks.Research.Decisions;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;
using AiStocks.Worker.Orchestration;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Worker;

public sealed class PostgresWorkerState(NpgsqlDataSource dataSource) :
    IDurableRunSchedulePort, IRunStore, IContestPausePort, IAgentContextPort, IAgentDecisionPort
{
    private static readonly Guid PromptId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public async Task<int> EnsureAtomicallyAsync(IReadOnlyList<RunWindow> windows, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var inserted = 0;
        foreach (var window in windows)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO scheduled_agent_runs(id,run_key,agent_id,model_id,scheduled_at,deadline_at,next_attempt_at)
                VALUES($1,$2,$3,$4,$5,$6,$5) ON CONFLICT (run_key) DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue(StableGuid(window.RunKey));
            command.Parameters.AddWithValue(window.RunKey);
            command.Parameters.AddWithValue(window.AgentId);
            command.Parameters.AddWithValue(window.ModelId);
            command.Parameters.AddWithValue(window.ScheduledAt);
            command.Parameters.AddWithValue(window.DeadlineAt);
            inserted += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<ClaimedRun?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT run_key,agent_id,model_id,scheduled_at,deadline_at
            FROM claim_scheduled_run($1,interval '5 minutes',$2)
            """, connection);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var run = new RunWindow(reader.GetString(0), reader.GetGuid(1), reader.GetString(2),
            ParseSequence(reader.GetString(0)), reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4));
        return new ClaimedRun(run, token.ToString("D"));
    }

    public async Task CompleteAsync(RunCompletion completion, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(completion.ClaimToken, out var token)) throw new InvalidOperationException("Invalid run claim token.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int attempt;
        Guid scheduledId;
        await using (var lookup = new NpgsqlCommand("SELECT id,attempt_count FROM scheduled_agent_runs WHERE run_key=$1 FOR UPDATE", connection, transaction))
        {
            lookup.Parameters.AddWithValue(completion.Run.RunKey);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Scheduled run does not exist.");
            scheduledId = reader.GetGuid(0);
            attempt = reader.GetInt32(1);
        }

        var runId = Guid.NewGuid();
        var attested = completion.Result?.Attestation;
        var decision = attested?.Decision;
        if (attested is not null && (decision!.AgentId != completion.Run.AgentId ||
            !StringComparer.Ordinal.Equals(decision.ExactModelId, completion.Run.ModelId) || decision.DecisionAt != completion.Run.ScheduledAt))
            throw new DecisionValidationException("Attested decision identity does not match the immutable run.");
        using var auditDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            completion.Run.RunKey,
            completion.Run.AgentId,
            completion.Run.ModelId,
            outcome = completion.Outcome.ToString(),
            completion.Reason,
            decision = completion.Result?.Decision,
            ok = completion.Result?.Ok,
            attestation = attested is null ? null : new { attested.Provenance.RuntimeReportSha256, evidence_count = decision!.Evidence.Count }
        }));
        var auditJson = CanonicalJson.Serialize(auditDocument.RootElement);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO agent_runs(id,scheduled_run_id,attempt,agent_id,model_id,prompt_id,started_at,ended_at,status,audit_json,audit_hash)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9::run_status,$10::jsonb,canonical_jsonb_sha256($10::jsonb))
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue(runId); insert.Parameters.AddWithValue(scheduledId); insert.Parameters.AddWithValue(attempt);
            insert.Parameters.AddWithValue(completion.Run.AgentId); insert.Parameters.AddWithValue(completion.Run.ModelId);
            insert.Parameters.AddWithValue(PromptId); insert.Parameters.AddWithValue(completion.Run.ScheduledAt);
            insert.Parameters.AddWithValue(completion.CompletedAt); insert.Parameters.AddWithValue(Status(completion.Outcome));
            insert.Parameters.AddWithValue(auditJson);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Guid? orderId = null;
        if (completion.Outcome == RunAttemptOutcome.Succeeded && decision is not null && decision.Action != DecisionAction.Hold)
        {
            if (decision.Action == DecisionAction.CancelPending)
                throw new DecisionValidationException("cancelPending requires an explicit persisted order identity and is unavailable in this response contract.");
            await using (var state = new NpgsqlCommand("SELECT status::text FROM contest_state WHERE singleton FOR SHARE", connection, transaction))
                if (!StringComparer.Ordinal.Equals((string?)await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "RUNNING"))
                    throw new DecisionValidationException("Contest paused before the decision transaction committed.");
            await using var instrument = new NpgsqlCommand("SELECT id FROM instruments WHERE isin=$1 AND order_book_id=$2 AND mic='XSTO'", connection, transaction);
            instrument.Parameters.AddWithValue(decision.Instrument!.Isin); instrument.Parameters.AddWithValue(decision.Instrument.OrderBookId);
            var instrumentId = await instrument.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid? ??
                throw new DecisionValidationException("Decision instrument is not in the reviewed universe.");
            using var requestDocument = JsonDocument.Parse(completion.Result!.Decision!);
            var requestJson = CanonicalJson.Serialize(requestDocument.RootElement);
            await using var submit = new NpgsqlCommand("SELECT submit_order($1,$2,$3,$4,$5::order_side,$6,$7,$8,$9::jsonb,canonical_jsonb_sha256($9::jsonb))", connection, transaction);
            submit.Parameters.AddWithValue(Guid.NewGuid()); submit.Parameters.AddWithValue(completion.Run.AgentId);
            submit.Parameters.AddWithValue(decision.DecisionId); submit.Parameters.AddWithValue($"run:{completion.Run.RunKey}:{decision.DecisionId}");
            submit.Parameters.AddWithValue(decision.Action == DecisionAction.Buy ? "BUY" : "SELL"); submit.Parameters.AddWithValue(instrumentId);
            submit.Parameters.AddWithValue(decision.Quantity); submit.Parameters.AddWithValue(decision.DecisionAt);
            submit.Parameters.AddWithValue(requestJson);
            orderId = (Guid)(await submit.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Order was not persisted."));
        }
        if (attested is not null)
            await new ResearchAttestationStore().PersistAsync(connection, transaction, CreatePersistableAttestation(attested, runId, orderId), cancellationToken).ConfigureAwait(false);
        await using (var finish = new NpgsqlCommand("SELECT complete_scheduled_run($1,$2,$3::run_status,$4,$5,$6)", connection, transaction))
        {
            finish.Parameters.AddWithValue(scheduledId); finish.Parameters.AddWithValue(token); finish.Parameters.AddWithValue(Status(completion.Outcome));
            finish.Parameters.AddWithValue(completion.CompletedAt); AddNullable(finish, completion.Reason, NpgsqlDbType.Text);
            AddNullable(finish, completion.RetryAt, NpgsqlDbType.TimestampTz);
            await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsPausedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT status::text='PAUSED' FROM contest_state WHERE singleton", connection);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task<AgentContext> LoadIsolatedAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT count(*)=1 FROM agents WHERE id=$1", connection);
        command.Parameters.AddWithValue(agentId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Agent context identity is unavailable.");
        return new AgentContext(agentId);
    }

    public async Task<bool> TryAcceptWhileRunningAsync(RunWindow run, AgentRunResult result, CancellationToken cancellationToken)
    {
        if (result.Decision is null || result.Attestation is null)
            throw new DecisionValidationException("Runner output did not contain an attested decision.");
        var decision = result.Attestation.Decision;
        if (decision.AgentId != run.AgentId || !StringComparer.Ordinal.Equals(decision.ExactModelId, run.ModelId) || decision.DecisionAt != run.ScheduledAt)
            throw new DecisionValidationException("Decision identity must equal the immutable run identity.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var state = new NpgsqlCommand("SELECT status::text FROM contest_state WHERE singleton", connection))
            if (!StringComparer.Ordinal.Equals((string?)await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "RUNNING")) return false;
        if (decision.Action == DecisionAction.Hold) return true;
        if (decision.Action == DecisionAction.CancelPending)
            throw new DecisionValidationException("cancelPending requires an explicit persisted order identity and is unavailable in this response contract.");
        await using var instrument = new NpgsqlCommand("SELECT id FROM instruments WHERE isin=$1 AND order_book_id=$2 AND mic='XSTO'", connection);
        instrument.Parameters.AddWithValue(decision.Instrument!.Isin); instrument.Parameters.AddWithValue(decision.Instrument.OrderBookId);
        _ = await instrument.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid? ??
            throw new DecisionValidationException("Decision instrument is not in the reviewed universe.");
        return true;
    }

    public async Task<bool> ReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("""
                SELECT (SELECT count(*)=$1 FROM schema_migrations)
                   AND (SELECT count(*)=4 FROM agents)
                   AND (SELECT count(*)=1 FROM prompts WHERE id='00000000-0000-0000-0000-000000000001')
                   AND (SELECT count(DISTINCT session_id)>=20 FROM market_observations
                        WHERE verified AND NOT warning AND NOT suspended AND complete_history_sessions>=20)
                """, connection);
            command.Parameters.AddWithValue(MigrationCatalog.All.Count);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (NpgsqlException) { return false; }
    }

    private static int ParseSequence(string runKey) => int.TryParse(runKey[(runKey.LastIndexOf(':') + 1)..], out var value) ? value : 0;
    private static string Status(RunAttemptOutcome outcome) => outcome switch
    {
        RunAttemptOutcome.Succeeded => "SUCCEEDED",
        RunAttemptOutcome.Failed => "FAILED",
        RunAttemptOutcome.Missed => "MISSED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };
    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
    private static void AddNullable(NpgsqlCommand command, object? value, NpgsqlDbType type) =>
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value });

    private static PersistableResearchAttestation CreatePersistableAttestation(AttestedResearchDecision value, Guid runId, Guid? orderId)
    {
        var provenance = value.Provenance;
        var invocation = Canonicalize(JsonSerializer.Serialize(new
        {
            agent_id = provenance.AgentId,
            requested_model_id = provenance.RequestedModelId,
            requested_provider = provenance.RequestedProvider,
            model_id = provenance.ModelId,
            provider = provenance.Provider,
            runtime_report_sha256 = provenance.RuntimeReportSha256,
            provenance.Executable,
            arguments = provenance.Arguments,
            environment_variable_names = provenance.EnvironmentVariableNames,
            prompt_sha256 = provenance.PromptSha256,
            started_at = provenance.StartedAt,
            completed_at = provenance.CompletedAt,
            exit_code = provenance.ExitCode,
            standard_output_sha256 = provenance.StandardOutputSha256,
            standard_error_sha256 = provenance.StandardErrorSha256
        }));
        var evidence = Canonicalize(JsonSerializer.Serialize(value.Decision.Evidence.Select(item => new
        {
            original_url = item.OriginalUrl,
            final_url = item.FinalUrl,
            published_at = item.PublishedAt,
            retrieved_at = item.RetrievedAt,
            verification_started_at = item.VerificationStartedAt,
            content_sha256 = item.ContentSha256,
            exact_excerpt = item.ExactExcerpt,
            content_type = item.ContentType,
            response_headers = item.ResponseHeaders,
            immutable_content = Convert.ToBase64String(item.ImmutableContent.AsSpan()),
            hops = item.Hops
        })));
        return new PersistableResearchAttestation
        {
            Id = Guid.NewGuid(),
            AgentRunId = runId,
            OrderId = orderId,
            AgentId = provenance.AgentId,
            RequestedModelId = provenance.RequestedModelId,
            RequestedProvider = provenance.RequestedProvider,
            ActualModelId = provenance.ModelId,
            ActualProvider = provenance.Provider,
            InvocationJson = invocation,
            InvocationSha256 = Sha256(invocation),
            RuntimeReport = provenance.RuntimeReport,
            RuntimeReportSha256 = provenance.RuntimeReportSha256,
            EvidenceJson = evidence,
            EvidenceSha256 = Sha256(evidence),
            AttestedAt = provenance.CompletedAt
        };
    }

    private static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CanonicalJson.Serialize(document.RootElement);
    }

    private static string Sha256(string canonicalJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
}

public sealed class HermesAgentRunner(HermesResearchRunner runner, ResearchDecisionAttestor attestor) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken)
    {
        var prompt = $"""
            You are {request.ModelId}, fixed paper-trading agent {request.AgentId:D}. Paper trading only.
            Return exactly one strict JSON decision for immutable run {request.RunKey} at {request.DecisionAt:O}.
            canonicalRequestSha256 must equal the lowercase SHA-256 of this exact UTF-8 prompt.
            You may use only public web research. Never request or use brokerage capability or rival state.
            """;
        var result = await runner.RunAsync(request.AgentId, request.ModelId, prompt, cancellationToken).ConfigureAwait(false);
        var draft = new StrictDecisionJsonParser().Parse(result.StandardOutput, request.AgentId, request.ModelId);
        var attested = await attestor.AttestAsync(draft, result.Provenance, cancellationToken).ConfigureAwait(false);
        return AgentRunResult.Success(result.StandardOutput, attested);
    }
}

public sealed class WorkerRuntimeService(
    PostgresWorkerState state,
    DurableOrchestrator orchestrator,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<WorkerRuntimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("WorkerPollSeconds", 5);
        if (seconds is < 1 or > 60) throw new InvalidOperationException("WorkerPollSeconds must be between 1 and 60.");
        var poll = TimeSpan.FromSeconds(seconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, StockholmCalendar.Zone).DateTime);
                if (StockholmCalendar.GetSession(localDay) is { } marketSession)
                    await state.EnsureAtomicallyAsync(RunSchedule.Create(new Orchestration.TradingSession(marketSession.Day, marketSession.Open, marketSession.Close)), stoppingToken).ConfigureAwait(false);
                while (await orchestrator.TickAsync(stoppingToken).ConfigureAwait(false)) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Worker iteration failed closed"); }
            await Task.Delay(poll, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
