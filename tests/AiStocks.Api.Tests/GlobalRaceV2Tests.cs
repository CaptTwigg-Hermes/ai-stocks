using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStocks.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiStocks.Api.Tests;

public sealed class GlobalRaceV2Tests
{
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
