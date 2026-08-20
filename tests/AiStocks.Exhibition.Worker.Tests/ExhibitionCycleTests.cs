using System.Collections.Immutable;
using System.Text.Json;
using AiStocks.Core;
using AiStocks.Exhibition.Worker;
using AiStocks.Research.Execution;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionCycleTests
{
    [Fact]
    public async Task RunAsync_RetriesOneInvalidFinalResponseWithoutWeakeningParser()
    {
        var affected = ContestContract.Agents[1];
        var api = new FakeApi();
        var invoker = new FakeInvoker { FailingAgentId = null, MalformedFirstAgentId = affected.Id };
        var cycle = new ExhibitionCycle(api, invoker, new FakeVerifier(), new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(4, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(2, invoker.InvocationCounts[affected.Id]);
        Assert.All(ContestContract.Agents.Where(agent => agent.Id != affected.Id),
            agent => Assert.Equal(1, invoker.InvocationCounts[agent.Id]));
        Assert.Contains("FINAL response must start with {", invoker.Prompts[affected.Id][1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_QueueStatusFailureForOneAgent_DoesNotPreventOtherAgents()
    {
        var failedAgent = ContestContract.Agents[0];
        var api = new FakeApi { FailQueuedAgentId = failedAgent.Id };
        var invoker = new FakeInvoker { FailingAgentId = null };
        var cycle = new ExhibitionCycle(api, invoker, new FakeVerifier(), new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, invoker.Agents.Count);
        Assert.DoesNotContain(failedAgent.Id, invoker.Agents);
        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(failedAgent.Id, result.Failures[0].AgentId);
    }

    [Fact]
    public async Task RunAsync_LostDecisionResponse_ReconcilesAuthoritativeSuccess()
    {
        var reconciledAgent = ContestContract.Agents[0];
        var api = new FakeApi { LoseDecisionResponseAgentId = reconciledAgent.Id };
        var invoker = new FakeInvoker { FailingAgentId = null };
        var cycle = new ExhibitionCycle(api, invoker, new FakeVerifier(), new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(4, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(api.Statuses, status =>
            status.Contains($"\"agentId\":\"{reconciledAgent.Id:D}\"", StringComparison.Ordinal) &&
            status.Contains("\"status\":\"failed\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ReconciliationRejectsSuccessWithoutLifecycleTimestamps()
    {
        var affectedAgent = ContestContract.Agents[0];
        var api = new FakeApi
        {
            LoseDecisionResponseAgentId = affectedAgent.Id,
            OmitReconciliationTimestamps = true
        };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affectedAgent.Id, result.Failures[0].AgentId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunAsync_ReconciliationRejectsMissingQueueTimeOrDuplicateIdentity(
        bool omitQueuedAt, bool duplicateParticipant)
    {
        var affectedAgent = ContestContract.Agents[0];
        var api = new FakeApi
        {
            LoseDecisionResponseAgentId = affectedAgent.Id,
            OmitQueuedAt = omitQueuedAt,
            DuplicateReconciliationParticipant = duplicateParticipant
        };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affectedAgent.Id, result.Failures[0].AgentId);
    }

    [Fact]
    public async Task RunAsync_ReconciliationRejectsAmbiguousDuplicateTargetIdentity()
    {
        var affectedAgent = ContestContract.Agents[0];
        var api = new FakeApi
        {
            LoseDecisionResponseAgentId = affectedAgent.Id,
            AmbiguousDuplicateTargetIdentity = true
        };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affectedAgent.Id, result.Failures[0].AgentId);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RunAsync_ReconciliationRejectsMissingFixtureOrMixedPortfolioProvenance(
        bool omitPortfolio, bool fixturePortfolio, bool mixedPortfolio)
    {
        var affectedAgent = ContestContract.Agents[0];
        var api = new FakeApi
        {
            LoseDecisionResponseAgentId = affectedAgent.Id,
            OmitReconciliationPortfolio = omitPortfolio,
            FixtureReconciliationPortfolio = fixturePortfolio,
            MixedReconciliationPortfolio = mixedPortfolio
        };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affectedAgent.Id, result.Failures[0].AgentId);
    }

    [Theory]
    [InlineData("XNAS", "SEK", false, false)]
    [InlineData("XSTO", "DKK", false, false)]
    [InlineData("XSTO", "SEK", true, false)]
    [InlineData("XSTO", "SEK", false, true)]
    public async Task RunAsync_RejectsWrongVenueCurrencyPreviewOrDkkInstrumentMetadata(
        string exchange, string currency, bool isPreviewPrice, bool includePriceDkk)
    {
        var api = new FakeApi
        {
            InstrumentExchange = exchange,
            InstrumentCurrency = currency,
            InstrumentIsPreviewPrice = isPreviewPrice,
            IncludeInstrumentPriceDkk = includePriceDkk
        };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(100, false)]
    public async Task RunAsync_RejectsNonPositivePriceOrMissingPaperTradable(decimal price, bool paperTradable)
    {
        var api = new FakeApi { InstrumentPrice = price, InstrumentPaperTradable = paperTradable };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None));
    }

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
        Assert.Equal(9, api.Statuses.Count);
        Assert.Equal(4, api.Statuses.Count(status => status.Contains("\"status\":\"queued\"", StringComparison.Ordinal)));
        Assert.Contains(api.Statuses, status => status.Contains("\"status\":\"failed\"", StringComparison.Ordinal));
        Assert.Equal(3, verifier.Calls);
        Assert.Single(result.Failures);
        Assert.Equal("degraded", health.Snapshot().Status);
        var posted = JsonDocument.Parse(api.Posts[0]);
        Assert.Equal("copilot", posted.RootElement.GetProperty("runtimeProvider").GetString());
        Assert.Equal(new string('a', 64), posted.RootElement.GetProperty("reportSha256").GetString());
        Assert.Equal("hold", posted.RootElement.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, posted.RootElement.GetProperty("instrumentId").ValueKind);
        Assert.StartsWith("20260816T120000Z-", posted.RootElement.GetProperty("runId").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNotSplitSurrogatePairWhenBoundingFailureStatus()
    {
        var api = new FakeApi();
        var invoker = new FakeInvoker { FailureMessage = new string('x', 999) + "😀tail" };
        var cycle = new ExhibitionCycle(api, invoker, new FakeVerifier(), new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        var failed = api.Statuses.Select(status => JsonDocument.Parse(status))
            .Single(status => status.RootElement.GetProperty("status").GetString() == "failed");
        var error = failed.RootElement.GetProperty("error").GetString()!;
        Assert.Equal(999, error.Length);
        Assert.False(char.IsSurrogate(error[^1]));
    }

    [Fact]
    public async Task RunAsync_PostsValidBuyAfterIndependentParserAndEvidenceValidation()
    {
        var api = new FakeApi();
        var invoker = new FakeInvoker { FailingAgentId = null, Action = "buy" };
        var verifier = new FakeVerifier();
        var cycle = new ExhibitionCycle(api, invoker, verifier, new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(4, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(4, verifier.Calls);
        Assert.All(api.Posts, json =>
        {
            using var posted = JsonDocument.Parse(json);
            Assert.Equal("buy", posted.RootElement.GetProperty("action").GetString());
            Assert.Equal("SE0000115446", posted.RootElement.GetProperty("instrumentId").GetString());
            Assert.Equal(1, posted.RootElement.GetProperty("quantity").GetInt32());
            Assert.Equal(100m, posted.RootElement.GetProperty("observedPriceSek").GetDecimal());
            Assert.Equal(DateTimeOffset.Parse("2026-08-16T10:15:00Z"),
                posted.RootElement.GetProperty("observationAvailableAt").GetDateTimeOffset());
        });
    }

    [Fact]
    public async Task RunAsync_BindsTradeToRefreshedDelayedObservationBeforeSubmission()
    {
        var api = new FakeApi
        {
            RefreshedInstrumentPrice = 101m,
            RefreshedInstrumentAvailableAt = DateTimeOffset.Parse("2026-08-16T10:30:00Z")
        };
        var invoker = new FakeInvoker { FailingAgentId = null, Action = "buy" };
        var cycle = new ExhibitionCycle(api, invoker,
            new FakeVerifier(), new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.All(ContestContract.Agents,
            agent => Assert.Equal(2, invoker.InvocationCounts[agent.Id]));
        Assert.All(ContestContract.Agents,
            agent => Assert.Contains("SNAPSHOT CORRECTION", invoker.Prompts[agent.Id][1], StringComparison.Ordinal));
        Assert.All(ContestContract.Agents, agent =>
        {
            Assert.Contains("\"price\":101", invoker.Prompts[agent.Id][1], StringComparison.Ordinal);
            Assert.Contains("2026-08-16T10:30:00.0000000+00:00", invoker.Prompts[agent.Id][1], StringComparison.Ordinal);
        });
        Assert.All(api.Posts, json =>
        {
            using var posted = JsonDocument.Parse(json);
            Assert.Equal(101m, posted.RootElement.GetProperty("observedPriceSek").GetDecimal());
            Assert.Equal(DateTimeOffset.Parse("2026-08-16T10:30:00Z"),
                posted.RootElement.GetProperty("observationAvailableAt").GetDateTimeOffset());
        });
    }

    [Fact]
    public async Task RunAsync_FailsClosedWhenSnapshotAdvancesAfterCorrectionWasConsumed()
    {
        var affected = ContestContract.Agents[0];
        var api = AdvancedSnapshotApi();
        var invoker = new FakeInvoker { FailingAgentId = null, Action = "buy", MalformedFirstAgentId = affected.Id };
        var cycle = CreateCycle(api, invoker, new FakeVerifier());

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affected.Id, result.Failures[0].AgentId);
        Assert.Contains("single corrective invocation", result.Failures[0].Error, StringComparison.Ordinal);
        Assert.Equal(2, invoker.InvocationCounts[affected.Id]);
        Assert.Equal(3, api.Posts.Count);
    }

    [Fact]
    public async Task RunAsync_FailsClosedWhenSelectedInstrumentDisappearsFromRefreshedSnapshot()
    {
        var api = new FakeApi { OmitInstrumentAfterFirstRead = true };
        var invoker = new FakeInvoker { FailingAgentId = null, Action = "buy" };
        var cycle = CreateCycle(api, invoker, new FakeVerifier());

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(4, result.Failures.Count);
        Assert.All(result.Failures, failure =>
            Assert.Contains("absent from the refreshed delayed snapshot", failure.Error, StringComparison.Ordinal));
        Assert.Empty(api.Posts);
        Assert.All(ContestContract.Agents, agent => Assert.Equal(1, invoker.InvocationCounts[agent.Id]));
    }

    [Fact]
    public async Task RunAsync_FailsClosedOnMalformedSnapshotCorrection()
    {
        var affected = ContestContract.Agents[0];
        var api = AdvancedSnapshotApi();
        var invoker = new FakeInvoker { FailingAgentId = null, Action = "buy", MalformedSecondAgentId = affected.Id };
        var cycle = CreateCycle(api, invoker, new FakeVerifier());

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affected.Id, result.Failures[0].AgentId);
        Assert.Contains("does not contain a JSON object", result.Failures[0].Error, StringComparison.Ordinal);
        Assert.Equal(2, invoker.InvocationCounts[affected.Id]);
        Assert.Equal(3, api.Posts.Count);
    }

    [Fact]
    public async Task RunAsync_FailsClosedOnEvidenceInvalidSnapshotCorrection()
    {
        var affected = ContestContract.Agents[0];
        var api = AdvancedSnapshotApi();
        var invoker = new FakeInvoker
        {
            FailingAgentId = null,
            Action = "buy",
            AlternateEvidenceOnSecondInvocation = true
        };
        var verifier = new FakeVerifier { RejectUrl = new Uri("https://alternate.example/news") };
        var cycle = CreateCycle(api, invoker, verifier);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affected.Id, result.Failures[0].AgentId);
        Assert.Contains("not independently verifiable", result.Failures[0].Error, StringComparison.Ordinal);
        Assert.Equal(2, invoker.InvocationCounts[affected.Id]);
        Assert.Equal(3, api.Posts.Count);
    }

    [Fact]
    public async Task RunAsync_HoldDoesNotRefreshOrInvokeSnapshotCorrection()
    {
        var api = AdvancedSnapshotApi();
        var invoker = new FakeInvoker { FailingAgentId = null, Action = "hold" };
        var cycle = CreateCycle(api, invoker, new FakeVerifier());

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(4, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(1, api.InstrumentReads);
        Assert.All(ContestContract.Agents, agent => Assert.Equal(1, invoker.InvocationCounts[agent.Id]));
        Assert.All(api.Posts, json =>
        {
            using var posted = JsonDocument.Parse(json);
            Assert.Equal("hold", posted.RootElement.GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, posted.RootElement.GetProperty("instrumentId").ValueKind);
            Assert.Equal(JsonValueKind.Null, posted.RootElement.GetProperty("observedPriceSek").ValueKind);
        });
    }

    private static FakeApi AdvancedSnapshotApi() => new()
    {
        RefreshedInstrumentPrice = 101m,
        RefreshedInstrumentAvailableAt = DateTimeOffset.Parse("2026-08-16T10:30:00Z")
    };

    private static ExhibitionCycle CreateCycle(FakeApi api, FakeInvoker invoker, FakeVerifier verifier) =>
        new(api, invoker, verifier, new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

    [Fact]
    public async Task RunAsync_RetriesRejectedEvidenceOnceWithADifferentSourceHost()
    {
        var affected = ContestContract.Agents[0];
        var api = new FakeApi();
        var invoker = new FakeInvoker
        {
            FailingAgentId = null,
            Action = "buy",
            AlternateEvidenceOnSecondInvocation = true,
            RejectedEvidenceSecondOnFirstInvocation = true
        };
        var verifier = new FakeVerifier { RejectUrl = new Uri("https://rejected.example/news") };
        var cycle = new ExhibitionCycle(api, invoker, verifier, new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(4, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(2, invoker.InvocationCounts[affected.Id]);
        Assert.All(ContestContract.Agents.Where(agent => agent.Id != affected.Id),
            agent => Assert.Equal(1, invoker.InvocationCounts[agent.Id]));
        Assert.Contains("different source host", invoker.Prompts[affected.Id][1], StringComparison.Ordinal);
        Assert.Contains("rejected.example", invoker.Prompts[affected.Id][1], StringComparison.Ordinal);
        using var posted = JsonDocument.Parse(api.Posts[0]);
        Assert.Equal("https://alternate.example/news",
            posted.RootElement.GetProperty("evidence")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task RunAsync_RejectsEvidenceCorrectionThatReusesTheRejectedHost()
    {
        var affected = ContestContract.Agents[0];
        var api = new FakeApi();
        var invoker = new FakeInvoker
        {
            FailingAgentId = null,
            Action = "buy",
            RejectedEvidenceSecondOnFirstInvocation = true,
            ReuseRejectedEvidenceOnSecondInvocation = true
        };
        var verifier = new FakeVerifier { RejectUrl = new Uri("https://rejected.example/news") };
        var cycle = new ExhibitionCycle(api, invoker, verifier, new ExhibitionHealthState(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(3, result.Succeeded);
        Assert.Single(result.Failures);
        Assert.Equal(affected.Id, result.Failures[0].AgentId);
        Assert.Contains("reused the rejected source host", result.Failures[0].Error, StringComparison.Ordinal);
        Assert.Equal(2, invoker.InvocationCounts[affected.Id]);
        Assert.Equal(3, api.Posts.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunAsync_FailsClosedOnMissingRootOrMixedPortfolioExecutionMode(
        bool omitRootExecutionMode, bool wrongPortfolioExecutionMode)
    {
        var api = new FakeApi
        {
            OmitRootExecutionMode = omitRootExecutionMode,
            WrongPortfolioExecutionMode = wrongPortfolioExecutionMode
        };
        var cycle = new ExhibitionCycle(api, new FakeInvoker { FailingAgentId = null }, new FakeVerifier(),
            new ExhibitionHealthState(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ExhibitionCycle>.Instance);

        var result = await cycle.RunAsync(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), CancellationToken.None);

        Assert.Equal(0, result.Succeeded);
        Assert.Equal(4, result.Failures.Count);
    }

    private sealed class FakeApi : IExhibitionApi
    {
        public Guid? FailQueuedAgentId { get; init; }
        public Guid? LoseDecisionResponseAgentId { get; init; }
        public bool OmitReconciliationTimestamps { get; init; }
        public bool OmitQueuedAt { get; init; }
        public bool DuplicateReconciliationParticipant { get; init; }
        public bool AmbiguousDuplicateTargetIdentity { get; init; }
        public bool OmitReconciliationPortfolio { get; init; }
        public bool FixtureReconciliationPortfolio { get; init; }
        public bool MixedReconciliationPortfolio { get; init; }
        public string InstrumentExchange { get; init; } = "XSTO";
        public string InstrumentCurrency { get; init; } = "SEK";
        public bool InstrumentIsPreviewPrice { get; init; }
        public bool IncludeInstrumentPriceDkk { get; init; }
        public decimal InstrumentPrice { get; init; } = 100m;
        public decimal? RefreshedInstrumentPrice { get; init; }
        public DateTimeOffset? RefreshedInstrumentAvailableAt { get; init; }
        public bool OmitInstrumentAfterFirstRead { get; init; }
        public bool InstrumentPaperTradable { get; init; } = true;
        public bool OmitRootExecutionMode { get; init; }
        public bool WrongPortfolioExecutionMode { get; init; }
        private string? committedDecision;
        public int InstrumentReads { get; private set; }
        public List<string> Posts { get; } = [];
        public List<string> Statuses { get; } = [];
        public Task<string> GetInstrumentsAsync(CancellationToken cancellationToken)
        {
            InstrumentReads++;
            var instrumentId = InstrumentReads > 1 && OmitInstrumentAfterFirstRead
                ? "SE9999999999" : "SE0000115446";
            var price = InstrumentReads > 1 && RefreshedInstrumentPrice is not null
                ? RefreshedInstrumentPrice.Value : InstrumentPrice;
            var availableAt = InstrumentReads > 1 && RefreshedInstrumentAvailableAt is not null
                ? RefreshedInstrumentAvailableAt.Value : DateTimeOffset.Parse("2026-08-16T10:15:00Z");
            var priceDkk = IncludeInstrumentPriceDkk ? ",\"priceDkk\":123.45" : string.Empty;
            return Task.FromResult("{\"items\":[{\"id\":\"" + instrumentId + "\",\"exchange\":\"" + InstrumentExchange +
                "\",\"currency\":\"" + InstrumentCurrency + "\",\"isPreviewPrice\":" +
                InstrumentIsPreviewPrice.ToString().ToLowerInvariant() + priceDkk +
                ",\"price\":" + price.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"executedAt\":\"2026-08-16T10:00:00Z\",\"availableAt\":\"" + availableAt.ToString("O") + "\",\"source\":\"Nasdaq Nordic MiFID II delayed post-trade\",\"delayMinutes\":15,\"tradable\":false,\"paperTradable\":" +
                InstrumentPaperTradable.ToString().ToLowerInvariant() + "}],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"}");
        }
        public Task<string> GetProgressAsync(CancellationToken cancellationToken)
        {
            if (committedDecision is not null)
            {
                using var decision = JsonDocument.Parse(committedDecision);
                var root = decision.RootElement;
                var participant = new Dictionary<string, object?>
                {
                    ["agentId"] = root.GetProperty("agentId").GetGuid(),
                    ["modelId"] = root.GetProperty("modelId").GetString(),
                    ["runId"] = root.GetProperty("runId").GetString(),
                    ["status"] = "succeeded",
                    ["queuedAt"] = OmitQueuedAt ? null : DateTimeOffset.Parse("2026-08-16T11:59:59Z"),
                    ["startedAt"] = OmitReconciliationTimestamps ? null : DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                    ["completedAt"] = OmitReconciliationTimestamps ? null : DateTimeOffset.Parse("2026-08-16T12:00:01Z"),
                    ["latestDecision"] = new
                    {
                        runId = root.GetProperty("runId").GetString(),
                        completedAt = OmitReconciliationTimestamps ? (DateTimeOffset?)null : DateTimeOffset.Parse("2026-08-16T12:00:01Z"),
                        action = root.GetProperty("action").GetString(),
                        instrumentId = root.GetProperty("instrumentId").ValueKind == JsonValueKind.Null
                            ? null : root.GetProperty("instrumentId").GetString(),
                        quantity = root.GetProperty("quantity").GetInt32(),
                        evidence = root.GetProperty("evidence").EnumerateArray().Select(item => new
                        {
                            url = item.GetProperty("url").GetString()
                        }).ToArray()
                    }
                };
                if (!OmitReconciliationPortfolio)
                    participant["portfolio"] = new
                    {
                        dataMode = FixtureReconciliationPortfolio
                            ? "preview-fixtures"
                            : "official-nasdaq-xsto-15m-delayed",
                        executionMode = "assumed-delayed-paper-fills-v1"
                    };
                var snapshotParticipants = DuplicateReconciliationParticipant
                    ? new List<Dictionary<string, object?>> { participant, participant }
                    : AmbiguousDuplicateTargetIdentity
                        ? new List<Dictionary<string, object?>>
                        {
                            participant,
                            new(participant) { ["modelId"] = "wrong-model" }
                        }
                        : new List<Dictionary<string, object?>> { participant };
                if (MixedReconciliationPortfolio)
                    snapshotParticipants.Add(new Dictionary<string, object?>
                    {
                        ["agentId"] = ContestContract.Agents[1].Id,
                        ["portfolio"] = new { dataMode = "preview-fixtures", executionMode = "assumed-delayed-paper-fills-v1" }
                    });
                var snapshot = new Dictionary<string, object?>
                {
                    ["participants"] = snapshotParticipants,
                    ["dataMode"] = "official-nasdaq-xsto-15m-delayed",
                    ["executionMode"] = "assumed-delayed-paper-fills-v1",
                    ["isNonLive"] = true,
                    ["strictContest"] = false,
                    ["holdOnly"] = false,
                    ["assumedFills"] = true,
                    ["assumedSekToDkk"] = 0.65m,
                    ["assumedSlippagePercent"] = 1m
                };
                return Task.FromResult(JsonSerializer.Serialize(snapshot));
            }
            var executionMode = WrongPortfolioExecutionMode ? "wrong" : "assumed-delayed-paper-fills-v1";
            var rootExecutionMode = OmitRootExecutionMode ? string.Empty : ",\"executionMode\":\"assumed-delayed-paper-fills-v1\"";
            return Task.FromResult("{\"participants\":[" + string.Join(',', ContestContract.Agents.Select(a => "{\"agentId\":\"" + a.Id.ToString("D") + "\",\"portfolio\":{\"cashDkk\":100000,\"holdings\":[],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\",\"executionMode\":\"" + executionMode + "\"}}")) + "],\"dataMode\":\"official-nasdaq-xsto-15m-delayed\"" + rootExecutionMode + ",\"isNonLive\":true,\"strictContest\":false,\"holdOnly\":false,\"assumedFills\":true,\"assumedSekToDkk\":0.65,\"assumedSlippagePercent\":1}");
        }
        public Task PostDecisionAsync(string runId, string json, CancellationToken cancellationToken)
        {
            Posts.Add(json);
            using var decision = JsonDocument.Parse(json);
            if (decision.RootElement.GetProperty("agentId").GetGuid() == LoseDecisionResponseAgentId)
            {
                committedDecision = json;
                throw new HttpRequestException("response lost after commit");
            }
            return Task.CompletedTask;
        }
        public Task PostStatusAsync(string json, CancellationToken cancellationToken)
        {
            if (FailQueuedAgentId is not null &&
                json.Contains($"\"agentId\":\"{FailQueuedAgentId:D}\"", StringComparison.Ordinal) &&
                json.Contains("\"status\":\"queued\"", StringComparison.Ordinal))
                throw new HttpRequestException("queued status unavailable");
            Statuses.Add(json);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInvoker : IExhibitionModelInvoker
    {
        public Guid? FailingAgentId { get; init; } = ContestContract.Agents[0].Id;
        public string FailureMessage { get; init; } = "outage";
        public Guid? MalformedFirstAgentId { get; init; }
        public Guid? MalformedSecondAgentId { get; init; }
        public string Action { get; init; } = "hold";
        public bool AlternateEvidenceOnSecondInvocation { get; init; }
        public bool RejectedEvidenceSecondOnFirstInvocation { get; init; }
        public bool ReuseRejectedEvidenceOnSecondInvocation { get; init; }
        public List<Guid> Agents { get; } = [];
        public Dictionary<Guid, int> InvocationCounts { get; } = [];
        public Dictionary<Guid, List<string>> Prompts { get; } = [];
        public Task<ResearchExecutionResult> InvokeAsync(AgentDefinition agent, string prompt, CancellationToken cancellationToken)
        {
            Agents.Add(agent.Id);
            InvocationCounts[agent.Id] = InvocationCounts.GetValueOrDefault(agent.Id) + 1;
            if (!Prompts.TryGetValue(agent.Id, out var prompts)) Prompts[agent.Id] = prompts = [];
            prompts.Add(prompt);
            if (agent.Id == FailingAgentId) throw new InvalidOperationException(FailureMessage);
            var instrument = Action == "hold" ? "null" : "\"SE0000115446\"";
            var quantity = Action == "hold" ? 0 : 1;
            var evidenceUrl = ReuseRejectedEvidenceOnSecondInvocation && InvocationCounts[agent.Id] == 2
                ? "https://rejected.example/news"
                : AlternateEvidenceOnSecondInvocation && InvocationCounts[agent.Id] == 2
                    ? "https://alternate.example/news"
                    : "https://example.com/news";
            var evidence = RejectedEvidenceSecondOnFirstInvocation && InvocationCounts[agent.Id] == 1
                ? "[{\"url\":\"https://example.com/news\",\"publishedAt\":\"2026-08-16T10:00:00Z\",\"exactExcerpt\":\"Exact public text\"},{\"url\":\"https://rejected.example/news\",\"publishedAt\":\"2026-08-16T10:00:00Z\",\"exactExcerpt\":\"Rejected public text\"}]"
                : "[{\"url\":\"" + evidenceUrl + "\",\"publishedAt\":\"2026-08-16T10:00:00Z\",\"exactExcerpt\":\"Exact public text\"}]";
            var output = (agent.Id == MalformedFirstAgentId && InvocationCounts[agent.Id] == 1) ||
                         (agent.Id == MalformedSecondAgentId && InvocationCounts[agent.Id] == 2)
                ? "Web research completed, but this is not JSON."
                : $$"""{"agentId":"{{agent.Id:D}}","modelId":"{{agent.ModelId}}","action":"{{Action}}","instrumentId":{{instrument}},"quantity":{{quantity}},"reason":"Assumed-fill paper decision","confidence":0.5,"evidence":{{evidence}}}""";
            return Task.FromResult(new ResearchExecutionResult(output, string.Empty, new InvocationProvenance
            {
                AgentId = agent.Id,
                RequestedModelId = agent.ModelId,
                RequestedProvider = "copilot",
                ModelId = agent.ModelId,
                Provider = "copilot",
                RuntimeReport = ImmutableArray<byte>.Empty,
                RuntimeReportSha256 = new string('a', 64),
                Executable = "/hermes",
                Arguments = [],
                EnvironmentVariableNames = ["HERMES_HOME"],
                PromptSha256 = new string('b', 64),
                StartedAt = DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                CompletedAt = DateTimeOffset.Parse("2026-08-16T12:00:01Z"),
                ExitCode = 0,
                StandardOutputSha256 = new string('c', 64),
                StandardErrorSha256 = new string('d', 64)
            }));
        }
    }

    private sealed class FakeVerifier : AiStocks.Research.Evidence.IEvidenceVerifier
    {
        public int Calls { get; private set; }
        public Uri? RejectUrl { get; init; }
        private bool rejected;
        public Task<VerifiedEvidence> VerifyAsync(AiStocks.Research.Decisions.EvidenceClaim claim, CancellationToken cancellationToken) =>
            !rejected && claim.Url == RejectUrl
                ? Reject()
                : Task.FromResult(Verified(claim));

        private Task<VerifiedEvidence> Reject()
        {
            rejected = true;
            throw new AiStocks.Research.Evidence.EvidenceVerificationException(
                "Evidence representation is not independently verifiable.");
        }

        private VerifiedEvidence Verified(AiStocks.Research.Decisions.EvidenceClaim claim)
        {
            Calls++;
            return new VerifiedEvidence(claim.Url, claim.PublishedAt, DateTimeOffset.UtcNow, new string('e', 64), claim.ExactExcerpt);
        }
    }
}
