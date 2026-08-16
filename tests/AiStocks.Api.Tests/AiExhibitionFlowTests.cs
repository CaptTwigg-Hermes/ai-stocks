using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStocks.Api;

namespace AiStocks.Api.Tests;

public sealed class AiExhibitionFlowTests
{
    [Fact]
    public async Task Progress_starts_with_exactly_four_isolated_ai_accounts()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");

        using var response = await client.GetAsync("/api/v1/ai-progress");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        Assert.Equal("preview-fixtures", json.RootElement.GetProperty("dataMode").GetString());
        Assert.False(json.RootElement.GetProperty("isLive").GetBoolean());
        var agents = json.RootElement.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal(4, agents.Length);
        Assert.Equal(new[] { "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview", "gpt-5.6-sol" },
            agents.Select(agent => agent.GetProperty("modelId").GetString()).Order(StringComparer.Ordinal));
        Assert.All(agents, agent =>
        {
            Assert.Equal(100_000m, agent.GetProperty("portfolio").GetProperty("cashDkk").GetDecimal());
            Assert.Equal("pending", agent.GetProperty("status").GetString());
        });
    }

    [Fact]
    public async Task Verified_buy_fills_only_named_agent_and_exact_replay_is_idempotent()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AI-Exhibition-Key", AiExhibitionApiFactory.Secret);
        var payload = Decision("run-valid-0001", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "buy", "aapl-us", 2);

        using var created = await client.PostAsJsonAsync("/internal/preview/ai-decisions", payload);
        using var replay = await client.PostAsJsonAsync("/internal/preview/ai-decisions", payload);

        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, replay.StatusCode);
        client.DefaultRequestHeaders.Remove("X-AI-Exhibition-Key");
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");
        using var progress = await client.GetFromJsonAsync<JsonDocument>("/api/v1/ai-progress");
        var agents = progress!.RootElement.GetProperty("agents").EnumerateArray().ToArray();
        var buyer = agents.Single(agent => agent.GetProperty("modelId").GetString() == "gpt-5.6-sol");
        Assert.Equal("succeeded", buyer.GetProperty("status").GetString());
        Assert.Equal(97_196.18m, buyer.GetProperty("portfolio").GetProperty("cashDkk").GetDecimal());
        Assert.Single(buyer.GetProperty("portfolio").GetProperty("holdings").EnumerateArray());
        Assert.All(agents.Where(agent => agent.GetProperty("modelId").GetString() != "gpt-5.6-sol"),
            agent => Assert.Equal(100_000m, agent.GetProperty("portfolio").GetProperty("cashDkk").GetDecimal()));
    }

    [Fact]
    public async Task Internal_decisions_require_secret_and_are_not_cors_enabled()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        using var unauthorized = await client.PostAsJsonAsync("/internal/preview/ai-decisions",
            Decision("run-secret-001", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "hold", null, 0));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/internal/preview/ai-decisions");
        preflight.Headers.Add("Origin", "https://stocks.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        using var response = await client.SendAsync(preflight);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Invalid_identity_evidence_hold_and_changed_replay_fail_closed()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AI-Exhibition-Key", AiExhibitionApiFactory.Secret);

        var mismatch = JsonSerializer.SerializeToNode(Decision("run-invalid-01", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "hold", null, 0))!.AsObject();
        mismatch["modelId"] = "claude-opus-4.8";
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/internal/preview/ai-decisions", mismatch)).StatusCode);

        var badEvidence = JsonSerializer.SerializeToNode(Decision("run-invalid-02", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "hold", null, 0))!.AsObject();
        badEvidence["evidence"]![0]!["url"] = "http://example.com/not-verified";
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/internal/preview/ai-decisions", badEvidence)).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/internal/preview/ai-decisions",
            Decision("run-invalid-03", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "hold", "aapl-us", 1))).StatusCode);

        var original = Decision("run-conflict-01", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "hold", null, 0);
        Assert.Equal(System.Net.HttpStatusCode.Created, (await client.PostAsJsonAsync("/internal/preview/ai-decisions", original)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/internal/preview/ai-decisions",
            Decision("run-conflict-01", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "buy", "aapl-us", 1))).StatusCode);
    }

    [Fact]
    public async Task Exhibition_leaderboard_contains_only_four_tie_aware_ai_values()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");

        using var leaderboard = await client.GetFromJsonAsync<JsonDocument>("/api/v1/leaderboard");
        var rows = leaderboard!.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal("ai", row.GetProperty("participantType").GetString());
            Assert.Equal(1, row.GetProperty("rank").GetInt32());
            Assert.Equal(100_000m, row.GetProperty("valueDkk").GetDecimal());
        });
    }

    [Fact]
    public void Decision_capacity_fails_closed_without_mutating_an_agent()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        for (var index = 0; index < PreviewRaceStore.MaximumIdempotencyEntries; index++)
            store.SubmitAi(HoldRequest($"capacity-{index:D8}"));

        var error = Assert.Throws<PreviewOrderException>(() => store.SubmitAi(HoldRequest("capacity-overflow")));

        Assert.Equal("run-capacity", error.Code);
        Assert.All(store.AiProgress().Agents, agent => Assert.Equal(100_000m, agent.Portfolio.CashDkk));
    }

    private static AiDecisionRequestDto HoldRequest(string runId) => new(runId,
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "gpt-5.6-sol", "hold", null, 0,
        "Fixture hold decision with bounded evidence.", 0.5m,
        [new("https://example.com/research", DateTimeOffset.Parse("2026-08-16T10:00:00Z"), "Exact fixture evidence excerpt.", new string('a', 64))],
        "copilot", "gpt-5.6-sol", new string('b', 64), DateTimeOffset.Parse("2026-08-16T10:01:00Z"));

    private static object Decision(string runId, string agentId, string modelId, string action, string? instrumentId, int quantity) => new
    {
        runId,
        agentId,
        modelId,
        action,
        instrumentId,
        quantity,
        reason = "Fixture research supports this deterministic paper decision.",
        confidence = 0.75m,
        evidence = new[] { new { url = "https://example.com/research", publishedAt = "2026-08-16T10:00:00Z", exactExcerpt = "Exact fixture evidence excerpt.", contentSha256 = new string('a', 64) } },
        runtimeProvider = "copilot",
        runtimeModel = modelId,
        reportSha256 = new string('b', 64),
        completedAt = "2026-08-16T10:01:00Z"
    };
}