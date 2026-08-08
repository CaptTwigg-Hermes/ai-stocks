using System.Security.Cryptography;
using System.Text.Json;

namespace AiStocks.MarketData;

public sealed class MarketReferenceAcquirer(
    HttpClient http,
    DurableFirdsStore firds,
    NasdaqStatusMachine statuses,
    string firdsPlanPath,
    string rssArchivePath)
{
    private static readonly Uri StatusRss = new("https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices");

    public async Task AcquireAsync(DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        var plan = await LoadPlanAsync(cancellationToken).ConfigureAwait(false);
        FirdsSnapshot? current = null;
        if (firds.Exists) current = firds.LoadVerified();

        foreach (var item in plan.Artifacts.OrderBy(x => x.Cursor))
        {
            if (current is not null && item.Cursor <= current.Cursor) continue;
            using var response = await http.GetAsync(item.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri != item.SourceUrl) throw new MarketDataException("FIRDS acquisition redirected away from its pinned source");
            var bytes = await ReadBoundedAsync(response.Content, 1_000_000_000, cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes, writable: false);
            if (item.Kind == "full") firds.ApplyFull(stream, item.EffectiveAt, item.SourceUrl, item.Sha256, item.Version, item.Cursor);
            else if (item.Kind == "full-part") firds.ApplyFullPart(stream, item.EffectiveAt, item.SourceUrl, item.Sha256, item.Version, item.Cursor);
            else if (item.Kind == "delta") firds.ApplyDelta(stream, item.EffectiveAt, item.SourceUrl, item.Sha256, item.Version, item.Cursor);
            else throw new MarketDataException("FIRDS acquisition plan kind is invalid");
            current = firds.LoadVerified();
        }
        if (current is null) throw new MarketDataException("FIRDS acquisition plan did not provide an initial full artifact");

        using var rssResponse = await http.GetAsync(StatusRss, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        rssResponse.EnsureSuccessStatusCode();
        if (rssResponse.RequestMessage?.RequestUri != StatusRss) throw new MarketDataException("Nasdaq RSS acquisition redirected away from its pinned source");
        var rss = await ReadBoundedAsync(rssResponse.Content, 10_000_000, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(rss));
        var root = Path.GetFullPath(rssArchivePath);
        Directory.CreateDirectory(root);
        var rawPath = Path.Combine(root, hash + ".xml");
        if (File.Exists(rawPath) && Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false))) != hash)
            throw new MarketDataException("Nasdaq RSS raw archive conflicts");
        if (!File.Exists(rawPath)) AtomicFile.Write(rawPath, rss);
        statuses.ApplyRssSnapshot(new MemoryStream(rss, writable: false), StatusRss, fetchedAt, hash, rawPath);
    }

    private async Task<FirdsPlan> LoadPlanAsync(CancellationToken cancellationToken)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<FirdsPlan>(await File.ReadAllBytesAsync(firdsPlanPath, cancellationToken).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            if (plan.Artifacts.Count == 0 || plan.Artifacts.Select(x => x.Cursor).Distinct().Count() != plan.Artifacts.Count ||
                plan.Artifacts.Any(x => !DurableFirdsStore.IsOfficialSource(x.SourceUrl) ||
                    x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(x.Version) || x.Cursor <= 0))
                throw new MarketDataException("FIRDS acquisition plan provenance is invalid");
            return plan;
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException)
        { throw new MarketDataException("FIRDS acquisition plan is missing or malformed", exception); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw new MarketDataException("Reference artifact is oversized");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maximumBytes) throw new MarketDataException("Reference artifact is oversized");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private sealed record FirdsPlan(IReadOnlyList<FirdsPlanArtifact> Artifacts);
    private sealed record FirdsPlanArtifact(string Kind, Uri SourceUrl, string Sha256, string Version, long Cursor, DateOnly EffectiveAt);
}