using System.Security.Cryptography;
using System.Text.Json;

namespace AiStocks.MarketData;

public sealed record ArchivedReport(string Report, string CsvPath, string MetadataPath, string Sha256, long Bytes, Uri SourceUrl, DateTimeOffset FetchedAt);

public sealed class ImmutableArchive(string root)
{
    private readonly string _root = Path.GetFullPath(root);

    public ArchivedReport Archive(string report, byte[] bytes, Uri sourceUrl, DateTimeOffset fetchedAt)
    {
        NasdaqReportName.Validate(report);
        ValidateSource(sourceUrl, report);
        if (bytes.Length is <= 0 or > 52_428_800 || !StartsWithSeparator(bytes)) throw new MarketDataException("Nasdaq report body is invalid");
        Directory.CreateDirectory(_root);
        var final = Path.Combine(_root, report);
        if (Directory.Exists(final)) return Verify(report);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var metadata = new ArchiveMetadata(report, hash, bytes.LongLength, sourceUrl.AbsoluteUri, fetchedAt);
        var temporary = Path.Combine(_root, $".{report}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            WriteDurable(Path.Combine(temporary, report + ".csv"), bytes);
            WriteDurable(Path.Combine(temporary, "metadata.json"), JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions));
            try { Directory.Move(temporary, final); }
            catch (IOException)
            {
                if (!Directory.Exists(final)) throw;
                var winner = Verify(report);
                if (winner.Sha256 != hash) throw new MarketDataException("Conflicting concurrent archive write");
                return winner;
            }
            return Verify(report);
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new MarketDataException("Atomic archive write failed", exception); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public ArchivedReport? TryVerify(string report)
    {
        NasdaqReportName.Validate(report);
        return Directory.Exists(Path.Combine(_root, report)) ? Verify(report) : null;
    }

    public ArchivedReport Verify(string report)
    {
        NasdaqReportName.Validate(report);
        var directory = Path.Combine(_root, report);
        var csv = Path.Combine(directory, report + ".csv");
        var metadataPath = Path.Combine(directory, "metadata.json");
        try
        {
            var bytes = File.ReadAllBytes(csv);
            var metadata = JsonSerializer.Deserialize<ArchiveMetadata>(File.ReadAllBytes(metadataPath), JsonOptions)
                ?? throw new MarketDataException("Archive metadata is malformed");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (metadata.Report != report || metadata.Sha256 != hash || metadata.Bytes != bytes.LongLength)
                throw new MarketDataException("Archive checksum or metadata mismatch");
            var source = new Uri(metadata.SourceUrl, UriKind.Absolute);
            ValidateSource(source, report);
            return new(report, csv, metadataPath, hash, bytes.LongLength, source, metadata.FetchedAt);
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or UriFormatException)
        { throw new MarketDataException("Archive entry is missing or malformed", exception); }
    }

    private static bool StartsWithSeparator(byte[] bytes)
    {
        var expected = "\"sep=;\""u8;
        return bytes.AsSpan().StartsWith(expected) || (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) && bytes.AsSpan(3).StartsWith(expected));
    }

    private static void ValidateSource(Uri uri, string report)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "tradereports.nasdaq.com" || uri.AbsolutePath != "/api/regulatory/trade-report/download")
            throw new MarketDataException("Archive source URL is not official");
        var expected = $"type=POST_TRADE&assetClass=EQUITY&fileName={report}";
        if (Uri.UnescapeDataString(uri.Query.TrimStart('?')) != expected) throw new MarketDataException("Archive source query is invalid");
    }

    private static void WriteDurable(string path, byte[] body)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(body); stream.Flush(true);
    }

    private sealed record ArchiveMetadata(string Report, string Sha256, long Bytes, string SourceUrl, DateTimeOffset FetchedAt);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record ManifestReport(string Report, string Sha256);
public sealed record CompleteSessionManifest(int SchemaVersion, bool Complete, string SessionId, DateTimeOffset SessionOpen,
    DateTimeOffset SessionClose, DateTimeOffset FinalizedAt, string SourceListingUrl, IReadOnlyList<ManifestReport> Reports);

public sealed class SessionManifestStore(string archiveRoot)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(archiveRoot), "sessions");

    public string Save(TradingSession session, IEnumerable<ArchivedReport> reports, DateTimeOffset finalizedAt)
    {
        var delay = finalizedAt - session.Close;
        if (delay < TimeSpan.FromMinutes(15) || delay > TimeSpan.FromMinutes(20)) throw new MarketDataException("Session finalization is outside feed delay");
        var rows = reports.OrderBy(x => x.Report, StringComparer.Ordinal).ToArray();
        var map = rows.ToDictionary(x => x.Report, x => x.Sha256, StringComparer.Ordinal);
        SessionManifest.ValidateComplete(session, map);
        var manifest = new CompleteSessionManifest(1, true, $"XSTO-{session.Day:yyyy-MM-dd}", session.Open, session.Close, finalizedAt,
            "https://tradereports.nasdaq.com/api/regulatory/trade-reports?type=POST_TRADE&assetClass=EQUITY",
            rows.Select(x => new ManifestReport(x.Report, x.Sha256)).ToArray());
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, manifest.SessionId + ".json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        if (File.Exists(path)) { ValidateExisting(path, manifest, session, options); return path; }
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true); }
        catch (IOException) { ValidateExisting(path, manifest, session, options); }
        return path;
    }

    private static void ValidateExisting(string path, CompleteSessionManifest candidate, TradingSession session, JsonSerializerOptions options)
    {
        CompleteSessionManifest existing;
        try { existing = JsonSerializer.Deserialize<CompleteSessionManifest>(File.ReadAllBytes(path), options) ?? throw new JsonException(); }
        catch (Exception exception) when (exception is IOException or JsonException)
        { throw new MarketDataException("Existing session manifest is malformed", exception); }
        var delay = existing.FinalizedAt - session.Close;
        if (delay < TimeSpan.FromMinutes(15) || delay > TimeSpan.FromMinutes(20) ||
            existing.SchemaVersion != candidate.SchemaVersion || existing.Complete != candidate.Complete ||
            existing.SessionId != candidate.SessionId || existing.SessionOpen != candidate.SessionOpen || existing.SessionClose != candidate.SessionClose ||
            existing.SourceListingUrl != candidate.SourceListingUrl || !existing.Reports.SequenceEqual(candidate.Reports))
            throw new MarketDataException("Conflicting immutable session manifest");
    }
}
