using AiStocks.Core;

namespace AiStocks.Trading.Tests;

public sealed class ExecutionAndRiskTests
{
    [Theory]
    [InlineData("100", null, null, "10000", "0.00125")]
    [InlineData("100", "99", "101", "10000", "0.01")]
    [InlineData("100", "80", "120", "1", "0.01")]
    [InlineData("100", "99.9", "100.1", "0", "0.001")]
    public void Slippage_has_deterministic_golden_values(
        string price, string? bid, string? ask, string orderValue, string expected)
    {
        var quote = TestData.Quote(price: decimal.Parse(price), bid: bid is null ? null : decimal.Parse(bid),
            ask: ask is null ? null : decimal.Parse(ask), adv: 1_000_000m);
        Assert.Equal(decimal.Parse(expected), ExecutionMath.AdverseSlippageRate(quote, decimal.Parse(orderValue)));
    }

    [Fact]
    public void Buy_fill_is_adverse_and_updates_cash_holdings_and_weighted_average_cost()
    {
        var engine = PaperTradingEngine.CreateContest();
        var first = engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session,
            TestData.Marks());
        var second = engine.Submit(TestData.Decision("d2", quantity: 10, observedPrice: 200m),
            TestData.Quote(price: 200m), TestData.Session,
            TestData.Marks((TestData.Volvo, 200m)));

        Assert.Equal(OrderStatus.Filled, first.Status);
        Assert.Equal(100.1018m, first.FillPrice);
        Assert.Equal(0m, first.Fee);
        Assert.Equal(OrderStatus.Filled, second.Status);
        var portfolio = engine.Portfolio(TestData.Agent.Id);
        Assert.Equal(26_996.93m, portfolio.Cash);
        var position = Assert.Single(portfolio.Positions);
        Assert.Equal(20, position.Quantity);
        Assert.Equal(150.1535m, position.AverageCost);
    }

    [Fact]
    public void Sell_uses_adverse_price_and_rejects_shorting()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        var sold = engine.Submit(TestData.Decision("sell", DecisionAction.Sell, 4), TestData.Quote(),
            TestData.Session, TestData.Marks((TestData.Volvo, 100m)));
        Assert.Equal(99.8989m, sold.FillPrice);
        Assert.Equal(29_398.58m, engine.Portfolio(TestData.Agent.Id).Cash);
        Assert.Equal(6, engine.Portfolio(TestData.Agent.Id).Positions.Single().Quantity);
        Assert.Throws<TradingException>(() => engine.Submit(
            TestData.Decision("short", DecisionAction.Sell, 7), TestData.Quote(), TestData.Session,
            TestData.Marks((TestData.Volvo, 100m))));
    }

    [Theory]
    [InlineData(19, 100, 1, "history")]
    [InlineData(20, 100, 101, "liquidity")]
    [InlineData(20, 100, 75, "concentration")]
    public void Buy_gates_reject_boundaries(int sessions, int advThousands, int quantity, string code)
    {
        var engine = PaperTradingEngine.CreateContest();
        var adv = code == "concentration" ? 1_000_000m : advThousands * 1000m;
        var error = Assert.Throws<TradingException>(() => engine.Submit(TestData.Decision(quantity: quantity),
            TestData.Quote(sessions: sessions, adv: adv), TestData.Session, TestData.Marks()));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Exact_one_percent_adv_and_exact_twenty_sessions_are_allowed()
    {
        var engine = PaperTradingEngine.CreateContest();
        var outcome = engine.Submit(TestData.Decision(quantity: 10),
            TestData.Quote(sessions: 20, adv: 100_000m), TestData.Session, TestData.Marks());
        Assert.Equal(OrderStatus.Filled, outcome.Status);
    }

    [Fact]
    public void Exact_twenty_five_percent_concentration_is_allowed_but_one_ore_over_is_not()
    {
        var engine = PaperTradingEngine.CreateContest();
        var exact = engine.Submit(TestData.Decision(quantity: 74), TestData.Quote(price: 100m, adv: 1_000_000m),
            TestData.Session, TestData.Marks());
        Assert.Equal(OrderStatus.Filled, exact.Status);
        var other = PaperTradingEngine.CreateContest();
        Assert.Throws<TradingException>(() => other.Submit(TestData.Decision(quantity: 75),
            TestData.Quote(price: 100m, adv: 1_000_000m), TestData.Session, TestData.Marks()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantity_must_be_positive_whole_shares(int quantity)
    {
        var error = Assert.Throws<TradingException>(() => PaperTradingEngine.CreateContest().Submit(
            TestData.Decision(quantity: quantity), TestData.Quote(), TestData.Session, TestData.Marks()));
        Assert.Equal("quantity", error.Code);
    }

    [Fact]
    public void Identity_evidence_quote_and_observed_price_fail_closed()
    {
        Assert.Equal("identity", Assert.Throws<TradingException>(() => PaperTradingEngine.CreateContest().Submit(
            TestData.Decision(modelId: TestData.OtherAgent.ModelId), TestData.Quote(), TestData.Session,
            TestData.Marks())).Code);
        Assert.Equal("evidence", Assert.Throws<TradingException>(() => PaperTradingEngine.CreateContest().Submit(
            TestData.Decision(evidence: []), TestData.Quote(), TestData.Session, TestData.Marks())).Code);
        Assert.Equal("observed-price", Assert.Throws<TradingException>(() => PaperTradingEngine.CreateContest().Submit(
            TestData.Decision(observedPrice: 0m), TestData.Quote(), TestData.Session, TestData.Marks())).Code);
        Assert.Equal("instrument", Assert.Throws<TradingException>(() => PaperTradingEngine.CreateContest().Submit(
            TestData.Decision(), TestData.Quote(instrument: TestData.Atlas), TestData.Session,
            TestData.Marks())).Code);
    }

    [Fact]
    public void Starter_transitions_permanently_to_mini_at_50000_or_trade_501()
    {
        var byCapital = PaperTradingEngine.CreateContest();
        byCapital.ApplyCorrection(TestData.Agent.Id, "capital", 20_000m, null, 0, 0m,
            TestData.Open, "verified correction");
        var capitalFill = byCapital.Submit(TestData.Decision(quantity: 1), TestData.Quote(), TestData.Session,
            TestData.Marks());
        Assert.Equal(1m, capitalFill.Fee);
        Assert.Equal(FeeTier.Mini, byCapital.Portfolio(TestData.Agent.Id).FeeTier);
        byCapital.ApplyCorrection(TestData.Agent.Id, "down", -25_000m, null, 0, 0m,
            TestData.Open, "verified correction");
        var permanent = byCapital.Submit(TestData.Decision("later", quantity: 1), TestData.Quote(),
            TestData.Session, TestData.Marks((TestData.Volvo, 100m)));
        Assert.Equal(1m, permanent.Fee);

        var byCount = PaperTradingEngine.CreateContest();
        byCount.ApplyCorrection(TestData.Agent.Id, "count", 0m, null, 0, 0m,
            TestData.Open, "verified correction", completedTradeCountDelta: 500);
        var trade501 = byCount.Submit(TestData.Decision(quantity: 1), TestData.Quote(), TestData.Session,
            TestData.Marks());
        Assert.Equal(1m, trade501.Fee);
    }
}
