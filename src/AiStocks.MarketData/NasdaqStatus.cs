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

public sealed partial class NasdaqStatusMachine
{
    private readonly Dictionary<string, InstrumentTradingState> _states;
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly List<NasdaqStatusEvent> _events = [];

    private NasdaqStatusMachine(Dictionary<string, InstrumentTradingState> states) => _states = states;
    public IReadOnlyList<NasdaqStatusEvent> Events => _events;
    public InstrumentTradingState StateOf(string isin) => _states.GetValueOrDefault(isin, InstrumentTradingState.Unknown);
    public bool IsEligible(string isin) => StateOf(isin) == InstrumentTradingState.Clear;

    public static NasdaqStatusMachine FromSignedSeed(string payload, byte[] signature, byte[] subjectPublicKeyInfo)
    {
        using var key = ECDsa.Create();
        try { key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var read); if (read != subjectPublicKeyInfo.Length) throw new CryptographicException(); }
        catch (CryptographicException exception) { throw new MarketDataException("Status seed public key is invalid", exception); }
        if (!key.VerifyData(Encoding.UTF8.GetBytes(payload), signature, HashAlgorithmName.SHA256))
            throw new MarketDataException("Status seed signature is invalid");
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("asOf", out var asOf) || !DateTimeOffset.TryParse(asOf.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                || !root.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Object)
                throw new MarketDataException("Status seed schema is invalid");
            var parsed = new Dictionary<string, InstrumentTradingState>(StringComparer.Ordinal);
            foreach (var property in states.EnumerateObject())
            {
                if (!IsinPattern().IsMatch(property.Name) || !Enum.TryParse<InstrumentTradingState>(property.Value.GetString(), false, out var state) || state == InstrumentTradingState.Unknown)
                    throw new MarketDataException("Status seed entry is invalid");
                parsed.Add(property.Name, state);
            }
            return new(parsed);
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        { throw new MarketDataException("Status seed is malformed", exception); }
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
        foreach (var entry in entries.OrderBy(x => x.PublishedAt).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            _eventIds.Add(entry.Id); _events.Add(entry); _states[entry.Isin] = entry.State;
        }
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
            : lower.Contains("warning", StringComparison.Ordinal) ? InstrumentTradingState.Warning
            : InstrumentTradingState.Unknown;
        return new(id, match.Value, state, published, source);
    }

    [GeneratedRegex(@"\b[A-Z]{2}[A-Z0-9]{9}[0-9]\b", RegexOptions.CultureInvariant)]
    private static partial Regex IsinPattern();
}
