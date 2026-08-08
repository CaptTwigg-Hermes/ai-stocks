using System.Security.Cryptography;
using System.Text;
using AiStocks.MarketData;

namespace AiStocks.MarketData.Tests;

public sealed class MarketDataSecurityRegressionTests
{
    private const string Report = "NordicEquity-posttrade-2026-08-06T1016";

    [Fact]
    public async Task SameReportNameWithChangedUpstreamBytesIsFetchedAndConflicts()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var source = Source(Report);
        var first = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var changed = first.Concat("\n"u8.ToArray()).ToArray();
        archive.Archive(Report, first, source, DateTimeOffset.Parse("2026-08-06T10:16:00Z"));
        var requests = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(changed) };
        }))
        { BaseAddress = new Uri("https://tradereports.nasdaq.com") };

        await Assert.ThrowsAsync<MarketDataException>(() => new NasdaqPostTradeClient(http, archive)
            .DownloadAsync(Report, DateTimeOffset.Parse("2026-08-06T10:17:00Z"), CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Fact]
    public void CsvRejectsCharactersAfterClosingQuote()
    {
        var fixture = File.ReadAllText(Fixture("nasdaq-posttrade.csv"));
        var malformed = fixture.Replace(";1;---", ";\"1\"junk;---", StringComparison.Ordinal);
        Assert.Throws<MarketDataException>(() => NasdaqCsvParser.Parse(Encoding.UTF8.GetBytes(malformed), DateTimeOffset.Parse("2026-08-06T16:00:00Z")));
    }

    [Fact]
    public void AdvRequiresTwentyConsecutiveExpectedXstoSessionsIncludingZeroTradeDays()
    {
        var expected = ExpectedSessionsEnding(new DateOnly(2026, 8, 6), 20);
        var values = expected.Select((day, i) => new SessionTradedValue(day, i == 3 ? 0m : 100m, true, "manifest-" + day)).ToArray();
        Assert.Equal(95m, AverageDailyValue.Calculate20(values));
        var gap = values.Where(x => x.Session != expected[10]).Append(new SessionTradedValue(new DateOnly(2026, 7, 1), 100m, true, "old")).ToArray();
        Assert.Throws<MarketDataException>(() => AverageDailyValue.Calculate20(gap));
        Assert.Throws<MarketDataException>(() => AverageDailyValue.Calculate20(values.Select(x => x.Session == expected[3] ? x with { ManifestChecksum = "" } : x)));
    }

    [Fact]
    public void SignedStatusPinsSignerPreservesAsOfAndPersistsMonotonicReplayState()
    {
        using var temp = new TemporaryDirectory();
        using var pinned = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = "{\"asOf\":\"2026-08-06T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var verifier = new PinnedStatusSeedVerifier("ops-2026", pinned.ExportSubjectPublicKeyInfo());
        Assert.Throws<MarketDataException>(() => verifier.Load(payload, attacker.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json")));
        var machine = verifier.Load(payload, pinned.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        Assert.Equal(DateTimeOffset.Parse("2026-08-06T07:00:00Z"), machine.SeedAsOf);
        Assert.Equal("ops-2026", machine.SignerKeyId);
        machine.ApplyRss(Rss("new", "Thu, 06 Aug 2026 08:00:00 GMT", "suspension"));
        Assert.Throws<MarketDataException>(() => machine.ApplyRss(Rss("old", "Wed, 05 Aug 2026 08:00:00 GMT", "resumption")));
        var restarted = verifier.Load(payload, pinned.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        Assert.Equal(InstrumentTradingState.Suspended, restarted.StateOf("SE0000108656"));
        Assert.Throws<MarketDataException>(() => restarted.ApplyRss(Rss("new", "Thu, 06 Aug 2026 08:00:00 GMT", "suspension")));
    }

    [Fact]
    public void DurableFirdsStateUsesIsinAndOrderBookAndRejectsCursorReplay()
    {
        using var temp = new TemporaryDirectory();
        var store = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json"));
        var fullBytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        store.ApplyFull(new MemoryStream(fullBytes), new DateOnly(2026, 8, 6), new Uri("https://registers.esma.europa.eu/firds/full.xml"), Convert.ToHexStringLower(SHA256.HashData(fullBytes)), "full-20260806", 10);
        var snapshot = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json")).LoadVerified();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(("SE0000108656", "ERIC-B"), (snapshot.Instruments[0].Isin, snapshot.Instruments[0].OrderBookId));
        Assert.Equal(10, snapshot.Cursor);
        var delta = File.ReadAllBytes(Fixture("firds-delta.xml"));
        store.ApplyDelta(new MemoryStream(delta), new DateOnly(2026, 8, 6), new Uri("https://registers.esma.europa.eu/firds/delta.xml"), Convert.ToHexStringLower(SHA256.HashData(delta)), "delta-20260806-11", 11);
        Assert.Empty(store.LoadVerified().Instruments);
        Assert.Throws<MarketDataException>(() => store.ApplyDelta(new MemoryStream(delta), new DateOnly(2026, 8, 6), new Uri("https://registers.esma.europa.eu/firds/delta.xml"), Convert.ToHexStringLower(SHA256.HashData(delta)), "replay", 11));
    }

    [Fact]
    public void FirdsFullIsMonotonicAndRetainsImmutableVersionHistory()
    {
        using var temp = new TemporaryDirectory();
        var store = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json"));
        var bytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var source = new Uri("https://registers.esma.europa.eu/firds/full.xml");
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6), source, hash, "full-10", 10);
        Assert.Throws<MarketDataException>(() => store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6), source, hash, "full-9", 9));
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6), source, hash, "full-20", 20);
        var state = store.LoadVerified();
        Assert.Equal(new long[] { 10, 20 }, state.Versions.Select(x => x.Cursor));
        Assert.All(state.Versions, version => Assert.True(File.Exists(version.RawPath)));
    }

    [Fact]
    public async Task CollectorVerifiesAndSkipsAlreadyFinalizedPriorSessionInFortyEightHourListing()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var manifests = new SessionManifestStore(temp.Path);
        var session = StockholmCalendar.GetSession(new DateOnly(2026, 8, 6))!;
        var body = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var archived = SessionManifest.ExpectedReports(session).Select(name => archive.Archive(name, body, Source(name), session.Close.AddMinutes(15))).ToArray();
        manifests.Save(session, archived, session.Close.AddMinutes(15));
        var downloads = 0;
        var listing = string.Join(',', archived.Select(x => $"\"{x.Report}\""));
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("trade-reports", StringComparison.Ordinal))
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent($"{{\"message\":null,\"reports\":[{listing}]}}") };
            downloads++; return new(System.Net.HttpStatusCode.InternalServerError);
        }))
        {
            BaseAddress = new Uri("https://tradereports.nasdaq.com")
        };
        var result = await new NasdaqCollector(new NasdaqPostTradeClient(http, archive), archive, manifests)
            .CollectOnceAsync(session.Close.AddDays(1), CancellationToken.None);
        Assert.Empty(result.Downloaded);
        Assert.Empty(result.Missing);
        Assert.Equal(0, downloads);
    }

    [Fact]
    public void ManifestChecksumTamperingFailsClosed()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var store = new SessionManifestStore(temp.Path);
        var session = StockholmCalendar.GetSession(new DateOnly(2026, 8, 6))!;
        var body = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var archived = SessionManifest.ExpectedReports(session).Select(name => archive.Archive(name, body, Source(name), session.Close.AddMinutes(15))).ToArray();
        var path = store.Save(session, archived, session.Close.AddMinutes(15));
        Assert.Equal(64, store.Verify(session).Sha256.Length);
        File.AppendAllText(path, " ");
        Assert.Throws<MarketDataException>(() => store.Verify(session));
        Assert.False(File.Exists(path + ".sha256"));
    }

    [Fact]
    public async Task CollectorFinalizesCompleteRetainedSessionAfterWindowAndRejectsMalformedCsv()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var manifests = new SessionManifestStore(temp.Path);
        var session = StockholmCalendar.GetSession(new DateOnly(2026, 8, 6))!;
        var expected = SessionManifest.ExpectedReports(session);
        var listing = string.Join(',', expected.Select(x => $"\"{x}\""));
        var valid = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("trade-reports", StringComparison.Ordinal)
            ? new(System.Net.HttpStatusCode.OK) { Content = new StringContent($"{{\"message\":null,\"reports\":[{listing}]}}") }
            : new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(valid) }))
        { BaseAddress = new Uri("https://tradereports.nasdaq.com") };
        var result = await new NasdaqCollector(new NasdaqPostTradeClient(http, archive), archive, manifests)
            .CollectOnceAsync(session.Close.AddHours(24), CancellationToken.None);
        Assert.NotNull(result.FinalizedManifest);

        using var malformedTemp = new TemporaryDirectory();
        var malformedArchive = new ImmutableArchive(malformedTemp.Path);
        foreach (var report in expected)
            malformedArchive.Archive(report, "\"sep=;\"\n"u8.ToArray(), Source(report), session.Close.AddMinutes(15));
        Assert.Throws<MarketDataException>(() => new SessionManifestStore(malformedTemp.Path)
            .Save(session, expected.Select(malformedArchive.Verify), session.Close.AddHours(24)));
    }

    [Fact]
    public void ObservationAggregatesAndPatsAreDerivedFromStrictManifestRows()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var manifests = new SessionManifestStore(temp.Path);
        var session = StockholmCalendar.GetSession(new DateOnly(2026, 8, 6))!;
        var body = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var reports = SessionManifest.ExpectedReports(session).Select(name => archive.Archive(name, body, Source(name), session.Close.AddMinutes(15))).ToArray();
        manifests.Save(session, reports, session.Close.AddHours(1));
        var store = new DurableObservationStore(Path.Combine(temp.Path, "observations.json"), archive, manifests);
        var values = store.DeriveSession(session, [new FirdsInstrument("SE0000108656", "ERIC-B", "5493001KJTIIGC8Y1R12", "Ericsson B", "ESVUFR", "SEK", "XSTO", null, null)]);
        Assert.Single(values);
        Assert.True(values[0].TradedValue > 0);
        Assert.True(values[0].UsableTradeCount > 0);
        Assert.Equal(98.10m, values[0].OfficialPatsPrice);
    }

    [Fact]
    public void StatusFreshnessIsEvaluatedAtRequestedAsOf()
    {
        using var temp = new TemporaryDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = "{\"asOf\":\"2026-08-01T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var machine = new PinnedStatusSeedVerifier("ops", key.ExportSubjectPublicKeyInfo()).Load(payload,
            key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        Assert.False(machine.IsFreshAt(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ReadinessFailsClosedWhenAnyPinnedDurableStateIsMissing()
    {
        using var temp = new TemporaryDirectory();
        var readiness = new ConfiguredMarketDataReadiness(temp.Path, Path.Combine(temp.Path, "firds.json"),
            Path.Combine(temp.Path, "observations.json"), Path.Combine(temp.Path, "seed.json"),
            Path.Combine(temp.Path, "seed.sig"), Path.Combine(temp.Path, "seed.der"), "ops-2026");
        var result = readiness.Evaluate(new DateOnly(2026, 8, 6));
        Assert.False(result.Ready);
        Assert.NotEmpty(result.Failures);
    }

    private static IReadOnlyList<DateOnly> ExpectedSessionsEnding(DateOnly ending, int count)
    {
        var result = new List<DateOnly>();
        for (var day = ending; result.Count < count; day = day.AddDays(-1)) if (StockholmCalendar.GetSession(day) is not null) result.Add(day);
        result.Reverse(); return result;
    }

    private static Stream Rss(string id, string published, string state) => new MemoryStream(Encoding.UTF8.GetBytes($"<rss><channel><item><guid>{id}</guid><title>SE0000108656 {state}</title><description>{state}</description><pubDate>{published}</pubDate><link>https://api.news.eu.nasdaq.com/news/{id}</link></item></channel></rss>"));
    private static Uri Source(string report) => new($"https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={report}");
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aistocks-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
