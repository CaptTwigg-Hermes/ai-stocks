using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiStocks.Core;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;

namespace AiStocks.Exhibition.Worker;

public interface IExhibitionApi
{
    Task<string> GetInstrumentsAsync(CancellationToken cancellationToken);
    Task<string> GetProgressAsync(CancellationToken cancellationToken);
    Task PostStatusAsync(string json, CancellationToken cancellationToken);
    Task PostDecisionAsync(string runId, string json, CancellationToken cancellationToken);
}

public interface IExhibitionModelInvoker
{
    Task<ResearchExecutionResult> InvokeAsync(AgentDefinition agent, string prompt, CancellationToken cancellationToken);
}

public sealed record ExhibitionAgentFailure(Guid AgentId, string ModelId, string Error);
public sealed record ExhibitionCycleResult(DateTimeOffset ScheduledAt, int Succeeded, IReadOnlyList<ExhibitionAgentFailure> Failures);
public sealed record ExhibitionHealth(string Status, bool PrerequisitesReady, DateTimeOffset? LastCycleCompletedAt, int LastCycleFailures, string? LastError);
public sealed record DelayedObservation(decimal PriceSek, DateTimeOffset AvailableAt);

public sealed class ExhibitionHealthState
{
    private readonly object _gate = new();
    private ExhibitionHealth _value = new("not-ready", false, null, 0, "Prerequisites have not been verified.");

    public void PrerequisitesReady()
    {
        lock (_gate) _value = _value with { Status = "ready", PrerequisitesReady = true, LastError = null };
    }

    public void Complete(DateTimeOffset completedAt, int failures, string? error)
    {
        lock (_gate) _value = new(failures == 0 ? "ready" : "degraded", true, completedAt, failures, error);
    }

    public ExhibitionHealth Snapshot() { lock (_gate) return _value; }
}

public sealed class ExhibitionCycle(
    IExhibitionApi api,
    IExhibitionModelInvoker invoker,
    IEvidenceVerifier evidenceVerifier,
    ExhibitionHealthState health,
    ILogger<ExhibitionCycle> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExhibitionCycleResult> RunAsync(DateTimeOffset scheduledAt, CancellationToken cancellationToken)
    {
        var instrumentsJson = await api.GetInstrumentsAsync(cancellationToken).ConfigureAwait(false);
        var progressJson = await api.GetProgressAsync(cancellationToken).ConfigureAwait(false);
        var observations = ReadDelayedObservations(instrumentsJson);
        var failures = new List<ExhibitionAgentFailure>();
        var succeeded = 0;
        foreach (var agent in ContestContract.Agents)
        {
            var runId = CreateRunId(scheduledAt, agent);
            try
            {
                await api.PostStatusAsync(StatusJson(runId, agent, "queued", null, scheduledAt), cancellationToken)
                    .ConfigureAwait(false);
                await api.PostStatusAsync(StatusJson(runId, agent, "running", null, DateTimeOffset.UtcNow), cancellationToken)
                    .ConfigureAwait(false);
                var prompt = ExhibitionPromptBuilder.Build(agent, runId, instrumentsJson, progressJson);
                var execution = await invoker.InvokeAsync(agent, prompt, cancellationToken).ConfigureAwait(false);
                var decision = new ExhibitionDecisionParser().Parse(execution.StandardOutput, agent, observations);
                DelayedObservation? selectedObservation = null;
                if (decision.InstrumentId is not null)
                    selectedObservation = observations[decision.InstrumentId];
                var verified = new List<VerifiedEvidence>(decision.Evidence.Count);
                foreach (var claim in decision.Evidence)
                    verified.Add(await evidenceVerifier.VerifyAsync(claim, cancellationToken).ConfigureAwait(false));
                var provenance = execution.Provenance;
                if (provenance.AgentId != agent.Id || provenance.ModelId != agent.ModelId ||
                    provenance.RequestedModelId != agent.ModelId || provenance.Provider != "copilot" ||
                    provenance.RequestedProvider != "copilot" || provenance.ExitCode != 0)
                    throw new InvalidOperationException("Hermes provenance does not attest the fixed successful model/provider.");
                var payload = JsonSerializer.Serialize(new
                {
                    runId,
                    agentId = agent.Id,
                    modelId = agent.ModelId,
                    action = decision.Action.ToString().ToLowerInvariant(),
                    decision.InstrumentId,
                    decision.Quantity,
                    decision.Reason,
                    decision.Confidence,
                    evidence = verified.Select(item => new
                    {
                        url = item.FinalUrl.AbsoluteUri,
                        item.PublishedAt,
                        item.RetrievedAt,
                        item.ContentSha256,
                        item.ExactExcerpt
                    }),
                    runtimeModel = provenance.ModelId,
                    runtimeProvider = provenance.Provider,
                    runtimeModelObserved = true,
                    runtimeProviderObserved = true,
                    providerMatch = provenance.Provider == provenance.RequestedProvider,
                    modelMatch = provenance.ModelId == provenance.RequestedModelId,
                    reportSha256 = provenance.RuntimeReportSha256,
                    completedAt = provenance.CompletedAt,
                    promptSha256 = provenance.PromptSha256,
                    observedPriceSek = selectedObservation?.PriceSek,
                    observationAvailableAt = selectedObservation?.AvailableAt
                }, JsonOptions);
                await api.PostDecisionAsync(runId, payload, cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                try
                {
                    var authoritative = await api.GetProgressAsync(cancellationToken).ConfigureAwait(false);
                    if (IsAuthoritativeSuccess(authoritative, agent, runId))
                    {
                        succeeded++;
                        logger.LogWarning(exception,
                            "Exhibition decision response was lost for {AgentId} ({RunId}); authoritative API state confirms success",
                            agent.Id, runId);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception reconciliationException)
                {
                    logger.LogWarning(reconciliationException,
                        "Could not reconcile exhibition agent {AgentId} ({RunId}) after failure", agent.Id, runId);
                }
                failures.Add(new ExhibitionAgentFailure(agent.Id, agent.ModelId, exception.Message));
                try
                {
                    var boundedError = exception.Message.Length > 1_000 ? exception.Message[..1_000] : exception.Message;
                    await api.PostStatusAsync(StatusJson(runId, agent, "failed", boundedError, DateTimeOffset.UtcNow), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception statusException)
                {
                    logger.LogError(statusException, "Could not publish failure status for exhibition agent {AgentId}", agent.Id);
                }
                logger.LogError(exception, "Exhibition agent {AgentId} ({ModelId}) failed for {RunId}; no result was fabricated", agent.Id, agent.ModelId, runId);
            }
        }
        health.Complete(DateTimeOffset.UtcNow, failures.Count, failures.FirstOrDefault()?.Error);
        return new ExhibitionCycleResult(scheduledAt, succeeded, failures);
    }

    private static string StatusJson(string runId, AgentDefinition agent, string status, string? error, DateTimeOffset occurredAt) =>
        JsonSerializer.Serialize(new { runId, agentId = agent.Id, modelId = agent.ModelId, status, error, occurredAt }, JsonOptions);

    private static Dictionary<string, DelayedObservation> ReadDelayedObservations(string json)
    {
        using var document = StrictJson.Parse(json, 2 * 1024 * 1024);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            !document.RootElement.TryGetProperty("dataMode", out var dataMode) || dataMode.GetString() != ExhibitionDataContract.DataMode)
            throw new InvalidOperationException("Instrument response must contain official delayed Nasdaq XSTO data.");
        var result = new Dictionary<string, DelayedObservation>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || result.ContainsKey(id.GetString()!) ||
                !item.TryGetProperty("price", out var priceElement) ||
                !priceElement.TryGetDecimal(out var price) || price <= 0m ||
                !item.TryGetProperty("executedAt", out var executedElement) || !executedElement.TryGetDateTimeOffset(out var executedAt) ||
                !item.TryGetProperty("availableAt", out var availableElement) || !availableElement.TryGetDateTimeOffset(out var availableAt) ||
                availableAt < executedAt.AddMinutes(15) ||
                !item.TryGetProperty("exchange", out var exchange) || exchange.GetString() != "XSTO" ||
                !item.TryGetProperty("currency", out var currency) || currency.GetString() != "SEK" ||
                !item.TryGetProperty("isPreviewPrice", out var isPreviewPrice) || isPreviewPrice.ValueKind != JsonValueKind.False ||
                item.TryGetProperty("priceDkk", out var priceDkk) && priceDkk.ValueKind != JsonValueKind.Null ||
                !item.TryGetProperty("source", out var source) || source.GetString() != ExhibitionDataContract.Source ||
                !item.TryGetProperty("delayMinutes", out var delayMinutes) || !delayMinutes.TryGetInt32(out var delay) || delay != 15 ||
                !item.TryGetProperty("tradable", out var tradable) || tradable.ValueKind != JsonValueKind.False ||
                !item.TryGetProperty("paperTradable", out var paperTradable) || paperTradable.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Every delayed instrument must have a positive price, timestamps, and exact assumed-fill Nasdaq metadata.");
            result.Add(id.GetString()!, new(price, availableAt));
        }
        if (result.Count == 0) throw new InvalidOperationException("Delayed instrument response cannot be empty.");
        return result;
    }

    private static bool IsAuthoritativeSuccess(string json, AgentDefinition agent, string runId)
    {
        using var document = StrictJson.Parse(json, 2 * 1024 * 1024);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("dataMode", out var dataMode) || dataMode.GetString() != ExhibitionDataContract.DataMode ||
            !root.TryGetProperty("executionMode", out var executionMode) || executionMode.GetString() != ExhibitionDataContract.ExecutionMode ||
            !root.TryGetProperty("isNonLive", out var isNonLive) || isNonLive.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("strictContest", out var strictContest) || strictContest.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("holdOnly", out var holdOnly) || holdOnly.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("assumedFills", out var assumedFills) || assumedFills.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("assumedSekToDkk", out var assumedSekToDkk) ||
            !assumedSekToDkk.TryGetDecimal(out var fx) || fx != ExhibitionDataContract.AssumedSekToDkk ||
            !root.TryGetProperty("assumedSlippagePercent", out var assumedSlippage) ||
            !assumedSlippage.TryGetDecimal(out var slippage) || slippage != ExhibitionDataContract.AssumedSlippagePercent ||
            !root.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array)
            return false;
        var matchingParticipants = 0;
        var targetIdentityCount = 0;
        foreach (var participant in participants.EnumerateArray())
        {
            if (participant.ValueKind != JsonValueKind.Object ||
                !participant.TryGetProperty("portfolio", out var portfolio) || portfolio.ValueKind != JsonValueKind.Object ||
                !portfolio.TryGetProperty("dataMode", out var portfolioDataMode) ||
                portfolioDataMode.GetString() != ExhibitionDataContract.DataMode ||
                !portfolio.TryGetProperty("executionMode", out var portfolioExecutionMode) ||
                portfolioExecutionMode.GetString() != ExhibitionDataContract.ExecutionMode)
                return false;
            if (!participant.TryGetProperty("agentId", out var agentId) || !agentId.TryGetGuid(out var parsedAgentId) ||
                parsedAgentId != agent.Id)
                continue;
            targetIdentityCount++;
            if (targetIdentityCount > 1) return false;
            if (!participant.TryGetProperty("modelId", out var modelId) || modelId.GetString() != agent.ModelId ||
                !participant.TryGetProperty("runId", out var participantRunId) || participantRunId.GetString() != runId ||
                !participant.TryGetProperty("status", out var status) || status.GetString() != "succeeded" ||
                !participant.TryGetProperty("queuedAt", out var queuedAtElement) ||
                !queuedAtElement.TryGetDateTimeOffset(out var queuedAt) ||
                !participant.TryGetProperty("startedAt", out var startedAtElement) ||
                !startedAtElement.TryGetDateTimeOffset(out var startedAt) || startedAt <= queuedAt ||
                !participant.TryGetProperty("completedAt", out var completedAtElement) ||
                !completedAtElement.TryGetDateTimeOffset(out var completedAt) || completedAt <= startedAt ||
                !participant.TryGetProperty("latestDecision", out var decision) || decision.ValueKind != JsonValueKind.Object ||
                !decision.TryGetProperty("runId", out var decisionRunId) || decisionRunId.GetString() != runId ||
                !decision.TryGetProperty("completedAt", out var decisionCompletedAtElement) ||
                !decisionCompletedAtElement.TryGetDateTimeOffset(out var decisionCompletedAt) || decisionCompletedAt != completedAt ||
                !HasValidDecisionAudit(decision))
                continue;
            matchingParticipants++;
            if (matchingParticipants > 1) return false;
        }
        return matchingParticipants == 1;
    }

    private static bool HasValidDecisionAudit(JsonElement decision)
    {
        if (!decision.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.String)
            return false;
        var action = actionElement.GetString();
        if (!decision.TryGetProperty("instrumentId", out var instrument) ||
            !decision.TryGetProperty("quantity", out var quantityElement) || !quantityElement.TryGetInt32(out var quantity))
            return false;
        if (action == "hold") return instrument.ValueKind == JsonValueKind.Null && quantity == 0;
        if (action is not ("buy" or "sell") || instrument.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(instrument.GetString()) || quantity <= 0 ||
            !decision.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array)
            return false;
        return evidence.EnumerateArray().Any();
    }

    private static string CreateRunId(DateTimeOffset scheduledAt, AgentDefinition agent)
    {
        var normalized = scheduledAt.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(agent.Id.ToString("D"))))[..12];
        return $"{normalized}-{suffix}";
    }
}
