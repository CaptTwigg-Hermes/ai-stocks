using System.Net;
using System.Text;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionApiClientTests
{
    [Fact]
    public async Task CallsExactContractsWithInternalKeyAndIdempotentPost()
    {
        var handler = new RecordingHandler();
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test/"),
            InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json",
            HermesHomeRoot = "/dev/shm/exhibition"
        };
        var api = new ExhibitionApiClient(new HttpClient(handler) { BaseAddress = options.ApiBaseUrl }, options);

        await api.GetInstrumentsAsync(CancellationToken.None);
        await api.GetProgressAsync(CancellationToken.None);
        await api.PostStatusAsync("{\"status\":\"queued\"}", CancellationToken.None);
        await api.PostDecisionAsync("run-1", "{}", CancellationToken.None);

        Assert.Equal(["/api/v1/instruments", "/api/v1/ai-progress", "/internal/preview/ai-status", "/internal/preview/ai-decisions"], handler.Paths);
        Assert.All(handler.Keys, key => Assert.Equal(new string('k', 32), key));
        Assert.Equal("run-1", handler.IdempotencyKeys.Single(value => value is not null));
    }

    [Fact]
    public async Task Api_problem_body_is_preserved_for_failed_decision_diagnostics()
    {
        var handler = new ProblemHandler();
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test/"),
            InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json",
            HermesHomeRoot = "/dev/shm/exhibition"
        };
        var api = new ExhibitionApiClient(new HttpClient(handler) { BaseAddress = options.ApiBaseUrl }, options);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.PostDecisionAsync("run-1", "{}", CancellationToken.None));

        Assert.Equal(
            "Exhibition API returned HTTP 400: evidence-lookahead: Evidence cannot postdate the observation.",
            error.Message);
    }

    [Fact]
    public async Task Api_problem_diagnostics_are_sanitized_and_bounded()
    {
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test/"),
            InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json",
            HermesHomeRoot = "/dev/shm/exhibition"
        };
        var api = new ExhibitionApiClient(
            new HttpClient(new ProblemHandler("line\nbreak\u2028split\u2029again" + new string('x', 2_000))) { BaseAddress = options.ApiBaseUrl },
            options);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.PostDecisionAsync("run-1", "{}", CancellationToken.None));

        Assert.DoesNotContain('\n', error.Message);
        Assert.DoesNotContain('\u2028', error.Message);
        Assert.DoesNotContain('\u2029', error.Message);
        Assert.Equal(1_034, error.Message.Length);
    }

    [Fact]
    public async Task Invalid_utf8_problem_body_preserves_http_failure()
    {
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test/"),
            InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json",
            HermesHomeRoot = "/dev/shm/exhibition"
        };
        var api = new ExhibitionApiClient(
            new HttpClient(new InvalidUtf8ProblemHandler()) { BaseAddress = options.ApiBaseUrl }, options);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.PostDecisionAsync("run-1", "{}", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("Exhibition API returned HTTP 400: unparseable problem response", error.Message);
    }

    [Fact]
    public async Task Api_problem_diagnostic_does_not_split_surrogate_pair_at_bound()
    {
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test/"),
            InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json",
            HermesHomeRoot = "/dev/shm/exhibition"
        };
        var api = new ExhibitionApiClient(
            new HttpClient(new ProblemHandler(new string('x', 979) + "😀tail")) { BaseAddress = options.ApiBaseUrl },
            options);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.PostDecisionAsync("run-1", "{}", CancellationToken.None));
        var summary = error.Message["Exhibition API returned HTTP 400: ".Length..];

        Assert.Equal(999, summary.Length);
        Assert.False(char.IsSurrogate(summary[^1]));
    }

    private sealed class InvalidUtf8ProblemHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new ByteArrayContent([0xff])
            });
    }

    private sealed class ProblemHandler(string detail = "Evidence cannot postdate the observation.") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        code = "evidence-lookahead",
                        title = "Rejected",
                        status = 400,
                        traceId = "trace-1",
                        detail
                    }),
                    Encoding.UTF8,
                    "application/json")
            });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> Keys { get; } = [];
        public List<string?> IdempotencyKeys { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Keys.Add(request.Headers.TryGetValues("X-AI-Exhibition-Key", out var keys) ? keys.Single() : null);
            IdempotencyKeys.Add(request.Headers.TryGetValues("Idempotency-Key", out var ids) ? ids.Single() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }
}
