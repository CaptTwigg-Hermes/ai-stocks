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
    Task PostDecisionAsync(string runId, string json, CancellationToken cancellationToken);
}

public interface IExhibitionModelInvoker
{
    Task<ResearchExecutionResult> InvokeAsync(AgentDefinition agent, string prompt, CancellationToken cancellationToken);
}

public sealed record ExhibitionAgentFailure(Guid AgentId, string ModelId, string Error);
public sealed record ExhibitionCycleResult(DateTimeOffset ScheduledAt, int Succeeded, IReadOnlyList<ExhibitionAgentFailure> Failures);
public sealed record ExhibitionHealth(string Status, bool PrerequisitesReady, DateTimeOffset? LastCycleCompletedAt, int LastCycleFailures, string? LastError);

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
        var instrumentIds = ReadFixtureInstrumentIds(instrumentsJson);
        var failures = new List<ExhibitionAgentFailure>();
        var succeeded = 0;
        foreach (var agent in ContestContract.Agents)
        {
            var runId = CreateRunId(scheduledAt, agent);
            try
            {
                var prompt = ExhibitionPromptBuilder.Build(agent, runId, instrumentsJson, progressJson);
                var execution = await invoker.InvokeAsync(agent, prompt, cancellationToken).ConfigureAwait(false);
                var decision = new ExhibitionDecisionParser().Parse(execution.StandardOutput, agent, instrumentIds);
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
                    requestedModelId = provenance.RequestedModelId,
                    requestedProvider = provenance.RequestedProvider,
                    actualModelId = provenance.ModelId,
                    actualProvider = provenance.Provider,
                    runtimeReportSha256 = provenance.RuntimeReportSha256,
                    promptSha256 = provenance.PromptSha256,
                    completedAt = provenance.CompletedAt,
                    decision = new
                    {
                        action = decision.Action.ToString().ToLowerInvariant(),
                        instrumentId = decision.InstrumentId,
                        quantity = decision.Quantity,
                        decision.Reason,
                        decision.Confidence,
                        evidence = verified.Select(item => new
                        {
                            originalUrl = item.OriginalUrl,
                            finalUrl = item.FinalUrl,
                            item.PublishedAt,
                            item.RetrievedAt,
                            item.VerificationStartedAt,
                            item.ContentSha256,
                            item.ExactExcerpt,
                            item.ContentType,
                            responseHeaders = item.ResponseHeaders,
                            hops = item.Hops.Select(hop => new
                            {
                                requestedUrl = hop.RequestedUrl,
                                resolvedAddresses = hop.ResolvedAddresses.Select(address => address.ToString()),
                                pinnedAddress = hop.PinnedAddress.ToString(),
                                hop.StatusCode,
                                redirectTarget = hop.RedirectTarget,
                                hop.ResponseReceivedAt
                            })
                        })
                    }
                }, JsonOptions);
                await api.PostDecisionAsync(runId, payload, cancellationToken).ConfigureAwait(false);
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failures.Add(new ExhibitionAgentFailure(agent.Id, agent.ModelId, exception.Message));
                logger.LogError(exception, "Exhibition agent {AgentId} ({ModelId}) failed for {RunId}; no result was fabricated", agent.Id, agent.ModelId, runId);
            }
        }
        health.Complete(DateTimeOffset.UtcNow, failures.Count, failures.FirstOrDefault()?.Error);
        return new ExhibitionCycleResult(scheduledAt, succeeded, failures);
    }

    private static HashSet<string> ReadFixtureInstrumentIds(string json)
    {
        using var document = StrictJson.Parse(json, 2 * 1024 * 1024);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Instrument response must be an array.");
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("instrumentId", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || !result.Add(id.GetString()!))
                throw new InvalidOperationException("Every fixture instrument must have a unique non-empty instrumentId.");
        }
        if (result.Count == 0) throw new InvalidOperationException("Fixture instrument response cannot be empty.");
        return result;
    }

    private static string CreateRunId(DateTimeOffset scheduledAt, AgentDefinition agent)
    {
        var normalized = scheduledAt.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(agent.Id.ToString("D"))))[..12];
        return $"{normalized}-{suffix}";
    }
}
