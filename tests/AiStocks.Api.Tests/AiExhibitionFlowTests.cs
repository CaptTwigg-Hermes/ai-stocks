using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStocks.Api;
using AiStocks.Core;

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

        Assert.Equal(DelayedNasdaqInstrumentStore.DataMode, json.RootElement.GetProperty("dataMode").GetString());
        Assert.True(json.RootElement.GetProperty("isNonLive").GetBoolean());
        Assert.False(json.RootElement.GetProperty("strictContest").GetBoolean());
        Assert.False(json.RootElement.GetProperty("holdOnly").GetBoolean());
        Assert.True(json.RootElement.GetProperty("assumedFills").GetBoolean());
        Assert.Equal("assumed-delayed-paper-fills-v1", json.RootElement.GetProperty("executionMode").GetString());
        Assert.Equal(0.65m, json.RootElement.GetProperty("assumedSekToDkk").GetDecimal());
        Assert.Equal(1m, json.RootElement.GetProperty("assumedSlippagePercent").GetDecimal());
        var agents = json.RootElement.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(4, agents.Length);
        Assert.Equal(new[] { "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview", "gpt-5.6-sol" },
            agents.Select(agent => agent.GetProperty("modelId").GetString()).Order(StringComparer.Ordinal));
        Assert.All(agents, agent =>
        {
            Assert.Equal(100_000m, agent.GetProperty("portfolio").GetProperty("cashDkk").GetDecimal());
            Assert.Equal("pending", agent.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(agent.GetProperty("displayName").GetString()));
        });
    }

    [Fact]
    public async Task Unknown_trade_is_rejected_without_mutation_in_assumed_fill_exhibition()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AI-Exhibition-Key", AiExhibitionApiFactory.Secret);
        var payload = Decision("run-valid-0001", "11111111-1111-1111-1111-111111111111", "gpt-5.6-sol", "buy", "aapl-us", 2);
        await StartRun(client, "run-valid-0001", ContestContract.Agents[0], DateTimeOffset.Parse("2026-08-16T10:01:00Z"));

        using var rejected = await client.PostAsJsonAsync("/internal/preview/ai-decisions", payload);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        using var problem = await JsonDocument.ParseAsync(await rejected.Content.ReadAsStreamAsync());
        Assert.Equal("instrument-not-found", problem.RootElement.GetProperty("code").GetString());
        client.DefaultRequestHeaders.Remove("X-AI-Exhibition-Key");
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");
        using var progress = await client.GetFromJsonAsync<JsonDocument>("/api/v1/ai-progress");
        var agents = progress!.RootElement.GetProperty("participants").EnumerateArray().ToArray();
        var participant = agents.Single(agent => agent.GetProperty("modelId").GetString() == "gpt-5.6-sol");
        Assert.Equal("running", participant.GetProperty("status").GetString());
        Assert.Equal(100_000m, participant.GetProperty("portfolio").GetProperty("cashDkk").GetDecimal());
        Assert.Empty(participant.GetProperty("portfolio").GetProperty("holdings").EnumerateArray());
        Assert.Empty(progress.RootElement.GetProperty("activity").EnumerateArray());
    }

    [Fact]
    public async Task Worker_status_updates_expose_running_and_failure_details()
    {
        await using var factory = new AiExhibitionApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AI-Exhibition-Key", AiExhibitionApiFactory.Secret);
        var agent = ContestContract.Agents[0];
        var at = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

        var queued = new AiStatusRequestDto("round-001", agent.Id, agent.ModelId, "queued", null, at.AddMinutes(-1));
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/internal/preview/ai-status", queued)).StatusCode);
        var running = queued with { Status = "running", OccurredAt = at };
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/internal/preview/ai-status", running)).StatusCode);
        var failed = running with { Status = "failed", Error = "model unavailable", OccurredAt = at.AddMinutes(1) };
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/internal/preview/ai-status", failed)).StatusCode);

        client.DefaultRequestHeaders.Remove("X-AI-Exhibition-Key");
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");
        using var progress = await client.GetFromJsonAsync<JsonDocument>("/api/v1/ai-progress");
        var participant = progress!.RootElement.GetProperty("participants").EnumerateArray()
            .Single(item => item.GetProperty("agentId").GetGuid() == agent.Id);
        Assert.Equal("failed", participant.GetProperty("status").GetString());
        Assert.Equal("round-001", participant.GetProperty("runId").GetString());
        Assert.Equal("model unavailable", participant.GetProperty("error").GetString());
        Assert.Equal(at, participant.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(at.AddMinutes(1), participant.GetProperty("completedAt").GetDateTimeOffset());
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
        await StartRun(client, "run-conflict-01", ContestContract.Agents[0], DateTimeOffset.Parse("2026-08-16T10:01:00Z"));
        Assert.Equal(System.Net.HttpStatusCode.Created, (await client.PostAsJsonAsync("/internal/preview/ai-decisions", original)).StatusCode);

        var truthfulHold = JsonSerializer.SerializeToNode(Decision("run-no-evidence", "22222222-2222-2222-2222-222222222222", "claude-opus-4.8", "hold", null, 0))!.AsObject();
        truthfulHold["evidence"] = new JsonArray();
        await StartRun(client, "run-no-evidence", ContestContract.Agents[1], DateTimeOffset.Parse("2026-08-16T10:01:00Z"));
        Assert.Equal(System.Net.HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/internal/preview/ai-decisions", truthfulHold)).StatusCode);

        var wrongProvider = JsonSerializer.SerializeToNode(Decision("run-provider-01", "33333333-3333-3333-3333-333333333333", "claude-sonnet-5", "hold", null, 0))!.AsObject();
        wrongProvider["runtimeProvider"] = "not-copilot";
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/internal/preview/ai-decisions", wrongProvider)).StatusCode);

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
        {
            var request = HoldRequest($"capacity-{index:D8}", DateTimeOffset.Parse("2026-08-16T10:01:00Z").AddSeconds(index * 3));
            StartRun(store, request);
            store.SubmitAi(request);
        }

        var overflow = HoldRequest("capacity-overflow", DateTimeOffset.Parse("2026-08-20T10:01:00Z"));
        var error = Assert.Throws<PreviewOrderException>(() => StartRun(store, overflow));

        Assert.Equal("run-capacity", error.Code);
        Assert.All(store.AiProgress().Participants, agent => Assert.Equal(100_000m, agent.Portfolio.CashDkk));
    }

    [Fact]
    public void Successful_decision_is_terminal_when_a_late_failure_status_arrives()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var request = HoldRequest("terminal-success-001");
        StartRun(store, request);
        store.SubmitAi(request);

        var error = Assert.Throws<PreviewOrderException>(() => store.UpdateAiStatus(new(
            request.RunId, request.AgentId, request.ModelId, "failed", "response was lost", request.CompletedAt.AddSeconds(1))));

        Assert.Equal("terminal-status-conflict", error.Code);
        var participant = store.AiProgress().Participants.Single(item => item.AgentId == request.AgentId);
        Assert.Equal("succeeded", participant.Status);
        Assert.Null(participant.Error);
        var activity = store.AiProgress().Activity;
        var terminal = Assert.Single(activity, item => item.RunId == request.RunId);
        Assert.Equal("succeeded", terminal.Status);
    }

    [Fact]
    public void Status_lifecycle_rejects_terminal_reversal_replay_and_stale_run_replacement()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var agent = ContestContract.Agents[0];
        var at = DateTimeOffset.Parse("2026-08-16T12:00:00Z");
        store.UpdateAiStatus(new("lifecycle-run-001", agent.Id, agent.ModelId, "queued", null, at));
        store.UpdateAiStatus(new("lifecycle-run-001", agent.Id, agent.ModelId, "running", null, at.AddSeconds(1)));
        store.UpdateAiStatus(new("lifecycle-run-001", agent.Id, agent.ModelId, "failed", "model outage", at.AddSeconds(2)));

        Assert.Equal("terminal-status-conflict", Assert.Throws<PreviewOrderException>(() => store.UpdateAiStatus(new(
            "lifecycle-run-001", agent.Id, agent.ModelId, "running", null, at.AddSeconds(3)))).Code);
        Assert.Equal("terminal-status-conflict", Assert.Throws<PreviewOrderException>(() => store.UpdateAiStatus(new(
            "lifecycle-run-001", agent.Id, agent.ModelId, "queued", null, at.AddSeconds(3)))).Code);
        Assert.Equal("stale-status", Assert.Throws<PreviewOrderException>(() => store.UpdateAiStatus(new(
            "lifecycle-run-002", agent.Id, agent.ModelId, "queued", null, at.AddSeconds(1)))).Code);

        store.UpdateAiStatus(new("lifecycle-run-002", agent.Id, agent.ModelId, "queued", null, at.AddSeconds(4)));
        Assert.Equal("stale-status", Assert.Throws<PreviewOrderException>(() => store.UpdateAiStatus(new(
            "lifecycle-run-001", agent.Id, agent.ModelId, "queued", null, at.AddSeconds(5)))).Code);
        var participant = store.AiProgress().Participants.Single(item => item.AgentId == agent.Id);
        Assert.Equal("queued", participant.Status);
        Assert.Equal("lifecycle-run-002", participant.RunId);
        Assert.Equal(at.AddSeconds(4), participant.QueuedAt);
    }

    [Fact]
    public void Decision_requires_current_running_run_and_monotonic_completion_time()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var first = HoldRequest("decision-run-001", DateTimeOffset.Parse("2026-08-16T12:00:03Z"));
        StartRun(store, first);
        store.UpdateAiStatus(new(first.RunId, first.AgentId, first.ModelId, "failed", "model outage", first.CompletedAt));
        Assert.Equal("decision-status-conflict",
            Assert.Throws<PreviewOrderException>(() => store.SubmitAi(first)).Code);

        var current = HoldRequest("decision-run-002", DateTimeOffset.Parse("2026-08-16T12:00:10Z"));
        StartRun(store, current);
        Assert.Equal("run-decision-conflict",
            Assert.Throws<PreviewOrderException>(() => store.SubmitAi(first with { CompletedAt = current.CompletedAt.AddSeconds(1) })).Code);
        Assert.Equal("stale-decision",
            Assert.Throws<PreviewOrderException>(() => store.SubmitAi(current with { CompletedAt = current.CompletedAt.AddSeconds(-2) })).Code);

        store.SubmitAi(current);
        Assert.Equal("succeeded", store.AiProgress().Participants.Single(item => item.AgentId == current.AgentId).Status);
    }

    [Fact]
    public void New_run_cannot_replace_an_active_queued_or_running_run()
    {
        var store = new PreviewRaceStore(TimeProvider.System);
        var agent = ContestContract.Agents[0];
        var at = DateTimeOffset.Parse("2026-08-16T12:00:00Z");
        store.UpdateAiStatus(new("active-run-001", agent.Id, agent.ModelId, "queued", null, at));
        store.UpdateAiStatus(new("active-run-001", agent.Id, agent.ModelId, "running", null, at.AddSeconds(1)));

        var error = Assert.Throws<PreviewOrderException>(() => store.UpdateAiStatus(new(
            "active-run-002", agent.Id, agent.ModelId, "queued", null, at.AddSeconds(2))));

        Assert.Equal("active-run-conflict", error.Code);
        var participant = store.AiProgress().Participants.Single(item => item.AgentId == agent.Id);
        Assert.Equal("active-run-001", participant.RunId);
        Assert.Equal("running", participant.Status);
    }

    private static AiDecisionRequestDto HoldRequest(string runId, DateTimeOffset? completedAt = null) => new(runId,
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "gpt-5.6-sol", "hold", null, 0,
        "Fixture hold decision with bounded evidence.", 0.5m,
        [new("https://example.com/research", DateTimeOffset.Parse("2026-08-16T10:00:00Z"), "Exact fixture evidence excerpt.", new string('a', 64))],
        "copilot", "gpt-5.6-sol", new string('b', 64), completedAt ?? DateTimeOffset.Parse("2026-08-16T10:01:00Z"));

    private static void StartRun(PreviewRaceStore store, AiDecisionRequestDto request)
    {
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "queued", null, request.CompletedAt.AddSeconds(-2)));
        store.UpdateAiStatus(new(request.RunId, request.AgentId, request.ModelId, "running", null, request.CompletedAt.AddSeconds(-1)));
    }

    private static async Task StartRun(HttpClient client, string runId, AgentDefinition agent, DateTimeOffset completedAt)
    {
        using var queued = await client.PostAsJsonAsync("/internal/preview/ai-status",
            new AiStatusRequestDto(runId, agent.Id, agent.ModelId, "queued", null, completedAt.AddSeconds(-2)));
        using var running = await client.PostAsJsonAsync("/internal/preview/ai-status",
            new AiStatusRequestDto(runId, agent.Id, agent.ModelId, "running", null, completedAt.AddSeconds(-1)));
        Assert.Equal(System.Net.HttpStatusCode.OK, queued.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, running.StatusCode);
    }

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