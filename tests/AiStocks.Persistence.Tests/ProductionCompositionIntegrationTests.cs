using AiStocks.Operations;
using AiStocks.Persistence;
using AiStocks.Worker;
using Npgsql;

namespace AiStocks.Persistence.Tests;

public sealed class ProductionCompositionIntegrationTests
{
    [Fact]
    public async Task AcceptedQueueFlowsThroughFillActionsFinalizationReportAndLeasedDelivery()
    {
        var configured = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        await using var database = await DisposableDatabase.CreateAsync(configured);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresMigrationRunner(source).ApplyAsync();
        var instrument = Guid.NewGuid();
        var order = Guid.NewGuid();
        var action = Guid.NewGuid();

        await using (var connection = await source.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, "UPDATE contest_state SET status='RUNNING',started_at='2026-06-01T08:00:00Z' WHERE singleton");
            await ExecuteAsync(connection, """
                INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
                VALUES($1,'SE0000000099','COMPOSEISSUER0000001','book-compose','XSTO','COMP','ESVUFR','2026-01-01',
                  '{"source":"composition"}',canonical_jsonb_sha256('{"source":"composition"}'))
                """, instrument);
            await ExecuteAsync(connection, """
                INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at)
                SELECT 'compose-s'||n,date '2026-06-01'+n,(date '2026-06-01'+n)+time '08:00',(date '2026-06-01'+n)+time '16:00'
                FROM generate_series(0,20) n
                """);
            await ExecuteAsync(connection, """
                INSERT INTO instrument_session_stats(instrument_id,session_id,traded_value,complete)
                SELECT $1,'compose-s'||n,1000000,true FROM generate_series(0,19) n
                """, instrument);
            await ExecuteAsync(connection, """
                INSERT INTO raw_market_reports VALUES(gen_random_uuid(),'compose-report','https://example.test/compose','2026-06-21T10:15:00Z',
                  'x',encode(digest('x','sha256'),'hex'),'{"fixture":true}',canonical_jsonb_sha256('{"fixture":true}'))
                """);
            await ExecuteAsync(connection, """
                INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,bid,ask,
                  average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
                SELECT gen_random_uuid(),$1,id,'2026-06-21T09:40:00Z','2026-06-21T09:55:00Z',99,10,99,99,
                  1000000,20,'compose-s20',false,false,false,true,'{"quote":"basis"}',canonical_jsonb_sha256('{"quote":"basis"}')
                FROM raw_market_reports WHERE report_name='compose-report'
                """, instrument);
            await ExecuteAsync(connection, """
                INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,bid,ask,
                  average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
                SELECT gen_random_uuid(),$1,id,'2026-06-21T10:00:00Z','2026-06-21T10:15:00Z',100,10,99.9,100.1,
                  1000000,20,'compose-s20',false,false,false,true,'{"quote":"fill"}',canonical_jsonb_sha256('{"quote":"fill"}')
                FROM raw_market_reports WHERE report_name='compose-report'
                """, instrument);
            await ExecuteAsync(connection, """
                SELECT submit_order($1,'11111111-1111-1111-1111-111111111111','compose-decision','compose-order','BUY',$2,1,
                  '2026-06-21T09:59:00Z','{"reason":"composition catalyst"}',canonical_jsonb_sha256('{"reason":"composition catalyst"}'))
                """, order, instrument);
        }

        var execution = new PostgresQueuedExecutionPort(source, TimeProvider.System);
        var coordinator = new AiStocks.Worker.Orchestration.QueuedExecutionCoordinator(execution);
        await coordinator.ExecuteAllAsync(default);
        await coordinator.ExecuteAllAsync(default);

        await using (var connection = await source.OpenConnectionAsync())
        {
            Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM fills WHERE order_id=$1", order));
            Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT quantity FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
            await ExecuteAsync(connection, """
                INSERT INTO corporate_actions VALUES($1,'compose-split',$2,'SPLIT','2026-06-22T08:00:00Z',
                  '{"numerator":2,"denominator":1}',canonical_jsonb_sha256('{"numerator":2,"denominator":1}'),
                  '{"authority":"nasdaq"}',canonical_jsonb_sha256('{"authority":"nasdaq"}'),
                  '{"authority":"issuer"}',canonical_jsonb_sha256('{"authority":"issuer"}'),'owner:test','2026-06-21T12:00:00Z')
                """, action, instrument);
        }

        var operations = new PostgresContestOperations(source);
        await operations.ApplyDueCorporateActionsAsync(new DateTimeOffset(2026, 6, 22, 8, 0, 0, TimeSpan.Zero), default);
        await operations.ApplyDueCorporateActionsAsync(new DateTimeOffset(2026, 6, 22, 8, 1, 0, TimeSpan.Zero), default);

        await using (var connection = await source.OpenConnectionAsync())
        {
            Assert.Equal(4L, await ScalarLongAsync(connection, "SELECT count(*) FROM corporate_action_applications WHERE corporate_action_id=$1", action));
            Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT quantity FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
            await ExecuteAsync(connection, """
                INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at,is_final,calendar_sha256,trading_hours_sha256)
                VALUES('XSTO-2026-12-30','2026-12-30','2026-12-30T08:00:00Z','2026-12-30T16:30:00Z',true,
                  '867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395',
                  'f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395')
                """);
            await ExecuteAsync(connection, """
                INSERT INTO raw_market_reports VALUES(gen_random_uuid(),'compose-final','https://example.test/final','2026-12-30T16:45:00Z',
                  'x',encode(digest('x','sha256'),'hex'),'{"final":true}',canonical_jsonb_sha256('{"final":true}'))
                """);
            await ExecuteAsync(connection, """
                INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,
                  average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
                SELECT gen_random_uuid(),$1,id,'2026-12-30T16:30:00Z','2026-12-30T16:45:00Z',110,100,1000000,20,
                  'XSTO-2026-12-30',true,false,false,true,'{"official":true}',canonical_jsonb_sha256('{"official":true}')
                FROM raw_market_reports WHERE report_name='compose-final'
                """, instrument);
        }

        await operations.FinalizeIfDueAsync(new DateTimeOffset(2026, 12, 30, 16, 50, 0, TimeSpan.Zero), default);
        await operations.FinalizeIfDueAsync(new DateTimeOffset(2026, 12, 30, 17, 0, 0, TimeSpan.Zero), default);

        var discord = new RecordingDiscord();
        var publisher = new PostgresDailyReportPublisher(source, new DailyReportService(),
            new AuditedDiscordDelivery(new PostgresDeliveryAuditPort(source), discord, new FixedClock(new DateTimeOffset(2026, 12, 30, 17, 30, 0, TimeSpan.Zero))));
        var published = await publisher.PublishIfDueAsync(new DateTimeOffset(2026, 12, 30, 17, 30, 0, TimeSpan.Zero), default);
        Assert.True(published);
        Assert.False(await publisher.PublishIfDueAsync(new DateTimeOffset(2026, 12, 30, 17, 31, 0, TimeSpan.Zero), default));
        Assert.Single(discord.Messages);

        await using var verify = await source.OpenConnectionAsync();
        Assert.Equal("FINISHED", await ScalarStringAsync(verify, "SELECT status::text FROM contest_state WHERE singleton"));
        Assert.Equal(4L, await ScalarLongAsync(verify, "SELECT count(*) FROM final_rankings"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT count(*) FROM daily_reports WHERE report_key='daily:2026-12-30'"));
        Assert.Equal(1L, await ScalarLongAsync(verify, "SELECT count(*) FROM delivery_audits WHERE delivery_key='daily:2026-12-30' AND status='SUCCEEDED'"));
        Assert.True(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_worker_runtime','execute_queued_order(uuid,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_worker_runtime','finalize_contest(text,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_worker_runtime','apply_corporate_action(uuid,uuid,uuid,uuid,timestamptz)','EXECUTE')"));
        Assert.True(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_operations_runtime','finalize_contest(text,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_operations_runtime','execute_queued_order(uuid,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_web_runtime','execute_queued_order(uuid,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_function_privilege('ai_stocks_web_runtime','finalize_contest(text,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_table_privilege('ai_stocks_worker_runtime','daily_reports','INSERT')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_table_privilege('ai_stocks_operations_runtime','orders','INSERT')"));
        Assert.False(await ScalarBoolAsync(verify, "SELECT has_table_privilege('ai_stocks_web_runtime','orders','INSERT')"));
    }

    private sealed class RecordingDiscord : IDiscordPort
    {
        public List<string> Messages { get; } = [];
        public Task<string> SendAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult("discord-receipt");
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : AiStocks.Core.IClock { public DateTimeOffset UtcNow => now; }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException());
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync() is true;
    }

    private sealed class DisposableDatabase : IAsyncDisposable
    {
        private readonly string adminConnectionString;
        private readonly string name;
        private DisposableDatabase(string adminConnectionString, string name, string connectionString) =>
            (this.adminConnectionString, this.name, ConnectionString) = (adminConnectionString, name, connectionString);
        public string ConnectionString { get; }

        public static async Task<DisposableDatabase> CreateAsync(string configured)
        {
            var configuredBuilder = new NpgsqlConnectionStringBuilder(configured);
            if (!(configuredBuilder.Database ?? "").Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("AISTOCKS_TEST_DATABASE_URL database name must contain test.");
            var admin = new NpgsqlConnectionStringBuilder(configuredBuilder.ConnectionString) { Database = "postgres" };
            var name = $"ai_stocks_composition_test_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", connection).ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name, IncludeErrorDetail = true };
            return new DisposableDatabase(admin.ConnectionString, name, target.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }
}
