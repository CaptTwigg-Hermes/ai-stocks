using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AiStocks.Core;

namespace AiStocks.MarketData;

public sealed record EcbFxSnapshot(
    DateOnly ReferenceDate,
    DateTimeOffset AvailableAt,
    Uri SourceUrl,
    string Sha256,
    string RawPath,
    IReadOnlyDictionary<string, decimal> DkkPerUnit);

public sealed class EcbFxStore
{

    public static readonly Uri OfficialSource =
        new("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml");
    private static readonly string[] RequiredCurrencies = ["DKK", "SEK", "NOK", "ISK"];
    private static readonly TimeSpan MaximumReferenceAge = TimeSpan.FromHours(96);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;

    public EcbFxStore(string archivePath) =>
        path = Path.Combine(Path.GetFullPath(archivePath), "ecb-fx-current.json");

    public bool Exists => File.Exists(path);

    public EcbFxSnapshot Archive(byte[] bytes, Uri sourceUrl, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        if (sourceUrl != OfficialSource || bytes.Length is 0 or > 1_000_000 || fetchedAt == default)
            throw new MarketDataException("ECB FX source provenance is invalid");

        var parsed = Parse(bytes);
        if (parsed.ReferenceDate > DateOnly.FromDateTime(fetchedAt.UtcDateTime))
            throw new MarketDataException("ECB FX reference date is in the future");
        if (Exists)
        {
            var current = LoadVerified(fetchedAt, requireFreshness: false);
            if (fetchedAt <= current.AvailableAt || parsed.ReferenceDate < current.ReferenceDate)
                throw new MarketDataException("ECB FX state cannot regress in acquisition time or reference date");
        }
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var rawRoot = path + ".raw";
        var rawPath = Path.Combine(rawRoot, sha256 + ".xml");
        if (File.Exists(rawPath))
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(File.ReadAllBytes(rawPath)), Convert.FromHexString(sha256)))
                throw new MarketDataException("ECB FX raw archive conflicts");
        }
        else
        {
            Directory.CreateDirectory(rawRoot);
            AtomicFile.Write(rawPath, bytes);
        }

        var snapshot = new EcbFxSnapshot(parsed.ReferenceDate, fetchedAt.ToUniversalTime(), sourceUrl,
            sha256, rawPath, parsed.DkkPerUnit);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var envelope = new Envelope(Convert.ToHexStringLower(SHA256.HashData(stateBytes)), snapshot);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions));
        return snapshot;
    }

    public EcbFxSnapshot LoadVerified(DateTimeOffset asOf) => LoadVerified(asOf, requireFreshness: true);

    private EcbFxSnapshot LoadVerified(DateTimeOffset asOf, bool requireFreshness)
    {
        if (!File.Exists(path)) throw new MarketDataException("ECB FX state is missing");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new JsonException();
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            var actualStateHash = SHA256.HashData(stateBytes);
            if (envelope.Sha256.Length != 64 ||
                !CryptographicOperations.FixedTimeEquals(actualStateHash, Convert.FromHexString(envelope.Sha256)) ||
                envelope.State.SourceUrl != OfficialSource || envelope.State.AvailableAt == default ||
                envelope.State.AvailableAt > asOf || envelope.State.ReferenceDate > DateOnly.FromDateTime(asOf.UtcDateTime) ||
                requireFreshness && asOf.UtcDateTime.Date -
                    envelope.State.ReferenceDate.ToDateTime(TimeOnly.MinValue) > MaximumReferenceAge ||
                envelope.State.Sha256.Length != 64)
                throw new MarketDataException("ECB FX state provenance or freshness is invalid");
            var raw = File.ReadAllBytes(envelope.State.RawPath);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), Convert.FromHexString(envelope.State.Sha256)))
                throw new MarketDataException("ECB FX raw archive checksum is invalid");
            var parsed = Parse(raw);
            if (parsed.ReferenceDate != envelope.State.ReferenceDate ||
                !RatesEqual(parsed.DkkPerUnit, envelope.State.DkkPerUnit))
                throw new MarketDataException("ECB FX normalized state disagrees with its raw source");
            return envelope.State;
        }
        catch (MarketDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or XmlException)
        {
            throw new MarketDataException("ECB FX state is malformed", exception);
        }
    }

    private static (DateOnly ReferenceDate, IReadOnlyDictionary<string, decimal> DkkPerUnit) Parse(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            MaxCharactersInDocument = 1_000_000
        };
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var dated = document.Descendants().Where(element => element.Attribute("time") is not null).ToArray();
        if (dated.Length != 1 || !DateOnly.TryParseExact(dated[0].Attribute("time")!.Value,
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var referenceDate))
            throw new MarketDataException("ECB FX reference date is missing or ambiguous");

        var perEuro = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var element in dated[0].Elements())
        {
            var currency = element.Attribute("currency")?.Value;
            var rateText = element.Attribute("rate")?.Value;
            if (currency is null || rateText is null) continue;
            if (currency.Length != 3 || !decimal.TryParse(rateText, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var rate) || rate <= 0m || !perEuro.TryAdd(currency, rate))
                throw new MarketDataException("ECB FX currency rate is malformed or duplicated");
        }
        if (RequiredCurrencies.Any(currency => !perEuro.ContainsKey(currency)))
            throw new MarketDataException("ECB FX source omits a required Nordic currency");

        var dkkPerEuro = perEuro["DKK"];
        var dkkPerUnit = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["DKK"] = 1m,
            ["EUR"] = dkkPerEuro
        };
        foreach (var currency in RequiredCurrencies.Where(currency => currency != "DKK"))
            dkkPerUnit[currency] = dkkPerEuro / perEuro[currency];
        return (referenceDate, dkkPerUnit);
    }

    private static bool RatesEqual(IReadOnlyDictionary<string, decimal> left,
        IReadOnlyDictionary<string, decimal> right) =>
        left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);

    private sealed record Envelope(string Sha256, EcbFxSnapshot State);
}

public sealed class EcbFxAcquirer(HttpClient http, EcbFxStore store)
{
    private const int MaximumBytes = 1_000_000;
    private static readonly TimeSpan MinimumFetchInterval = TimeSpan.FromHours(1);
    private DateTimeOffset nextFetchAt;

    public async Task<EcbFxSnapshot> AcquireAsync(DateTimeOffset fetchedAt,
        CancellationToken cancellationToken)
    {
        if (store.Exists && fetchedAt < nextFetchAt) return store.LoadVerified(fetchedAt);
        using var response = await http.GetAsync(EcbFxStore.OfficialSource,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != EcbFxStore.OfficialSource)
            throw new MarketDataException("ECB FX acquisition redirected away from its pinned source");
        if (response.Content.Headers.ContentLength is > MaximumBytes)
            throw new MarketDataException("ECB FX response is oversized");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaximumBytes)
                throw new MarketDataException("ECB FX response is oversized");
            output.Write(buffer, 0, read);
        }
        var snapshot = store.Archive(output.ToArray(), EcbFxStore.OfficialSource, fetchedAt);
        nextFetchAt = fetchedAt.Add(MinimumFetchInterval);
        return snapshot;
    }
}
