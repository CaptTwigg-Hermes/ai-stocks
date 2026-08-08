using AiStocks.Core;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiStocks.Research.Decisions;

public sealed record AttestedResearchDecision(OrderDecision Decision, InvocationProvenance Provenance);

public sealed class ResearchDecisionAttestor(IEvidenceVerifier evidenceVerifier)
{
    private readonly IEvidenceVerifier _evidenceVerifier = evidenceVerifier ?? throw new ArgumentNullException(nameof(evidenceVerifier));

    public async Task<AttestedResearchDecision> AttestAsync(
        ResearchDecisionDraft draft,
        InvocationProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(provenance);
        if (draft.AgentId != provenance.AgentId || !StringComparer.Ordinal.Equals(draft.ModelId, provenance.ModelId) ||
            !StringComparer.Ordinal.Equals(provenance.RequestedModelId, provenance.ModelId) ||
            !StringComparer.Ordinal.Equals(provenance.RequestedProvider, provenance.Provider) ||
            !StringComparer.Ordinal.Equals(provenance.Provider, "copilot") || provenance.ExitCode != 0 ||
            provenance.RuntimeReport.IsDefaultOrEmpty ||
            !StringComparer.Ordinal.Equals(Convert.ToHexStringLower(SHA256.HashData(provenance.RuntimeReport.AsSpan())), provenance.RuntimeReportSha256) ||
            !RuntimeReportMatches(provenance) ||
            !StringComparer.Ordinal.Equals(draft.CanonicalRequestSha256, provenance.PromptSha256))
            throw new DecisionValidationException("Decision is not bound to the successful Copilot invocation that produced it.");

        var evidence = new List<VerifiedEvidence>(draft.Evidence.Count);
        foreach (var claim in draft.Evidence)
            evidence.Add(await _evidenceVerifier.VerifyAsync(claim, cancellationToken).ConfigureAwait(false));

        var decision = new OrderDecision(draft.DecisionId, draft.AgentId, draft.ModelId, draft.Action, draft.Instrument,
            draft.Quantity, draft.DecisionAt, draft.ObservedPrice, draft.Reason, draft.Catalyst,
            draft.Risks.ToArray(), draft.Confidence, evidence.AsReadOnly(), draft.CanonicalRequestSha256);
        return new AttestedResearchDecision(decision, provenance);
    }

    private static bool RuntimeReportMatches(InvocationProvenance provenance)
    {
        try
        {
            using var document = JsonDocument.Parse(provenance.RuntimeReport.AsMemory(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                SingleString(root, "model", provenance.ModelId) &&
                SingleString(root, "provider", provenance.Provider) &&
                SingleBoolean(root, "completed", true) &&
                SingleBoolean(root, "failed", false) &&
                SinglePositiveInteger(root, "api_calls");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool SingleString(JsonElement root, string name, string expected)
    {
        var values = root.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return values.Length == 1 && values[0].Value.ValueKind == JsonValueKind.String &&
            StringComparer.Ordinal.Equals(values[0].Value.GetString(), expected);
    }

    private static bool SingleBoolean(JsonElement root, string name, bool expected)
    {
        var values = root.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return values.Length == 1 && values[0].Value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            values[0].Value.GetBoolean() == expected;
    }

    private static bool SinglePositiveInteger(JsonElement root, string name)
    {
        var values = root.EnumerateObject().Where(property => property.NameEquals(name)).ToArray();
        return values.Length == 1 && values[0].Value.ValueKind == JsonValueKind.Number &&
            values[0].Value.TryGetInt64(out var value) && value > 0;
    }
}
