using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AiStocks.MarketData;

public enum InstrumentTradingState { Unknown, Clear, Warning, Observation, Suspended }
public sealed record NasdaqStatusEvent(string Id, string Isin, InstrumentTradingState State, DateTimeOffset PublishedAt, Uri Source);

public sealed class PinnedStatusSeedVerifier
{
    private readonly byte[] _publicKey;
    public PinnedStatusSeedVerifier(string keyId, byte[] subjectPublicKeyInfo)
    {
        if (string.IsNullOrWhiteSpace(keyId)) throw new ArgumentException("Pinned key identity is required", nameof(keyId));
        KeyId = keyId;
        _publicKey = subjectPublicKeyInfo.ToArray();
        using var key = ECDsa.Create();
        try { key.ImportSubjectPublicKeyInfo(_publicKey, out var read); if (read != _publicKey.Length) throw new CryptographicException(); }
        catch (CryptographicException exception) { throw new MarketDataException("Pinned status seed public key is invalid", exception); }
        PublicKeySha256 = Convert.ToHexStringLower(SHA256.HashData(_publicKey));
    }

    public string KeyId { get; }
    public string PublicKeySha256 { get; }

    public NasdaqStatusMachine Load(string payload, byte[] signature, string durableStatePath)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(_publicKey, out _);
        if (!key.VerifyData(Encoding.UTF8.GetBytes(payload), signature, HashAlgorithmName.SHA256))
            throw new MarketDataException("Status seed signature does not match the pinned signer");
        return NasdaqStatusMachine.LoadVerifiedSeed(payload, KeyId, PublicKeySha256, durableStatePath);
    }
}

public sealed partial class NasdaqStatusMachine
{
    private readonly Dictionary<string, InstrumentTradingState> _states;
    private readonly HashSet<string> _eventIds;
    private readonly List<NasdaqStatusEvent> _events;
    private readonly string _durableStatePath;
    private DateTimeOffset _latestPublishedAt;

    private NasdaqStatusMachine(Dictionary<string, InstrumentTradingState> states, DateTimeOffset seedAsOf,
        string signerKeyId, string signerKeySha256, string durableStatePath, IEnumerable<NasdaqStatusEvent>? events = null)
    {
        _states = states; SeedAsOf = seedAsOf; SignerKeyId = signerKeyId; SignerKeySha256 = signerKeySha256;
        _durableStatePath = Path.GetFullPath(durableStatePath);
        _events = events?.ToList() ?? [];
        _eventIds = _events.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        _latestPublishedAt = _events.Count == 0 ? seedAsOf : _events.Max(x => x.PublishedAt);
    }

    public IReadOnlyList<NasdaqStatusEvent> Events => _events;
    public DateTimeOffset SeedAsOf { get; }
    public string SignerKeyId { get; }
    public string SignerKeySha256 { get; }
    public InstrumentTradingState StateOf(string isin) => _states.GetValueOrDefault(isin, InstrumentTradingState.Unknown);
    public bool IsEligible(string isin) => StateOf(isin) == InstrumentTradingState.Clear;

    internal static NasdaqStatusMachine LoadVerifiedSeed(string payload, string keyId, string keySha256, string durableStatePath)
    {
        var (asOf, seedStates) = ParseSeed(payload);
        var path = Path.GetFullPath(durableStatePath);
        if (!File.Exists(path))
        {
            var created = new NasdaqStatusMachine(seedStates, asOf, keyId, keySha256, path);
            created.Persist();
            return created;
        }
        try
        {
            var envelope = JsonSerializer.Deserialize<StatusEnvelope>(File.ReadAllBytes(path), JsonOptions) ?? throw new JsonException();
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stateBytes));
            if (envelope.Sha256 != actual || envelope.State.SeedAsOf != asOf || envelope.State.SignerKeyId != keyId ||
                envelope.State.SignerKeySha256 != keySha256) throw new MarketDataException("Durable status state identity or checksum mismatch");
            var states = envelope.State.States.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            return new(states, asOf, keyId, keySha256, path, envelope.State.Events);
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException)
        { throw new MarketDataException("Durable status state is malformed", exception); }
    }

    public void ApplyRss(Stream rss)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 };
            using var reader = XmlReader.Create(rss, settings); document = XDocument.Load(reader);
        }
        catch (XmlException exception) { throw new MarketDataException("Nasdaq RSS is malformed", exception); }
        var entries = document.Descendants().Where(x => x.Name.LocalName == "item").Select(ParseEvent).ToArray();
        if (entries.Length == 0) throw new MarketDataException("Nasdaq RSS contains no notices");
        if (entries.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length || entries.Any(x => _eventIds.Contains(x.Id)))
            throw new MarketDataException("Nasdaq RSS notice replay detected");
        var ordered = entries.OrderBy(x => x.PublishedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (ordered.Any(x => x.PublishedAt <= _latestPublishedAt || x.State == InstrumentTradingState.Unknown))
            throw new MarketDataException("Nasdaq RSS publication time is stale or state is unknown");
        foreach (var entry in ordered)
        {
            _eventIds.Add(entry.Id); _events.Add(entry); _states[entry.Isin] = entry.State; _latestPublishedAt = entry.PublishedAt;
        }
        Persist();
    }

    private void Persist()
    {
        var state = new StatusState(SeedAsOf, SignerKeyId, SignerKeySha256,
            _states.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(), _events.ToArray());
        var checksum = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions)));
        AtomicFile.Write(_durableStatePath, JsonSerializer.SerializeToUtf8Bytes(new StatusEnvelope(checksum, state), JsonOptions));
    }

    private static (DateTimeOffset AsOf, Dictionary<string, InstrumentTradingState> States) ParseSeed(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("asOf", out var asOfElement) || !DateTimeOffset.TryParse(asOfElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var asOf)
                || !root.TryGetProperty("states", out var statesElement) || statesElement.ValueKind != JsonValueKind.Object)
                throw new MarketDataException("Status seed schema is invalid");
            var states = new Dictionary<string, InstrumentTradingState>(StringComparer.Ordinal);
            foreach (var property in statesElement.EnumerateObject())
            {
                if (!IsinPattern().IsMatch(property.Name) || !Enum.TryParse<InstrumentTradingState>(property.Value.GetString(), false, out var state) || state == InstrumentTradingState.Unknown)
                    throw new MarketDataException("Status seed entry is invalid");
                states.Add(property.Name, state);
            }
            return (asOf, states);
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        { throw new MarketDataException("Status seed is malformed", exception); }
    }

    private static NasdaqStatusEvent ParseEvent(XElement item)
    {
        string Value(string name) => item.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
        var id = Value("guid"); var text = Value("title") + " " + Value("description");
        var match = IsinPattern().Match(text);
        if (id.Length == 0 || !match.Success || !DateTimeOffset.TryParse(Value("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var published)
            || !Uri.TryCreate(Value("link"), UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps || source.Host != "api.news.eu.nasdaq.com")
            throw new MarketDataException("Nasdaq RSS notice identity or provenance is invalid");
        var lower = text.ToLowerInvariant();
        var state = lower.Contains("resumption", StringComparison.Ordinal) || lower.Contains("resume", StringComparison.Ordinal) ? InstrumentTradingState.Clear
            : lower.Contains("suspension", StringComparison.Ordinal) || lower.Contains("suspended", StringComparison.Ordinal) ? InstrumentTradingState.Suspended
            : lower.Contains("observation", StringComparison.Ordinal) ? InstrumentTradingState.Observation
            : lower.Contains("warning", StringComparison.Ordinal) ? InstrumentTradingState.Warning : InstrumentTradingState.Unknown;
        return new(id, match.Value, state, published, source);
    }

    private sealed record StatusState(DateTimeOffset SeedAsOf, string SignerKeyId, string SignerKeySha256,
        Dictionary<string, InstrumentTradingState> States, IReadOnlyList<NasdaqStatusEvent> Events);
    private sealed record StatusEnvelope(string Sha256, StatusState State);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [GeneratedRegex(@"\b[A-Z]{2}[A-Z0-9]{9}[0-9]\b", RegexOptions.CultureInvariant)] private static partial Regex IsinPattern();
}

internal static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
