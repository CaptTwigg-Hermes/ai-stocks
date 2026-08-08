using AiStocks.Core;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;

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
            !StringComparer.Ordinal.Equals(provenance.Provider, "copilot") || provenance.ExitCode != 0 ||
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
}
