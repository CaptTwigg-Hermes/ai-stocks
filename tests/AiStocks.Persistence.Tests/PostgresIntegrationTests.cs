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
        await EnsureTestDatabase(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetDatabase(dataSource);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(4L, await ScalarLong(connection, "SELECT count(*) FROM agents"));
        Assert.Equal(120000m, await ScalarDecimal(connection, "SELECT sum(cash) FROM account_balances"));
        Assert.Equal(MigrationCatalog.All.Count, await ScalarLong(connection,
            "SELECT count(*) FROM schema_migrations WHERE length(sha256)=64"));

        var immutable = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "UPDATE ledger_events SET occurred_at=clock_timestamp() WHERE event_type='INITIAL_FUNDING'"));
        Assert.Equal(PostgresErrorCodes.RaiseException, immutable.SqlState);

        var truncate = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, "TRUNCATE ledger_events CASCADE"));
        Assert.Equal(PostgresErrorCodes.RaiseException, truncate.SqlState);

        var instrument = Guid.NewGuid();
        await Execute(connection,
            "UPDATE contest_state SET status='RUNNING',started_at=clock_timestamp() WHERE singleton");
        await Execute(connection, """
            INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
            VALUES($1,'SE0000000001','TESTISSUER0000000001','book-1','XSTO','TEST','ESVUFR','2026-01-01',
                   '{"source":"test"}',canonical_jsonb_sha256('{"source":"test"}'))
            """, instrument);
        await Execute(connection, """
            INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at)
            VALUES('authority-session','2026-06-01','2026-06-01T08:00:00Z','2026-06-01T16:00:00Z')
            """);
        await Execute(connection, """
            INSERT INTO raw_market_reports VALUES(gen_random_uuid(),'authority-report','https://example.test/report',
              '2026-06-01T09:15:00Z','x',encode(digest('x','sha256'),'hex'),'{"fixture":true}',
              canonical_jsonb_sha256('{"fixture":true}'))
            """);
        await Execute(connection, """
            INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,
              average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
            SELECT gen_random_uuid(),$1,id,'2026-06-01T09:00:00Z','2026-06-01T09:15:00Z',99,10,
              1000000,20,'authority-session',false,false,false,true,'{"price":99}',canonical_jsonb_sha256('{"price":99}')
            FROM raw_market_reports WHERE report_name='authority-report'
            """, instrument);

        using var request = JsonDocument.Parse("{\"decision\":\"same\",\"quantity\":1}");
        var json = CanonicalJson.Serialize(request.RootElement);
        var hash = CanonicalJson.Sha256(request.RootElement);
        var order = Guid.NewGuid();
        var first = await ScalarGuid(connection, """
            SELECT submit_order($1,$2,'same-decision','same-key','BUY',$3,1,'2026-06-01T09:16:00Z',
                                $4::jsonb,$5::sha256_hex)
            """, order, Guid.Parse("11111111-1111-1111-1111-111111111111"), instrument, json, hash);
        var replay = await ScalarGuid(connection, """
            SELECT submit_order($1,$2,'same-decision','same-key','BUY',$3,1,'2026-06-01T09:16:00Z',
                                $4::jsonb,$5::sha256_hex)
            """, Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), instrument, json, hash);
        Assert.Equal(first, replay);

        using var conflictDocument = JsonDocument.Parse("{\"decision\":\"different\",\"quantity\":1}");
        var conflictJson = CanonicalJson.Serialize(conflictDocument.RootElement);
        var conflict = await Assert.ThrowsAsync<PostgresException>(() => ScalarGuid(connection, """
            SELECT submit_order($1,$2,'other-decision','same-key','BUY',$3,1,'2026-06-01T09:16:00Z',
                                $4::jsonb,$5::sha256_hex)
            """, Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), instrument,
            conflictJson, CanonicalJson.Sha256(conflictDocument.RootElement)));
        Assert.Contains("conflicting canonical hash", conflict.MessageText);
    }

    [Fact]
    public async Task RealPostgresSerializesConcurrentOverspendAndOversell()
    {
        if (ConnectionString is not { } connectionString) return;
        await EnsureTestDatabase(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetDatabase(dataSource);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);
        var instrument = Guid.NewGuid();
        await using (var setup = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            await Execute(setup, """
                INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
                VALUES($1,'SE0000000002','TESTISSUER0000000002','book-2','XSTO','RACE','ESVUFR','2026-01-01',
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

    [Fact]
    public async Task RealPostgresEnforcesFirstEligibleFillCostBasisCorporateActionsAndReplay()
    {
        if (ConnectionString is not { } connectionString) return;
        await EnsureTestDatabase(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetDatabase(dataSource);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var instrument = Guid.NewGuid();
        var report = Guid.NewGuid();
        var firstObservation = Guid.NewGuid();
        var laterObservation = Guid.NewGuid();
        var order = Guid.NewGuid();
        await Execute(connection, "UPDATE contest_state SET status='RUNNING',started_at='2026-06-01T08:00:00Z' WHERE singleton");
        await Execute(connection, """
            INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
            VALUES($1,'SE0000000010','SHAREDISSUER00000001','book-10','XSTO','BASIS','ESVUFR','2026-01-01',
              '{"instrument":10}',canonical_jsonb_sha256('{"instrument":10}'))
            """, instrument);
        await Execute(connection, """
            INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at)
            SELECT 's'||n, date '2026-06-01'+n, (date '2026-06-01'+n)+time '08:00',
                   (date '2026-06-01'+n)+time '16:00' FROM generate_series(0,20) n
            """);
        await Execute(connection, """
            INSERT INTO instrument_session_stats(instrument_id,session_id,traded_value,complete)
            SELECT $1,'s'||n,1000000,true FROM generate_series(0,19) n
            """, instrument);
        await Execute(connection, """
            INSERT INTO raw_market_reports VALUES($1,'accounting-fixture','https://example.test/report',
              '2026-06-21T10:20:00Z','x',encode(digest('x','sha256'),'hex'),'{"fixture":true}',
              canonical_jsonb_sha256('{"fixture":true}'))
            """, report);
        await Execute(connection, """
            INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,
              bid,ask,average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
            VALUES
              (gen_random_uuid(),$2,$3,'2026-06-21T09:40:00Z','2026-06-21T09:55:00Z',99,10,99,99,1000000,20,'s20',false,false,false,true,
               '{"observation":0}',canonical_jsonb_sha256('{"observation":0}')),
              ($1,$2,$3,'2026-06-21T10:00:00Z','2026-06-21T10:15:00Z',100,10,99.9,100.1,1000000,20,'s20',false,false,false,true,
               '{"observation":1}',canonical_jsonb_sha256('{"observation":1}')),
              ($4,$2,$3,'2026-06-21T10:01:00Z','2026-06-21T10:16:00Z',100,10,99.9,100.1,1000000,20,'s20',false,false,false,true,
               '{"observation":2}',canonical_jsonb_sha256('{"observation":2}'))
            """, firstObservation, instrument, report, laterObservation);
        await ScalarGuid(connection, """
            SELECT submit_order($1,'11111111-1111-1111-1111-111111111111','basis-order','basis-key','BUY',$2,1,
              '2026-06-21T09:59:00Z','{"order":"basis"}',canonical_jsonb_sha256('{"order":"basis"}'))
            """, order, instrument);
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => ScalarGuid(connection, """
            SELECT record_fill(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),$1,
              '11111111-1111-1111-1111-111111111111',$2,1,100.1025,100.10,0,0.00102500,
              '2026-06-21T10:16:00Z','{"fill":2}',canonical_jsonb_sha256('{"fill":2}'),
              '{"ledger":2}',canonical_jsonb_sha256('{"ledger":2}'),'{"outcome":2}',
              canonical_jsonb_sha256('{"outcome":2}'),'fill-key')
            """, order, laterObservation));
        Assert.Contains("first eligible", rejected.MessageText);
        var fill = Guid.NewGuid();
        var ledger = Guid.NewGuid();
        var outcome = Guid.NewGuid();
        var recorded = await ScalarGuid(connection, """
            SELECT record_fill($1,$2,$3,$4,'11111111-1111-1111-1111-111111111111',$5,1,
              100.1025,100.10,0,0.00102500,'2026-06-21T10:15:00Z',
              '{"fill":1}',canonical_jsonb_sha256('{"fill":1}'),'{"ledger":1}',canonical_jsonb_sha256('{"ledger":1}'),
              '{"outcome":1}',canonical_jsonb_sha256('{"outcome":1}'),'fill-key')
            """, fill, ledger, outcome, order, firstObservation);
        Assert.Equal(fill, recorded);
        Assert.Equal(100.1000m, await ScalarDecimal(connection,
            "SELECT average_cost FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
        var replay = await ScalarGuid(connection, """
            SELECT record_fill($1,$2,$3,$4,'11111111-1111-1111-1111-111111111111',$5,1,
              100.1025,100.10,0,0.00102500,'2026-06-21T10:15:00Z',
              '{"fill":1}',canonical_jsonb_sha256('{"fill":1}'),'{"ledger":1}',canonical_jsonb_sha256('{"ledger":1}'),
              '{"outcome":1}',canonical_jsonb_sha256('{"outcome":1}'),'fill-key')
            """, fill, ledger, outcome, order, firstObservation);
        Assert.Equal(fill, replay);

        var action = Guid.NewGuid();
        var actionLedger = Guid.NewGuid();
        await Execute(connection, """
            INSERT INTO corporate_actions VALUES($1,'split-fixture',$2,'SPLIT','2026-06-22T08:00:00Z',
              '{"numerator":3,"denominator":2}',canonical_jsonb_sha256('{"numerator":3,"denominator":2}'),
              '{"authority":"nasdaq","primary":true}',canonical_jsonb_sha256('{"authority":"nasdaq","primary":true}'),
              '{"authority":"independent","secondary":true}',canonical_jsonb_sha256('{"authority":"independent","secondary":true}'),'owner:test','2026-06-21T12:00:00Z')
            """, action, instrument);
        var applied = await ScalarGuid(connection,
            "SELECT apply_corporate_action($1,'11111111-1111-1111-1111-111111111111',$2,$3,'2026-06-22T08:00:00Z')",
            action, Guid.NewGuid(), actionLedger);
        var actionReplay = await ScalarGuid(connection,
            "SELECT apply_corporate_action($1,'11111111-1111-1111-1111-111111111111',$2,$3,'2026-06-22T08:01:00Z')",
            action, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(actionLedger, applied);
        Assert.Equal(actionLedger, actionReplay);
        Assert.Equal(1L, await ScalarLong(connection,
            "SELECT quantity FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
        Assert.Equal(66.7333m, await ScalarDecimal(connection,
            "SELECT average_cost FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
        Assert.Equal(0.50000000m, await ScalarDecimal(connection,
            "SELECT quantity FROM fractional_entitlements WHERE corporate_action_id=$1", action));
        Assert.Equal(66.7333m, await ScalarDecimal(connection,
            "SELECT average_cost FROM fractional_entitlements WHERE corporate_action_id=$1", action));
    }

    [Fact]
    public async Task RealPostgresFinalLiquidationUsesOfficialCloseCompetitionRankingAndReplay()
    {
        if (ConnectionString is not { } connectionString) return;
        await EnsureTestDatabase(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetDatabase(dataSource);
        await new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None);
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        var instrument = Guid.NewGuid();
        var report = Guid.NewGuid();
        await Execute(connection, "UPDATE contest_state SET status='RUNNING',started_at='2026-01-02T08:00:00Z' WHERE singleton");
        await Execute(connection, """
            INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
            VALUES($1,'SE0000000020','FINALISSUER000000001','book-20','XSTO','FINAL','ESVUFR','2026-01-01',
              '{"instrument":20}',canonical_jsonb_sha256('{"instrument":20}'))
            """, instrument);
        await Execute(connection, """
            INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at,is_final,calendar_sha256,trading_hours_sha256)
            VALUES('XSTO-2026-12-30','2026-12-30','2026-12-30T08:00:00Z','2026-12-30T16:30:00Z',true,
              '867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395',
              'f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395')
            """);
        await Execute(connection, """
            INSERT INTO raw_market_reports VALUES($1,'final-fixture','https://example.test/final','2026-12-30T16:45:00Z',
              'x',encode(digest('x','sha256'),'hex'),'{"final":true}',canonical_jsonb_sha256('{"final":true}'))
            """, report);
        await Execute(connection, """
            INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,bid,ask,
              average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
            VALUES(gen_random_uuid(),$1,$2,'2026-12-30T16:30:00Z','2026-12-30T16:45:00Z',100,100,NULL,NULL,
              1000000,20,'XSTO-2026-12-30',true,false,false,true,'{"official":true}',canonical_jsonb_sha256('{"official":true}'))
            """, instrument, report);
        foreach (var agent in new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" })
            await Execute(connection, """
                INSERT INTO ledger_events(id,agent_id,event_type,instrument_id,cash_delta,quantity_delta,average_cost_after,
                  occurred_at,event_json,event_hash)
                VALUES(gen_random_uuid(),$1::uuid,'CORRECTION',$2,0,1,80,'2026-12-29T12:00:00Z',
                  jsonb_build_object('seed_agent',$1),canonical_jsonb_sha256(jsonb_build_object('seed_agent',$1)))
                """, agent, instrument);
        await Execute(connection, """
            SELECT finalize_contest('XSTO-2026-12-30-final',gen_random_uuid(),'XSTO-2026-12-30-final',
              '{"session_id":"XSTO-2026-12-30"}',canonical_jsonb_sha256('{"session_id":"XSTO-2026-12-30"}'),
              '2026-12-30T16:50:00Z')
            """);
        Assert.Equal(2L, await ScalarLong(connection, "SELECT count(*) FROM ledger_events WHERE event_type='FINAL_LIQUIDATION'"));
        Assert.Equal(0L, await ScalarLong(connection, "SELECT sum(quantity) FROM positions"));
        Assert.Equal(1L, await ScalarLong(connection,
            "SELECT rank FROM final_rankings WHERE reference='XSTO-2026-12-30-final' AND agent_id='11111111-1111-1111-1111-111111111111'"));
        Assert.Equal(1L, await ScalarLong(connection,
            "SELECT rank FROM final_rankings WHERE reference='XSTO-2026-12-30-final' AND agent_id='22222222-2222-2222-2222-222222222222'"));
        Assert.Equal(3L, await ScalarLong(connection,
            "SELECT rank FROM final_rankings WHERE reference='XSTO-2026-12-30-final' AND agent_id='33333333-3333-3333-3333-333333333333'"));
        await Execute(connection, """
            SELECT finalize_contest('XSTO-2026-12-30-final',gen_random_uuid(),'XSTO-2026-12-30-final',
              '{"session_id":"XSTO-2026-12-30"}',canonical_jsonb_sha256('{"session_id":"XSTO-2026-12-30"}'),
              '2026-12-30T16:50:00Z')
            """);
        Assert.Equal(2L, await ScalarLong(connection, "SELECT count(*) FROM ledger_events WHERE event_type='FINAL_LIQUIDATION'"));
        Assert.Equal(4L, await ScalarLong(connection,
            "SELECT count(*) FROM final_rankings WHERE reference='XSTO-2026-12-30-final'"));
    }

    [Fact]
    public async Task MigrationRefusesUnmanagedLegacyApplicationTables()
    {
        if (ConnectionString is not { } connectionString) return;
        await EnsureTestDatabase(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetDatabase(dataSource);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
            await Execute(connection, "CREATE TABLE orders(id bigint PRIMARY KEY)");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PostgresMigrationRunner(dataSource).ApplyAsync(CancellationToken.None));
            Assert.Contains("clean database", exception.Message, StringComparison.OrdinalIgnoreCase);
            await using var check = new NpgsqlCommand("SELECT to_regclass('public.schema_migrations') IS NULL", connection);
            Assert.True(await check.ExecuteScalarAsync(CancellationToken.None) is true);
        }
        finally
        {
            await ResetDatabase(dataSource);
        }
    }

    private static async Task ResetDatabase(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync(CancellationToken.None);
        await Execute(connection, "DROP SCHEMA public CASCADE; CREATE SCHEMA public");
    }

    private static async Task EnsureTestDatabase(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var database = target.Database ?? throw new InvalidOperationException("Test database is required.");
        if (!database.Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test database name must contain test.");
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=$1)", connection);
        exists.Parameters.AddWithValue(database);
        if (await exists.ExecuteScalarAsync(CancellationToken.None) is true) return;
        await using var create = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(database)} TEMPLATE template0", connection);
        await create.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

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
