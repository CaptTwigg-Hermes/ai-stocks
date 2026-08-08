namespace AiStocks.Web;

/// <summary>Read and narrowly-scoped lifecycle boundary used by the private dashboard.</summary>
public interface IDashboardFacade
{
    Task<DashboardSnapshot> QueryAsync(CancellationToken cancellationToken);
    Task<ContestControlResult> ControlAsync(ContestControlCommand command, CancellationToken cancellationToken);
}

public enum ContestControlAction { Start, Pause, Resume, PreStartReset }

public sealed record ContestControlCommand(ContestControlAction Action, string ActorEmail, string IdempotencyKey);
public sealed record ContestControlResult(string Status, bool Paused);
public sealed record ValuePoint(DateTimeOffset At, decimal ValueSek);
public sealed record LeaderboardRow(string ModelId, int Rank, decimal ValueSek, decimal ReturnPercent, IReadOnlyList<ValuePoint> History);
public sealed record HoldingRow(string Isin, string Symbol, int Quantity, decimal PriceSek, decimal ValueSek);
public sealed record PortfolioRow(string ModelId, decimal CashSek, decimal ValueSek, IReadOnlyList<HoldingRow> Holdings);
public sealed record QueuedOrderRow(string Id, string ModelId, string Side, string Symbol, int Quantity, DateTimeOffset QueuedAt);
public sealed record EvidenceRow(string Id, string ModelId, string Symbol, string Catalyst, Uri SourceUrl, DateTimeOffset PublishedAt, DateTimeOffset DecisionAt);
public sealed record FeeRow(string Id, string ModelId, decimal AmountSek, string Tier, DateTimeOffset At);
public sealed record DividendRow(string Id, string ModelId, string Symbol, decimal AmountSek, DateTimeOffset PaidAt);
public sealed record FailureRow(string Id, string ModelId, string Code, string Message, DateTimeOffset At);
public sealed record AuditRow(string Id, string Actor, string Action, string Reason, DateTimeOffset At);
public sealed record DashboardSnapshot(
    string Status,
    bool Paused,
    DateTimeOffset AsOf,
    IReadOnlyList<LeaderboardRow> Leaderboard,
    IReadOnlyList<PortfolioRow> Portfolios,
    IReadOnlyList<QueuedOrderRow> QueuedOrders,
    IReadOnlyList<EvidenceRow> Evidence,
    IReadOnlyList<FeeRow> Fees,
    IReadOnlyList<DividendRow> Dividends,
    IReadOnlyList<FailureRow> Failures,
    IReadOnlyList<AuditRow> Audit);

public sealed class DashboardUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class ContestControlRejectedException(string message) : Exception(message);

/// <summary>Safe default: no synthetic state and no lifecycle mutation if persistence has not supplied an adapter.</summary>
public sealed class FailClosedDashboardFacade : IDashboardFacade
{
    public Task<DashboardSnapshot> QueryAsync(CancellationToken cancellationToken) =>
        Task.FromException<DashboardSnapshot>(new DashboardUnavailableException("Dashboard data source is not configured."));

    public Task<ContestControlResult> ControlAsync(ContestControlCommand command, CancellationToken cancellationToken) =>
        Task.FromException<ContestControlResult>(new ContestControlRejectedException("Contest command source is not configured."));
}
