using AiStocks.Core;

namespace AiStocks.Api.Tests;

public sealed class AssumedFillExhibitionTests
{
    private static readonly AgentDefinition Agent = ContestContract.Agents[0];
    private static readonly DateTimeOffset ExecutedAt = DateTimeOffset.Parse("2026-08-16T10:01:00Z");
    private static readonly DateTimeOffset AvailableAt = DateTimeOffset.Parse("2026-08-16T10:16:00Z");

    [Fact]
    public void Buy_uses_assumed_fx_and_adverse_slippage_and_marks_without_slippage()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:20:01Z"));
        var store = new PreviewRaceStore(clock);
        var snapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        var request = Decision("assumed-buy-001", "buy", "SE0000108656", 10);
        StartRun(store, request);

        var submission = store.SubmitAi(request, snapshot);
        var progress = store.AiProgress(snapshot);
        var participant = progress.Participants.Single(item => item.AgentId == Agent.Id);

        Assert.False(submission.Replayed);
        Assert.Equal("assumed-delayed-paper-fills-v1", progress.ExecutionMode);
        Assert.All(progress.Participants,
            item => Assert.Equal("assumed-delayed-paper-fills-v1", item.Portfolio.ExecutionMode));
        Assert.True(progress.AssumedFills);
        Assert.False(progress.HoldOnly);
        Assert.Equal(0.65m, progress.AssumedSekToDkk);
        Assert.Equal(1m, progress.AssumedSlippagePercent);
        Assert.Equal(656.50m, submission.Decision.AssumedPaperFill!.TotalDkk);
        Assert.Equal(65.65m, submission.Decision.AssumedPaperFill.FillPriceDkk);
        Assert.Equal(100m, submission.Decision.AssumedPaperFill.ObservedPriceSek);
        Assert.Equal(ExecutedAt, submission.Decision.AssumedPaperFill.ObservationExecutedAt);
        Assert.Equal(AvailableAt, submission.Decision.AssumedPaperFill.ObservationAvailableAt);
        Assert.Equal(clock.GetUtcNow(), submission.Decision.AssumedPaperFill.FilledAt);
        Assert.Equal(99_343.50m, participant.Portfolio.CashDkk);
        Assert.Equal(650m, participant.Portfolio.HoldingsValueDkk);
        Assert.Equal(10, Assert.Single(participant.Portfolio.Holdings).Quantity);
        Assert.All(progress.Participants.Where(item => item.AgentId != Agent.Id),
            item => Assert.Equal(100_000m, item.Portfolio.CashDkk));
    }

    [Fact]
    public void Sell_uses_adverse_slippage_and_cannot_exceed_holdings()
    {
        var store = new PreviewRaceStore(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:30:00Z")));
        var snapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        Submit(store, Decision("sell-seed-buy", "buy", "SE0000108656", 10), snapshot);
        var sell = Decision("assumed-sell-01", "sell", "SE0000108656", 4, AvailableAt.AddMinutes(4));
        StartRun(store, sell);

        var accepted = store.SubmitAi(sell, snapshot);
        var beforeRejected = store.AiProgress(snapshot);
        var rejected = Decision("assumed-sell-02", "sell", "SE0000108656", 7, AvailableAt.AddMinutes(6));
        StartRun(store, rejected);
        var error = Assert.Throws<PreviewOrderException>(() => store.SubmitAi(rejected, snapshot));
        var afterRejected = store.AiProgress(snapshot);

        Assert.Equal(257.40m, accepted.Decision.AssumedPaperFill!.TotalDkk);
        Assert.Equal(64.35m, accepted.Decision.AssumedPaperFill.FillPriceDkk);
        Assert.Equal("insufficient-holdings", error.Code);
        Assert.Equal(beforeRejected.Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk,
            afterRejected.Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk);
        Assert.Equal("running", afterRejected.Participants.Single(x => x.AgentId == Agent.Id).Status);
    }

    [Fact]
    public void Buy_and_sell_require_evidence_with_no_lookahead_but_hold_allows_none()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var snapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        var futureEvidence = Decision("lookahead-buy-1", "buy", "SE0000108656", 1) with
        {
            Evidence = [Evidence(AvailableAt.AddSeconds(1))]
        };
        StartRun(store, futureEvidence);

        var error = Assert.Throws<PreviewOrderException>(() => store.SubmitAi(futureEvidence, snapshot));
        var holdStore = new PreviewRaceStore(TimeProvider.System);
        var hold = Decision("evidence-free-hold", "hold", null, 0, AvailableAt.AddMinutes(4)) with { Evidence = [] };
        StartRun(holdStore, hold);
        var accepted = holdStore.SubmitAi(hold, snapshot);

        Assert.Equal("evidence-lookahead", error.Code);
        Assert.Null(accepted.Decision.AssumedPaperFill);
        Assert.Equal(100_000m, holdStore.AiProgress(snapshot).Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk);

        var sellStore = new PreviewRaceStore(TimeProvider.System);
        Submit(sellStore, Decision("lookahead-seed", "buy", "SE0000108656", 1), snapshot);
        var futureSell = Decision("lookahead-sell-1", "sell", "SE0000108656", 1, AvailableAt.AddMinutes(6)) with
        {
            Evidence = [Evidence(AvailableAt.AddSeconds(1))]
        };
        StartRun(sellStore, futureSell);
        Assert.Equal("evidence-lookahead",
            Assert.Throws<PreviewOrderException>(() => sellStore.SubmitAi(futureSell, snapshot)).Code);
    }

    [Fact]
    public void Buy_enforces_maximum_order_and_marked_position_without_mutation()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var snapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        Submit(store, Decision("position-seed-01", "buy", "SE0000108656", 150), snapshot);
        Submit(store, Decision("position-seed-02", "buy", "SE0000108656", 150, AvailableAt.AddMinutes(6)), snapshot);
        var cap = Decision("position-cap-01", "buy", "SE0000108656", 85, AvailableAt.AddMinutes(8));
        StartRun(store, cap);
        var before = store.AiProgress(snapshot);
        Assert.Equal("maximum-position-value", Assert.Throws<PreviewOrderException>(() => store.SubmitAi(cap, snapshot)).Code);
        Assert.Equal(before.Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk,
            store.AiProgress(snapshot).Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk);

        var orderStore = new PreviewRaceStore(TimeProvider.System);
        var tooLarge = Decision("order-limit-001", "buy", "SE0000108656", 153); // DKK 10,044.45
        StartRun(orderStore, tooLarge);
        Assert.Equal("maximum-order-total", Assert.Throws<PreviewOrderException>(() => orderStore.SubmitAi(tooLarge, snapshot)).Code);
        Assert.Equal(100_000m, orderStore.AiProgress(snapshot).Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk);
    }

    [Fact]
    public void Insufficient_cash_is_rejected_without_mutation()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var instruments = Enumerable.Range(1, 11).Select(index =>
            Instrument($"SE{index:0000000000}", $"TEST-{index}", 100m)).ToArray();
        var snapshot = Snapshot(instruments);
        for (var index = 0; index < 10; index++)
            Submit(store, Decision($"cash-spend-{index:00}", "buy", instruments[index].Id, 150,
                AvailableAt.AddMinutes(index * 2 + 2)), snapshot);
        var request = Decision("cash-reject-01", "buy", instruments[10].Id, 150, AvailableAt.AddMinutes(24));
        StartRun(store, request);
        var before = store.AiProgress(snapshot);

        var error = Assert.Throws<PreviewOrderException>(() => store.SubmitAi(request, snapshot));

        Assert.Equal("insufficient-cash", error.Code);
        var prior = before.Participants.Single(x => x.AgentId == Agent.Id).Portfolio;
        var after = store.AiProgress(snapshot).Participants.Single(x => x.AgentId == Agent.Id).Portfolio;
        Assert.Equal(prior.CashDkk, after.CashDkk);
        Assert.Equal(prior.Holdings.Select(x => (x.InstrumentId, x.Quantity)), after.Holdings.Select(x => (x.InstrumentId, x.Quantity)));
    }

    [Fact]
    public void Unknown_or_not_yet_available_instrument_is_rejected_without_mutation()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var current = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        var unknown = Decision("unknown-item-01", "buy", "SE9999999999", 1);
        StartRun(store, unknown);
        Assert.Equal("instrument-not-found", Assert.Throws<PreviewOrderException>(() => store.SubmitAi(unknown, current)).Code);

        var staleStore = new PreviewRaceStore(TimeProvider.System);
        var stale = Decision("stale-item-001", "buy", "SE0000108656", 1, AvailableAt.AddSeconds(-1));
        StartRun(staleStore, stale);
        Assert.Equal("observation-not-available", Assert.Throws<PreviewOrderException>(() => staleStore.SubmitAi(stale, current)).Code);
        Assert.Equal(100_000m, staleStore.AiProgress(current).Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk);
    }

    [Fact]
    public void Exact_replay_survives_market_drift_while_conflicting_reuse_is_rejected()
    {
        var store = new PreviewRaceStore(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:30:00Z")));
        var originalSnapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        var request = Decision("replay-drift-01", "buy", "SE0000108656", 10);
        StartRun(store, request);
        var first = store.SubmitAi(request, originalSnapshot);
        var driftedSnapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 120m,
            ExecutedAt.AddMinutes(1), AvailableAt.AddMinutes(1)));

        var replay = store.SubmitAi(request, driftedSnapshot);
        var conflict = Assert.Throws<PreviewOrderException>(() =>
            store.SubmitAi(request with { Quantity = 11 }, driftedSnapshot));

        Assert.True(replay.Replayed);
        Assert.Same(first.Decision, replay.Decision);
        Assert.Equal(656.50m, replay.Decision.AssumedPaperFill!.TotalDkk);
        Assert.Equal("run-id-conflict", conflict.Code);
        Assert.Equal(10, store.AiProgress(driftedSnapshot).Participants.Single(x => x.AgentId == Agent.Id).Portfolio.Holdings.Single().Quantity);
        Assert.Equal(780m, store.AiLeaderboard(driftedSnapshot).Items.Single(x => x.DisplayName == Agent.ModelId).ValueDkk - 99_343.50m);
    }

    [Fact]
    public void First_submission_is_bound_to_the_exact_observation_seen_by_the_worker()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var request = Decision("observation-drift-01", "buy", "SE0000108656", 10);
        StartRun(store, request);
        var newerSnapshot = Snapshot(Instrument("SE0000108656", "ERIC-B", 120m,
            ExecutedAt.AddMinutes(1), AvailableAt.AddMinutes(1)));

        var error = Assert.Throws<PreviewOrderException>(() => store.SubmitAi(request, newerSnapshot));

        Assert.Equal("observation-mismatch", error.Code);
        Assert.Equal(100_000m,
            store.AiProgress(newerSnapshot).Participants.Single(x => x.AgentId == Agent.Id).Portfolio.CashDkk);
    }

    [Fact]
    public void Progress_fails_closed_when_a_held_instrument_has_no_current_delayed_mark()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        Submit(store, Decision("stale-mark-seed-01", "buy", "SE0000108656", 1),
            Snapshot(Instrument("SE0000108656", "ERIC-B", 100m)));
        var currentWithoutHolding = Snapshot(Instrument("SE0000115446", "VOLV-B", 200m));

        var error = Assert.Throws<PreviewOrderException>(() => store.AiProgress(currentWithoutHolding));

        Assert.Equal("stale-portfolio-mark", error.Code);
    }

    private static AiDecisionSubmission Submit(PreviewRaceStore store, AiDecisionRequestDto request, InstrumentListDto snapshot)
    {
        StartRun(store, request);
        return store.SubmitAi(request, snapshot);
    }

    private static void StartRun(PreviewRaceStore store, AiDecisionRequestDto request)
    {
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null, request.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null, request.CompletedAt.AddSeconds(-1)));
    }

    private static AiDecisionRequestDto Decision(string runId, string action, string? instrumentId, int quantity,
        DateTimeOffset? completedAt = null) => new(runId, Agent.Id, Agent.ModelId, action, instrumentId, quantity,
        "Verified exhibition decision.", 0.75m, [Evidence(AvailableAt)], "copilot", Agent.ModelId,
        new string('b', 64), completedAt ?? AvailableAt.AddMinutes(2),
        action == "hold" ? null : 100m, action == "hold" ? null : AvailableAt);

    private static AiEvidenceDto Evidence(DateTimeOffset publishedAt) =>
        new("https://example.com/research", publishedAt, "Exact verified excerpt.", new string('a', 64));

    private static InstrumentDto Instrument(string id, string symbol, decimal price,
        DateTimeOffset? executedAt = null, DateTimeOffset? availableAt = null) =>
        new(id, symbol, symbol, "XSTO", "Sweden", "SEK", price, null, false,
            executedAt ?? ExecutedAt, availableAt ?? AvailableAt,
            "Nasdaq Nordic MiFID II delayed post-trade", 15, false, PaperTradable: true);

    private static InstrumentListDto Snapshot(params InstrumentDto[] instruments) =>
        new(instruments, DelayedNasdaqInstrumentStore.DataMode);
}
