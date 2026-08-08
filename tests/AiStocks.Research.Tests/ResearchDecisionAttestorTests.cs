using System.Collections.Immutable;
using AiStocks.Research.Decisions;
using AiStocks.Research.Evidence;
using AiStocks.Research.Execution;

namespace AiStocks.Research.Tests;

public sealed class ResearchDecisionAttestorTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AttestAsync_BindsVerifiedEvidenceAndInvocationIdentityIntoDecision()
    {
        var parser = new StrictDecisionJsonParser();
        var hash = new string('a', 64);
        var draft = parser.Parse(Json(hash), AgentId, "gpt-5.6-sol");
        var evidence = new StubVerifier();
        var attestor = new ResearchDecisionAttestor(evidence);
        var provenance = Provenance(hash);

        var result = await attestor.AttestAsync(draft, provenance, CancellationToken.None);

        Assert.Equal(provenance, result.Provenance);
        Assert.Equal(AgentId, result.Decision.AgentId);
        Assert.Equal("gpt-5.6-sol", result.Decision.ExactModelId);
        Assert.Single(result.Decision.Evidence);
        Assert.Equal("https://example.com/news", result.Decision.Evidence[0].FinalUrl.AbsoluteUri);
        Assert.Equal(hash, result.Decision.CanonicalRequestSha256);
    }

    [Fact]
    public async Task AttestAsync_RejectsProvenanceIdentityOrPromptHashMismatchBeforeFetching()
    {
        var hash = new string('a', 64);
        var draft = new StrictDecisionJsonParser().Parse(Json(hash), AgentId, "gpt-5.6-sol");
        var evidence = new StubVerifier();
        var attestor = new ResearchDecisionAttestor(evidence);

        await Assert.ThrowsAsync<DecisionValidationException>(() => attestor.AttestAsync(draft, Provenance(new string('b', 64)), CancellationToken.None));
        await Assert.ThrowsAsync<DecisionValidationException>(() => attestor.AttestAsync(draft, Provenance(hash) with { ModelId = "claude-opus-4.8" }, CancellationToken.None));
        Assert.Equal(0, evidence.Calls);
    }

    private static InvocationProvenance Provenance(string promptHash) => new()
    {
        AgentId = AgentId,
        ModelId = "gpt-5.6-sol",
        Provider = "copilot",
        Executable = "hermes",
        Arguments = ImmutableArray.Create("--provider", "copilot"),
        EnvironmentVariableNames = ImmutableArray<string>.Empty,
        PromptSha256 = promptHash,
        StartedAt = DateTimeOffset.Parse("2026-08-08T09:59:00Z"),
        CompletedAt = DateTimeOffset.Parse("2026-08-08T10:01:00Z"),
        ExitCode = 0,
        StandardOutputSha256 = new string('c', 64),
        StandardErrorSha256 = new string('d', 64)
    };

    private static string Json(string hash) => $$"""
    {"decisionId":"decision-1","agentId":"{{AgentId}}","modelId":"gpt-5.6-sol","action":"buy",
    "instrument":{"isin":"SE0000000001","orderBookId":"123","mic":"XSTO"},"quantity":10,
    "decisionAt":"2026-08-08T10:00:00Z","observedPrice":123.45,"reason":"Reason","catalyst":"Catalyst",
    "risks":["Risk"],"confidence":0.75,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-08T09:00:00Z","exactExcerpt":"Exact catalyst text"}],
    "canonicalRequestSha256":"{{hash}}"}
    """;

    private sealed class StubVerifier : IEvidenceVerifier
    {
        public int Calls { get; private set; }
        public Task<AiStocks.Core.VerifiedEvidence> VerifyAsync(EvidenceClaim claim, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AiStocks.Core.VerifiedEvidence(claim.Url, claim.PublishedAt,
                DateTimeOffset.Parse("2026-08-08T10:00:01Z"), new string('e', 64), claim.ExactExcerpt));
        }
    }
}
