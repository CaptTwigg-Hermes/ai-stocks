using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiStocks.Core;

namespace AiStocks.Operations;

public sealed class OperationsException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record DailyAgentSnapshot(
    Guid AgentId,
    string ModelId,
    decimal NetValue,
    decimal DailyReturn,
    decimal TotalReturn,
    decimal Cash,
    string Holdings,
    string Trades,
    decimal Fees,
    int MissedRuns,
    string Rationale);

public sealed record RankedDailyAgent(int Rank, DailyAgentSnapshot Snapshot);
public sealed record DailyReport(string Key, string ContentHash, string Message, IReadOnlyList<RankedDailyAgent> Rows);

public sealed class DailyReportService
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public DailyReport Generate(DateOnly tradingDay, DateTimeOffset generatedAt, IReadOnlyList<DailyAgentSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var local = TimeZoneInfo.ConvertTime(generatedAt, Stockholm);
        if (DateOnly.FromDateTime(local.DateTime) != tradingDay || local.Hour != 18 || local.Minute != 30 || local.Second != 0)
            throw new OperationsException("Daily report must be generated at exactly 18:30 Europe/Stockholm.");

        var expected = ContestContract.Agents.ToDictionary(x => x.Id);
        if (snapshots.Count != expected.Count || snapshots.Select(x => x.AgentId).Distinct().Count() != expected.Count)
            throw new OperationsException("Daily report requires exactly the four isolated agents.");

        foreach (var row in snapshots)
        {
            if (!expected.TryGetValue(row.AgentId, out var agent) || !StringComparer.Ordinal.Equals(agent.ModelId, row.ModelId))
                throw new OperationsException("Daily report contains an agent/model identity mismatch.");
            ValidateRow(row);
        }

        var ranked = snapshots
            .OrderByDescending(x => x.NetValue)
            .ThenBy(x => x.ModelId, StringComparer.Ordinal)
            .Select((snapshot, index) => new RankedDailyAgent(index + 1, snapshot))
            .ToArray();
        var lines = new List<string> { $"AI Stocks — {tradingDay:yyyy-MM-dd} — 18:30 Stockholm" };
        lines.AddRange(ranked.Select(row => FormatRow(row)));
        var message = string.Join('\n', lines);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
        return new DailyReport($"daily:{tradingDay:yyyy-MM-dd}", hash, message, ranked);
    }

    private static void ValidateRow(DailyAgentSnapshot row)
    {
        if (row.NetValue < 0 || row.Cash < 0 || row.Fees < 0 || row.MissedRuns < 0 ||
            string.IsNullOrWhiteSpace(row.Holdings) || string.IsNullOrWhiteSpace(row.Trades) ||
            string.IsNullOrWhiteSpace(row.Rationale))
            throw new OperationsException("Daily report snapshot is incomplete or invalid.");
    }

    private static string FormatRow(RankedDailyAgent row)
    {
        var value = row.Snapshot;
        return string.Create(CultureInfo.InvariantCulture,
            $"{row.Rank}. {value.ModelId}: {value.NetValue:F2} SEK; daily {value.DailyReturn:F2}%; total {value.TotalReturn:F2}%; cash {value.Cash:F2}; holdings {value.Holdings}; trades {value.Trades}; fees {value.Fees:F2}; missed {value.MissedRuns}; rationale {value.Rationale}");
    }
}

public enum ImmediateAlertKind
{
    SystemPause,
    RunWideInvalidMarketData,
    DatabaseOrBackupFailure,
    MultiModelAuthenticationOutage,
    AccountingInvariantViolation
}

public sealed record ImmediateAlert(ImmediateAlertKind Kind, string Detail, string IdempotencyKey)
{
    public static ImmediateAlert Create(ImmediateAlertKind kind, string detail, string idempotencyKey)
    {
        if (!Enum.IsDefined(kind))
            throw new OperationsException("Unsupported immediate alert kind.");
        if (string.IsNullOrWhiteSpace(detail) || string.IsNullOrWhiteSpace(idempotencyKey))
            throw new OperationsException("Alert detail and idempotency key are required.");
        return new ImmediateAlert(kind, detail.Trim(), idempotencyKey.Trim());
    }
}

public enum DeliveryStatus { Succeeded, Failed }

public sealed record DeliveryAudit(
    string Key,
    string ContentHash,
    DeliveryStatus Status,
    string? Receipt,
    string? Error,
    DateTimeOffset AttemptedAt);

public enum ReservationState { Acquired, AlreadyCompleted, Conflict }

public sealed record DeliveryReservation(ReservationState State, DeliveryAudit? Existing)
{
    public static DeliveryReservation Acquired() => new(ReservationState.Acquired, null);
    public static DeliveryReservation AlreadyCompleted(DeliveryAudit audit) => new(ReservationState.AlreadyCompleted, audit);
    public static DeliveryReservation Conflict() => new(ReservationState.Conflict, null);
}

public interface IDeliveryAuditPort
{
    Task<DeliveryReservation> ReserveAsync(string key, string contentHash, CancellationToken cancellationToken);
    Task RecordAsync(DeliveryAudit audit, CancellationToken cancellationToken);
}

public interface IDiscordPort
{
    Task<string> SendAsync(string message, CancellationToken cancellationToken);
}

public sealed class AuditedDiscordDelivery(IDeliveryAuditPort auditPort, IDiscordPort discordPort, IClock clock)
{
    public async Task<DeliveryAudit> DeliverAsync(
        string key,
        string contentHash,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(contentHash) || string.IsNullOrWhiteSpace(message))
            throw new OperationsException("Delivery key, content hash, and message are required.");

        var reservation = await auditPort.ReserveAsync(key, contentHash, cancellationToken).ConfigureAwait(false);
        if (reservation.State == ReservationState.Conflict)
            throw new OperationsException("Delivery idempotency conflict.");
        if (reservation.State == ReservationState.AlreadyCompleted)
            return reservation.Existing!;

        var attemptedAt = clock.UtcNow;
        try
        {
            var receipt = await discordPort.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var success = new DeliveryAudit(key, contentHash, DeliveryStatus.Succeeded, receipt, null, attemptedAt);
            await auditPort.RecordAsync(success, cancellationToken).ConfigureAwait(false);
            return success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = new DeliveryAudit(key, contentHash, DeliveryStatus.Failed, null,
                $"{exception.GetType().Name}: {exception.Message}", attemptedAt);
            await auditPort.RecordAsync(failure, cancellationToken).ConfigureAwait(false);
            throw new OperationsException("Discord delivery failed.", exception);
        }
    }
}

public enum OperationsCommand { Preflight, Migrate, Bootstrap }

public static class OperationsCommandParser
{
    public static OperationsCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1)
            throw new OperationsException("Specify exactly one command: preflight, migrate, or bootstrap.");
        return arguments[0] switch
        {
            "preflight" => OperationsCommand.Preflight,
            "migrate" => OperationsCommand.Migrate,
            "bootstrap" => OperationsCommand.Bootstrap,
            _ => throw new OperationsException("Unknown operations command.")
        };
    }
}

public sealed record InitialAgentState(Guid AgentId, string ModelId, decimal Cash, DateTimeOffset InitializedAt);

public interface IContestBootstrapPort
{
    Task InitializeAtomicallyAsync(IReadOnlyList<InitialAgentState> agents, CancellationToken cancellationToken);
}

public sealed class ContestBootstrapper(IContestBootstrapPort port, IClock clock)
{
    public Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var at = clock.UtcNow;
        var agents = ContestContract.Agents
            .Select(x => new InitialAgentState(x.Id, x.ModelId, ContestContract.InitialCash, at))
            .ToArray();
        return port.InitializeAtomicallyAsync(agents, cancellationToken);
    }
}

public interface IReadinessPort
{
    Task<bool> DatabaseAsync(CancellationToken cancellationToken);
    Task<bool> MigrationsAsync(CancellationToken cancellationToken);
    Task<bool> MarketDataAsync(CancellationToken cancellationToken);
    Task<bool> FourAgentsAsync(CancellationToken cancellationToken);
}

public sealed record ReadinessResult(bool Ready, IReadOnlyList<string> Failures);

public sealed class OperationsHealth(IReadinessPort port)
{
    public bool IsLive() => true;

    public async Task<ReadinessResult> ReadinessAsync(CancellationToken cancellationToken)
    {
        var database = port.DatabaseAsync(cancellationToken);
        var migrations = port.MigrationsAsync(cancellationToken);
        var marketData = port.MarketDataAsync(cancellationToken);
        var agents = port.FourAgentsAsync(cancellationToken);
        await Task.WhenAll(database, migrations, marketData, agents).ConfigureAwait(false);
        var failures = new List<string>();
        if (!await database.ConfigureAwait(false)) failures.Add("database");
        if (!await migrations.ConfigureAwait(false)) failures.Add("migrations");
        if (!await marketData.ConfigureAwait(false)) failures.Add("market-data");
        if (!await agents.ConfigureAwait(false)) failures.Add("four-agents");
        return new ReadinessResult(failures.Count == 0, failures);
    }
}

public sealed record BackupRequest(Uri DatabaseUrl, string EncryptedOutputPath, string PassphraseFile);
public sealed record RestoreRequest(Uri DatabaseUrl, string EncryptedBackupPath, string PassphraseFile);
public sealed record ValidatedBackupCommand(Uri DatabaseUrl, string OutputPath, string PassphraseFile, bool Encrypted);
public sealed record ValidatedRestoreCommand(Uri DatabaseUrl, string BackupPath, string PassphraseFile, string DatabaseName);

public static class BackupRestoreCommands
{
    public static ValidatedBackupCommand ValidateBackup(BackupRequest request)
    {
        ValidatePostgres(request.DatabaseUrl);
        ValidateEncryptedPath(request.EncryptedOutputPath, "backup output");
        ValidatePassphrase(request.PassphraseFile);
        return new(request.DatabaseUrl, request.EncryptedOutputPath, request.PassphraseFile, true);
    }

    public static ValidatedRestoreCommand ValidateRestore(RestoreRequest request)
    {
        ValidatePostgres(request.DatabaseUrl);
        ValidateEncryptedPath(request.EncryptedBackupPath, "restore input");
        ValidatePassphrase(request.PassphraseFile);
        var databaseName = request.DatabaseUrl.AbsolutePath.Trim('/');
        if (!StringComparer.Ordinal.Equals(databaseName, "ai_stocks_test"))
            throw new OperationsException("Restore target must be exactly the dedicated ai_stocks_test database; production restore is forbidden.");
        return new(request.DatabaseUrl, request.EncryptedBackupPath, request.PassphraseFile, databaseName);
    }

    private static void ValidatePostgres(Uri databaseUrl)
    {
        if (!databaseUrl.IsAbsoluteUri ||
            !(StringComparer.OrdinalIgnoreCase.Equals(databaseUrl.Scheme, "postgresql") ||
              StringComparer.OrdinalIgnoreCase.Equals(databaseUrl.Scheme, "postgres")) ||
            string.IsNullOrWhiteSpace(databaseUrl.Host))
            throw new OperationsException("A valid PostgreSQL database URL is required.");
    }

    private static void ValidateEncryptedPath(string path, string label)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            throw new OperationsException($"{label} must be an absolute encrypted .enc path.");
    }

    private static void ValidatePassphrase(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new OperationsException("Passphrase file path must be absolute.");
    }
}
