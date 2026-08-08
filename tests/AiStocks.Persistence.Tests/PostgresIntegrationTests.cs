using System.Text.Json;
using AiStocks.Persistence;
using Npgsql;

namespace AiStocks.Persistence.Tests;

public sealed class PostgresIntegrationTests
{
    private static string? ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
            if (string.IsNullOrWhiteSpace(value)) return null;
            var database = new NpgsqlConnectionStringBuilder(value).Database ?? string.Empty;
            if (!database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AISTOCKS_TEST_DATABASE_URL database name must contain 'test'.");
            return value;
        }
    }

    [Fact]
    public async Task RealPostgresEnforcesMigrationReplayAuditAndIdempotency()
    {
        if (ConnectionString is not { } connectionString) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(4L, await ScalarLong(connection, "SELECT count(*) FROM agents"));
        Assert.Equal(120000m, await ScalarDecimal(connection, "SELECT sum(cash) FROM account_balances"));
        Assert.Equal(1L, await ScalarLong(connection, "SELECT count(*) FROM schema_migrations WHERE length(sha256)=64"));

        var immutable = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "UPDATE ledger_events SET occurred_at=clock_timestamp() WHERE event_type='INITIAL_FUNDING'"));
        Assert.Equal(PostgresErrorCodes.RaiseException, immutable.SqlState);

        var truncate = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, "TRUNCATE ledger_events CASCADE"));
        Assert.Equal(PostgresErrorCodes.RaiseException, truncate.SqlState);

        var instrument = Guid.NewGuid();
        await Execute(connection,
            "UPDATE contest_state SET status='RUNNING',started_at=clock_timestamp() WHERE singleton");
        await Execute(connection, """
            INSERT INTO instruments(id,isin,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
            VALUES($1,'SE0000000001','book-1','XSTO','TEST','ESVUFR','2026-01-01',
                   '{"source":"test"}',canonical_jsonb_sha256('{"source":"test"}'))
            """, instrument);

        using var request = JsonDocument.Parse("{\"decision\":\"same\",\"quantity\":1}");
        var json = CanonicalJson.Serialize(request.RootElement);
        var hash = CanonicalJson.Sha256(request.RootElement);
        var order = Guid.NewGuid();
        var first = await ScalarGuid(connection, """
            SELECT submit_order($1,$2,'same-decision','same-key','BUY',$3,1,clock_timestamp(),100,
                                $4::jsonb,$5::sha256_hex)
            """, order, Guid.Parse("11111111-1111-1111-1111-111111111111"), instrument, json, hash);
        var replay = await ScalarGuid(connection, """
            SELECT submit_order($1,$2,'same-decision','same-key','BUY',$3,1,clock_timestamp(),100,
                                $4::jsonb,$5::sha256_hex)
            """, Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), instrument, json, hash);
        Assert.Equal(first, replay);

        using var conflictDocument = JsonDocument.Parse("{\"decision\":\"different\",\"quantity\":1}");
        var conflictJson = CanonicalJson.Serialize(conflictDocument.RootElement);
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => ScalarGuid(connection, """
            SELECT submit_order($1,$2,'other-decision','same-key','BUY',$3,1,clock_timestamp(),100,
                                $4::jsonb,$5::sha256_hex)
            """, Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), instrument,
            conflictJson, CanonicalJson.Sha256(conflictDocument.RootElement)));
        Assert.Contains("conflicting canonical hash", conflict.MessageText);
    }

    [Fact]
    public async Task RealPostgresSerializesConcurrentOverspendAndOversell()
    {
        if (ConnectionString is not { } connectionString) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);
        var instrument = Guid.NewGuid();
        await using (var setup = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await Execute(setup, """
                INSERT INTO instruments(id,isin,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
                VALUES($1,'SE0000000002','book-2','XSTO','RACE','ESVUFR','2026-01-01',
                       '{"source":"race"}',canonical_jsonb_sha256('{"source":"race"}'));
                """, instrument);
            await Execute(setup, """
                INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,cash_delta,quantity_delta,
                                          occurred_at,event_json,event_hash)
                VALUES(gen_random_uuid(),'22222222-2222-2222-2222-222222222222','CORRECTION',$1,0,1,
                       clock_timestamp(),'{"correction":"seed-share"}',
                       canonical_jsonb_sha256('{"correction":"seed-share"}'))
                """, instrument);
        }

        var spendResults = await Race(dataSource, Enumerable.Range(0, 2).Select(index => (
            """
            INSERT INTO ledger_events(id,agent_id,event_type,cash_delta,quantity_delta,occurred_at,event_json,event_hash)
               VALUES(gen_random_uuid(),'33333333-3333-3333-3333-333333333333','CORRECTION',-20000,0,
                      clock_timestamp(),jsonb_build_object('race',$1),canonical_jsonb_sha256(jsonb_build_object('race',$1)))
            """,
            (object)index)));
        Assert.Equal(1, spendResults.Count(success => success));

        var sellResults = await Race(dataSource, Enumerable.Range(0, 2).Select(index => (
            """
            INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,cash_delta,quantity_delta,occurred_at,event_json,event_hash)
               VALUES(gen_random_uuid(),'22222222-2222-2222-2222-222222222222','CORRECTION',$1,0,-1,
                      clock_timestamp(),jsonb_build_object('sell_race',$2),canonical_jsonb_sha256(jsonb_build_object('sell_race',$2)))
            """,
            (object)instrument, (object?)index)));
        Assert.Equal(1, sellResults.Count(success => success));

        await using var verify = await dataSource.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(10000m, await ScalarDecimal(verify,
            "SELECT cash FROM account_balances WHERE agent_id='33333333-3333-3333-3333-333333333333'"));
        Assert.Equal(0L, await ScalarLong(verify,
            "SELECT quantity FROM positions WHERE agent_id='22222222-2222-2222-2222-222222222222' AND instrument_id=$1", instrument));
    }

    private static async Task<bool[]> Race(NpgsqlDataSource source, IEnumerable<(string Sql, object P1)> commands)
        => await Race(source, commands.Select(x => (x.Sql, x.P1, (object?)null)));

    private static async Task<bool[]> Race(NpgsqlDataSource source, IEnumerable<(string Sql, object P1, object? P2)> commands)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        var array = commands.ToArray();
        var tasks = array.Select(async item =>
        {
            await using var connection = await source.OpenConnectionAsync(CancellationToken.None);
            if (Interlocked.Increment(ref ready) == array.Length) gate.SetResult();
            await gate.Task;
            try
            {
                if (item.P2 is null) await Execute(connection, item.Sql, item.P1);
                else await Execute(connection, item.Sql, item.P1, item.P2);
                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.RaiseException || exception.SqlState == PostgresErrorCodes.CheckViolation)
            {
                Console.WriteLine($"Expected race rejection: {exception.SqlState} {exception.MessageText}");
                return false;
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(values[i]);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> ScalarLong(NpgsqlConnection connection, string sql, params object[] values)
        => Convert.ToInt64(await Scalar(connection, sql, values));
    private static async Task<decimal> ScalarDecimal(NpgsqlConnection connection, string sql, params object[] values)
        => Convert.ToDecimal(await Scalar(connection, sql, values));
    private static async Task<Guid> ScalarGuid(NpgsqlConnection connection, string sql, params object[] values)
        => (Guid)(await Scalar(connection, sql, values) ?? throw new InvalidOperationException("Expected UUID."));
    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue(values[i]);
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }
}
