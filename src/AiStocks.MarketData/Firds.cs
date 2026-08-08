using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace AiStocks.MarketData;

public sealed record FirdsInstrument(string Isin, string OrderBookId, string IssuerId, string Name, string Cfi,
    string Currency, string Venue, DateOnly? FirstTradeDate, DateOnly? TerminationDate);
public sealed record FirdsSourceVersion(string Version, long Cursor, Uri SourceUrl, string Sha256, DateTimeOffset AppliedAt, bool IsFull, string RawPath);
public sealed record FirdsSnapshot(long Cursor, IReadOnlyList<FirdsInstrument> Instruments, IReadOnlyList<FirdsSourceVersion> Versions);

public sealed class FirdsUniverseParser
{
    public IReadOnlyList<FirdsInstrument> ParseFull(Stream xml, DateOnly effectiveAt) =>
        Read(xml).Where(x => x.Operation is not Operation.Delete && IsEligible(x.Instrument, effectiveAt))
            .Select(x => x.Instrument).GroupBy(x => (x.Isin, x.OrderBookId))
            .Select(group => group.Last()).OrderBy(x => x.Isin, StringComparer.Ordinal).ThenBy(x => x.OrderBookId, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<FirdsInstrument> ApplyDelta(IEnumerable<FirdsInstrument> current, Stream deltaXml, DateOnly effectiveAt)
    {
        var result = current.ToDictionary(x => (x.Isin, x.OrderBookId));
        foreach (var change in Read(deltaXml))
        {
            var key = (change.Instrument.Isin, change.Instrument.OrderBookId);
            if (change.Operation == Operation.Delete || !IsEligible(change.Instrument, effectiveAt)) result.Remove(key);
            else result[key] = change.Instrument;
        }
        return result.Values.OrderBy(x => x.Isin, StringComparer.Ordinal).ThenBy(x => x.OrderBookId, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Change> Read(Stream xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = 1_000_000_000 };
        using var reader = XmlReader.Create(xml, settings);
        var found = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("NewRcrd" or "ModfdRcrd" or "TermntdRcrd")) continue;
            found = true;
            var operation = reader.LocalName == "TermntdRcrd" ? Operation.Delete : Operation.Upsert;
            using var subtree = reader.ReadSubtree();
            yield return new(operation, Parse(XElement.Load(subtree, LoadOptions.None)));
        }
        if (!found) throw new MarketDataException("FIRDS file contains no instrument records");
    }

    private static FirdsInstrument Parse(XElement record)
    {
        string Value(string name) => record.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
        DateOnly? Date(string name) => DateOnly.TryParse(Value(name), out var result) ? result : null;
        var general = record.Descendants().FirstOrDefault(x => x.Name.LocalName == "FinInstrmGnlAttrbts") ?? throw new MarketDataException("FIRDS general attributes are missing");
        string General(string name) => general.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
        var venueAttributes = record.Descendants().FirstOrDefault(x => x.Name.LocalName == "TradgVnRltdAttrbts")
            ?? throw new MarketDataException("FIRDS venue attributes are missing");
        string VenueValue(string name) => venueAttributes.Elements()
            .FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
        var item = new FirdsInstrument(
            General("Id"), VenueValue("TradgVnInstrmId"), Value("Issr"), General("FullNm"),
            General("ClssfctnTp"), General("NtnlCcy"), VenueValue("Id"),
            Date("FrstTradDt"), Date("TermntnDt"));
        if (item.Isin.Length != 12 || item.IssuerId.Length != 20 || item.Cfi.Length != 6 ||
            item.Venue.Length != 4 || string.IsNullOrWhiteSpace(item.OrderBookId))
            throw new MarketDataException("FIRDS ISIN/order-book/issuer identity is malformed");
        return item;
    }

    private static bool IsEligible(FirdsInstrument item, DateOnly at) =>
        item.Venue == "XSTO" && item.Currency == "SEK" && IsCommonOrdinaryShare(item.Cfi) &&
        (item.FirstTradeDate is null || item.FirstTradeDate <= at) &&
        (item.TerminationDate is null || item.TerminationDate > at);

    private static bool IsCommonOrdinaryShare(string cfi) =>
        cfi.Length == 6 && cfi[0] == 'E' && cfi[1] == 'S' &&
        "VNRE".Contains(cfi[2], StringComparison.Ordinal) &&
        "TU".Contains(cfi[3], StringComparison.Ordinal) &&
        "FOP".Contains(cfi[4], StringComparison.Ordinal) &&
        "BRNM".Contains(cfi[5], StringComparison.Ordinal);
    private enum Operation { Upsert, Delete }
    private sealed record Change(Operation Operation, FirdsInstrument Instrument);
}

public sealed class DurableFirdsStore
{
    private readonly string _path;
    private readonly FirdsUniverseParser _parser = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public DurableFirdsStore(string path) => _path = Path.GetFullPath(path);
    public bool Exists => File.Exists(_path);

    public void ApplyFull(Stream xml, DateOnly effectiveAt, Uri sourceUrl, string sha256, string version, long cursor)
    {
        var bytes = ReadAndVerify(xml, sourceUrl, sha256, version, cursor);
        var instruments = _parser.ParseFull(OpenXmlPayload(bytes), effectiveAt);
        var current = File.Exists(_path) ? LoadVerified() : null;
        if (current is not null && (cursor <= current.Cursor || current.Versions.Any(x => x.Version == version)))
            throw new MarketDataException("FIRDS full cursor/version is not monotonic");
        var versionRow = new FirdsSourceVersion(version, cursor, sourceUrl, sha256, DateTimeOffset.UtcNow, true, ArchiveRaw(bytes, sha256));
        Persist(new(cursor, instruments, current is null ? [versionRow] : current.Versions.Append(versionRow).ToArray()));
    }

    public void ApplyDelta(Stream xml, DateOnly effectiveAt, Uri sourceUrl, string sha256, string version, long cursor)
    {
        var current = LoadVerified();
        if (cursor != current.Cursor + 1 || current.Versions.Any(x => x.Version == version)) throw new MarketDataException("FIRDS delta cursor/version replay or gap");
        var bytes = ReadAndVerify(xml, sourceUrl, sha256, version, cursor);
        var instruments = _parser.ApplyDelta(current.Instruments, OpenXmlPayload(bytes), effectiveAt);
        Persist(new(cursor, instruments, current.Versions.Append(new(version, cursor, sourceUrl, sha256, DateTimeOffset.UtcNow, false, ArchiveRaw(bytes, sha256))).ToArray()));
    }

    public void ApplyFullPart(Stream xml, DateOnly effectiveAt, Uri sourceUrl, string sha256, string version, long cursor)
    {
        var current = LoadVerified();
        if (cursor != current.Cursor + 1 || current.Versions.Any(x => x.Version == version))
            throw new MarketDataException("FIRDS full-part cursor/version replay or gap");
        var bytes = ReadAndVerify(xml, sourceUrl, sha256, version, cursor);
        var instruments = current.Instruments.Concat(_parser.ParseFull(OpenXmlPayload(bytes), effectiveAt))
            .GroupBy(x => (x.Isin, x.OrderBookId)).Select(group => group.Last())
            .OrderBy(x => x.Isin, StringComparer.Ordinal).ThenBy(x => x.OrderBookId, StringComparer.Ordinal).ToArray();
        Persist(new(cursor, instruments, current.Versions.Append(new(version, cursor, sourceUrl, sha256,
            DateTimeOffset.UtcNow, true, ArchiveRaw(bytes, sha256))).ToArray()));
    }

    public FirdsSnapshot LoadVerified()
    {
        if (!File.Exists(_path)) throw new MarketDataException("Durable FIRDS state is missing");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(_path), JsonOptions) ?? throw new JsonException();
            var actual = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions)));
            if (actual != envelope.Sha256 || envelope.State.Cursor <= 0 || envelope.State.Versions.Count == 0 ||
                envelope.State.Versions[^1].Cursor != envelope.State.Cursor) throw new MarketDataException("Durable FIRDS state checksum/cursor is invalid");
            foreach (var version in envelope.State.Versions)
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(version.RawPath))) != version.Sha256)
                    throw new MarketDataException("Durable FIRDS raw version checksum is invalid");
            return envelope.State;
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException)
        { throw new MarketDataException("Durable FIRDS state is malformed", exception); }
    }

    private static byte[] ReadAndVerify(Stream stream, Uri sourceUrl, string sha256, string version, long cursor)
    {
        if (!IsOfficialSource(sourceUrl) || string.IsNullOrWhiteSpace(version) || cursor <= 0 ||
            sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new MarketDataException("FIRDS source provenance is invalid");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(sha256))) throw new MarketDataException("FIRDS source checksum mismatch");
        return bytes;
    }

    public static bool IsOfficialSource(Uri sourceUrl) =>
        sourceUrl.Scheme == Uri.UriSchemeHttps && sourceUrl.IsDefaultPort && string.IsNullOrEmpty(sourceUrl.UserInfo) &&
        string.IsNullOrEmpty(sourceUrl.Query) && string.IsNullOrEmpty(sourceUrl.Fragment) &&
        sourceUrl.Host == "firds.esma.europa.eu" && sourceUrl.AbsolutePath.StartsWith("/firds/", StringComparison.Ordinal) &&
        sourceUrl.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static Stream OpenXmlPayload(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K') return new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length <= 0 || entries[0].Length > 1_000_000_000)
            throw new MarketDataException("FIRDS ZIP must contain exactly one bounded XML payload");
        using var input = entries[0].Open();
        var output = new MemoryStream(checked((int)Math.Min(entries[0].Length, int.MaxValue)));
        input.CopyTo(output);
        if (output.Length != entries[0].Length) throw new MarketDataException("FIRDS ZIP payload length is invalid");
        output.Position = 0;
        return output;
    }

    private void Persist(FirdsSnapshot state)
    {
        var checksum = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions)));
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(new Envelope(checksum, state), JsonOptions));
    }
    private string ArchiveRaw(byte[] bytes, string sha256)
    {
        var path = Path.Combine(_path + ".raw", sha256 + ".xml");
        if (File.Exists(path))
        {
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) != sha256)
                throw new MarketDataException("Conflicting FIRDS raw archive");
            return path;
        }
        AtomicFile.Write(path, bytes);
        return path;
    }
    private sealed record Envelope(string Sha256, FirdsSnapshot State);
}
