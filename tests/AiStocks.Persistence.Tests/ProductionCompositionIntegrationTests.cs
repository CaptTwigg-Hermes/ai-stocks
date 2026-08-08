using System.Text;
using AiStocks.Collector;
using AiStocks.Operations;
using AiStocks.Persistence;
using AiStocks.Worker;
using Npgsql;

namespace AiStocks.Persistence.Tests;

public sealed class ProductionCompositionIntegrationTests
{
    [Fact]
    public void HermesDiscordReceiptRequiresExactDiscordSuccessAndNumericSnowflake()
    {
        Assert.Equal("1534975156781322311", HermesDiscordPort.ParseReceipt(
            "{\"success\":true,\"platform\":\"discord\",\"message_id\":\"1534975156781322311\"}"));
    }

    [Theory]
    [InlineData("{\"ok\":true,\"platform\":\"discord\",\"message_id\":\"1534975156781322311\"}")]
    [InlineData("{\"success\":false,\"platform\":\"discord\",\"message_id\":\"1534975156781322311\"}")]
    [InlineData("{\"success\":true,\"platform\":\"slack\",\"message_id\":\"1534975156781322311\"}")]
    [InlineData("{\"success\":true,\"platform\":\"discord\",\"message_id\":\"discord-message-123\"}")]
    [InlineData("{\"success\":true,\"success\":false,\"platform\":\"discord\",\"message_id\":\"1534975156781322311\"}")]
    [InlineData("{\"success\":true,\"platform\":\"discord\",\"platform\":\"slack\",\"message_id\":\"1534975156781322311\"}")]
    [InlineData("{\"success\":true,\"platform\":\"discord\",\"message_id\":\"1534975156781322311\",\"message_id\":\"1534975156781322312\"}")]
    public void HermesDiscordReceiptRejectsFakeNonDiscordAndDuplicateProperties(string receipt)
    {
        Assert.Throws<OperationsException>(() => HermesDiscordPort.ParseReceipt(receipt));
    }

    [Fact]
    public async Task ModelCancellationIsWorkerOnlyOwnedIdempotentAndTerminal()
    {
        var configured = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        await using var database = await DisposableDatabase.CreateAsync(configured);
        await using var source = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresMigrationRunner(source).ApplyAsync();
        var instrument = Guid.NewGuid();
        var ownOrder = Guid.NewGuid();
        var otherOrder = Guid.NewGuid();
        var ownAgent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var otherAgent = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, "UPDATE contest_state SET status='RUNNING',started_at='2026-06-01T08:00:00Z' WHERE singleton");
        await ExecuteAsync(connection, """
            INSERT INTO instruments(id,isin,issuer_id,order_book_id,mic,symbol,cfi,active_from,source_json,source_hash)
            VALUES($1,'SE0000000088','CCCCCCCCCCCCCCCCCCCC','book-cancel','XSTO','CANCEL','ESVUFR','2026-01-01',
              '{"source":"cancellation"}',canonical_jsonb_sha256('{"source":"cancellation"}'))
            """, instrument);
        await ExecuteAsync(connection, """
            INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at)
            VALUES('cancel-session','2026-06-01','2026-06-01T08:00:00Z','2026-06-01T16:00:00Z')
            """);
        await ExecuteAsync(connection, """
            INSERT INTO raw_market_reports VALUES(gen_random_uuid(),'cancel-report','https://example.test/cancel','2026-06-01T09:55:00Z',
              'x',encode(digest('x','sha256'),'hex'),'{"fixture":true}',canonical_jsonb_sha256('{"fixture":true}'))
            """);
        await ExecuteAsync(connection, """
            INSERT INTO market_observations(id,instrument_id,raw_market_report_id,traded_at,retrieved_at,price,quantity,
              average_daily_value_20,complete_history_sessions,session_id,is_official_pats,warning,suspended,verified,source_json,source_hash)
            SELECT gen_random_uuid(),$1,id,observation.traded_at,observation.retrieved_at,100,10,1000000,20,
              'cancel-session',false,false,false,true,jsonb_build_object('at',observation.traded_at),
              canonical_jsonb_sha256(jsonb_build_object('at',observation.traded_at))
            FROM raw_market_reports CROSS JOIN (VALUES
              ('2026-06-01T09:00:00Z'::timestamptz,'2026-06-01T09:15:00Z'::timestamptz),
              ('2026-06-01T09:40:00Z'::timestamptz,'2026-06-01T09:55:00Z'::timestamptz)
            ) observation(traded_at,retrieved_at)
            WHERE report_name='cancel-report'
            """, instrument);
        await ExecuteAsync(connection, """
            SELECT submit_order($1,$2,'own-decision','own-order','BUY',$3,1,'2026-06-01T09:30:00Z',
              '{"reason":"own"}',canonical_jsonb_sha256('{"reason":"own"}'))
            """, ownOrder, ownAgent, instrument);
        await ExecuteAsync(connection, """
            SELECT submit_order($1,$2,'other-decision','other-order','BUY',$3,1,'2026-06-01T09:30:00Z',
              '{"reason":"other"}',canonical_jsonb_sha256('{"reason":"other"}'))
            """, otherOrder, otherAgent, instrument);

        Assert.True(await ScalarBoolAsync(connection,
            "SELECT has_function_privilege('ai_stocks_worker_runtime','cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(connection,
            "SELECT has_function_privilege('ai_stocks_runtime','cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(connection,
            "SELECT has_function_privilege('ai_stocks_operations_runtime','cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));
        Assert.False(await ScalarBoolAsync(connection,
            "SELECT has_function_privilege('ai_stocks_web_runtime','cancel_order(uuid,uuid,uuid,text,jsonb,sha256_hex,timestamptz)','EXECUTE')"));

        await ExecuteAsync(connection, "SET ROLE ai_stocks_worker_runtime");
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, """
            SELECT cancel_order(gen_random_uuid(),$1,$2,'cancel:foreign','{"reason":"forbidden"}',
              'db09347e7a86964a792c586ea2c20ff42ae265a7053c4b3f8ea50fcb66dc78fd','2026-06-01T09:31:00Z')
            """, otherOrder, ownAgent));
        Assert.Contains("ownership mismatch", foreign.MessageText, StringComparison.Ordinal);

        var outcome = Guid.NewGuid();
        await ExecuteAsync(connection, """
            SELECT cancel_order($1,$2,$3,'cancel:own','{"reason":"model cancellation"}',
              'c7a05a30b988513168c5f21740620f586c372839452dc4b8a7d463cf02335684','2026-06-01T09:31:00Z')
            """, outcome, ownOrder, ownAgent);
        var replay = await ScalarStringAsync(connection, """
            SELECT cancel_order(gen_random_uuid(),$1,$2,'cancel:own','{"reason":"model cancellation"}',
              'c7a05a30b988513168c5f21740620f586c372839452dc4b8a7d463cf02335684','2026-06-01T09:31:00Z')::text
            """, ownOrder, ownAgent);
        Assert.Equal(outcome.ToString(), replay);
        var terminal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, """
            SELECT cancel_order(gen_random_uuid(),$1,$2,'cancel:different','{"reason":"different"}',
              'f1d71130c2e74c5b820202982aa4b665af3f38e4368b40f76205ea7a0bc7988b','2026-06-01T09:32:00Z')
            """, ownOrder, ownAgent));
        Assert.Contains("terminal outcome", terminal.MessageText, StringComparison.Ordinal);
        await ExecuteAsync(connection, "SELECT execute_queued_order($1,'2026-06-01T10:00:00Z')", ownOrder);
        await ExecuteAsync(connection, "RESET ROLE");

        Assert.Equal(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM fills WHERE order_id=$1", ownOrder));
        Assert.Equal("CANCELLED", await ScalarStringAsync(connection,
            "SELECT status::text FROM order_outcomes WHERE order_id=$1", ownOrder));
    }

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
                SELECT gen_random_uuid(),$1,id,'2026-06-21T09:59:30Z','2026-06-21T10:14:30Z',99.5,10,99.4,99.6,
                  1000000,19,'compose-s20',false,false,false,true,'{"quote":"ineligible"}',canonical_jsonb_sha256('{"quote":"ineligible"}')
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
            Assert.Equal("2026-06-21 10:00:00", await ScalarStringAsync(connection,
                "SELECT (observation.traded_at AT TIME ZONE 'UTC')::text FROM fills fill JOIN market_observations observation ON observation.id=fill.market_observation_id WHERE fill.order_id=$1", order));
            Assert.Equal(1L, await ScalarLongAsync(connection, "SELECT quantity FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
        }
        await new PostgresCorporateActionIngestion(database.ConnectionString).IngestAsync(Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "id": "{{action:D}}",
              "externalReference": "compose-split",
              "isin": "SE0000000099",
              "orderBookId": "book-compose",
              "actionType": "SPLIT",
              "effectiveAt": "2026-06-22T08:00:00Z",
              "normalized": {"numerator": 2, "denominator": 1},
              "primaryEvidence": {
                "authority": "nasdaq-main-market-notices",
                "sourceUrl": "https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices",
                "publishedAt": "2026-06-21T10:00:00Z",
                "retrievedAt": "2026-06-21T10:15:00Z",
                "payloadSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
              },
              "secondaryEvidence": {
                "authority": "issuer-ir",
                "sourceUrl": "https://issuer.example/actions/compose-split",
                "publishedAt": "2026-06-21T10:01:00Z",
                "retrievedAt": "2026-06-21T10:16:00Z",
                "payloadSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
              },
              "approval": {
                "approvedBy": "owner:test",
                "approvedAt": "2026-06-21T12:00:00Z"
              }
            }
            """), default);

        var operations = new PostgresContestOperations(source);
        await operations.ApplyDueCorporateActionsAsync(new DateTimeOffset(2026, 6, 22, 8, 0, 0, TimeSpan.Zero), default);
        await operations.ApplyDueCorporateActionsAsync(new DateTimeOffset(2026, 6, 22, 8, 1, 0, TimeSpan.Zero), default);

        await using (var connection = await source.OpenConnectionAsync())
        {
            Assert.Equal(4L, await ScalarLongAsync(connection, "SELECT count(*) FROM corporate_action_applications WHERE corporate_action_id=$1", action));
            Assert.Equal(2L, await ScalarLongAsync(connection, "SELECT quantity FROM positions WHERE agent_id='11111111-1111-1111-1111-111111111111' AND instrument_id=$1", instrument));
            await ExecuteAsync(connection, """
                INSERT INTO trading_sessions(session_id,session_day,opens_at,closes_at,is_final,calendar_sha256,trading_hours_sha256)
                VALUES('XSTO-2026-12-29','2026-12-29','2026-12-29T08:00:00Z','2026-12-29T16:30:00Z',false,
                  '867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395',
                  'f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395'),
                  ('XSTO-2026-12-30','2026-12-30','2026-12-30T08:00:00Z','2026-12-30T16:30:00Z',true,
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

        var discord = new RecordingDiscord();
        var publisher = new PostgresDailyReportPublisher(source, new DailyReportService(),
            new AuditedDiscordDelivery(new PostgresDeliveryAuditPort(source), discord, new FixedClock(new DateTimeOffset(2026, 12, 29, 17, 30, 0, TimeSpan.Zero))));
        Assert.True(await publisher.PublishIfDueAsync(new DateTimeOffset(2026, 12, 29, 17, 30, 0, TimeSpan.Zero), default));

        await operations.FinalizeIfDueAsync(new DateTimeOffset(2026, 12, 30, 16, 50, 0, TimeSpan.Zero), default);
        await operations.FinalizeIfDueAsync(new DateTimeOffset(2026, 12, 30, 17, 0, 0, TimeSpan.Zero), default);

        publisher = new PostgresDailyReportPublisher(source, new DailyReportService(),
            new AuditedDiscordDelivery(new PostgresDeliveryAuditPort(source), discord, new FixedClock(new DateTimeOffset(2026, 12, 30, 17, 30, 0, TimeSpan.Zero))));
        var published = await publisher.PublishIfDueAsync(new DateTimeOffset(2026, 12, 30, 17, 30, 0, TimeSpan.Zero), default);
        Assert.True(published);
        Assert.False(await publisher.PublishIfDueAsync(new DateTimeOffset(2026, 12, 30, 17, 31, 0, TimeSpan.Zero), default));
        Assert.Equal(2, discord.Messages.Count);
        Assert.DoesNotContain("daily 0.40%; total 0.40%", discord.Messages[1], StringComparison.Ordinal);

        var alertStore = new PostgresImmediateAlertStore(source);
        foreach (var kind in Enum.GetValues<ImmediateAlertKind>())
        {
            var alert = ImmediateAlert.Create(kind, $"production {kind}", $"test:{kind}");
            await alertStore.EnqueueAsync(alert, default);
            await alertStore.EnqueueAsync(alert, default);
        }
        var alertPublisher = new PostgresImmediateAlertPublisher(source,
            new AuditedDiscordDelivery(new PostgresDeliveryAuditPort(source), discord,
                new FixedClock(new DateTimeOffset(2026, 12, 30, 17, 31, 0, TimeSpan.Zero))));
        Assert.Equal(5, await alertPublisher.PublishPendingAsync(default));
        Assert.Equal(0, await alertPublisher.PublishPendingAsync(default));
        Assert.Equal(7, discord.Messages.Count);

        await using var verify = await source.OpenConnectionAsync();
        Assert.Equal("FINISHED", await ScalarStringAsync(verify, "SELECT status::text FROM contest_state WHERE singleton"));
        Assert.Equal(4L, await ScalarLongAsync(verify, "SELECT count(*) FROM final_rankings"));
        Assert.Equal(2L, await ScalarLongAsync(verify, "SELECT count(*) FROM daily_reports"));
        Assert.Equal(8L, await ScalarLongAsync(verify, "SELECT count(*) FROM daily_report_values"));
        Assert.Equal(7L, await ScalarLongAsync(verify, "SELECT count(*) FROM delivery_audits WHERE status='SUCCEEDED'"));
        Assert.Equal(5L, await ScalarLongAsync(verify, "SELECT count(*) FROM immediate_alerts"));
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

    private static async Task<string> ScalarStringAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
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
