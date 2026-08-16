using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionPromptBuilderTests
{
    [Fact]
    public void Build_IncludesCompleteSchemaAndOnlyTheRequestedAgentsPortfolio()
    {
        var agent = ContestContract.Agents[0];
        const string instruments = "{\"items\":[{\"id\":\"SE0000115446\",\"name\":\"Fixture AB\",\"dataMode\":\"preview-fixtures\"}],\"dataMode\":\"preview-fixtures\"}";
        var progress = "{\"participants\":[" +
            "{\"agentId\":\"" + agent.Id.ToString("D") + "\",\"portfolio\":{\"cash\":30000,\"positions\":[]}}," +
            "{\"agentId\":\"" + ContestContract.Agents[1].Id.ToString("D") + "\",\"portfolio\":{\"cash\":99999,\"positions\":[{\"secret\":\"RIVAL_SECRET\"}]}}],\"isNonLive\":true,\"strictContest\":false}";

        var prompt = ExhibitionPromptBuilder.Build(agent, "run-123", instruments, progress);

        Assert.Contains("NON-LIVE FIXTURE PAPER-TRADING EXHIBITION", prompt, StringComparison.Ordinal);
        Assert.Contains("may use an empty evidence array", prompt, StringComparison.Ordinal);
        Assert.Contains("\"evidence\":[]", prompt, StringComparison.Ordinal);
        Assert.Contains("30000", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("RIVAL_SECRET", prompt, StringComparison.Ordinal);
    }
}
