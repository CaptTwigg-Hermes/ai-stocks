using AiStocks.Core;

namespace AiStocks.Trading.Tests;

public sealed class FinalLiquidationTests
{
    [Fact]
    public void Final_liquidation_uses_official_close_adverse_slippage_and_shared_ranks()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        engine.ApplyCorrection(ContestContract.Agents[1].Id, "bonus", 100m, null, 0, 0m,
            TestData.Close, "verified correction");
        var finalSession = new TradingSession("XSTO-2026-12-30",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var close = TestData.Quote(price: 110m, tradedAt: finalSession.CloseAt) with
        {
            SessionId = finalSession.Id,
            RetrievedAt = finalSession.CloseAt.AddMinutes(15)
        };

        var standings = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>
        { [TestData.Volvo] = close }, finalSession, finalSession.CloseAt.AddMinutes(15), "final-2026");
        var replay = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>
        { [TestData.Volvo] = close }, finalSession, finalSession.CloseAt.AddMinutes(15), "final-2026");

        Assert.Equal(standings, replay);
        Assert.Equal(ContestStatus.Finished, engine.Status);
        Assert.Equal(ContestContract.Agents[1].Id, standings[0].AgentId);
        Assert.Equal(30_100m, standings[0].NetLiquidationValue);
        Assert.Equal(TestData.Agent.Id, standings[1].AgentId);
        Assert.Equal(30_097.86m, standings[1].NetLiquidationValue);
        Assert.Equal([1, 2, 3, 3], standings.Select(item => item.Rank).Order().ToArray());
        Assert.All(ContestContract.Agents, agent => Assert.Empty(engine.Portfolio(agent.Id).Positions));
    }

    [Fact]
    public void Final_liquidation_fails_closed_without_unambiguous_official_final_close()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 1), TestData.Quote(), TestData.Session, TestData.Marks());
        var wrongSession = TestData.Session;
        var error = Assert.Throws<TradingException>(() => engine.FinalLiquidation(
            new Dictionary<InstrumentId, VerifiedMarketObservation> { [TestData.Volvo] = TestData.Quote() },
            wrongSession, TestData.Close.AddMinutes(15), "too-early"));
        Assert.Equal("final-date", error.Code);
        Assert.Equal(ContestStatus.Running, engine.Status);
    }

    [Fact]
    public void Frozen_delisting_is_ranked_at_zero_without_substitute_quote()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        engine.ApplyDelisting(TestData.Agent.Id, TestData.Volvo, null, TestData.Close, "delist");
        var finalSession = new TradingSession("final",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var standings = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>(),
            finalSession, finalSession.CloseAt, "final-frozen");
        Assert.Equal(28_998.98m,
            standings.Single(item => item.AgentId == TestData.Agent.Id).NetLiquidationValue);
    }

    [Fact]
    public void Mini_fee_applies_to_hypothetical_final_sale_and_transition_is_permanent()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        engine.ApplyCorrection(TestData.Agent.Id, "capital", 20_001.02m, null, 0, 0m,
            TestData.Close, "verified correction");
        var finalSession = new TradingSession("final",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var close = TestData.Quote(price: 100m, tradedAt: finalSession.CloseAt) with
        {
            SessionId = finalSession.Id,
            RetrievedAt = finalSession.CloseAt.AddMinutes(15)
        };
        var standings = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>
        { [TestData.Volvo] = close }, finalSession, close.RetrievedAt, "final-mini");
        var agent = standings.Single(item => item.AgentId == TestData.Agent.Id);
        var liquidation = Assert.Single(agent.Liquidations);
        Assert.Equal(2.50m, liquidation.Fee);
        Assert.Equal(FeeTier.Mini, engine.Portfolio(TestData.Agent.Id).FeeTier);
        Assert.Equal(49_996.48m, agent.NetLiquidationValue);
    }
}
