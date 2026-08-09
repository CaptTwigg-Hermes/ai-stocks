using Npgsql;

namespace AiStocks.Persistence;

public sealed class MigrationExecutionException(string stage, string? sqlState, Exception innerException)
    : InvalidOperationException($"Migration failed at {stage}.", innerException)
{
    public string Stage { get; } = stage;
    public string? SqlState { get; } = sqlState;
}

public sealed class PostgresMigrationRunner(NpgsqlDataSource dataSource)
{
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var stage = "connection";
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            stage = "migration-lock";
            await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(741025001)", connection, transaction))
                await migrationLock.ExecuteNonQueryAsync(cancellationToken);

            stage = "schema-guard";
            await RefuseUnmanagedApplicationSchemaAsync(connection, transaction, cancellationToken);

            stage = "schema-migrations-bootstrap";
            await using (var bootstrap = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS schema_migrations (id text PRIMARY KEY, sha256 char(64) NOT NULL, applied_at timestamptz NOT NULL DEFAULT clock_timestamp())",
                connection, transaction))
                await bootstrap.ExecuteNonQueryAsync(cancellationToken);

            foreach (var migration in MigrationCatalog.All)
            {
                stage = migration.Id;
                await using var check = new NpgsqlCommand("SELECT sha256 FROM schema_migrations WHERE id = $1", connection, transaction);
                check.Parameters.AddWithValue(migration.Id);
                var existing = (string?)await check.ExecuteScalarAsync(cancellationToken);
                if (existing is not null)
                {
                    if (!StringComparer.Ordinal.Equals(existing.Trim(), migration.Sha256))
                        throw new InvalidOperationException($"Applied migration {migration.Id} has an unexpected checksum.");
                    continue;
                }

                await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
                    await command.ExecuteNonQueryAsync(cancellationToken);
                await using var record = new NpgsqlCommand("INSERT INTO schema_migrations(id, sha256) VALUES ($1, $2)", connection, transaction);
                record.Parameters.AddWithValue(migration.Id);
                record.Parameters.AddWithValue(migration.Sha256);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            stage = "commit";
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            throw new MigrationExecutionException(stage, (exception as PostgresException)?.SqlState, exception);
        }
    }

    private static async Task RefuseUnmanagedApplicationSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT to_regclass('public.schema_migrations') IS NULL
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_class c
                   JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                   WHERE n.nspname = 'public'
                     AND c.relkind IN ('r','p')
                     AND c.relname = ANY (ARRAY[
                         'agents','contest_state','instruments','market_observations',
                         'orders','fills','positions','account_balances','final_rankings'
                     ]::text[])
               )
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is true)
            throw new InvalidOperationException(
                "The target contains unmanaged AI Stocks tables. Use a clean database; in-place legacy migration is refused.");
    }
}