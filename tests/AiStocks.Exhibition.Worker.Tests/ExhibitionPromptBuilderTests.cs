using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionPromptBuilderTests
{
    [Fact]
    public void Build_IncludesCompleteSchemaAndOnlyTheRequestedAgentsPortfolio()
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "{\"items\":[{\"id\":\"SE0000115446\",\"name\":\"Example AB\",\"exchange\":\"XSTO\",\"currency\":\"SEK\",\"isPreviewPrice\":false,\"executedAt\":\"2026-08-16T10:00:00Z\",\"availableAt\":\"2026-08-16T10:15:00Z\",\"source\":\"Nasdaq Nordic MiFID II delayed post-trade\",\"delayMinutes\":15,\"tradable\":false}],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}";
        var progress = "{\"participants\":[" +
            "{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":{\"cash\":30000,\"positions\":[],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}}," +
            "{\"agentId\":\"" + ContestContract.Agents[1].Id.ToString("D") + "\",\"portfolio\":{\"cash\":99999,\"positions\":[{\"secret\":\"RIVAL_SECRET\"}],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}}],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\",\"isNonLive\":true,\"strictContest\":false,\"holdOnly\":true}";

        var prompt = ExhibitionPromptBuilder.Build(agent, "run-123", instruments, progress);

        Assert.Contains("official Nasdaq XSTO", prompt, StringComparison.Ordinal);
        Assert.Contains("at least 15-minute delayed", prompt, StringComparison.Ordinal);
        Assert.Contains("non-live", prompt, StringComparison.Ordinal);
        Assert.Contains("paper-only", prompt, StringComparison.Ordinal);
        Assert.Contains("HOLD-only", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("buy|sell", prompt, StringComparison.Ordinal);
        Assert.Contains("may use an empty evidence array", prompt, StringComparison.Ordinal);
        Assert.Contains("\"evidence\":[]", prompt, StringComparison.Ordinal);
        Assert.Contains("30000", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("RIVAL_SECRET", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "official-nasdaq-xsto-15m-delayed")]
    [InlineData("preview-fixtures", "official-nasdaq-xsto-15m-delayed")]
    [InlineData("official-nasdaq-xsto-15m-delayed", "preview-fixtures")]
    public void Build_RejectsMissingWrongOrMixedPortfolioDataModes(string? targetMode, string rivalMode)
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "{\"items\":[],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}";
        static string Portfolio(string? mode) => mode is null
            ? "{\"cashDkk\":100000,\"holdings\":[]}"
            : "{\"cashDkk\":100000,\"holdings\":[],\"dataMode\":\"" + mode + "\"}";
        var progress = "{\"participants\":[" +
            "{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":" + Portfolio(targetMode) + "}," +
            "{\"agentId\":\"" + ContestContract.Agents[1].Id.ToString("D") + "\",\"portfolio\":" + Portfolio(rivalMode) + "}]," +
            "\"dataMode\":\"official-nasdaq-xsto-15m-delayed\",\"isNonLive\":true,\"strictContest\":false,\"holdOnly\":true}";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ExhibitionPromptBuilder.Build(agent, "run-invalid", instruments, progress));

        Assert.Contains("Every AI portfolio", exception.Message, StringComparison.Ordinal);
    }
}
