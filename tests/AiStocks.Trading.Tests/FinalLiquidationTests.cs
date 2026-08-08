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
            TestData.Close, "verified correction", TestData.Authorization("bonus"));
        var finalSession = new TradingSession("XSTO-2026-12-30",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var close = TestData.Quote(price: 110m, tradedAt: finalSession.CloseAt, officialPats: true) with
        {
            SessionId = finalSession.Id,
            RetrievedAt = finalSession.CloseAt.AddMinutes(15)
        };

        var standings = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>
        { [TestData.Volvo] = close }, finalSession, finalSession.CloseAt.AddMinutes(20), "XSTO-2026-12-30-final");
        var replay = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>
        { [TestData.Volvo] = close }, finalSession, finalSession.CloseAt.AddMinutes(20), "XSTO-2026-12-30-final");

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
    public void Final_liquidation_rejects_non_pats_close_and_unpinned_session_identity()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.ApplyCorrection(TestData.Agent.Id, "position", 0m, TestData.Volvo, 1, 100m,
            TestData.Open, "authorized", TestData.Authorization());
        var finalSession = new TradingSession("fabricated",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var close = TestData.Quote(tradedAt: finalSession.CloseAt) with
        {
            SessionId = finalSession.Id,
            RetrievedAt = finalSession.CloseAt.AddMinutes(15),
            IsOfficialPats = false
        };

        Assert.Equal("final-session", Assert.Throws<TradingException>(() => engine.FinalLiquidation(
            new Dictionary<InstrumentId, VerifiedMarketObservation> { [TestData.Volvo] = close },
            finalSession, finalSession.CloseAt.AddMinutes(20), "XSTO-2026-12-30-final")).Code);
    }

    [Fact]
    public void Frozen_delisting_is_ranked_at_zero_without_substitute_quote()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        engine.ApplyDelisting(TestData.Agent.Id, TestData.Volvo, null, TestData.Close, "delist",
            TestData.Authorization("delist"));
        var finalSession = new TradingSession("XSTO-2026-12-30",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var standings = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>(),
            finalSession, finalSession.CloseAt.AddMinutes(20), "XSTO-2026-12-30-final");
        Assert.Equal(28_998.98m,
            standings.Single(item => item.AgentId == TestData.Agent.Id).NetLiquidationValue);
    }

    [Fact]
    public void Mini_fee_applies_to_hypothetical_final_sale_and_transition_is_permanent()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.Submit(TestData.Decision(quantity: 10), TestData.Quote(), TestData.Session, TestData.Marks());
        engine.ApplyCorrection(TestData.Agent.Id, "capital", 20_001.02m, null, 0, 0m,
            TestData.Close, "verified correction", TestData.Authorization("capital"));
        var finalSession = new TradingSession("XSTO-2026-12-30",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var close = TestData.Quote(price: 100m, tradedAt: finalSession.CloseAt, officialPats: true) with
        {
            SessionId = finalSession.Id,
            RetrievedAt = finalSession.CloseAt.AddMinutes(15)
        };
        var standings = engine.FinalLiquidation(new Dictionary<InstrumentId, VerifiedMarketObservation>
        { [TestData.Volvo] = close }, finalSession, finalSession.CloseAt.AddMinutes(20), "XSTO-2026-12-30-final");
        var agent = standings.Single(item => item.AgentId == TestData.Agent.Id);
        var liquidation = Assert.Single(agent.Liquidations);
        Assert.Equal(2.50m, liquidation.Fee);
        Assert.Equal(FeeTier.Mini, engine.Portfolio(TestData.Agent.Id).FeeTier);
        Assert.Equal(49_996.48m, agent.NetLiquidationValue);
    }

    [Fact]
    public void Final_liquidation_recomputes_fee_tier_before_trade_501()
    {
        var engine = PaperTradingEngine.CreateContest();
        engine.ApplyCorrection(TestData.Agent.Id, "first-position", 0m, TestData.Volvo, 1, 100m,
            TestData.Open, "verified position", TestData.Authorization("first"), 499);
        engine.ApplyCorrection(TestData.Agent.Id, "second-position", 0m, TestData.Atlas, 1, 100m,
            TestData.Open, "verified position", TestData.Authorization("second"));
        var finalSession = new TradingSession("XSTO-2026-12-30",
            new DateTimeOffset(2026, 12, 30, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 30, 16, 30, 0, TimeSpan.Zero));
        var close = TestData.Quote(price: 100m, tradedAt: finalSession.CloseAt, officialPats: true) with
        {
            SessionId = finalSession.Id,
            RetrievedAt = finalSession.CloseAt.AddMinutes(15)
        };
        var atlasClose = close with { Instrument = TestData.Atlas };

        var standing = engine.FinalLiquidation(
            new Dictionary<InstrumentId, VerifiedMarketObservation>
            {
                [TestData.Volvo] = close,
                [TestData.Atlas] = atlasClose
            }, finalSession, finalSession.CloseAt.AddMinutes(20), "XSTO-2026-12-30-final")
            .Single(x => x.AgentId == TestData.Agent.Id);

        Assert.Equal([0m, 1m], standing.Liquidations.Select(x => x.Fee).Order().ToArray());
        Assert.Equal(FeeTier.Mini, engine.Portfolio(TestData.Agent.Id).FeeTier);
    }
}
