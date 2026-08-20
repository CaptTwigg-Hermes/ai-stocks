using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStocks.Api;
using AiStocks.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace AiStocks.Api.Tests;

public sealed class GlobalRaceV2Tests
{
    [Fact]
    public void Non_testing_global_v2_requires_database_url()
    {
        using var factory = new GlobalV2ProductionWithoutDatabaseFactory();

        var exception = Assert.Throws<RuntimeConfigurationException>(() => factory.CreateClient());

        Assert.Equal("DATABASE_URL is required.", exception.Message);
    }

    [Fact]
    public async Task Postgres_store_survives_restart_serializes_join_and_enforces_principal_order_boundaries()
    {
        var configured = Environment.GetEnvironmentVariable("AISTOCKS_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) return;
        await using var database = await GlobalV2TestDatabase.CreateAsync(configured);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await new PostgresMigrationRunner(dataSource).ApplyAsync();
        var firstStore = new PostgresGlobalRaceStore(dataSource, TimeProvider.System);

        var joins = await Task.WhenAll(
            Task.Run(() => firstStore.Join("durable@example.com", GlobalRaceStore.HumanSandboxRaceId, "concurrent-join-key")),
            Task.Run(() => firstStore.Join("durable@example.com", GlobalRaceStore.HumanSandboxRaceId, "concurrent-join-key")));

        Assert.Single(joins, result => !result.Replayed);
        Assert.Single(joins, result => result.Replayed);
        Assert.Equal(joins[0].Participant.Id, joins[1].Participant.Id);
        Assert.Single(firstStore.LedgerEvents(joins[0].Participant.Id), item => item.EventType == "initial_cash");

        var restarted = new PostgresGlobalRaceStore(dataSource, TimeProvider.System);
        var durablePortfolio = restarted.Portfolio("durable@example.com", GlobalRaceStore.HumanSandboxRaceId);
        Assert.Equal(100_000m, durablePortfolio.CashDkk);
        Assert.DoesNotContain("durable@example.com", durablePortfolio.DisplayName, StringComparison.OrdinalIgnoreCase);
        var request = new GlobalHumanOrderRequest("buy", "novo-dk", 2, "durable intent");
        var order = restarted.SubmitHumanOrder("durable@example.com", GlobalRaceStore.HumanSandboxRaceId,
            "durable-order-key", request);
        var replayed = new PostgresGlobalRaceStore(dataSource, TimeProvider.System).SubmitHumanOrder(
            "durable@example.com", GlobalRaceStore.HumanSandboxRaceId, "durable-order-key", request);
        Assert.True(replayed.Replayed);
        Assert.Equal(order.Order.Id, replayed.Order.Id);

        var denied = Assert.Throws<GlobalRaceException>(() => restarted.Cancel("other@example.com",
            GlobalRaceStore.HumanSandboxRaceId, order.Order.Id, "other-cancel-key"));
        Assert.Equal("portfolio-not-found", denied.Code);
        restarted.Cancel("durable@example.com", GlobalRaceStore.HumanSandboxRaceId, order.Order.Id, "own-cancel-key");
        Assert.Equal("cancelled", Assert.Single(new PostgresGlobalRaceStore(dataSource, TimeProvider.System)
            .Orders("durable@example.com", GlobalRaceStore.HumanSandboxRaceId)).Status);

        var aiRequest = new GlobalAiOrderRequest("gpt-5.6-sol", "buy", "asml-nl", 1,
            new GlobalAiRationale("Durable AI thesis",
                [new("https://example.invalid/evidence", DateTimeOffset.UtcNow.AddMinutes(-5),
                    "Exact excerpt", new string('a', 64))], 0.7m));
        var aiOrder = restarted.SubmitAiOrder(GlobalRaceStore.AiLeagueRaceId, "durable-ai-order-key", aiRequest);
        var aiReplay = new PostgresGlobalRaceStore(dataSource, TimeProvider.System).SubmitAiOrder(
            GlobalRaceStore.AiLeagueRaceId, "durable-ai-order-key", aiRequest);
        Assert.False(aiOrder.Replayed);
        Assert.True(aiReplay.Replayed);
        Assert.Equal(aiOrder.Order.Id, aiReplay.Order.Id);
        Assert.NotEqual(Guid.Empty, aiOrder.Order.ParticipantId);

        await using var count = dataSource.CreateCommand(
            "SELECT count(*) FROM v2_ledger_events WHERE participant_id=$1 AND event_type='initial_cash'");
        count.Parameters.AddWithValue(joins[0].Participant.Id);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public void Join_is_idempotent_and_creates_exactly_one_initial_cash_event()
    {
        var store = new GlobalRaceStore(TimeProvider.System);

        var first = store.Join("human@example.com", GlobalRaceStore.HumanSandboxRaceId, "join-key-0001");
        var replay = store.Join("human@example.com", GlobalRaceStore.HumanSandboxRaceId, "join-key-0001");

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Participant.Id, replay.Participant.Id);
        Assert.Equal(100_000m, store.Portfolio("human@example.com", GlobalRaceStore.HumanSandboxRaceId).CashDkk);
        Assert.Single(store.LedgerEvents(first.Participant.Id), item =>
            item.EventType == "initial_cash" && item.CashDeltaDkk == 100_000m);
        var publicEntry = Assert.Single(store.Leaderboard(GlobalRaceStore.HumanSandboxRaceId));
        Assert.DoesNotContain("human@example.com", publicEntry.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Human ", publicEntry.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void Human_order_is_an_unfilled_intent_with_optional_note_and_identity_bound_hash()
    {
        var store = new GlobalRaceStore(TimeProvider.System);
        store.Join("human@example.com", GlobalRaceStore.HumanSandboxRaceId, "join-key-0001");

        var first = store.SubmitHumanOrder("human@example.com", GlobalRaceStore.HumanSandboxRaceId,
            "order-key-0001", new GlobalHumanOrderRequest("buy", "novo-dk", 2, "Long term paper idea"));
        var replay = store.SubmitHumanOrder("human@example.com", GlobalRaceStore.HumanSandboxRaceId,
            "order-key-0001", new GlobalHumanOrderRequest("buy", "novo-dk", 2, "Long term paper idea"));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Order.Id, replay.Order.Id);
        Assert.Equal("queued", first.Order.Status);
        Assert.Null(first.Order.FillPriceDkk);
        Assert.Equal("Long term paper idea", first.Order.Note);
        Assert.Throws<GlobalRaceException>(() => store.SubmitHumanOrder("other@example.com",
            GlobalRaceStore.HumanSandboxRaceId, "order-key-0001",
            new GlobalHumanOrderRequest("buy", "novo-dk", 2, "Long term paper idea")));
    }

    [Fact]
    public void Ai_trade_requires_trusted_model_identity_structured_rationale_and_bounded_intent()
    {
        var store = new GlobalRaceStore(TimeProvider.System);
        var evidence = new[]
        {
            new GlobalEvidence("https://example.invalid/evidence", DateTimeOffset.UtcNow.AddMinutes(-5),
                "Exact public excerpt", new string('a', 64))
        };
        var rationale = new GlobalAiRationale("Bounded thesis", evidence, 0.8m);
        var valid = store.SubmitAiOrder(GlobalRaceStore.AiLeagueRaceId, "ai-order-key-0001",
            new GlobalAiOrderRequest("gpt-5.6-sol", "buy", "novo-dk", 1, rationale));

        Assert.Equal("queued", valid.Order.Status);
        Assert.Null(valid.Order.FillPriceDkk);
        var model = Assert.Throws<GlobalRaceException>(() => store.SubmitAiOrder(
            GlobalRaceStore.AiLeagueRaceId, "ai-order-key-0002",
            new GlobalAiOrderRequest("attacker-selected-model", "buy", "novo-dk", 1, rationale)));
        Assert.Equal("invalid-model", model.Code);
        var quantity = Assert.Throws<GlobalRaceException>(() => store.SubmitAiOrder(
            GlobalRaceStore.AiLeagueRaceId, "ai-order-key-0003",
            new GlobalAiOrderRequest("gpt-5.6-sol", "buy", "novo-dk", 100_001, rationale)));
        Assert.Equal("invalid-order", quantity.Code);
    }

    [Fact]
    public async Task Authenticated_v2_surface_lists_races_searches_global_index_and_enforces_self_portfolio()
    {
        await using var factory = new GlobalV2ApiFactory();
        using var human = factory.CreateClient();
        human.DefaultRequestHeaders.Add("X-Test-User-Email", "human@example.com");
        human.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");

        var races = await human.GetFromJsonAsync<JsonElement>("/api/v1/races");
        Assert.Equal(new[] { "ai_league", "human_sandbox", "mixed_exhibition" },
            races.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).Order().ToArray());
        var instruments = await human.GetFromJsonAsync<JsonElement>("/api/v1/instruments/search?q=novo");
        Assert.Equal("non-production-approved-provider-index-fixture",
            instruments.GetProperty("dataMode").GetString());
        Assert.Equal("NOVO B", instruments.GetProperty("items")[0].GetProperty("symbol").GetString());

        using (var hostileJoin = new HttpRequestMessage(HttpMethod.Post,
                   $"/api/v1/races/{GlobalRaceStore.HumanSandboxRaceId}/join"))
        {
            hostileJoin.Headers.Add("Origin", "https://evil.example");
            hostileJoin.Headers.Add("Idempotency-Key", "hostile-join-0001");
            Assert.Equal(HttpStatusCode.Forbidden, (await human.SendAsync(hostileJoin)).StatusCode);
        }
        human.DefaultRequestHeaders.Add("Origin", "https://stocks.example.com");
        human.DefaultRequestHeaders.Add("Idempotency-Key", "join-http-0001");
        using var joined = await human.PostAsync($"/api/v1/races/{GlobalRaceStore.HumanSandboxRaceId}/join", null);
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var racesAfterJoin = await human.GetFromJsonAsync<JsonElement>("/api/v1/races");
        Assert.True(racesAfterJoin.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == GlobalRaceStore.HumanSandboxRaceId)
            .GetProperty("joined").GetBoolean());
        var own = await human.GetFromJsonAsync<JsonElement>(
            $"/api/v1/races/{GlobalRaceStore.HumanSandboxRaceId}/accounts/me/portfolio");
        Assert.Equal(100_000m, own.GetProperty("cashDkk").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound,
            (await human.GetAsync($"/api/v1/races/{GlobalRaceStore.HumanSandboxRaceId}/portfolio")).StatusCode);

        using var other = factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Test-User-Email", "other@example.com");
        other.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");
        var otherRaces = await other.GetFromJsonAsync<JsonElement>("/api/v1/races");
        Assert.False(otherRaces.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == GlobalRaceStore.HumanSandboxRaceId)
            .GetProperty("joined").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.GetAsync($"/api/v1/races/{GlobalRaceStore.HumanSandboxRaceId}/accounts/me/portfolio")).StatusCode);
    }
}

internal sealed class GlobalV2ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("GLOBAL_V2_MODE", "1");
    }
}

internal sealed class GlobalV2TestDatabase : IAsyncDisposable
{
    private readonly string adminConnectionString;
    private readonly string databaseName;

    private GlobalV2TestDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<GlobalV2TestDatabase> CreateAsync(string configured)
    {
        var builder = new NpgsqlConnectionStringBuilder(configured);
        if (!(builder.Database ?? string.Empty).Contains("test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AISTOCKS_TEST_DATABASE_URL database name must contain test.");
        var name = $"ai_stocks_global_v2_test_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(builder.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE template0", connection);
            await command.ExecuteNonQueryAsync();
        }
        builder.Database = name;
        return new GlobalV2TestDatabase(admin.ConnectionString, name, builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

internal sealed class GlobalV2ProductionWithoutDatabaseFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("GLOBAL_V2_MODE", "1");
        builder.UseSetting("GLOBAL_V2_RUNNER_KEY", "global-v2-runner-key-at-least-32-chars");
        builder.UseSetting("UI_ORIGINS", "https://stocks.example.com");
        builder.UseSetting("ACCESS_TEAM_DOMAIN", "https://contest.cloudflareaccess.com");
        builder.UseSetting("ACCESS_AUD", "production-audience");
        builder.UseSetting("API_PUBLIC_ORIGIN", "https://api.stocks.example.com");
        builder.UseSetting("ACCESS_OWNER_EMAILS", "owner@example.com");
        builder.UseSetting("ACCESS_VIEWER_EMAILS", "viewer@example.com");
        builder.UseSetting("DATABASE_URL", null);
    }
}
