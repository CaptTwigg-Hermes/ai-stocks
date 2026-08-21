using AiStocks.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json.Nodes;

namespace AiStocks.Api.Tests;

public sealed class PreviewRacePersistenceTests
{
    private static readonly AgentDefinition Agent = ContestContract.Agents[0];
    private static readonly DateTimeOffset ExecutedAt = DateTimeOffset.Parse("2026-08-16T10:01:00Z");
    private static readonly DateTimeOffset AvailableAt = DateTimeOffset.Parse("2026-08-16T10:16:00Z");

    [Fact]
    public void Ai_exhibition_trade_activity_and_idempotency_survive_store_recreation()
    {
        var persistence = new FakePersistence();
        var snapshot = new InstrumentListDto(
            [new("SE0000108656", "ERIC-B", "Ericsson B", "XSTO", "Sweden", "SEK", 100m, null, false,
                ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true)],
            DelayedNasdaqInstrumentStore.DataMode);
        var request = new AiDecisionRequestDto("durable-buy-001", Agent.Id, Agent.ModelId, "buy",
            "SE0000108656", 10, "Verified durable decision.", 0.75m,
            [new("https://example.com/research", AvailableAt, "Exact verified excerpt.", new string('a', 64))],
            "copilot", Agent.ModelId, new string('b', 64), AvailableAt.AddMinutes(2), 100m, AvailableAt);
        var first = new PreviewRaceStore(TimeProvider.System, persistence);
        first.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null,
            request.CompletedAt.AddSeconds(-2)));
        first.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null,
            request.CompletedAt.AddSeconds(-1)));
        Assert.False(first.SubmitAi(request, snapshot).Replayed);

        var restarted = new PreviewRaceStore(TimeProvider.System, persistence);
        var progress = restarted.AiProgress(snapshot);
        var participant = progress.Participants.Single(item => item.AgentId == Agent.Id);

        Assert.Equal("succeeded", participant.Status);
        Assert.Equal(99_343.50m, participant.Portfolio.CashDkk);
        Assert.Equal(10, Assert.Single(participant.Portfolio.Holdings).Quantity);
        Assert.Equal("durable-buy-001", participant.LatestDecision!.RunId);
        Assert.Contains(progress.Activity, item => item.RunId == "durable-buy-001" && item.Action == "buy");
        var savesBeforeReplay = persistence.SaveCalls;
        Assert.True(restarted.SubmitAi(request, snapshot).Replayed);
        Assert.Equal(savesBeforeReplay, persistence.SaveCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Legacy_schema_state_loads_and_next_mutation_rewrites_current_schema(int schemaVersion)
    {
        var persistence = PersistedTradeState();
        var document = JsonNode.Parse(persistence.Json!)!;
        document["SchemaVersion"] = schemaVersion;
        persistence.Seed(document.ToJsonString());
        var restarted = new PreviewRaceStore(TimeProvider.System, persistence);
        var nextAgent = ContestContract.Agents[1];

        restarted.UpdateAiStatus(new("schema-migrate-001", nextAgent.Id, nextAgent.ModelId, "queued", null,
            AvailableAt.AddMinutes(10)));

        Assert.Equal(3, JsonNode.Parse(persistence.Json!)!["SchemaVersion"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(DelayedNasdaqInstrumentStore.DataMode, PreviewRaceStore.NordicAssumedExecutionMode)]
    [InlineData(DelayedNasdaqInstrumentStore.NordicDataMode, PreviewRaceStore.AssumedExecutionMode)]
    [InlineData(PreviewRaceStore.DataMode, PreviewRaceStore.NordicAssumedExecutionMode)]
    [InlineData("unknown-data-mode", PreviewRaceStore.AssumedExecutionMode)]
    [InlineData(DelayedNasdaqInstrumentStore.DataMode, "unknown-execution-mode")]
    public void Store_rejects_unsupported_data_and_execution_mode_pairs(string dataMode, string executionMode)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new PreviewRaceStore(TimeProvider.System, null, dataMode, executionMode));

        Assert.Contains("mode", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Persisted_state_cannot_be_reopened_under_a_different_exhibition_mode()
    {
        var persistence = new FakePersistence();
        var stockholm = new PreviewRaceStore(TimeProvider.System, persistence,
            DelayedNasdaqInstrumentStore.DataMode, PreviewRaceStore.AssumedExecutionMode);
        stockholm.UpdateAiStatus(new("mode-bound-001", Agent.Id, Agent.ModelId, "queued", null, AvailableAt));

        var exception = Assert.Throws<PreviewRacePersistenceException>(() => new PreviewRaceStore(
            TimeProvider.System, persistence, DelayedNasdaqInstrumentStore.NordicDataMode,
            PreviewRaceStore.NordicAssumedExecutionMode));

        Assert.Contains("different exhibition mode", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nordic_native_fx_trade_survives_store_recreation()
    {
        var (persistence, snapshot) = PersistedNordicTradeState();

        var restarted = new PreviewRaceStore(TimeProvider.System, persistence,
            DelayedNasdaqInstrumentStore.NordicDataMode, PreviewRaceStore.NordicAssumedExecutionMode);
        var participant = restarted.AiProgress(snapshot).Participants.Single(item => item.AgentId == Agent.Id);

        Assert.Equal("assumed-delayed-paper-fills-v2", participant.Portfolio.ExecutionMode);
        Assert.Equal(7_500m, participant.Portfolio.HoldingsValueDkk);
        Assert.Equal(7_575m, participant.LatestDecision!.AssumedPaperFill!.TotalDkk);
    }

    [Theory]
    [InlineData("cross-mode-fill")]
    [InlineData("wrong-venue-currency")]
    [InlineData("tuple-mismatch")]
    [InlineData("missing-mark-fx")]
    [InlineData("future-fill-fx")]
    [InlineData("future-mark-fx")]
    [InlineData("future-fill-reference-date")]
    [InlineData("future-mark-reference-date")]
    public void Nordic_persisted_state_rejects_mode_tuple_and_fx_corruption(string corruption)
    {
        var (persistence, _) = PersistedNordicTradeState();
        var document = JsonNode.Parse(persistence.Json!)!;
        var account = document["Accounts"]!.AsArray().Single(item =>
            item!["AgentId"]!.GetValue<Guid>() == Agent.Id)!;
        var runFill = document["Runs"]![0]!["Decision"]!["assumedPaperFill"]!;
        var latestFill = account["LatestDecision"]!["assumedPaperFill"]!;
        switch (corruption)
        {
            case "cross-mode-fill":
                runFill["executionMode"] = PreviewRaceStore.AssumedExecutionMode;
                latestFill["executionMode"] = PreviewRaceStore.AssumedExecutionMode;
                break;
            case "wrong-venue-currency":
                runFill["observedCurrency"] = "SEK";
                latestFill["observedCurrency"] = "SEK";
                break;
            case "tuple-mismatch":
                runFill["observedVenue"] = "XSTO";
                runFill["observedCurrency"] = "SEK";
                latestFill["observedVenue"] = "XSTO";
                latestFill["observedCurrency"] = "SEK";
                break;
            case "missing-mark-fx":
                var mark = account["Marks"]!["XCSE:DK0010181676:CARL-A"]!;
                mark["fxToDkk"] = null;
                mark["fxReferenceDate"] = null;
                mark["fxAvailableAt"] = null;
                mark["fxSource"] = null;
                mark["fxSha256"] = null;
                mark["priceDkk"] = 487.50m;
                account["Performance"]!.AsArray()[^1]!["valueDkk"] = 97_300m;
                break;
            case "future-fill-fx":
                var afterDecision = account["LatestDecision"]!["completedAt"]!
                    .GetValue<DateTimeOffset>().AddMinutes(1);
                runFill["fxAvailableAt"] = afterDecision;
                latestFill["fxAvailableAt"] = afterDecision;
                break;
            case "future-mark-fx":
                account["Marks"]!["XCSE:DK0010181676:CARL-A"]!["fxAvailableAt"] =
                    account["Performance"]!.AsArray()[^1]!["at"]!.GetValue<DateTimeOffset>().AddMinutes(1);
                break;
            case "future-fill-reference-date":
                runFill["fxReferenceDate"] = "2026-08-15";
                latestFill["fxReferenceDate"] = "2026-08-15";
                break;
            case "future-mark-reference-date":
                account["Marks"]!["XCSE:DK0010181676:CARL-A"]!["fxReferenceDate"] =
                    "2026-08-15";
                break;
        }
        persistence.Seed(document.ToJsonString());

        Assert.Throws<PreviewRacePersistenceException>(() => new PreviewRaceStore(
            TimeProvider.System, persistence, DelayedNasdaqInstrumentStore.NordicDataMode,
            PreviewRaceStore.NordicAssumedExecutionMode));
    }

    [Fact]
    public void Configured_store_rejects_a_snapshot_from_the_other_official_mode()
    {
        var (_, nordicSnapshot) = PersistedNordicTradeState();
        var stockholmSnapshot = new InstrumentListDto(
            [new("SE0000108656", "ERIC-B", "Ericsson B", "XSTO", "Sweden", "SEK", 100m, null, false,
                ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true)],
            DelayedNasdaqInstrumentStore.DataMode);

        Assert.Throws<PreviewOrderException>(() =>
            new PreviewRaceStore(TimeProvider.System, null, DelayedNasdaqInstrumentStore.DataMode,
                PreviewRaceStore.AssumedExecutionMode).AiProgress(nordicSnapshot));
        Assert.Throws<PreviewOrderException>(() =>
            new PreviewRaceStore(TimeProvider.System, null, DelayedNasdaqInstrumentStore.NordicDataMode,
                PreviewRaceStore.NordicAssumedExecutionMode).AiProgress(stockholmSnapshot));
    }

    [Theory]
    [InlineData("missing-fx")]
    [InlineData("tuple-mismatch")]
    [InlineData("incomplete-provenance")]
    [InlineData("price-mismatch")]
    public void Nordic_hold_rejects_invalid_current_marks_before_persistence(string corruption)
    {
        var (persistence, snapshot) = PersistedNordicTradeState();
        var instrument = snapshot.Items[0];
        instrument = corruption switch
        {
            "missing-fx" => instrument with { FxToDkk = null },
            "tuple-mismatch" => instrument with { Exchange = "XSTO", Currency = "SEK" },
            "incomplete-provenance" => instrument with { FxSource = null },
            "price-mismatch" => instrument with { PriceDkk = instrument.PriceDkk + 1m },
            _ => throw new InvalidOperationException()
        };
        var invalidSnapshot = new InstrumentListDto([instrument], DelayedNasdaqInstrumentStore.NordicDataMode);
        var store = new PreviewRaceStore(TimeProvider.System, persistence,
            DelayedNasdaqInstrumentStore.NordicDataMode, PreviewRaceStore.NordicAssumedExecutionMode);
        var hold = new AiDecisionRequestDto("nordic-hold-invalid-001", Agent.Id, Agent.ModelId, "hold", null, 0,
            "No verified trade catalyst.", 0.5m, [], "copilot", Agent.ModelId, new string('d', 64),
            AvailableAt.AddMinutes(10), null, null);
        store.UpdateAiStatus(new(hold.RunId, hold.AgentId, hold.ModelId, "queued", null,
            hold.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(hold.RunId, hold.AgentId, hold.ModelId, "running", null,
            hold.CompletedAt.AddSeconds(-1)));
        var savesBeforeRejection = persistence.SaveCalls;

        Assert.Throws<PreviewOrderException>(() => store.SubmitAi(hold, invalidSnapshot));
        Assert.Equal(savesBeforeRejection, persistence.SaveCalls);
    }

    [Fact]
    public void Hold_refreshes_all_persisted_marks_before_restart()
    {
        var persistence = new FakePersistence();
        var buySnapshot = new InstrumentListDto(
            [new("SE0000108656", "ERIC-B", "Ericsson B", "XSTO", "Sweden", "SEK", 100m, null, false,
                ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true)],
            DelayedNasdaqInstrumentStore.DataMode);
        var buy = new AiDecisionRequestDto("mark-buy-001", Agent.Id, Agent.ModelId, "buy", "SE0000108656", 10,
            "Verified durable buy.", 0.75m,
            [new("https://example.com/research", AvailableAt, "Exact verified excerpt.", new string('a', 64))],
            "copilot", Agent.ModelId, new string('b', 64), AvailableAt.AddMinutes(2), 100m, AvailableAt);
        var store = new PreviewRaceStore(TimeProvider.System, persistence);
        store.UpdateAiStatus(new(buy.RunId, buy.AgentId, buy.ModelId, "queued", null, buy.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(buy.RunId, buy.AgentId, buy.ModelId, "running", null, buy.CompletedAt.AddSeconds(-1)));
        store.SubmitAi(buy, buySnapshot);

        var holdSnapshot = new InstrumentListDto(
            [buySnapshot.Items[0] with { Price = 110m, ExecutedAt = ExecutedAt.AddMinutes(3), AvailableAt = AvailableAt.AddMinutes(3) }],
            DelayedNasdaqInstrumentStore.DataMode);
        var hold = new AiDecisionRequestDto("mark-hold-002", Agent.Id, Agent.ModelId, "hold", null, 0,
            "No verified trade catalyst.", 0.5m, [], "copilot", Agent.ModelId, new string('c', 64),
            AvailableAt.AddMinutes(5), null, null);
        store.UpdateAiStatus(new(hold.RunId, hold.AgentId, hold.ModelId, "queued", null, hold.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(hold.RunId, hold.AgentId, hold.ModelId, "running", null, hold.CompletedAt.AddSeconds(-1)));
        store.SubmitAi(hold, holdSnapshot);

        var restarted = new PreviewRaceStore(TimeProvider.System, persistence);
        var participant = restarted.AiProgress(holdSnapshot).Participants.Single(item => item.AgentId == Agent.Id);
        Assert.Equal(71.50m, Assert.Single(participant.Portfolio.Holdings).PriceDkk);
        Assert.Equal(100_058.50m, participant.Portfolio.TotalValueDkk);
    }

    [Fact]
    public async Task Ai_exhibition_state_survives_a_real_postgres_backed_store_recreation()
    {
        var configured = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        var configuredBuilder = new NpgsqlConnectionStringBuilder(configured);
        if (!(configuredBuilder.Database ?? string.Empty).Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AISTOCKS_TEST_DATABASE_URL database name must contain 'test'.");

        var database = $"ai_stocks_preview_test_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredBuilder.ConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);
            await create.ExecuteNonQueryAsync();
            await using var grant = new NpgsqlCommand(
                $"GRANT CONNECT ON DATABASE {database} TO ai_stocks_web_runtime", admin);
            await grant.ExecuteNonQueryAsync();
        }

        NpgsqlDataSource? dataSource = null;
        try
        {
            configuredBuilder.Database = database;
            dataSource = NpgsqlDataSource.Create(configuredBuilder.ConnectionString);
            await using (var schema = dataSource.CreateCommand("""
                CREATE TABLE exhibition_preview_state (
                  state_key text PRIMARY KEY CHECK (length(state_key) BETWEEN 1 AND 100),
                  revision bigint NOT NULL CHECK (revision > 0),
                  state_json jsonb NOT NULL CHECK (
                    jsonb_typeof(state_json) = 'object' AND octet_length(state_json::text) <= 4194304
                  ),
                  updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
                );
                CREATE TABLE exhibition_preview_mutation_receipts (
                  mutation_id uuid PRIMARY KEY,
                  state_revision bigint NOT NULL CHECK (state_revision > 0),
                  state_key text NOT NULL CHECK (length(state_key) BETWEEN 1 AND 100),
                  committed_at timestamptz NOT NULL DEFAULT clock_timestamp()
                );
                CREATE FUNCTION bound_exhibition_preview_mutation_receipts()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $$
                BEGIN
                  DELETE FROM public.exhibition_preview_mutation_receipts
                  WHERE state_key=NEW.state_key AND state_revision <= NEW.state_revision - 100000;
                  RETURN NULL;
                END;
                $$;
                REVOKE ALL ON FUNCTION bound_exhibition_preview_mutation_receipts() FROM PUBLIC;
                CREATE TRIGGER exhibition_preview_receipt_bound
                AFTER INSERT ON exhibition_preview_mutation_receipts
                FOR EACH ROW EXECUTE FUNCTION bound_exhibition_preview_mutation_receipts();
                REVOKE ALL ON exhibition_preview_state FROM PUBLIC;
                REVOKE ALL ON exhibition_preview_mutation_receipts FROM PUBLIC;
                GRANT USAGE ON SCHEMA public TO ai_stocks_web_runtime;
                GRANT SELECT, INSERT, UPDATE ON exhibition_preview_state TO ai_stocks_web_runtime;
                GRANT SELECT, INSERT ON exhibition_preview_mutation_receipts TO ai_stocks_web_runtime;
                """)) await schema.ExecuteNonQueryAsync();

            await dataSource.DisposeAsync();
            var runtimeBuilder = new NpgsqlConnectionStringBuilder(configuredBuilder.ConnectionString)
            {
                Options = "-c role=ai_stocks_web_runtime"
            };
            dataSource = NpgsqlDataSource.Create(runtimeBuilder.ConnectionString);

            var persistence = new PostgresPreviewRaceStatePersistence(dataSource,
                DelayedNasdaqInstrumentStore.DataMode);
            var snapshot = new InstrumentListDto(
                [new("SE0000108656", "ERIC-B", "Ericsson B", "XSTO", "Sweden", "SEK", 100m, null, false,
                    ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true)],
                DelayedNasdaqInstrumentStore.DataMode);
            var request = new AiDecisionRequestDto("postgres-buy-001", Agent.Id, Agent.ModelId, "buy",
                "SE0000108656", 10, "Verified durable decision.", 0.75m,
                [new("https://example.com/research", AvailableAt, "Exact verified excerpt.", new string('a', 64))],
                "copilot", Agent.ModelId, new string('b', 64), AvailableAt.AddMinutes(2), 100m, AvailableAt);
            var first = new PreviewRaceStore(TimeProvider.System, persistence);
            first.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null,
                request.CompletedAt.AddSeconds(-2)));
            first.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null,
                request.CompletedAt.AddSeconds(-1)));
            Assert.False(first.SubmitAi(request, snapshot).Replayed);

            var restarted = new PreviewRaceStore(TimeProvider.System,
                new PostgresPreviewRaceStatePersistence(dataSource, DelayedNasdaqInstrumentStore.DataMode));
            var participant = restarted.AiProgress(snapshot).Participants.Single(item => item.AgentId == Agent.Id);
            Assert.Equal("succeeded", participant.Status);
            Assert.Equal("postgres-buy-001", participant.LatestDecision!.RunId);
            Assert.Equal(10, Assert.Single(participant.Portfolio.Holdings).Quantity);
            Assert.True(restarted.SubmitAi(request, snapshot).Replayed);

            var oldReceipt = Guid.NewGuid();
            var newestReceipt = Guid.NewGuid();
            await using (var receipts = dataSource.CreateCommand("""
                INSERT INTO exhibition_preview_mutation_receipts(mutation_id,state_revision,state_key)
                VALUES ($1,1,'official-nasdaq-xsto-15m-delayed'),
                       ($2,100001,'official-nasdaq-xsto-15m-delayed')
                """))
            {
                receipts.Parameters.AddWithValue(oldReceipt);
                receipts.Parameters.AddWithValue(newestReceipt);
                await receipts.ExecuteNonQueryAsync();
            }
            Assert.False(persistence.WasCommitted(oldReceipt));
            Assert.True(persistence.WasCommitted(newestReceipt));

            await using var oversized = dataSource.CreateCommand(
                "UPDATE exhibition_preview_state SET state_json=$1::jsonb WHERE state_key='official-nasdaq-xsto-15m-delayed'");
            oversized.Parameters.AddWithValue($"{{\"payload\":\"{new string('x', 4 * 1024 * 1024)}\"}}");
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => oversized.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
        }
        finally
        {
            if (dataSource is not null) await dataSource.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Failed_database_write_rolls_back_trade_and_does_not_consume_run_id()
    {
        var persistence = new FakePersistence();
        var snapshot = new InstrumentListDto(
            [new("SE0000108656", "ERIC-B", "Ericsson B", "XSTO", "Sweden", "SEK", 100m, null, false,
                ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true)],
            DelayedNasdaqInstrumentStore.DataMode);
        var request = new AiDecisionRequestDto("rollback-buy-001", Agent.Id, Agent.ModelId, "buy",
            "SE0000108656", 10, "Verified durable decision.", 0.75m,
            [new("https://example.com/research", AvailableAt, "Exact verified excerpt.", new string('a', 64))],
            "copilot", Agent.ModelId, new string('b', 64), AvailableAt.AddMinutes(2), 100m, AvailableAt);
        var store = new PreviewRaceStore(TimeProvider.System, persistence);
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null,
            request.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null,
            request.CompletedAt.AddSeconds(-1)));
        persistence.FailNextSave = true;

        Assert.Throws<PreviewRacePersistenceException>(() => store.SubmitAi(request, snapshot));
        var failed = store.AiProgress(snapshot).Participants.Single(item => item.AgentId == Agent.Id);
        Assert.Equal(PreviewRaceStore.StartingCashDkk, failed.Portfolio.CashDkk);
        Assert.Empty(failed.Portfolio.Holdings);
        Assert.DoesNotContain(store.AiProgress(snapshot).Activity, item => item.RunId == request.RunId);

        Assert.False(store.SubmitAi(request, snapshot).Replayed);
    }

    [Fact]
    public void Lost_save_response_reconciles_the_committed_state()
    {
        var persistence = new FakePersistence();
        var store = new PreviewRaceStore(TimeProvider.System, persistence);
        var occurredAt = AvailableAt.AddMinutes(2);
        persistence.CommitThenThrow = true;

        store.UpdateAiStatus(new("uncertain-001", Agent.Id, Agent.ModelId, "queued", null, occurredAt));

        var restored = new PreviewRaceStore(TimeProvider.System, persistence);
        var participant = restored.AiProgress().Participants.Single(item => item.AgentId == Agent.Id);
        Assert.Equal("queued", participant.Status);
        Assert.Equal("uncertain-001", participant.RunId);
    }

    [Fact]
    public void Lost_save_response_reconciles_after_a_superseding_write()
    {
        var persistence = new FakePersistence { CommitThenSupersedeAndThrow = true };
        var store = new PreviewRaceStore(TimeProvider.System, persistence);
        var occurredAt = AvailableAt.AddMinutes(2);

        store.UpdateAiStatus(new("superseded-001", Agent.Id, Agent.ModelId, "queued", null, occurredAt));

        var participant = store.AiProgress().Participants.Single(item => item.AgentId == Agent.Id);
        Assert.Equal("running", participant.Status);
        Assert.Equal("superseded-001", participant.RunId);
    }

    [Fact]
    public void Existing_instances_refresh_durable_state_before_reads_and_mutations()
    {
        var persistence = new FakePersistence();
        var first = new PreviewRaceStore(TimeProvider.System, persistence);
        var second = new PreviewRaceStore(TimeProvider.System, persistence);
        var occurredAt = AvailableAt.AddMinutes(2);

        first.UpdateAiStatus(new("shared-001", Agent.Id, Agent.ModelId, "queued", null, occurredAt));
        second.UpdateAiStatus(new("shared-001", Agent.Id, Agent.ModelId, "running", null, occurredAt.AddSeconds(1)));

        var participant = first.AiProgress().Participants.Single(item => item.AgentId == Agent.Id);
        Assert.Equal("running", participant.Status);
        Assert.Equal("shared-001", participant.RunId);
    }

    [Fact]
    public void Null_persisted_collections_fail_with_a_bounded_domain_exception()
    {
        var persistence = new FakePersistence();
        persistence.Seed("{\"SchemaVersion\":1,\"Accounts\":null,\"Runs\":[],\"Activity\":[]}");

        var exception = Assert.Throws<PreviewRacePersistenceException>(
            () => new PreviewRaceStore(TimeProvider.System, persistence));

        Assert.Null(exception.InnerException);
        Assert.Equal("Persisted preview state violates its bounded schema.", exception.Message);
    }

    [Fact]
    public void Incoherent_persisted_lifecycle_fails_closed()
    {
        var persistence = new FakePersistence();
        var store = new PreviewRaceStore(TimeProvider.System, persistence);
        store.UpdateAiStatus(new("invalid-lifecycle-001", Agent.Id, Agent.ModelId, "queued", null,
            AvailableAt.AddMinutes(2)));
        var json = JsonNode.Parse(persistence.Json!)!;
        var account = json["Accounts"]!.AsArray().Single(item =>
            item!["AgentId"]!.GetValue<Guid>() == Agent.Id)!;
        account["Status"] = "succeeded";
        persistence.Seed(json.ToJsonString());

        var exception = Assert.Throws<PreviewRacePersistenceException>(
            () => new PreviewRaceStore(TimeProvider.System, persistence));

        Assert.Null(exception.InnerException);
        Assert.Equal("Persisted preview account violates its bounded schema.", exception.Message);
    }

    [Theory]
    [InlineData("timestamp-order")]
    [InlineData("missing-latest-decision")]
    [InlineData("duplicate-seen-run")]
    [InlineData("invalid-sha")]
    [InlineData("invalid-evidence-sha")]
    [InlineData("wrong-runtime-provider")]
    [InlineData("wrong-runtime-model")]
    [InlineData("http-evidence")]
    [InlineData("future-evidence-time")]
    [InlineData("incomplete-mark")]
    [InlineData("null-account")]
    [InlineData("invalid-activity-time")]
    [InlineData("orphan-run")]
    [InlineData("cross-agent-run")]
    [InlineData("null-performance")]
    [InlineData("extra-mark")]
    [InlineData("wrong-mark-flags")]
    [InlineData("missing-activity")]
    [InlineData("duplicate-activity")]
    [InlineData("missing-trade-evidence")]
    [InlineData("wrong-fill-fx")]
    [InlineData("wrong-fill-total")]
    [InlineData("zero-cost-basis")]
    [InlineData("missing-current-activity-at-cap")]
    [InlineData("wrong-mark-dkk")]
    [InlineData("wrong-activity-reason")]
    [InlineData("invalid-run-characters")]
    [InlineData("wrong-cash")]
    [InlineData("wrong-holding-quantity")]
    [InlineData("wrong-positive-cost-basis")]
    [InlineData("wrong-performance-value")]
    [InlineData("missing-decision-performance")]
    public void Corrupt_persisted_state_is_rejected_with_a_domain_exception(string corruption)
    {
        var persistence = PersistedTradeState();
        var document = JsonNode.Parse(persistence.Json!)!;
        var accounts = document["Accounts"]!.AsArray();
        var account = accounts.Single(item => item!["AgentId"]!.GetValue<Guid>() == Agent.Id)!;
        switch (corruption)
        {
            case "timestamp-order":
                account["StartedAt"] = account["QueuedAt"]!.GetValue<DateTimeOffset>().AddSeconds(-1);
                break;
            case "missing-latest-decision":
                account["LatestDecision"] = null;
                break;
            case "duplicate-seen-run":
                account["SeenRunIds"]!.AsArray().Add(account["RunId"]!.GetValue<string>());
                break;
            case "invalid-sha":
                document["Runs"]![0]!["Decision"]!["attestation"]!["reportSha256"] = new string('z', 64);
                account["LatestDecision"]!["attestation"]!["reportSha256"] = new string('z', 64);
                break;
            case "invalid-evidence-sha":
                document["Runs"]![0]!["Decision"]!["evidence"]![0]!["contentSha256"] = new string('z', 64);
                account["LatestDecision"]!["evidence"]![0]!["contentSha256"] = new string('z', 64);
                break;
            case "wrong-runtime-provider":
                document["Runs"]![0]!["Decision"]!["attestation"]!["runtimeProvider"] = "other";
                account["LatestDecision"]!["attestation"]!["runtimeProvider"] = "other";
                break;
            case "wrong-runtime-model":
                document["Runs"]![0]!["Decision"]!["attestation"]!["runtimeModel"] = "other-model";
                account["LatestDecision"]!["attestation"]!["runtimeModel"] = "other-model";
                break;
            case "http-evidence":
                document["Runs"]![0]!["Decision"]!["evidence"]![0]!["url"] = "http://example.com/research";
                account["LatestDecision"]!["evidence"]![0]!["url"] = "http://example.com/research";
                break;
            case "future-evidence-time":
                var future = account["LatestDecision"]!["completedAt"]!.GetValue<DateTimeOffset>().AddMinutes(1);
                document["Runs"]![0]!["Decision"]!["evidence"]![0]!["publishedAt"] = future;
                account["LatestDecision"]!["evidence"]![0]!["publishedAt"] = future;
                break;
            case "incomplete-mark":
                account["Marks"]!["SE0000108656"]!["priceDkk"] = null;
                break;
            case "null-account":
                accounts[0] = null;
                break;
            case "invalid-activity-time":
                document["Activity"]![0]!["occurredAt"] = default(DateTimeOffset);
                break;
            case "orphan-run":
                var orphan = document["Runs"]![0]!.DeepClone();
                orphan["RunId"] = "orphan-run-001";
                orphan["Decision"]!["runId"] = "orphan-run-001";
                document["Runs"]!.AsArray().Add(orphan);
                break;
            case "cross-agent-run":
                accounts.First(item => item!["AgentId"]!.GetValue<Guid>() != Agent.Id)!["SeenRunIds"]!
                    .AsArray().Add(account["RunId"]!.GetValue<string>());
                break;
            case "null-performance":
                account["Performance"] = new JsonArray((JsonNode?)null);
                break;
            case "extra-mark":
                var extraMark = account["Marks"]!["SE0000108656"]!.DeepClone();
                extraMark["id"] = "EXTRA";
                account["Marks"]!["EXTRA"] = extraMark;
                break;
            case "wrong-mark-flags":
                account["Marks"]!["SE0000108656"]!["paperTradable"] = false;
                break;
            case "missing-activity":
                document["Activity"] = new JsonArray();
                break;
            case "duplicate-activity":
                document["Activity"]!.AsArray().Add(document["Activity"]![0]!.DeepClone());
                break;
            case "missing-trade-evidence":
                document["Runs"]![0]!["Decision"]!["evidence"] = new JsonArray();
                account["LatestDecision"]!["evidence"] = new JsonArray();
                break;
            case "wrong-fill-fx":
                document["Runs"]![0]!["Decision"]!["assumedPaperFill"]!["assumedSekToDkk"] = 1m;
                account["LatestDecision"]!["assumedPaperFill"]!["assumedSekToDkk"] = 1m;
                break;
            case "wrong-fill-total":
                document["Runs"]![0]!["Decision"]!["assumedPaperFill"]!["totalDkk"] = 1m;
                account["LatestDecision"]!["assumedPaperFill"]!["totalDkk"] = 1m;
                break;
            case "zero-cost-basis":
                account["CostBasisDkk"]!["SE0000108656"] = 0m;
                break;
            case "missing-current-activity-at-cap":
                var cappedActivity = new JsonArray();
                var completedAt = account["LatestDecision"]!["completedAt"]!.GetValue<DateTimeOffset>();
                for (var index = 0; index < 100; index++)
                {
                    var runId = $"failed-history-{index:000}";
                    account["SeenRunIds"]!.AsArray().Add(runId);
                    cappedActivity.Add(new JsonObject
                    {
                        ["runId"] = runId,
                        ["agentId"] = Agent.Id,
                        ["modelId"] = Agent.ModelId,
                        ["status"] = "failed",
                        ["action"] = null,
                        ["reason"] = null,
                        ["error"] = "Bounded historical failure.",
                        ["occurredAt"] = completedAt.AddMinutes(-200 + index)
                    });
                }
                document["Activity"] = cappedActivity;
                break;
            case "wrong-mark-dkk":
                account["Marks"]!["SE0000108656"]!["priceDkk"] = 1m;
                break;
            case "wrong-activity-reason":
                document["Activity"]![0]!["reason"] = "Different reason.";
                break;
            case "invalid-run-characters":
                const string invalidRunId = "invalid\nrun-id";
                var originalRunId = account["RunId"]!.GetValue<string>();
                account["RunId"] = invalidRunId;
                account["SeenRunIds"]!.AsArray()[0] = invalidRunId;
                account["LatestDecision"]!["runId"] = invalidRunId;
                document["Runs"]![0]!["RunId"] = invalidRunId;
                document["Runs"]![0]!["Decision"]!["runId"] = invalidRunId;
                document["Activity"]![0]!["runId"] = invalidRunId;
                Assert.NotEqual(invalidRunId, originalRunId);
                break;
            case "wrong-cash":
                account["CashDkk"] = 99_000m;
                break;
            case "wrong-holding-quantity":
                account["Holdings"]!["SE0000108656"] = 11;
                break;
            case "wrong-positive-cost-basis":
                account["CostBasisDkk"]!["SE0000108656"] = 1m;
                break;
            case "wrong-performance-value":
                account["Performance"]!.AsArray()[^1]!["valueDkk"] = 1m;
                break;
            case "missing-decision-performance":
                account["Performance"]!.AsArray().RemoveAt(account["Performance"]!.AsArray().Count - 1);
                break;
        }
        persistence.Seed(document.ToJsonString());

        var exception = Assert.Throws<PreviewRacePersistenceException>(
            () => new PreviewRaceStore(TimeProvider.System, persistence));
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Postgres_failures_expose_only_a_fixed_redacted_exception()
    {
        const string secret = "not-a-real-secret-password";
        using var dataSource = NpgsqlDataSource.Create(
            $"Host=127.0.0.1;Port=1;Database=private_database;Username=private_user;Password={secret};Timeout=1");
        var persistence = new PostgresPreviewRaceStatePersistence(dataSource,
            DelayedNasdaqInstrumentStore.DataMode);

        var exception = Assert.Throws<PreviewRacePersistenceException>(() => persistence.Load());

        Assert.Null(exception.InnerException);
        Assert.Equal("Could not load durable exhibition state.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private_database", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Exhibition_host_fails_startup_when_durable_state_is_unavailable()
    {
        using var factory = new AiExhibitionApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IPreviewRaceStatePersistence, FailingLoadPersistence>()));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Could not load durable exhibition state.", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class FailingLoadPersistence : IPreviewRaceStatePersistence
    {
        public PreviewRacePersistedState? Load() =>
            throw new PreviewRacePersistenceException("Could not load durable exhibition state.");

        public PreviewRacePersistedState Save(long? expectedRevision, string json, Guid mutationId) =>
            throw new NotSupportedException();

        public bool WasCommitted(Guid mutationId) => false;
    }

    private static (FakePersistence Persistence, InstrumentListDto Snapshot) PersistedNordicTradeState()
    {
        var persistence = new FakePersistence();
        var fxAvailableAt = DateTimeOffset.Parse("2026-08-14T14:10:00Z");
        var instrument = new InstrumentDto("XCSE:DK0010181676:CARL-A", "CARL-A", "Carlsberg A A/S",
            "XCSE", "Denmark", "DKK", 750m, 750m, false, ExecutedAt, AvailableAt,
            "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true, 1m,
            new DateOnly(2026, 8, 14), fxAvailableAt, MarketDataProvenance.EcbInformationalReferenceRates,
            new string('c', 64));
        var snapshot = new InstrumentListDto([instrument], DelayedNasdaqInstrumentStore.NordicDataMode);
        var request = new AiDecisionRequestDto("durable-nordic-001", Agent.Id, Agent.ModelId, "buy",
            instrument.Id, 10, "Verified durable Nordic decision.", 0.75m,
            [new("https://example.com/research", AvailableAt, "Exact verified excerpt.", new string('a', 64))],
            "copilot", Agent.ModelId, new string('b', 64), AvailableAt.AddMinutes(2), null, AvailableAt,
            750m, "DKK", "XCSE", 1m, ExecutedAt, fxAvailableAt, new string('c', 64),
            new DateOnly(2026, 8, 14), MarketDataProvenance.EcbInformationalReferenceRates);
        var store = new PreviewRaceStore(TimeProvider.System, persistence,
            DelayedNasdaqInstrumentStore.NordicDataMode, PreviewRaceStore.NordicAssumedExecutionMode);
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null,
            request.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null,
            request.CompletedAt.AddSeconds(-1)));
        store.SubmitAi(request, snapshot);
        return (persistence, snapshot);
    }

    private static FakePersistence PersistedTradeState()
    {
        var persistence = new FakePersistence();
        var snapshot = new InstrumentListDto(
            [new("SE0000108656", "ERIC-B", "Ericsson B", "XSTO", "Sweden", "SEK", 100m, null, false,
                ExecutedAt, AvailableAt, "Nasdaq Nordic MiFID II delayed post-trade", 15, false, true)],
            DelayedNasdaqInstrumentStore.DataMode);
        var request = new AiDecisionRequestDto("corruption-buy-001", Agent.Id, Agent.ModelId, "buy",
            "SE0000108656", 10, "Verified durable decision.", 0.75m,
            [new("https://example.com/research", AvailableAt, "Exact verified excerpt.", new string('a', 64))],
            "copilot", Agent.ModelId, new string('b', 64), AvailableAt.AddMinutes(2), 100m, AvailableAt);
        var store = new PreviewRaceStore(TimeProvider.System, persistence);
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null,
            request.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null,
            request.CompletedAt.AddSeconds(-1)));
        store.SubmitAi(request, snapshot);
        return persistence;
    }

    private sealed class FakePersistence : IPreviewRaceStatePersistence
    {
        private PreviewRacePersistedState? state;
        private readonly HashSet<Guid> receipts = [];
        public bool FailNextSave { get; set; }
        public bool CommitThenThrow { get; set; }
        public bool CommitThenSupersedeAndThrow { get; set; }
        public int SaveCalls { get; private set; }
        public string? Json => state?.Json;

        public void Seed(string json) => state = new PreviewRacePersistedState(1, json);

        public PreviewRacePersistedState? Load() => state;

        public PreviewRacePersistedState Save(long? expectedRevision, string json, Guid mutationId)
        {
            SaveCalls++;
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new PreviewRacePersistenceException("Injected database failure.");
            }
            if (state?.Revision != expectedRevision)
                throw new PreviewRacePersistenceException("Concurrent preview state update.");
            state = new PreviewRacePersistedState((state?.Revision ?? 0) + 1, json);
            receipts.Add(mutationId);
            if (CommitThenSupersedeAndThrow)
            {
                CommitThenSupersedeAndThrow = false;
                var document = JsonNode.Parse(state.Json)!;
                var account = document["Accounts"]!.AsArray().Single(item =>
                    item!["AgentId"]!.GetValue<Guid>() == Agent.Id)!;
                account["Status"] = "running";
                account["StartedAt"] = account["QueuedAt"]!.GetValue<DateTimeOffset>().AddSeconds(1);
                state = new PreviewRacePersistedState(state.Revision + 1, document.ToJsonString());
                throw new PreviewRacePersistenceException("Injected lost response after superseding write.");
            }
            if (CommitThenThrow)
            {
                CommitThenThrow = false;
                throw new PreviewRacePersistenceException("Injected lost save response.");
            }
            return state;
        }

        public bool WasCommitted(Guid mutationId) => receipts.Contains(mutationId);
    }
}
