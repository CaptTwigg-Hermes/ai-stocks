namespace AiStocks.Core;

public static class ContestContract
{
    public const string Currency = "SEK";
    public const decimal InitialCash = 30_000m;
    public const decimal MaximumIssuerWeight = 0.25m;
    public const decimal MaximumAdvParticipation = 0.01m;
    public const int RequiredHistorySessions = 20;
    public static readonly DateOnly FinalTradingDate = new(2026, 12, 30);

    public static IReadOnlyList<AgentDefinition> Agents { get; } =
    [
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "gpt-5.6-sol"),
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "claude-opus-4.8"),
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "claude-sonnet-5"),
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"), "gemini-3.1-pro-preview")
    ];
}

public sealed record AgentDefinition(Guid Id, string ModelId);
public sealed record InstrumentId(string Isin, string OrderBookId, string Mic);

public enum ContestStatus { Draft, Running, Paused, Finished }
public enum OrderSide { Buy, Sell }
public enum DecisionAction { Buy, Sell, Hold, CancelPending }
public enum FeeTier { Starter, Mini }
public enum RunStatus { Scheduled, Running, Succeeded, Missed, Failed }
public enum OrderStatus { Queued, Filled, Rejected, Cancelled, Replaced }

public sealed record VerifiedEvidence(
    Uri FinalUrl,
    DateTimeOffset PublishedAt,
    DateTimeOffset RetrievedAt,
    string ContentSha256,
    string ExactExcerpt);

public sealed record VerifiedMarketObservation(
    InstrumentId Instrument,
    decimal Price,
    decimal? Bid,
    decimal? Ask,
    long Quantity,
    decimal AverageDailyValue20,
    int CompleteHistorySessions,
    DateTimeOffset TradedAt,
    DateTimeOffset RetrievedAt,
    string SessionId,
    string RawSha256,
    bool HasWarning,
    bool IsSuspended);

public sealed record OrderDecision(
    string DecisionId,
    Guid AgentId,
    string ExactModelId,
    DecisionAction Action,
    InstrumentId? Instrument,
    int Quantity,
    DateTimeOffset DecisionAt,
    decimal? ObservedPrice,
    string Reason,
    string Catalyst,
    IReadOnlyList<string> Risks,
    decimal Confidence,
    IReadOnlyList<VerifiedEvidence> Evidence,
    string CanonicalRequestSha256);

public sealed record Position(InstrumentId Instrument, int Quantity, decimal AverageCost);
public sealed record PortfolioSnapshot(
    Guid AgentId,
    decimal Cash,
    IReadOnlyList<Position> Positions,
    int CompletedTradeCount,
    FeeTier FeeTier,
    DateTimeOffset AsOf);

public sealed record OrderOutcome(
    Guid OrderId,
    OrderStatus Status,
    string Code,
    string Message,
    decimal? FillPrice = null,
    decimal? Fee = null,
    DateTimeOffset? FilledAt = null);

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IPaperTradingCommands
{
    Task<OrderOutcome> SubmitAsync(OrderDecision decision, CancellationToken cancellationToken);
    Task<OrderOutcome> CancelAsync(Guid agentId, Guid orderId, string reason, string idempotencyKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderOutcome>> ExecuteQueuedAsync(CancellationToken cancellationToken);
}
