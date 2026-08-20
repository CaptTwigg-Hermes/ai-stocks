using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
        string body;
        try
        {
            body = await ReadBodyAsync(response.Content, deadline.Token).ConfigureAwait(false);
        }
        catch (DecoderFallbackException) when ((int)response.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException(
                $"Exhibition API returned HTTP {(int)response.StatusCode}: unparseable problem response", null,
                response.StatusCode);
        }
        if ((int)response.StatusCode is < 200 or >= 300)
            throw new HttpRequestException(
                $"Exhibition API returned HTTP {(int)response.StatusCode}: {ProblemSummary(body)}", null, response.StatusCode);
        return body;
    }

    private static string ProblemSummary(string body)
    {
        const int maximumSummaryCharacters = 1_000;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "unparseable problem response";
            var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
            var detail = root.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
                ? detailElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(code)) return "unparseable problem response";
            var summary = string.IsNullOrWhiteSpace(detail) ? code : $"{code}: {detail}";
            summary = string.Concat(summary.Select(character =>
                char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character));
            return summary.Length <= maximumSummaryCharacters ? summary : summary[..maximumSummaryCharacters];
        }
        catch (JsonException)
        {
            return "unparseable problem response";
        }
    }

    private async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > options.MaximumApiResponseBytes)
                throw new InvalidDataException("Exhibition API response exceeded its byte bound.");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
