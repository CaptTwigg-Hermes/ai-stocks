using System.Text.Json;

namespace AiStocks.MarketData;

public sealed class NasdaqPostTradeClient(HttpClient http, ImmutableArchive archive)
{
    private const string Listing = "/api/regulatory/trade-reports?type=POST_TRADE&assetClass=EQUITY";

    public async Task<IReadOnlyList<string>> ListReportsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(Listing, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Null ||
                !root.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Array)
                throw new MarketDataException("Nasdaq listing schema is invalid");
            var names = reports.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new MarketDataException("Nasdaq report name is not a string")).ToArray();
            if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new MarketDataException("Nasdaq listing is empty or contains replayed names");
            foreach (var name in names) NasdaqReportName.Validate(name);
            return names;
        }
        catch (MarketDataException) { throw; }
        catch (JsonException exception) { throw new MarketDataException("Nasdaq listing is malformed", exception); }
    }

    public async Task<ArchivedReport> DownloadAsync(string report, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        NasdaqReportName.Validate(report);
        var path = $"/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={Uri.EscapeDataString(report)}";
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 52_428_800) throw new MarketDataException("Nasdaq report is oversized");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var source = response.RequestMessage?.RequestUri ?? (http.BaseAddress is { } baseAddress ? new Uri(baseAddress, path) : throw new MarketDataException("Nasdaq response source is missing"));
        return archive.Archive(report, bytes, source, fetchedAt);
    }
}

public sealed record CollectionResult(IReadOnlyList<string> Downloaded, IReadOnlyList<string> FinalizedManifests, IReadOnlyList<string> Missing)
{
    public string? FinalizedManifest => FinalizedManifests.LastOrDefault();
}

public sealed record CollectorDownloadPolicy(int MaximumDownloadsPerPoll, TimeSpan ConflictRefetchInterval)
{
    public static CollectorDownloadPolicy Default { get; } = new(32, TimeSpan.FromHours(6));
    public CollectorDownloadPolicy() : this(32, TimeSpan.FromHours(6)) { }
}

public sealed class NasdaqCollector
{
    private readonly NasdaqPostTradeClient client;
    private readonly ImmutableArchive archive;
    private readonly SessionManifestStore manifests;
    private readonly CollectorDownloadPolicy policy;
    private readonly Dictionary<string, DateTimeOffset> lastConflictChecks = new(StringComparer.Ordinal);

    public NasdaqCollector(NasdaqPostTradeClient client, ImmutableArchive archive, SessionManifestStore manifests)
        : this(client, archive, manifests, CollectorDownloadPolicy.Default) { }

    public NasdaqCollector(NasdaqPostTradeClient client, ImmutableArchive archive, SessionManifestStore manifests,
        CollectorDownloadPolicy policy)
    {
        if (policy.MaximumDownloadsPerPoll is < 1 or > 128 || policy.ConflictRefetchInterval < TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(policy));
        this.client = client;
        this.archive = archive;
        this.manifests = manifests;
        this.policy = policy;
    }

    public async Task<CollectionResult> CollectOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var listing = await client.ListReportsAsync(cancellationToken).ConfigureAwait(false);
        var listed = listing.ToHashSet(StringComparer.Ordinal);
        var downloaded = new List<string>();
        var missing = new List<string>();
        var finalized = new List<string>();
        var downloadBudget = policy.MaximumDownloadsPerPoll;
        var days = listing.Select(NasdaqReportName.ParseTimestamp).Select(x => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x, StockholmCalendar.Zone).DateTime))
            .Append(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, StockholmCalendar.Zone).DateTime)).Distinct().Order().ToArray();
        foreach (var day in days)
        {
            if (StockholmCalendar.GetSession(day) is not { } session) continue;
            if (manifests.TryVerify(session) is { } complete)
            {
                foreach (var report in complete.Manifest.Reports)
                {
                    var archivedReport = archive.Verify(report.Report);
                    if (archivedReport.Sha256 != report.Sha256) throw new MarketDataException("Finalized manifest archive provenance mismatch");
                }
                continue;
            }
            var due = listing.Where(x =>
            {
                var timestamp = NasdaqReportName.ParseTimestamp(x);
                return timestamp >= session.Open.AddMinutes(15) && timestamp <= session.Close.AddMinutes(15) && timestamp <= now;
            }).Select(report => (Report: report, Existing: archive.TryVerify(report)))
              .OrderBy(item => item.Existing is null ? 0 : 1)
              .ThenBy(item => NasdaqReportName.ParseTimestamp(item.Report));
            foreach (var item in due)
            {
                var report = item.Report;
                var existing = item.Existing;
                var lastChecked = lastConflictChecks.GetValueOrDefault(report, existing?.FetchedAt ?? DateTimeOffset.MinValue);
                if (existing is not null && now - lastChecked < policy.ConflictRefetchInterval) continue;
                if (downloadBudget == 0) break;
                await client.DownloadAsync(report, now, cancellationToken).ConfigureAwait(false);
                lastConflictChecks[report] = now;
                downloaded.Add(report);
                downloadBudget--;
            }
            if (now < session.Close.AddMinutes(15)) continue;
            var expected = SessionManifest.ExpectedReports(session);
            var absent = expected.Where(x => !listed.Contains(x)).ToArray();
            if (absent.Length > 0) { missing.AddRange(absent); continue; }
            if (expected.Any(x => archive.TryVerify(x) is null)) continue;
            var archived = expected.Select(archive.Verify).ToArray();
            finalized.Add(manifests.Save(session, archived, now));
        }
        return new(downloaded, finalized, missing.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
}

public sealed class CollectorHealth
{
    private readonly TimeSpan _maximumAge;
    private readonly object _gate = new();
    private DateTimeOffset? _lastSuccess;
    private Exception? _lastFailure;

    public CollectorHealth(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        _maximumAge = maximumAge;
    }

    public void RecordSuccess(DateTimeOffset at) { lock (_gate) { _lastSuccess = at; _lastFailure = null; } }
    public void RecordFailure(Exception error, DateTimeOffset at) { ArgumentNullException.ThrowIfNull(error); lock (_gate) { _lastFailure = error; } }
    public bool IsHealthy(DateTimeOffset now) { lock (_gate) { return _lastFailure is null && _lastSuccess is { } success && now >= success && now - success <= _maximumAge; } }
    public string? Failure { get { lock (_gate) return _lastFailure?.Message; } }
}
