using System.Net.Http.Headers;
using System.Text;

namespace AiStocks.Exhibition.Worker;

public sealed class ExhibitionApiClient(HttpClient client, ExhibitionOptions options) : IExhibitionApi
{
    public Task<string> GetInstrumentsAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/instruments", null, null, cancellationToken);

    public Task<string> GetProgressAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/api/v1/ai-progress", null, null, cancellationToken);

    public async Task PostStatusAsync(string json, CancellationToken cancellationToken)
    {
        _ = await SendAsync(HttpMethod.Post, "/internal/preview/ai-status", json, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task PostDecisionAsync(string runId, string json, CancellationToken cancellationToken)
    {
        _ = await SendAsync(HttpMethod.Post, "/internal/preview/ai-decisions", json, runId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? json, string? runId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-AI-Exhibition-Key", options.InternalKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (runId is not null) request.Headers.Add("Idempotency-Key", runId);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.HttpTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if ((int)response.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"Exhibition API returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > options.MaximumApiResponseBytes)
                throw new InvalidDataException("Exhibition API response exceeded its byte bound.");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
