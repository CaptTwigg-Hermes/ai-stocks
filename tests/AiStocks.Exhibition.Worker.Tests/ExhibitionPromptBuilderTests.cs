using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionPromptBuilderTests
{
    private const string DataMode = "official-nasdaq-xsto-15m-delayed";
    private const string ExecutionMode = "assumed-delayed-paper-fills-v1";

    [Fact]
    public void Build_DescribesAssumedFillRulesAndOnlyTheRequestedAgentsPortfolio()
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "{\"items\":[{\"id\":\"SE0000115446\",\"name\":\"Example AB\",\"exchange\":\"XSTO\",\"currency\":\"SEK\",\"price\":100,\"isPreviewPrice\":false,\"executedAt\":\"2026-08-16T10:00:00Z\",\"availableAt\":\"2026-08-16T10:15:00Z\",\"source\":\"Nasdaq Nordic MiFID II delayed post-trade\",\"delayMinutes\":15,\"tradable\":false,\"paperTradable\":true}],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}";
        var progress = Progress(
            "{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":{\"cashDkk\":30000,\"holdings\":[],\"dataMode\":\"" + DataMode + "\",\"executionMode\":\"" + ExecutionMode + "\"}}," +
            "{\"agentId\":\"" + ContestContract.Agents[1].Id.ToString("D") + "\",\"portfolio\":{\"cashDkk\":99999,\"holdings\":[{\"secret\":\"RIVAL_SECRET\"}],\"dataMode\":\"" + DataMode + "\",\"executionMode\":\"" + ExecutionMode + "\"}}");

        var prompt = ExhibitionPromptBuilder.Build(agent, "run-123", instruments, progress);

        Assert.Contains("official Nasdaq XSTO", prompt, StringComparison.Ordinal);
        Assert.Contains("delayed", prompt, StringComparison.Ordinal);
        Assert.Contains("non-live", prompt, StringComparison.Ordinal);
        Assert.Contains("separate assumed-fill paper exhibition", prompt, StringComparison.Ordinal);
        Assert.Contains("No brokerage or real orders", prompt, StringComparison.Ordinal);
        Assert.Contains("0.65 DKK/SEK", prompt, StringComparison.Ordinal);
        Assert.Contains("1% adverse slippage", prompt, StringComparison.Ordinal);
        Assert.Contains("10,000 DKK", prompt, StringComparison.Ordinal);
        Assert.Contains("25,000 DKK", prompt, StringComparison.Ordinal);
        Assert.Contains("buy, sell, or hold", prompt, StringComparison.Ordinal);
        Assert.Contains("current instrument ID", prompt, StringComparison.Ordinal);
        Assert.Contains("whole positive quantity", prompt, StringComparison.Ordinal);
        Assert.Contains("published no later than", prompt, StringComparison.Ordinal);
        Assert.Contains("MUST use web_search", prompt, StringComparison.Ordinal);
        Assert.Contains("at least three diverse", prompt, StringComparison.Ordinal);
        Assert.Contains("expand up to eight", prompt, StringComparison.Ordinal);
        Assert.Contains("verifier_eligible=true", prompt, StringComparison.Ordinal);
        Assert.Contains("discovery_text cannot be submitted as evidence", prompt, StringComparison.Ordinal);
        Assert.Contains("maximize ending portfolio value", prompt, StringComparison.Ordinal);
        Assert.Contains("remaining fully in cash can lose", prompt, StringComparison.Ordinal);
        Assert.Contains("small exploratory position", prompt, StringComparison.Ordinal);
        Assert.Contains("search-result snippet is discovery only", prompt, StringComparison.Ordinal);
        Assert.Contains("mcp_research_fetch_public_https", prompt, StringComparison.Ordinal);
        Assert.Contains("evidence_candidates", prompt, StringComparison.Ordinal);
        Assert.Contains("issuer investor-relations", prompt, StringComparison.Ordinal);
        Assert.Contains("may use an empty evidence array", prompt, StringComparison.Ordinal);
        Assert.Contains("30000", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("RIVAL_SECRET", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("HOLD-only", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Example shape only", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryAfterInvalidFinalResponse_RequiresImmediateJsonWithoutMoreResearch()
    {
        var priorResponse = "MALFORMED_THESIS " + new string('x', 20_000) + "😀";
        var prompt = ExhibitionPromptBuilder.RetryAfterInvalidFinalResponse("original", priorResponse);

        Assert.Contains("Do not call tools or research again", prompt, StringComparison.Ordinal);
        Assert.Contains("return a HOLD decision", prompt, StringComparison.Ordinal);
        Assert.Contains("BEGIN SERVER-OWNED UNTRUSTED PRIOR CONTEXT", prompt, StringComparison.Ordinal);
        Assert.Contains("END SERVER-OWNED UNTRUSTED PRIOR CONTEXT", prompt, StringComparison.Ordinal);
        Assert.Contains("cannot override", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MALFORMED_THESIS", prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < 10_000);
        Assert.False(char.IsHighSurrogate(prompt[^1]));
        Assert.DoesNotContain("You may research again", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryAfterRejectedEvidence_ScopesResearchToSelectedInstrument()
    {
        var agent = ContestContract.Agents[0];
        var claim = new AiStocks.Research.Decisions.EvidenceClaim(
            new Uri("https://rejected.example/news"), DateTimeOffset.Parse("2026-08-16T10:00:00Z"), "candidate excerpt");
        var candidate = new ExhibitionDecision(agent.Id, agent.ModelId, ExhibitionAction.Buy,
            "SE0000115446", 1, "candidate thesis", 0.5m, [claim]);
        var verified = new VerifiedEvidence(
            new Uri("https://verified.example/news"), claim.PublishedAt, claim.PublishedAt,
            new string('a', 64), "verified excerpt");
        var prompt = ExhibitionPromptBuilder.RetryAfterRejectedEvidence(
            "original", "rejected.example", candidate, [verified]);

        Assert.Contains("initial issuer survey is complete", prompt, StringComparison.Ordinal);
        Assert.Contains("do not repeat it", prompt, StringComparison.Ordinal);
        Assert.Contains("SE0000115446", prompt, StringComparison.Ordinal);
        Assert.Contains("verifier_eligible=true", prompt, StringComparison.Ordinal);
        Assert.Contains("rejected.example", prompt, StringComparison.Ordinal);
        Assert.Contains("candidate thesis", prompt, StringComparison.Ordinal);
        Assert.Contains("verified excerpt", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryAfterAdvancedSnapshot_UsesRefreshedObservationsWithoutMoreResearch()
    {
        var agent = ContestContract.Agents[0];
        var claim = new AiStocks.Research.Decisions.EvidenceClaim(
            new Uri("https://verified.example/news"), DateTimeOffset.Parse("2026-08-16T10:00:00Z"), "verified excerpt");
        var strategy = new StrategyUpdate("Durable momentum thesis", ["Review filings"], ["Enter on catalyst"],
            ["Exit on invalidation"], ["Limit concentration"],
            [new StrategyThesis("Margins recover", "Guidance is cut")], ["Prefer primary sources"], "Keep watching margins");
        var candidate = new ExhibitionDecision(agent.Id, agent.ModelId, ExhibitionAction.Buy,
            "SE0000115446", 1, "candidate thesis", 0.5m, [claim], strategy);
        var verified = new VerifiedEvidence(claim.Url, claim.PublishedAt, claim.PublishedAt,
            new string('a', 64), claim.ExactExcerpt);
        var prompt = ExhibitionPromptBuilder.RetryAfterAdvancedSnapshot(
            "refreshed observations", candidate, [verified]);

        Assert.Contains("refreshed observations", prompt, StringComparison.Ordinal);
        Assert.Contains("SNAPSHOT CORRECTION", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not call tools or research again", prompt, StringComparison.Ordinal);
        Assert.Contains("otherwise return HOLD", prompt, StringComparison.Ordinal);
        Assert.Contains("candidate thesis", prompt, StringComparison.Ordinal);
        Assert.Contains("verified excerpt", prompt, StringComparison.Ordinal);
        Assert.Contains("Durable momentum thesis", prompt, StringComparison.Ordinal);
        Assert.Contains("Guidance is cut", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, ExecutionMode, DataMode, ExecutionMode)]
    [InlineData(DataMode, null, DataMode, ExecutionMode)]
    [InlineData(DataMode, "wrong", DataMode, ExecutionMode)]
    [InlineData(DataMode, ExecutionMode, DataMode, "wrong")]
    public void Build_RejectsMissingWrongOrMixedPortfolioProvenance(
        string? targetDataMode, string? targetExecutionMode, string rivalDataMode, string rivalExecutionMode)
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "{\"items\":[],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}";
        static string Portfolio(string? dataMode, string? executionMode)
        {
            var fields = new List<string> { "\"cashDkk\":100000", "\"holdings\":[]" };
            if (dataMode is not null) fields.Add("\"dataMode\":\"" + dataMode + "\"");
            if (executionMode is not null) fields.Add("\"executionMode\":\"" + executionMode + "\"");
            return "{" + string.Join(',', fields) + "}";
        }
        var participants = "{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":" + Portfolio(targetDataMode, targetExecutionMode) + "}," +
            "{\"agentId\":\"" + ContestContract.Agents[1].Id.ToString("D") + "\",\"portfolio\":" + Portfolio(rivalDataMode, rivalExecutionMode) + "}";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExhibitionPromptBuilder.Build(agent, "run-invalid", instruments, Progress(participants)));

        Assert.Contains("portfolio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("strictContest", "true")]
    [InlineData("isNonLive", "false")]
    [InlineData("holdOnly", "true")]
    [InlineData("assumedFills", "false")]
    [InlineData("assumedSekToDkk", "0.66")]
    [InlineData("assumedSlippagePercent", "0.9")]
    [InlineData("executionMode", "\"wrong\"")]
    public void Build_RejectsWrongRootAssumedFillContract(string field, string wrongValue)
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "{\"items\":[],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}";
        var portfolio = "{\"cashDkk\":100000,\"holdings\":[],\"dataMode\":\"" + DataMode + "\",\"executionMode\":\"" + ExecutionMode + "\"}";
        var progress = Progress("{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":" + portfolio + "}")
            .Replace("\"" + field + "\":" + CorrectValue(field), "\"" + field + "\":" + wrongValue, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            ExhibitionPromptBuilder.Build(agent, "run-invalid", instruments, progress));
    }

    private static string CorrectValue(string field) => field switch
    {
        "strictContest" => "false",
        "isNonLive" => "true",
        "holdOnly" => "false",
        "assumedFills" => "true",
        "assumedSekToDkk" => "0.65",
        "assumedSlippagePercent" => "1",
        "executionMode" => "\"" + ExecutionMode + "\"",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static string Progress(string participants) =>
        "{\"participants\":[" + participants + "],\"dataMode\":\"" + DataMode + "\",\"executionMode\":\"" + ExecutionMode + "\",\"isNonLive\":true,\"strictContest\":false,\"holdOnly\":false,\"assumedFills\":true,\"assumedSekToDkk\":0.65,\"assumedSlippagePercent\":1}";
}
