using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionPromptBuilderTests
{
    [Fact]
    public void Build_IncludesCompleteSchemaAndOnlyTheRequestedAgentsPortfolio()
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "[{\"instrumentId\":\"SE0000115446\",\"name\":\"Fixture AB\",\"dataMode\":\"fixture\"}]";
        var progress = "{\"agents\":[" +
            "{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":{\"cash\":30000,\"positions\":[]}}," +
            "{\"agentId\":\"" + ContestContract.Agents[1].Id.ToString("D") + "\",\"portfolio\":{\"cash\":99999,\"positions\":[{\"secret\":\"RIVAL_SECRET\"}]}}]}";

        var prompt = ExhibitionPromptBuilder.Build(agent, "run-123", instruments, progress);

        Assert.Contains("NON-LIVE FIXTURE PAPER-TRADING EXHIBITION", prompt, StringComparison.Ordinal);
        Assert.Contains("https://example.com/article", prompt, StringComparison.Ordinal);
        Assert.Contains("exactExcerpt", prompt, StringComparison.Ordinal);
        Assert.Contains("30000", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("RIVAL_SECRET", prompt, StringComparison.Ordinal);
    }
}
