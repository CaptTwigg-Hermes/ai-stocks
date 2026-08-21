using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class StrategyMemoryStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aistocks-strategy-memory-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_LoadsOnlyMatchingAgentAndIdempotentlyBoundsJournal()
    {
        var agent = ContestContract.Agents[0];
        var store = new StrategyMemoryStore(root);
        for (var index = 0; index < 25; index++)
        {
            var update = Update("note-" + index);
            store.Save(agent, "accepted-run-" + index, update);
            store.Save(agent, "accepted-run-" + index, update);
        }

        var memory = Assert.IsType<AgentStrategyMemory>(store.Load(agent));

        Assert.Equal(agent.Id, memory.AgentId);
        Assert.Equal("Evidence-led quality at a reasonable price", memory.Philosophy);
        Assert.Equal(20, memory.Journal.Count);
        Assert.Equal("accepted-run-5", memory.Journal[0].RunId);
        Assert.Equal("accepted-run-24", memory.Journal[^1].RunId);
        Assert.Null(store.Load(ContestContract.Agents[1]));
        Assert.Single(Directory.GetFiles(root));
    }

    private static StrategyUpdate Update(string note) => new(
        "Evidence-led quality at a reasonable price",
        ["Check issuer filings"],
        ["Buy only with a dated catalyst"],
        ["Exit when thesis breaks"],
        ["Keep position sizing conservative"],
        [new StrategyThesis("Margins recover", "Guidance is cut")],
        ["Prefer primary sources"],
        note);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
