using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiStocks.Api.Tests;

public sealed class PreviewTradingFlowTests
{
    [Fact]
    public async Task Human_can_search_buy_without_note_and_see_updated_portfolio()
    {
        await using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "human-flow@example.com");
        client.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "human-buy-0001");

        var instruments = await client.GetFromJsonAsync<JsonElement>("/api/v1/instruments?query=apple");
        var apple = instruments.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("AAPL", apple.GetProperty("symbol").GetString());
        Assert.True(apple.GetProperty("isPreviewPrice").GetBoolean());

        var before = await client.GetFromJsonAsync<JsonElement>("/api/v1/portfolio");
        Assert.Equal(100_000m, before.GetProperty("cashDkk").GetDecimal());

        using var response = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            side = "buy",
            instrumentId = apple.GetProperty("id").GetString(),
            quantity = 10,
            note = ""
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filled", order.GetProperty("status").GetString());

        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/portfolio");
        Assert.True(after.GetProperty("cashDkk").GetDecimal() < 100_000m);
        Assert.Equal(10, after.GetProperty("holdings")[0].GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Sell_without_holdings_fails_closed_as_json_problem()
    {
        await using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "sell-flow@example.com");
        client.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "human-sell-0001");

        using var response = await client.PostAsJsonAsync("/api/v1/orders", new
        {
            side = "sell",
            instrumentId = "aapl-us",
            quantity = 1
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient-holdings", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Duplicate_idempotency_key_replays_exactly_once_and_viewer_cannot_trade()
    {
        await using var factory = new ApiApplicationFactory();
        using var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Add("X-Test-User-Email", "idempotent@example.com");
        owner.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");
        owner.DefaultRequestHeaders.Add("Idempotency-Key", "duplicate-order-0001");
        var request = new { side = "buy", instrumentId = "aapl-us", quantity = 1 };

        using var first = await owner.PostAsJsonAsync("/api/v1/orders", request);
        using var replay = await owner.PostAsJsonAsync("/api/v1/orders", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());

        var portfolio = await owner.GetFromJsonAsync<JsonElement>("/api/v1/portfolio");
        Assert.Equal(1, portfolio.GetProperty("holdings")[0].GetProperty("quantity").GetInt32());

        using var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");
        viewer.DefaultRequestHeaders.Add("X-Test-User-Role", "viewer");
        viewer.DefaultRequestHeaders.Add("Idempotency-Key", "viewer-order-0001");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsJsonAsync("/api/v1/orders", request)).StatusCode);
    }

    [Fact]
    public void Idempotency_does_not_evict_accepted_keys_when_capacity_is_reached()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var identity = "capacity@example.com";
        var first = store.Submit(identity, "capacity-key-0000",
            new HumanOrderRequestDto("buy", "aapl-us", 1, null));

        for (var index = 1; index < PreviewRaceStore.MaximumIdempotencyEntries; index++)
        {
            var side = index % 2 == 1 ? "sell" : "buy";
            store.Submit(identity, $"capacity-key-{index:0000}",
                new HumanOrderRequestDto(side, "aapl-us", 1, null));
        }

        var rejected = Assert.Throws<PreviewOrderException>(() => store.Submit(identity,
            "capacity-overflow", new HumanOrderRequestDto("sell", "aapl-us", 1, null)));
        var replay = store.Submit(identity, "capacity-key-0000",
            new HumanOrderRequestDto("buy", "aapl-us", 1, null));

        Assert.Equal("idempotency-capacity", rejected.Code);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Order.Id, replay.Order.Id);
    }

    [Fact]
    public async Task Order_rate_limit_rejects_the_twenty_first_request()
    {
        await using var factory = new ApiApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "rate-limit@example.com");
        client.DefaultRequestHeaders.Add("X-Test-User-Role", "owner");

        for (var index = 0; index < 20; index++)
        {
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"rate-limit-{index:0000}");
            var side = index % 2 == 0 ? "buy" : "sell";
            using var accepted = await client.PostAsJsonAsync("/api/v1/orders",
                new { side, instrumentId = "aapl-us", quantity = 1 });
            Assert.True(accepted.IsSuccessStatusCode, $"request {index + 1}: {accepted.StatusCode}");
        }

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "rate-limit-overflow");
        using var limited = await client.PostAsJsonAsync("/api/v1/orders",
            new { side = "buy", instrumentId = "aapl-us", quantity = 1 });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public void Preview_leaderboard_keeps_all_four_fixed_ai_competitors()
    {
        var leaderboard = new PreviewRaceStore(TimeProvider.System).Leaderboard("human@example.com");
        var aiNames = leaderboard.Items.Where(item => item.ParticipantType == "ai").Select(item => item.DisplayName).ToArray();

        Assert.Equal(4, aiNames.Length);
        Assert.Contains("GPT-5.6 Sol", aiNames);
        Assert.Contains("Claude Opus 4.8", aiNames);
        Assert.Contains("Claude Sonnet 5", aiNames);
        Assert.Contains("Gemini 3.1 Pro", aiNames);
    }

    [Fact]
    public async Task Functional_surface_is_authenticated_and_contains_no_broker_routes()
    {
        await using var factory = new ApiApplicationFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/portfolio")).StatusCode);

        using var client = factory.CreateClient();
        var inventory = await client.GetFromJsonAsync<JsonElement[]>("/__endpoint-inventory") ?? [];
        var routes = inventory.Select(item => item.GetProperty("path").GetString()).ToArray();

        Assert.Contains("/api/v1/instruments", routes);
        Assert.Contains("/api/v1/portfolio", routes);
        Assert.Contains("/api/v1/leaderboard", routes);
        Assert.Contains("/api/v1/orders", routes);
        Assert.DoesNotContain(routes, route => route?.Contains("broker", StringComparison.OrdinalIgnoreCase) == true);
    }
}
