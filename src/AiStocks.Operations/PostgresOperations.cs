using AiStocks.Core;
using AiStocks.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Operations;

public interface IOperationsPorts
{
    Task MigrateAsync(CancellationToken cancellationToken);
    Task BootstrapAsync(CancellationToken cancellationToken);
    Task<ReadinessResult> PreflightAsync(CancellationToken cancellationToken);
}

public static class OperationsApplication
{
    public static async Task<int> RunAsync(IReadOnlyList<string> arguments, IOperationsPorts ports, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            switch (OperationsCommandParser.Parse(arguments))
            {
                case OperationsCommand.Migrate:
                    await ports.MigrateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case OperationsCommand.Bootstrap:
                    await ports.BootstrapAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case OperationsCommand.Preflight:
                    var readiness = await ports.PreflightAsync(cancellationToken).ConfigureAwait(false);
                    if (!readiness.Ready)
                    {
                        await error.WriteLineAsync($"preflight failed: {string.Join(',', readiness.Failures)}").ConfigureAwait(false);
                        return 1;
                    }
                    break;
                default:
                    throw new OperationsException("Unsupported operations command.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationsException or RuntimeConfigurationException or NpgsqlException or InvalidOperationException)
        {
            await error.WriteLineAsync($"operations failed safely: {SafeCategory(exception)}").ConfigureAwait(false);
            return 2;
        }
    }

    private static string SafeCategory(Exception exception) => exception switch
    {
        MigrationExecutionException migration => migration.SqlState is null
            ? $"migration failed at {migration.Stage}"
            : $"migration failed at {migration.Stage} (SQLSTATE {migration.SqlState})",
        OperationsException => "invalid command or contest state",
        RuntimeConfigurationException => "invalid configuration",
        NpgsqlException => "database operation failed",
        _ => "runtime operation failed"
    };
}

public sealed class PostgresOperationsPorts(NpgsqlDataSource migrator, NpgsqlDataSource runtime) : IOperationsPorts
{
    public Task MigrateAsync(CancellationToken cancellationToken) => new PostgresMigrationRunner(migrator).ApplyAsync(cancellationToken);

    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await using var connection = await runtime.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(741025002)", connection, transaction))
            await advisory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)=4
               AND count(DISTINCT model_id)=4
               AND bool_and(initial_cash=30000)
               AND (SELECT count(*)=4 AND sum(cash)=120000 FROM account_balances)
               AND (SELECT count(*)=1 FROM prompts WHERE id='00000000-0000-0000-0000-000000000001')
            FROM agents
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new OperationsException("Bootstrap verification failed; migrations and exact initial state are required.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReadinessResult> PreflightAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        try
        {
            await using var connection = await runtime.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!await BooleanAsync(connection, "SELECT true", cancellationToken).ConfigureAwait(false)) failures.Add("database");
            if (!await BooleanAsync(connection,
                    "SELECT count(*)=(SELECT count(*) FROM schema_migrations) AND count(*)=$1 FROM schema_migrations",
                    cancellationToken, MigrationCatalog.All.Count).ConfigureAwait(false)) failures.Add("migrations");
            if (!await BooleanAsync(connection,
                    "SELECT count(*)=4 AND count(DISTINCT model_id)=4 AND bool_and(initial_cash=30000) FROM agents",
                    cancellationToken).ConfigureAwait(false)) failures.Add("four-agents");
            if (!await BooleanAsync(connection,
                    "SELECT count(DISTINCT session_id)>=20 FROM market_observations WHERE verified AND NOT warning AND NOT suspended AND complete_history_sessions>=20",
                    cancellationToken).ConfigureAwait(false)) failures.Add("market-data");
        }
        catch (PostgresException) { failures.Add("database"); }
        catch (NpgsqlException) { failures.Add("database"); }
        return new ReadinessResult(failures.Count == 0, failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static async Task<bool> BooleanAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, int? value = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (value is not null) command.Parameters.AddWithValue(value.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}

public sealed class PostgresDeliveryAuditPort(NpgsqlDataSource dataSource) : IDeliveryAuditPort
{
    public async Task<DeliveryReservation> ReserveAsync(string key, string contentHash, CancellationToken cancellationToken)
    {
        Validate(key, contentHash);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT outcome,lease_token FROM reserve_delivery($1,$2::sha256_hex,clock_timestamp(),interval '5 minutes')", connection);
            command.Parameters.AddWithValue(key);
            command.Parameters.AddWithValue(contentHash);
            await using var result = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await result.ReadAsync(cancellationToken).ConfigureAwait(false)) return DeliveryReservation.Conflict();
            var status = result.GetString(0);
            var leaseToken = result.IsDBNull(1) ? null : result.GetGuid(1).ToString("D");
            await result.DisposeAsync().ConfigureAwait(false);
            if (StringComparer.Ordinal.Equals(status, "ACQUIRED")) return DeliveryReservation.Acquired(leaseToken!);
            if (StringComparer.Ordinal.Equals(status, "BUSY")) return DeliveryReservation.Busy();
            if (StringComparer.Ordinal.Equals(status, "UNCERTAIN")) return DeliveryReservation.Uncertain();
            if (!StringComparer.Ordinal.Equals(status, "SUCCEEDED")) return DeliveryReservation.Conflict();
            await using var existing = new NpgsqlCommand("SELECT receipt,updated_at FROM delivery_reservations WHERE delivery_key=$1", connection);
            existing.Parameters.AddWithValue(key);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return DeliveryReservation.Conflict();
            return DeliveryReservation.AlreadyCompleted(new DeliveryAudit(key, contentHash, DeliveryStatus.Succeeded,
                reader.GetString(0), null, reader.GetFieldValue<DateTimeOffset>(1)));
        }
        catch (PostgresException exception) when (exception.MessageText.Contains("idempotency conflict", StringComparison.Ordinal))
        {
            return DeliveryReservation.Conflict();
        }
    }

    public async Task BeginSendAsync(string key, string contentHash, string leaseToken, CancellationToken cancellationToken)
    {
        Validate(key, contentHash);
        if (!Guid.TryParse(leaseToken, out var token)) throw new OperationsException("Delivery lease is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT begin_delivery_send($1,$2::sha256_hex,$3,clock_timestamp())", connection);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(contentHash);
        command.Parameters.AddWithValue(token);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAsync(DeliveryAudit audit, string leaseToken, CancellationToken cancellationToken)
    {
        Validate(audit.Key, audit.ContentHash);
        if (!Guid.TryParse(leaseToken, out var token)) throw new OperationsException("Delivery lease is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT record_delivery($1,$2,$3::sha256_hex,$4::delivery_status,$5,$6,$7,$8)", connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(audit.Key);
        command.Parameters.AddWithValue(audit.ContentHash);
        command.Parameters.AddWithValue(audit.Status == DeliveryStatus.Succeeded ? "SUCCEEDED" : "FAILED");
        AddNullable(command, audit.Receipt, NpgsqlDbType.Text);
        AddNullable(command, audit.Error, NpgsqlDbType.Text);
        command.Parameters.AddWithValue(audit.AttemptedAt);
        command.Parameters.AddWithValue(token);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(string key, string hash)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
            throw new OperationsException("Delivery identity is invalid.");
    }

    private static void AddNullable(NpgsqlCommand command, string? value, NpgsqlDbType type) =>
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value is null ? DBNull.Value : value });
}
