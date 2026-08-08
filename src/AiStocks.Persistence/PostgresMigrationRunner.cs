using Npgsql;

namespace AiStocks.Persistence;

public sealed class PostgresMigrationRunner(NpgsqlDataSource dataSource)
{
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(741025001)", connection, transaction))
            await migrationLock.ExecuteNonQueryAsync(cancellationToken);

        await using (var bootstrap = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (id text PRIMARY KEY, sha256 char(64) NOT NULL, applied_at timestamptz NOT NULL DEFAULT clock_timestamp())",
            connection, transaction))
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);

        foreach (var migration in MigrationCatalog.All)
        {
            await using var check = new NpgsqlCommand("SELECT sha256 FROM schema_migrations WHERE id = $1", connection, transaction);
            check.Parameters.AddWithValue(migration.Id);
            var existing = (string?)await check.ExecuteScalarAsync(cancellationToken);
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing.Trim(), migration.Sha256))
                    throw new InvalidOperationException($"Applied migration {migration.Id} has checksum {existing.Trim()}, expected {migration.Sha256}.");
                continue;
            }

            await using (var command = new NpgsqlCommand(migration.Sql, connection, transaction))
                await command.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand("INSERT INTO schema_migrations(id, sha256) VALUES ($1, $2)", connection, transaction);
            record.Parameters.AddWithValue(migration.Id);
            record.Parameters.AddWithValue(migration.Sha256);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}