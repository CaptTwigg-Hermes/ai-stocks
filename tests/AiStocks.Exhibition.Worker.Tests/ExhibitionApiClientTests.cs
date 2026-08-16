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
            ApiBaseUrl = new Uri("https://api.example.test/"), InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json", HermesHomeRoot = "/dev/shm/exhibition"
        };
        var api = new ExhibitionApiClient(new HttpClient(handler) { BaseAddress = options.ApiBaseUrl }, options);

        await api.GetInstrumentsAsync(CancellationToken.None);
        await api.GetProgressAsync(CancellationToken.None);
        await api.PostDecisionAsync("run-1", "{}", CancellationToken.None);

        Assert.Equal(["/api/v1/instruments", "/api/v1/ai-progress", "/api/v1/internal/ai-decisions"], handler.Paths);
        Assert.All(handler.Keys, key => Assert.Equal(new string('k', 32), key));
        Assert.Equal("run-1", handler.IdempotencyKeys.Single(value => value is not null));
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
