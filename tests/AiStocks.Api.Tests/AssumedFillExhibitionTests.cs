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
    public void Nordic_buy_binds_native_observation_and_verified_fx()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:20:01Z"));
        var store = new PreviewRaceStore(clock, null, DelayedNasdaqInstrumentStore.NordicDataMode,
            PreviewRaceStore.NordicAssumedExecutionMode);
        var fxAvailableAt = DateTimeOffset.Parse("2026-08-14T14:10:00Z");
        var instrument = new InstrumentDto("XCSE:DK0010181676:CARL-A", "CARL-A", "Carlsberg A A/S",
            "XCSE", "Denmark", "DKK", 750m, 750m, false, ExecutedAt, AvailableAt,
            "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true, 1m,
            new DateOnly(2026, 8, 14), fxAvailableAt, "ECB euro foreign exchange reference rates (informational, not transaction rates)",
            new string('c', 64));
        var snapshot = new InstrumentListDto([instrument], DelayedNasdaqInstrumentStore.NordicDataMode);
        var request = Decision("nordic-buy-001", "buy", instrument.Id, 10) with
        {
            ObservedPrice = 750m,
            ObservedCurrency = "DKK",
            ObservedVenue = "XCSE",
            ObservedFxToDkk = 1m,
            ObservationExecutedAt = ExecutedAt,
            FxAvailableAt = fxAvailableAt,
            FxSha256 = new string('c', 64),
            FxReferenceDate = new DateOnly(2026, 8, 14),
            FxSource = "ECB euro foreign exchange reference rates (informational, not transaction rates)"
        };
        StartRun(store, request);

        var submission = store.SubmitAi(request, snapshot);

        Assert.Equal("assumed-delayed-paper-fills-v2", submission.Decision.AssumedPaperFill!.ExecutionMode);
        Assert.Equal("DKK", submission.Decision.AssumedPaperFill.ObservedCurrency);
        Assert.Equal("XCSE", submission.Decision.AssumedPaperFill.ObservedVenue);
        Assert.Equal(1m, submission.Decision.AssumedPaperFill.FxToDkk);
        Assert.Equal(7_575m, submission.Decision.AssumedPaperFill.TotalDkk);
    }

    [Fact]
    public void Nordic_sell_uses_native_fx_and_marks_remaining_position_in_dkk()
    {
        var store = new PreviewRaceStore(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:40:00Z")),
            null, DelayedNasdaqInstrumentStore.NordicDataMode, PreviewRaceStore.NordicAssumedExecutionMode);
        var fxAvailableAt = DateTimeOffset.Parse("2026-08-14T14:10:00Z");
        var bought = NordicInstrument(750m, fxAvailableAt);
        Submit(store, NordicDecision("nordic-sell-seed", "buy", bought, 10, AvailableAt.AddMinutes(2)),
            new InstrumentListDto([bought], DelayedNasdaqInstrumentStore.NordicDataMode));
        var marked = NordicInstrument(800m, fxAvailableAt) with
        {
            ExecutedAt = ExecutedAt.AddMinutes(5),
            AvailableAt = AvailableAt.AddMinutes(5),
            PriceDkk = 800m
        };
        var snapshot = new InstrumentListDto([marked], DelayedNasdaqInstrumentStore.NordicDataMode);
        var sell = NordicDecision("nordic-sell-001", "sell", marked, 4, AvailableAt.AddMinutes(8));

        var accepted = Submit(store, sell, snapshot);
        var portfolio = store.AiProgress(snapshot).Participants.Single(item => item.AgentId == Agent.Id).Portfolio;

        Assert.Equal(792m, accepted.Decision.AssumedPaperFill!.FillPriceDkk);
        Assert.Equal(3_168m, accepted.Decision.AssumedPaperFill.TotalDkk);
        Assert.Equal(6, Assert.Single(portfolio.Holdings).Quantity);
        Assert.Equal(4_800m, portfolio.HoldingsValueDkk);
        Assert.Equal(100_393m, portfolio.TotalValueDkk);
    }

    [Fact]
    public void Nordic_snapshot_without_verified_fx_fails_closed()
    {
        var store = new PreviewRaceStore(TimeProvider.System, null, DelayedNasdaqInstrumentStore.NordicDataMode,
            PreviewRaceStore.NordicAssumedExecutionMode);
        var instrument = NordicInstrument(750m, DateTimeOffset.Parse("2026-08-14T14:10:00Z")) with
        {
            PriceDkk = null,
            FxToDkk = null,
            FxReferenceDate = null,
            FxAvailableAt = null,
            FxSource = null,
            FxSha256 = null
        };
        var snapshot = new InstrumentListDto([instrument], DelayedNasdaqInstrumentStore.NordicDataMode);
        var request = NordicDecision("nordic-no-fx-01", "buy", instrument, 1, AvailableAt.AddMinutes(2));
        StartRun(store, request);

        Assert.Equal("invalid-fx",
            Assert.Throws<PreviewOrderException>(() => store.SubmitAi(request, snapshot)).Code);
        Assert.Equal("invalid-fx",
            Assert.Throws<PreviewOrderException>(() => store.AiProgress(snapshot)).Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nordic_buy_rejects_changed_fx_reference_provenance(bool changeDate)
    {
        var store = new PreviewRaceStore(TimeProvider.System, null, DelayedNasdaqInstrumentStore.NordicDataMode,
            PreviewRaceStore.NordicAssumedExecutionMode);
        var instrument = NordicInstrument(750m, DateTimeOffset.Parse("2026-08-14T14:10:00Z"));
        var snapshot = new InstrumentListDto([instrument], DelayedNasdaqInstrumentStore.NordicDataMode);
        var valid = NordicDecision("nordic-fx-provenance", "buy", instrument, 1, AvailableAt.AddMinutes(2));
        var request = changeDate
            ? valid with { FxReferenceDate = valid.FxReferenceDate!.Value.AddDays(-1) }
            : valid with { FxSource = "untrusted FX source" };
        StartRun(store, request);

        Assert.Equal("observation-mismatch",
            Assert.Throws<PreviewOrderException>(() => store.SubmitAi(request, snapshot)).Code);
    }

    [Fact]
    public void Nordic_buy_rejects_fx_that_was_unavailable_when_the_decision_completed()
    {
        var store = new PreviewRaceStore(TimeProvider.System, null, DelayedNasdaqInstrumentStore.NordicDataMode,
            PreviewRaceStore.NordicAssumedExecutionMode);
        var instrument = NordicInstrument(750m, AvailableAt.AddMinutes(3));
        var snapshot = new InstrumentListDto([instrument], DelayedNasdaqInstrumentStore.NordicDataMode);
        var request = NordicDecision("nordic-future-fx", "buy", instrument, 1, AvailableAt.AddMinutes(2));
        StartRun(store, request);

        Assert.Equal("invalid-fx",
            Assert.Throws<PreviewOrderException>(() => store.SubmitAi(request, snapshot)).Code);
    }

    [Fact]
    public void Progress_reports_stock_name_cost_basis_gain_and_filterable_performance_series()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:30:00Z"));
        var store = new PreviewRaceStore(clock);
        var bought = Snapshot(Instrument("SE0000108656", "ERIC-B", 100m));
        Submit(store, Decision("detailed-buy-001", "buy", "SE0000108656", 10), bought);
        var marked = Snapshot(Instrument("SE0000108656", "ERIC-B", 120m,
            ExecutedAt.AddMinutes(1), AvailableAt.AddMinutes(1)));

        var progress = store.AiProgress(marked);
        var participant = progress.Participants.Single(item => item.AgentId == Agent.Id);
        var holding = Assert.Single(participant.Portfolio.Holdings);

        Assert.Equal("ERIC-B", holding.Name);
        Assert.Equal(78m, holding.PriceDkk);
        Assert.Equal(65.65m, holding.AverageBuyPriceDkk);
        Assert.Equal(656.50m, holding.CostBasisDkk);
        Assert.Equal(123.50m, holding.GainDkk);
        Assert.Equal(18.81m, holding.GainPercent);
        var performance = Assert.IsAssignableFrom<IReadOnlyList<PerformanceSeriesDto>>(progress.Performance);
        Assert.Equal(4, performance.Count(item => item.Type == "model"));
        Assert.Contains(performance, item => item.Id == "starting-cash" && item.Type == "benchmark");
        Assert.Contains(performance, item => item.Id == "ai-field-average" && item.Type == "benchmark");
        Assert.All(performance, item => Assert.NotEmpty(item.Points));
        Assert.True(performance.Single(item => item.Id == Agent.ModelId).Points.Count >= 2);
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

    [Fact]
    public void Missing_existing_mark_rejects_trade_without_mutation_or_consuming_run_id()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var held = Instrument("SE0000108656", "ERIC-B", 100m);
        var traded = Instrument("SE0000115446", "VOLV-B", 100m);
        Submit(store, Decision("atomic-mark-seed", "buy", held.Id, 10), Snapshot(held));
        var request = Decision("atomic-mark-trade", "buy", traded.Id, 10, AvailableAt.AddMinutes(4));
        StartRun(store, request);
        var completeSnapshot = Snapshot(held, traded);
        var before = store.AiProgress(completeSnapshot);

        var error = Assert.Throws<PreviewOrderException>(() => store.SubmitAi(request, Snapshot(traded)));
        var after = store.AiProgress(completeSnapshot);

        Assert.Equal("stale-portfolio-mark", error.Code);
        var beforeParticipant = before.Participants.Single(item => item.AgentId == Agent.Id);
        var afterParticipant = after.Participants.Single(item => item.AgentId == Agent.Id);
        Assert.Equal("running", afterParticipant.Status);
        Assert.Equal(beforeParticipant.LatestDecision, afterParticipant.LatestDecision);
        Assert.Equal(beforeParticipant.Portfolio.CashDkk, afterParticipant.Portfolio.CashDkk);
        Assert.Equal(beforeParticipant.Portfolio.Holdings, afterParticipant.Portfolio.Holdings);
        Assert.Equal(before.Activity, after.Activity);

        Assert.False(store.SubmitAi(request, completeSnapshot).Replayed);
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

    private static AiDecisionRequestDto NordicDecision(string runId, string action, InstrumentDto instrument,
        int quantity, DateTimeOffset completedAt) =>
        Decision(runId, action, instrument.Id, quantity, completedAt) with
        {
            ObservedPrice = instrument.Price,
            ObservedCurrency = instrument.Currency,
            ObservedVenue = instrument.Exchange,
            ObservedFxToDkk = instrument.FxToDkk,
            ObservationExecutedAt = instrument.ExecutedAt,
            ObservationAvailableAt = instrument.AvailableAt,
            FxAvailableAt = instrument.FxAvailableAt,
            FxSha256 = instrument.FxSha256,
            FxReferenceDate = instrument.FxReferenceDate,
            FxSource = instrument.FxSource
        };

    private static InstrumentDto NordicInstrument(decimal price, DateTimeOffset fxAvailableAt) =>
        new("XCSE:DK0010181676:CARL-A", "CARL-A", "Carlsberg A A/S", "XCSE", "Denmark", "DKK",
            price, price, false, ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15,
            false, true, 1m, new DateOnly(2026, 8, 14), fxAvailableAt,
            "ECB euro foreign exchange reference rates (informational, not transaction rates)", new string('c', 64));

    private static InstrumentDto Instrument(string id, string symbol, decimal price,
        DateTimeOffset? executedAt = null, DateTimeOffset? availableAt = null) =>
        new(id, symbol, symbol, "XSTO", "Sweden", "SEK", price, null, false,
            executedAt ?? ExecutedAt, availableAt ?? AvailableAt,
            "Nasdaq Nordic MiFID II delayed post-trade", 15, false, PaperTradable: true);

    private static InstrumentListDto Snapshot(params InstrumentDto[] instruments) =>
        new(instruments, DelayedNasdaqInstrumentStore.DataMode);
}
