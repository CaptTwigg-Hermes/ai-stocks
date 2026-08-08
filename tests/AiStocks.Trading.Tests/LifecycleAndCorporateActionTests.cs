using AiStocks.Core;

namespace AiStocks.Trading.Tests;

public sealed class LifecycleAndCorporateActionTests
{
    [Fact]
    public void Out_of_hours_order_queues_and_only_first_valid_post_resume_quote_fills()
    {
        var engine = PaperTradingEngine.CreateContest();
        var decision = TestData.Decision(at: TestData.Open.AddHours(-1));
        var queued = engine.Submit(decision, null, null, TestData.Marks());
        var order = Assert.Single(engine.Orders);
        Assert.Equal(OrderStatus.Queued, queued.Status);

        engine.Pause(TestData.Open, "feed incident");
        Assert.Throws<TradingException>(() => engine.ExecuteQueued(order.Id, [TestData.Quote()],
            TestData.Session, TestData.Marks()));
        engine.Resume(TestData.Open.AddMinutes(30), "feed verified");
        var pausedQuote = TestData.Quote(tradedAt: TestData.Open.AddMinutes(15));
        Assert.Equal("no-eligible-quote", engine.ExecuteQueued(order.Id, [pausedQuote], TestData.Session,
            TestData.Marks()).Code);
        var postResume = TestData.Quote(tradedAt: TestData.Open.AddMinutes(31));
        var filled = engine.ExecuteQueued(order.Id, [postResume], TestData.Session, TestData.Marks());
        Assert.Equal(OrderStatus.Filled, filled.Status);
    }

    [Fact]
    public void Queued_execution_atomically_selects_the_first_eligible_observation()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(at: TestData.Open.AddHours(-1)), null, null, TestData.Marks());
        var order = Assert.Single(engine.Orders);
        var first = TestData.Quote(price: 100m, tradedAt: TestData.Open.AddMinutes(1));
        var later = TestData.Quote(price: 150m, tradedAt: TestData.Open.AddMinutes(2));

        var filled = engine.ExecuteQueued(order.Id, [later, first], TestData.Session, TestData.Marks());

        Assert.Equal(100.1006m, filled.FillPrice);
        Assert.Equal(first.TradedAt, filled.FilledAt);
    }

    [Fact]
    public void Cancel_is_owner_only_terminal_and_idempotent_with_conflict_detection()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(), null, null, TestData.Marks());
        var order = Assert.Single(engine.Orders);
        Assert.Equal("ownership", Assert.Throws<TradingException>(() => engine.Cancel(
            TestData.OtherAgent.Id, order.Id, "no", "wrong-owner", TestData.Open)).Code);
        var first = engine.Cancel(TestData.Agent.Id, order.Id, "thesis changed", "cancel-1", TestData.Open);
        var replay = engine.Cancel(TestData.Agent.Id, order.Id, "thesis changed", "cancel-1", TestData.Open);
        Assert.Equal(first, replay);
        Assert.Equal(OrderStatus.Cancelled, first.Status);
        Assert.Throws<TradingException>(() => engine.ExecuteQueued(order.Id, [TestData.Quote()],
            TestData.Session, TestData.Marks()));
        Assert.Equal("idempotency", Assert.Throws<TradingException>(() => engine.Cancel(
            TestData.Agent.Id, order.Id, "different", "cancel-1", TestData.Open)).Code);
    }

    [Fact]
    public void Replace_is_atomic_terminal_and_idempotent()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision("original"), null, null, TestData.Marks());
        var original = Assert.Single(engine.Orders);
        var replacement = TestData.Decision("replacement", quantity: 2,
            at: original.Decision.DecisionAt.AddMinutes(1));
        var first = engine.Replace(TestData.Agent.Id, original.Id, replacement, "more conviction",
            "replace-1", replacement.DecisionAt);
        var replay = engine.Replace(TestData.Agent.Id, original.Id, replacement, "more conviction",
            "replace-1", replacement.DecisionAt);
        Assert.Equal(first, replay);
        Assert.Equal(OrderStatus.Replaced, engine.Orders.Single(order => order.Id == original.Id).Status);
        var replacementOrder = engine.Orders.Single(order => order.Id != original.Id);
        Assert.Equal(OrderStatus.Queued, replacementOrder.Status);
        Assert.Equal(replacementOrder.Id, engine.Orders.Single(order => order.Id == original.Id).ReplacedBy);
    }

    [Fact]
    public void Pause_lifecycle_rejects_mutation_and_invalid_transitions()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Pause(TestData.Open, "security");
        Assert.Equal(ContestStatus.Paused, engine.Status);
        Assert.Equal("pause-state", Assert.Throws<TradingException>(() =>
            engine.Pause(TestData.Open.AddMinutes(1), "again")).Code);
        Assert.Equal("paused", Assert.Throws<TradingException>(() => engine.Submit(
            TestData.Decision(), null, null, TestData.Marks())).Code);
        Assert.Equal("pause-state", Assert.Throws<TradingException>(() =>
            engine.Resume(TestData.Open, "too soon")).Code);
        engine.Resume(TestData.Open.AddMinutes(1), "safe");
        Assert.Equal(ContestStatus.Running, engine.Status);
    }

    [Fact]
    public void Dividend_uses_verified_pre_ex_date_ownership_and_payment_date()
    {
        var engine = SeedTenShares();
        var closeBeforeExDate = TestData.Open.AddDays(1).AddHours(8);
        engine.ApplyDividend(TestData.Agent.Id, TestData.Volvo, 5m, closeBeforeExDate,
            DateOnly.FromDateTime(closeBeforeExDate.AddDays(1).Date), closeBeforeExDate.AddDays(10), "div-1");
        engine.ApplyDividend(TestData.Agent.Id, TestData.Volvo, 5m, closeBeforeExDate,
            DateOnly.FromDateTime(closeBeforeExDate.AddDays(1).Date), closeBeforeExDate.AddDays(10), "div-1");
        Assert.Equal(29_048.98m, engine.Portfolio(TestData.Agent.Id).Cash);
        var dividend = Assert.Single(engine.AuditTrail, item => item.Type == "DIVIDEND");
        Assert.Equal(closeBeforeExDate.AddDays(10), dividend.OccurredAt);
        Assert.Equal(50m, dividend.CashDelta);
    }

    [Fact]
    public void Split_preserves_total_cost_and_freezes_fractional_entitlement()
    {
        var engine = SeedTenShares();
        engine.ApplySplit(TestData.Agent.Id, TestData.Volvo, 3, 4, TestData.Open.AddDays(1), "split-1");
        var position = Assert.Single(engine.Portfolio(TestData.Agent.Id).Positions);
        Assert.Equal(7, position.Quantity);
        Assert.Equal(133.4693m, position.AverageCost);
        var frozen = Assert.Single(engine.FrozenEntitlements);
        Assert.Equal(0.5m, frozen.FractionalQuantity);
    }

    [Fact]
    public void Stock_and_cash_mergers_transfer_weighted_cost_then_settle()
    {
        var engine = SeedTenShares();
        engine.ApplyStockMerger(TestData.Agent.Id, TestData.Volvo, TestData.Atlas, 1, 2,
            TestData.Open.AddDays(1), "stock-merger");
        var transferred = Assert.Single(engine.Portfolio(TestData.Agent.Id).Positions);
        Assert.Equal(TestData.Atlas, transferred.Instrument);
        Assert.Equal(5, transferred.Quantity);
        Assert.Equal(200.204m, transferred.AverageCost);
        engine.ApplyCashMerger(TestData.Agent.Id, TestData.Atlas, 250m,
            TestData.Open.AddDays(2), "cash-merger");
        Assert.Empty(engine.Portfolio(TestData.Agent.Id).Positions);
        Assert.Equal(30_248.98m, engine.Portfolio(TestData.Agent.Id).Cash);
    }

    [Fact]
    public void Fractional_stock_merger_allocates_cost_between_whole_and_frozen_entitlements()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.ApplyCorrection(TestData.Agent.Id, "old", 0m, TestData.Volvo, 3, 100m,
            TestData.Open, "verified old shares");
        engine.ApplyCorrection(TestData.Agent.Id, "target", 0m, TestData.Atlas, 1, 100m,
            TestData.Open, "verified target share");

        engine.ApplyStockMerger(TestData.Agent.Id, TestData.Volvo, TestData.Atlas, 1, 2,
            TestData.Open.AddDays(1), "fractional-merger");

        var position = Assert.Single(engine.Portfolio(TestData.Agent.Id).Positions);
        Assert.Equal(2, position.Quantity);
        Assert.Equal(150m, position.AverageCost);
        var frozen = Assert.Single(engine.FrozenEntitlements);
        Assert.Equal(0.5m, frozen.FractionalQuantity);
        Assert.Equal(200m, frozen.AverageCost);
    }

    [Fact]
    public void Delisting_without_proceeds_freezes_at_zero_until_separate_verified_settlement()
    {
        var engine = SeedTenShares();
        engine.ApplyDelisting(TestData.Agent.Id, TestData.Volvo, null,
            TestData.Open.AddDays(1), "delist");
        Assert.True(engine.IsFrozen(TestData.Agent.Id, TestData.Volvo));
        Assert.Throws<TradingException>(() => engine.Submit(TestData.Decision("sell", DecisionAction.Sell),
            TestData.Quote(), TestData.Session, TestData.Marks((TestData.Volvo, 100m))));
        engine.SettleDelisting(TestData.Agent.Id, TestData.Volvo, 80m,
            TestData.Open.AddDays(5), "delist-settlement");
        Assert.False(engine.IsFrozen(TestData.Agent.Id, TestData.Volvo));
        Assert.Empty(engine.Portfolio(TestData.Agent.Id).Positions);
        Assert.Equal(29_798.98m, engine.Portfolio(TestData.Agent.Id).Cash);
    }

    [Fact]
    public void Corrections_are_append_only_idempotent_and_conflicting_payloads_fail()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.ApplyCorrection(TestData.Agent.Id, "fix-1", 10m, null, 0, 0m,
            TestData.Open, "bad source corrected");
        engine.ApplyCorrection(TestData.Agent.Id, "fix-1", 10m, null, 0, 0m,
            TestData.Open, "bad source corrected");
        Assert.Equal(30_010m, engine.Portfolio(TestData.Agent.Id).Cash);
        Assert.Equal("reference-conflict", Assert.Throws<TradingException>(() => engine.ApplyCorrection(
            TestData.Agent.Id, "fix-1", 11m, null, 0, 0m, TestData.Open,
            "bad source corrected")).Code);
        Assert.Single(engine.AuditTrail, item => item.Type == "CORRECTION");
    }

    private static PaperTradingEngine SeedTenShares()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        return engine;
    }
}
