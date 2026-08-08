using AiStocks.Core;

namespace AiStocks.Trading.Tests;

public sealed class ValidationBoundaryTests
{
    [Theory]
    [InlineData(14, 59, "market-data")]
    [InlineData(15, 0, null)]
    [InlineData(20, 0, null)]
    [InlineData(20, 1, "market-data")]
    public void Official_delay_window_is_inclusive(int minutes, int seconds, string? errorCode)
    {
        var quote = TestData.Quote() with
        {
            RetrievedAt = TestData.Quote().TradedAt.AddMinutes(minutes).AddSeconds(seconds)
        };
        var engine = PaperTradingEngine.CreateContest();
        if (errorCode is null)
            Assert.Equal(OrderStatus.Filled,
                engine.Submit(TestData.Decision(), quote, TestData.Session, TestData.Marks()).Status);
        else
            Assert.Equal(errorCode, Assert.Throws<TradingException>(() => engine.Submit(
                TestData.Decision(), quote, TestData.Session, TestData.Marks())).Code);
    }

    [Theory]
    [InlineData("0", "1", "spread")]
    [InlineData("101", "100", "spread")]
    [InlineData(null, "100", "spread")]
    [InlineData(null, null, null)]
    public void Spread_provenance_fails_closed(string? bid, string? ask, string? errorCode)
    {
        var quote = TestData.Quote(bid: bid is null ? null : decimal.Parse(bid),
            ask: ask is null ? null : decimal.Parse(ask));
        var engine = PaperTradingEngine.CreateContest();
        if (errorCode is null)
            Assert.Equal(OrderStatus.Filled,
                engine.Submit(TestData.Decision(), quote, TestData.Session, TestData.Marks()).Status);
        else
            Assert.Equal(errorCode, Assert.Throws<TradingException>(() => engine.Submit(
                TestData.Decision(), quote, TestData.Session, TestData.Marks())).Code);
    }

    [Fact]
    public void Sell_remains_allowed_below_history_and_above_buy_adv_gate()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        var quote = TestData.Quote(sessions: 0, adv: 1m);
        var sold = engine.Submit(TestData.Decision("sell", DecisionAction.Sell, 10), quote,
            TestData.Session, TestData.Marks((TestData.Volvo, 100m)));
        Assert.Equal(OrderStatus.Filled, sold.Status);
    }

    [Fact]
    public void Same_issuer_share_classes_are_aggregated_for_concentration()
    {
        var engine = PaperTradingEngine.CreateContest();
        var first = new InstrumentId("SE-A", "A", "XSTO", "issuer-1");
        var second = new InstrumentId("SE-B", "B", "XSTO", "issuer-1");
        engine.ApplyCorrection(TestData.Agent.Id, "seed", 0m, first, 75, 100m,
            TestData.Open, "verified seed");
        var decision = TestData.Decision(instrument: second, quantity: 20);
        var quote = TestData.Quote(instrument: second, adv: 1_000_000m);
        var error = Assert.Throws<TradingException>(() => engine.Submit(decision, quote,
            TestData.Session, TestData.Marks((first, 100m))));
        Assert.Equal("concentration", error.Code);
    }

    [Fact]
    public void Decision_and_lifecycle_idempotency_are_payload_bound()
    {
        var engine = PaperTradingEngine.CreateContest();
        var queued = engine.Submit(TestData.Decision(), null, null, TestData.Marks());
        var replay = engine.Submit(TestData.Decision(), null, null, TestData.Marks());
        Assert.Equal(queued.OrderId, replay.OrderId);
        var conflict = TestData.Decision() with { CanonicalRequestSha256 = new string('d', 64) };
        Assert.Equal("idempotency", Assert.Throws<TradingException>(() => engine.Submit(
            conflict, null, null, TestData.Marks())).Code);
    }

    [Fact]
    public void Future_or_unverified_evidence_is_rejected_before_order_creation()
    {
        var future = new VerifiedEvidence(new Uri("https://example.com"),
            TestData.Open.AddHours(2), TestData.Open, new string('a', 64), "future");
        var engine = PaperTradingEngine.CreateContest();
        Assert.Equal("evidence", Assert.Throws<TradingException>(() => engine.Submit(
            TestData.Decision(evidence: [future]), TestData.Quote(), TestData.Session,
            TestData.Marks())).Code);
        Assert.Empty(engine.Orders);
    }
}
