using AiStocks.Core;

namespace AiStocks.Trading.Tests;

internal static class TestData
{
    internal static readonly AgentDefinition Agent = ContestContract.Agents[0];
    internal static readonly AgentDefinition OtherAgent = ContestContract.Agents[1];
    internal static readonly InstrumentId Volvo = new("SE0000115446", "VOLV-B", "XSTO");
    internal static readonly InstrumentId Atlas = new("SE0017486889", "ATCO-A", "XSTO");
    internal static readonly DateTimeOffset Open = new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Close = new(2026, 8, 6, 15, 30, 0, TimeSpan.Zero);
    internal static readonly TradingSession Session = new("XSTO-2026-08-06", Open, Close);

    internal static OrderDecision Decision(
        string id = "d1", DecisionAction action = DecisionAction.Buy, int quantity = 1,
        InstrumentId? instrument = null, Guid? agentId = null, string? modelId = null,
        DateTimeOffset? at = null, decimal observedPrice = 100m,
        decimal confidence = 0.7m, IReadOnlyList<VerifiedEvidence>? evidence = null) =>
        new(id, agentId ?? Agent.Id, modelId ?? Agent.ModelId, action,
            instrument ?? Volvo, quantity, at ?? Open.AddHours(1), observedPrice,
            "value", "earnings", ["market risk"], confidence,
            evidence ?? [new VerifiedEvidence(new Uri("https://example.com/research"),
                (at ?? Open.AddHours(1)).AddHours(-1), (at ?? Open.AddHours(1)).AddMinutes(-30),
                new string('a', 64), "public evidence")], new string('b', 64));

    internal static VerifiedMarketObservation Quote(
        InstrumentId? instrument = null, decimal price = 100m, decimal adv = 20_000_000m,
        int sessions = 20, DateTimeOffset? tradedAt = null, decimal? bid = null,
        decimal? ask = null, bool warning = false, bool suspended = false,
        long quantity = 1_000_000) =>
        new(instrument ?? Volvo, price, bid, ask, quantity, adv, sessions,
            tradedAt ?? Open.AddHours(1), (tradedAt ?? Open.AddHours(1)).AddMinutes(15),
            Session.Id, new string('c', 64), warning, suspended);

    internal static IReadOnlyDictionary<InstrumentId, decimal> Marks(params (InstrumentId Instrument, decimal Price)[] marks) =>
        marks.ToDictionary(mark => mark.Instrument, mark => mark.Price);
}
