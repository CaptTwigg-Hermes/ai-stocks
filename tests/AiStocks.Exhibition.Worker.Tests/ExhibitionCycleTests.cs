using System.Collections.Immutable;
using System.Text.Json;
using AiStocks.Core;
using AiStocks.Exhibition.Worker;
using AiStocks.Research.Execution;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionCycleTests
{
    [Fact]
    public async Task RunAsync_RecordsOneFailureButRunsAndPostsAttestedResultsForOtherAgents()
    {
        var api = new FakeApi();
        var invoker = new FakeInvoker();
        var verifier = new FakeVerifier();
        var health = new ExhibitionHealthState();
        var cycle = new ExhibitionCycle(api, invoker, verifier, health, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);
        var scheduledAt = DateTimeOffset.Parse("2026-08-16T12:00:00Z");

        var result = await cycle.RunAsync(scheduledAt, CancellationToken.None);

        Assert.Equal(4, invoker.Agents.Count);
        Assert.Equal(3, api.Posts.Count);
        Assert.Equal(1, verifier.Calls);
        Assert.Single(result.Failures);
        Assert.Equal("degraded", health.Snapshot().Status);
        var posted = JsonDocument.Parse(api.Posts[0]);
        Assert.Equal("copilot", posted.RootElement.GetProperty("actualProvider").GetString());
        Assert.Equal(new string('a', 64), posted.RootElement.GetProperty("runtimeReportSha256").GetString());
        Assert.StartsWith("20260816T120000Z-", posted.RootElement.GetProperty("runId").GetString(), StringComparison.Ordinal);
    }

    private sealed class FakeApi : IExhibitionApi
    {
        public List<string> Posts { get; } = [];
        public Task<string> GetInstrumentsAsync(CancellationToken cancellationToken) => Task.FromResult("[{\"instrumentId\":\"SE0000115446\",\"dataMode\":\"fixture\"}]");
        public Task<string> GetProgressAsync(CancellationToken cancellationToken) => Task.FromResult("{\"agents\":[" + string.Join(',', ContestContract.Agents.Select(a => "{\"agentId\":\"" + a.Id.ToString("D") + "\",\"portfolio\":{\"cash\":30000,\"positions\":[]}}")) + "]}");
        public Task PostDecisionAsync(string runId, string json, CancellationToken cancellationToken) { Posts.Add(json); return Task.CompletedTask; }
    }

    private sealed class FakeInvoker : IExhibitionModelInvoker
    {
        public List<Guid> Agents { get; } = [];
        public Task<ResearchExecutionResult> InvokeAsync(AgentDefinition agent, string prompt, CancellationToken cancellationToken)
        {
            Agents.Add(agent.Id);
            if (agent == ContestContract.Agents[0]) throw new InvalidOperationException("outage");
            var output = agent == ContestContract.Agents[1]
                ? $$"""{"agentId":"{{agent.Id:D}}","modelId":"{{agent.ModelId}}","action":"buy","instrumentId":"SE0000115446","quantity":1,"reason":"Verified fixture opportunity","confidence":0.5,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-16T10:00:00Z","exactExcerpt":"Exact public text"}]}"""
                : $$"""{"agentId":"{{agent.Id:D}}","modelId":"{{agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"No verified opportunity","confidence":0.5,"evidence":[]}""";
            return Task.FromResult(new ResearchExecutionResult(output, string.Empty, new InvocationProvenance
            {
                AgentId = agent.Id, RequestedModelId = agent.ModelId, RequestedProvider = "copilot",
                ModelId = agent.ModelId, Provider = "copilot", RuntimeReport = ImmutableArray<byte>.Empty,
                RuntimeReportSha256 = new string('a', 64), Executable = "/hermes", Arguments = [],
                EnvironmentVariableNames = ["HERMES_HOME"], PromptSha256 = new string('b', 64),
                StartedAt = DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CompletedAt = DateTimeOffset.Parse("2026-08-16T12:00:01Z"),
                ExitCode = 0, StandardOutputSha256 = new string('c', 64), StandardErrorSha256 = new string('d', 64)
            }));
        }
    }

    private sealed class FakeVerifier : AiStocks.Research.Evidence.IEvidenceVerifier
    {
        public int Calls { get; private set; }
        public Task<VerifiedEvidence> VerifyAsync(AiStocks.Research.Decisions.EvidenceClaim claim, CancellationToken cancellationToken) =>
            Task.FromResult(Verified(claim));

        private VerifiedEvidence Verified(AiStocks.Research.Decisions.EvidenceClaim claim)
        {
            Calls++;
            return new VerifiedEvidence(claim.Url, claim.PublishedAt, DateTimeOffset.UtcNow, new string('e', 64), claim.ExactExcerpt);
        }
    }
}
