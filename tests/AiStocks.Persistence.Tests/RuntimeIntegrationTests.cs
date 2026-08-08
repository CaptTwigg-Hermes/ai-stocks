using AiStocks.Core;
using AiStocks.Operations;
using AiStocks.Persistence;
using AiStocks.Research.Decisions;
using AiStocks.Research.Execution;
using AiStocks.Web;
using AiStocks.Worker;
using AiStocks.Worker.Orchestration;
using Npgsql;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace AiStocks.Persistence.Tests;

public sealed class RuntimeIntegrationTests
{
    [Fact]
    public void StrictDatabaseConfigurationRejectsMissingAndMalformedValuesWithoutEchoingSecrets()
    {
        Assert.Throws<RuntimeConfigurationException>(() =>
            PostgresConfiguration.Require(new Dictionary<string, string?>(), "DATABASE_URL"));
        var exception = Assert.Throws<RuntimeConfigurationException>(() =>
            PostgresConfiguration.Require(new Dictionary<string, string?> { ["DATABASE_URL"] = "secret-value" }, "DATABASE_URL"));
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationsCommandsExecuteDistinctPortsAndReturnSafeErrors()
    {
        var ports = new RecordingOperationsPorts();
        Assert.Equal(0, await OperationsApplication.RunAsync(["migrate"], ports, TextWriter.Null, default));
        Assert.Equal(0, await OperationsApplication.RunAsync(["bootstrap"], ports, TextWriter.Null, default));
        Assert.Equal(0, await OperationsApplication.RunAsync(["preflight"], ports, TextWriter.Null, default));
        Assert.Equal(["migrate", "bootstrap", "preflight"], ports.Calls);
        var output = new StringWriter();
        Assert.Equal(2, await OperationsApplication.RunAsync(["unknown"], ports, output, default));
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposablePostgresExercisesRunDeliveryDashboardAndOwnerControlTransactions()
    {
        var configured = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        await using var database = await DisposableDatabase.CreateAsync(configured);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresMigrationRunner(source).ApplyAsync();

        var operations = new PostgresOperationsPorts(source, source);
        await operations.BootstrapAsync(default);
        var health = await operations.PreflightAsync(default);
        Assert.False(health.Ready); // no twenty-session market-data warm-up in a fresh database

        var session = new TradingSession(new(2026, 8, 10), new DateTimeOffset(2026, 8, 10, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 10, 15, 30, 0, TimeSpan.Zero));
        var worker = new PostgresWorkerState(source);
        Assert.Equal(24, await worker.EnsureAtomicallyAsync(RunSchedule.Create(session), default));
        var claimed = Assert.IsType<ClaimedRun>(await worker.ClaimNextAsync(session.OpenAt.AddHours(-1), default));

        var delivery = new PostgresDeliveryAuditPort(source);
        var reservation = await delivery.ReserveAsync("daily:2026-08-10", new string('a', 64), default);
        Assert.Equal(ReservationState.Acquired, reservation.State);
        await delivery.BeginSendAsync("daily:2026-08-10", new string('a', 64), reservation.LeaseToken!, default);
        await delivery.RecordAsync(new DeliveryAudit("daily:2026-08-10", new string('a', 64), DeliveryStatus.Succeeded,
            "receipt", null, DateTimeOffset.UtcNow), reservation.LeaseToken!, default);
        Assert.Equal(ReservationState.AlreadyCompleted, (await delivery.ReserveAsync("daily:2026-08-10", new string('a', 64), default)).State);

        var dashboard = new PostgresDashboardFacade(source, TimeProvider.System);
        var draft = await dashboard.QueryAsync(default);
        Assert.Equal("draft", draft.Status);
        var started = await dashboard.ControlAsync(new(ContestControlAction.Start, "owner@example.com", "start-1"), default);
        Assert.False(started.Paused);
        await using (var setup = await source.OpenConnectionAsync())
        await using (var instrument = new NpgsqlCommand("""
            INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
            VALUES(gen_random_uuid(),'SE0000000001','RUNTIMEISSUER0000001','book-runtime','XSTO','RUNTIME','ESVUFR','2026-01-01',
                   '{"source":"runtime-test"}',canonical_jsonb_sha256('{"source":"runtime-test"}'))
            """, setup))
            await instrument.ExecuteNonQueryAsync();
        await using (var setup = await source.OpenConnectionAsync())
        await using (var observation = new NpgsqlCommand("""
            INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at)
            VALUES('runtime-authority','2026-08-07','2026-08-07T07:00:00Z','2026-08-07T15:30:00Z');
            INSERT INTO raw_market_reports VALUES(gen_random_uuid(),'runtime-authority-report','https://example.test/runtime',
              '2026-08-07T14:15:00Z','x',encode(digest('x','sha256'),'hex'),'{"fixture":true}',
              canonical_jsonb_sha256('{"fixture":true}'));
            INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,
              average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
            SELECT gen_random_uuid(),i.id,r.id,'2026-08-07T14:00:00Z','2026-08-07T14:15:00Z',100,10,
              1000000,20,'runtime-authority',false,false,false,true,'{"price":100}',canonical_jsonb_sha256('{"price":100}')
            FROM instruments i CROSS JOIN raw_market_reports r
            WHERE i.isin='SE0000000001' AND r.report_name='runtime-authority-report'
            """, setup))
            await observation.ExecuteNonQueryAsync();
        var decision = $$"""
            {"decisionId":"runtime-buy","agentId":"{{claimed.Run.AgentId:D}}","modelId":"{{claimed.Run.ModelId}}",
             "action":"buy","instrument":{"isin":"SE0000000001","orderBookId":"book-runtime","mic":"XSTO"},
             "quantity":1,"decisionAt":"{{claimed.Run.ScheduledAt:O}}","observedPrice":100,"reason":"verified test decision",
             "catalyst":"public catalyst","risks":["loss"],"confidence":0.5,
             "evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-09T10:00:00+00:00","exactExcerpt":"news"}],
             "canonicalRequestSha256":"{{new string('a', 64)}}"}
            """;
        var parsed = new StrictDecisionJsonParser().Parse(decision, claimed.Run.AgentId, claimed.Run.ModelId);
        var verifiedEvidence = parsed.Evidence.Select(claim => new VerifiedEvidence(
            claim.Url, claim.PublishedAt, claimed.Run.ScheduledAt,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(claim.ExactExcerpt))), claim.ExactExcerpt)
        {
            VerificationStartedAt = claimed.Run.ScheduledAt,
            Hops = [],
            ResponseHeaders = ImmutableDictionary<string, string>.Empty,
            ContentType = "text/plain",
            ImmutableContent = Encoding.UTF8.GetBytes(claim.ExactExcerpt).ToImmutableArray()
        }).ToArray();
        var orderDecision = new OrderDecision(parsed.DecisionId, parsed.AgentId, parsed.ModelId, parsed.Action,
            parsed.Instrument, parsed.Quantity, parsed.DecisionAt, parsed.ObservedPrice, parsed.Reason,
            parsed.Catalyst, parsed.Risks, parsed.Confidence, verifiedEvidence, parsed.CanonicalRequestSha256);
        var runtimeReport = Encoding.UTF8.GetBytes($$"""
            {"model":"{{claimed.Run.ModelId}}","provider":"copilot","completed":true,"failed":false,"api_calls":1}
            """);
        var provenance = new InvocationProvenance
        {
            AgentId = claimed.Run.AgentId,
            RequestedModelId = claimed.Run.ModelId,
            RequestedProvider = "copilot",
            ModelId = claimed.Run.ModelId,
            Provider = "copilot",
            RuntimeReport = runtimeReport.ToImmutableArray(),
            RuntimeReportSha256 = Convert.ToHexStringLower(SHA256.HashData(runtimeReport)),
            Executable = "hermes",
            Arguments = ImmutableArray.Create("--provider", "copilot", "--model", claimed.Run.ModelId),
            EnvironmentVariableNames = ImmutableArray<string>.Empty,
            PromptSha256 = parsed.CanonicalRequestSha256,
            StartedAt = claimed.Run.ScheduledAt.AddMinutes(-1),
            CompletedAt = claimed.Run.ScheduledAt,
            ExitCode = 0,
            StandardOutputSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(decision))),
            StandardErrorSha256 = Convert.ToHexStringLower(SHA256.HashData([]))
        };
        var attestation = new AttestedResearchDecision(orderDecision, provenance);
        var attestedResult = AgentRunResult.Success(decision, attestation);
        Assert.True(await worker.TryAcceptWhileRunningAsync(claimed.Run, attestedResult, default));
        var badOutput = AgentRunResult.Success(decision, new AttestedResearchDecision(orderDecision,
            provenance with { StandardOutputSha256 = new string('0', 64) }));
        await Assert.ThrowsAsync<DecisionValidationException>(() => worker.CompleteAsync(new RunCompletion(
            claimed.Run, claimed.ClaimToken, RunAttemptOutcome.Succeeded,
            session.OpenAt.AddHours(-1).AddMinutes(1), null, null, badOutput), default));
        var corruptEvidence = orderDecision with
        {
            Evidence = [verifiedEvidence[0] with { ContentSha256 = new string('0', 64) }]
        };
        var badEvidence = AgentRunResult.Success(decision, new AttestedResearchDecision(corruptEvidence, provenance));
        await Assert.ThrowsAsync<DecisionValidationException>(() => worker.CompleteAsync(new RunCompletion(
            claimed.Run, claimed.ClaimToken, RunAttemptOutcome.Succeeded,
            session.OpenAt.AddHours(-1).AddMinutes(1), null, null, badEvidence), default));
        await worker.CompleteAsync(new RunCompletion(claimed.Run, claimed.ClaimToken, RunAttemptOutcome.Succeeded,
            session.OpenAt.AddHours(-1).AddMinutes(1), null, null, attestedResult), default);
        var paused = await dashboard.ControlAsync(new(ContestControlAction.Pause, "owner@example.com", "pause-1"), default);
        Assert.True(paused.Paused);

        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM scheduled_agent_runs WHERE status='SUCCEEDED'), (SELECT count(*) FROM agent_runs), (SELECT count(*) FROM delivery_audits WHERE status='SUCCEEDED'), (SELECT count(*) FROM orders)", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(1, reader.GetInt64(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public async Task DisposablePostgresSerializesDeliveryLeaseAndFailsClosedAfterSendBegins()
    {
        var configured = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        await using var database = await DisposableDatabase.CreateAsync(configured);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresMigrationRunner(source).ApplyAsync();
        var port = new PostgresDeliveryAuditPort(source);
        var hash = new string('b', 64);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => port.ReserveAsync("daily:lease-race", hash, default)));
        var acquired = Assert.Single(attempts, x => x.State == ReservationState.Acquired);
        Assert.All(attempts.Where(x => x != acquired), x => Assert.Equal(ReservationState.Busy, x.State));

        await port.BeginSendAsync("daily:lease-race", hash, acquired.LeaseToken!, default);
        Assert.Equal(ReservationState.Uncertain,
            (await port.ReserveAsync("daily:lease-race", hash, default)).State);
    }

    private sealed class RecordingOperationsPorts : IOperationsPorts
    {
        public List<string> Calls { get; } = [];
        public Task MigrateAsync(CancellationToken cancellationToken) { Calls.Add("migrate"); return Task.CompletedTask; }
        public Task BootstrapAsync(CancellationToken cancellationToken) { Calls.Add("bootstrap"); return Task.CompletedTask; }
        public Task<ReadinessResult> PreflightAsync(CancellationToken cancellationToken) { Calls.Add("preflight"); return Task.FromResult(new ReadinessResult(true, [])); }
    }

    private sealed class DisposableDatabase : IAsyncDisposable
    {
        private readonly string adminConnectionString;
        private readonly string name;
        private DisposableDatabase(string adminConnectionString, string name, string connectionString) =>
            (this.adminConnectionString, this.name, ConnectionString) = (adminConnectionString, name, connectionString);
        public string ConnectionString { get; }

        public static async Task<DisposableDatabase> CreateAsync(string configured)
        {
            var configuredBuilder = new NpgsqlConnectionStringBuilder(configured);
            if (!(configuredBuilder.Database ?? "").Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AISTOCKS_TEST_DATABASE_URL database name must contain test.");
            var admin = new NpgsqlConnectionStringBuilder(configuredBuilder.ConnectionString) { Database = "postgres" };
            var name = $"ai_stocks_runtime_test_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", connection);
            await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name, IncludeErrorDetail = true };
            return new DisposableDatabase(admin.ConnectionString, name, target.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}