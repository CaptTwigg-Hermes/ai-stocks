using System.Security.Cryptography;
using System.Text;
using AiStocks.MarketData;

namespace AiStocks.MarketData.Tests;

public sealed class MarketDataTests
{
    private static readonly DateOnly FullDay = new(2026, 8, 6);

    [Fact]
    public void PinnedCalendarHasExactSessionsAndSixRuns()
    {
        StockholmCalendar.VerifyPinnedArtifacts(RepositoryRoot());
        var full = StockholmCalendar.GetSession(FullDay)!;
        var half = StockholmCalendar.GetSession(new DateOnly(2026, 4, 30))!;

        Assert.Equal(SessionKind.Full, full.Kind);
        Assert.Equal((9, 0, 17, 30), (full.Open.Hour, full.Open.Minute, full.Close.Hour, full.Close.Minute));
        Assert.Equal(new[] { "08:00", "10:42", "12:24", "14:06", "15:48", "18:00" },
            StockholmCalendar.SixRunTimes(full).Select(x => x.ToString("HH:mm")));
        Assert.Equal(new[] { "08:00", "09:48", "10:36", "11:24", "12:12", "13:30" },
            StockholmCalendar.SixRunTimes(half).Select(x => x.ToString("HH:mm")));
        Assert.Null(StockholmCalendar.GetSession(new DateOnly(2026, 6, 19)));
        Assert.Equal(new DateOnly(2026, 12, 30), StockholmCalendar.FinalSession2026().Day);
    }

    [Fact]
    public void ReportNamesAreStockholmLocalAndCoverExactFullAndHalfSessions()
    {
        var full = SessionManifest.ExpectedReports(StockholmCalendar.GetSession(FullDay)!);
        var half = SessionManifest.ExpectedReports(StockholmCalendar.GetSession(new DateOnly(2026, 10, 30))!);
        Assert.Equal((511, "NordicEquity-posttrade-2026-08-06T0915", "NordicEquity-posttrade-2026-08-06T1745"), (full.Count, full[0], full[^1]));
        Assert.Equal((241, "NordicEquity-posttrade-2026-10-30T0915", "NordicEquity-posttrade-2026-10-30T1315"), (half.Count, half[0], half[^1]));
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 9, 15, 0, TimeSpan.FromHours(2)), NasdaqReportName.ParseTimestamp(full[0]));
    }

    [Fact]
    public void StrictCsvFindsFirstPostDecisionTradeAndClosingPats()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new TemporaryDirectory();
        var payload = "{\"asOf\":\"2026-08-06T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var statuses = new PinnedStatusSeedVerifier("test-key", key.ExportSubjectPublicKeyInfo()).Load(payload,
            key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        var rows = NasdaqCsvParser.Parse(File.ReadAllBytes(Fixture("nasdaq-posttrade.csv")), DateTimeOffset.Parse("2026-08-06T16:00:00Z"));
        var session = StockholmCalendar.GetSession(FullDay)!;
        var trade = NasdaqTradeSelection.FirstEligible(rows, "SE0000108656", DateTimeOffset.Parse("2026-08-06T10:00:00Z"), session, statuses);
        Assert.Equal(96.95m, trade.Price);
        Assert.Equal("3", trade.TransactionId);
        Assert.Equal(98.10m, NasdaqTradeSelection.ClosingAuctionPrice(rows, "SE0000108656", session));
    }

    [Fact]
    public void CsvMalformedSchemaAndAmbiguousPatsFailClosed()
    {
        Assert.Throws<MarketDataException>(() => NasdaqCsvParser.Parse(Encoding.UTF8.GetBytes("a,b\n1,2"), DateTimeOffset.UtcNow));
        var rows = NasdaqCsvParser.Parse(File.ReadAllBytes(Fixture("nasdaq-posttrade.csv")), DateTimeOffset.Parse("2026-08-06T16:00:00Z"));
        var conflicting = rows.Concat(new[] { rows.Single(x => x.Flags.Contains("PATS", StringComparison.Ordinal)) with { Price = 99m, TransactionId = "other" } });
        Assert.Throws<MarketDataException>(() => NasdaqTradeSelection.ClosingAuctionPrice(conflicting, "SE0000108656", StockholmCalendar.GetSession(FullDay)!));
    }

    [Fact]
    public void AtomicArchiveDetectsTamperingAndReplayConflict()
    {
        using var temp = new TemporaryDirectory();
        var store = new ImmutableArchive(temp.Path);
        var bytes = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var report = "NordicEquity-posttrade-2026-08-06T1016";
        var item = store.Archive(report, bytes, new Uri("https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName=" + report), DateTimeOffset.Parse("2026-08-06T10:16:00Z"));
        Assert.Equal(item, store.Verify(report));
        File.AppendAllText(item.CsvPath, "tamper");
        Assert.Throws<MarketDataException>(() => store.Verify(report));
        Assert.Throws<MarketDataException>(() => store.Archive(report, bytes, item.SourceUrl, item.FetchedAt));
    }

    [Fact]
    public void CompleteManifestAndAdvRequireExactlyTwentyBoundSessions()
    {
        var days = new List<DateOnly>();
        for (var day = new DateOnly(2026, 7, 31); days.Count < 20; day = day.AddDays(-1))
            if (StockholmCalendar.GetSession(day) is not null) days.Add(day);
        days.Reverse();
        var values = days.Select((day, i) => new SessionTradedValue(day, 1000m + i, true)).ToArray();
        Assert.Equal(1009.5m, AverageDailyValue.Calculate20(values));
        Assert.Throws<MarketDataException>(() => AverageDailyValue.Calculate20(values[..19]));
        Assert.Throws<MarketDataException>(() => AverageDailyValue.Calculate20(values.Concat(new[] { values[0] with { TradedValue = 1m } })));
    }

    [Fact]
    public void FirdsFullAndDeltaKeepOnlyActiveXstoCommonShares()
    {
        var parser = new FirdsUniverseParser();
        var full = parser.ParseFull(File.OpenRead(Fixture("firds-full.xml")), FullDay);
        Assert.Single(full);
        Assert.Equal("SE0000108656", full[0].Isin);
        Assert.Equal("5493001KJTIIGC8Y1R12", full[0].IssuerId);
        var updated = parser.ApplyDelta(full, File.OpenRead(Fixture("firds-delta.xml")), FullDay);
        Assert.Empty(updated);
    }

    [Fact]
    public void FirdsOfficialFullFileRefDataUsesIsinAsStableXstoIdentity()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02"><RefData>
              <FinInstrmGnlAttrbts><Id>SE0000118952</Id><FullNm>NCC AB ser. A</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts>
              <Issr>213800WRGLW3CY4MHW53</Issr>
              <TradgVnRltdAttrbts><Id>XSTO</Id><FrstTradDt>1998-10-04T06:00:00Z</FrstTradDt></TradgVnRltdAttrbts>
            </RefData></Document>
            """;

        var instruments = new FirdsUniverseParser().ParseFull(
            new MemoryStream(Encoding.UTF8.GetBytes(xml)), FullDay);

        var instrument = Assert.Single(instruments);
        Assert.Equal("SE0000118952", instrument.Isin);
        Assert.Equal("SE0000118952", instrument.OrderBookId);
        Assert.Equal(new DateOnly(1998, 10, 4), instrument.FirstTradeDate);
    }

    [Fact]
    public void FirdsCfiEligibilityAdmitsOnlyDefinedCommonOrdinaryShareSubtypes()
    {
        var instruments = new FirdsUniverseParser().ParseFull(
            File.OpenRead(Fixture("firds-cfi-subtypes.xml")), FullDay);

        Assert.Equal(new[] { "ESNUFR", "ESVUFR" }, instruments.Select(x => x.Cfi).Order());
        Assert.DoesNotContain(instruments, x => x.OrderBookId is "PREF" or "SDB" or "OTHER-X" or "OTHER-C");
    }

    [Fact]
    public void OfficialNoticeStateStartsUnknownUsesSignedSeedAndRejectsReplay()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new TemporaryDirectory();
        var payload = "{\"asOf\":\"2026-08-06T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var signature = key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256);
        var machine = new PinnedStatusSeedVerifier("test-key", key.ExportSubjectPublicKeyInfo())
            .Load(payload, signature, Path.Combine(temp.Path, "status.json"));
        Assert.True(machine.IsEligible("SE0000108656"));
        Assert.False(machine.IsEligible("SE9999999999"));
        machine.ApplyRss(File.OpenRead(Fixture("nasdaq-status-rss.xml")));
        Assert.False(machine.IsEligible("SE0000108656"));
        Assert.Throws<MarketDataException>(() => machine.ApplyRss(File.OpenRead(Fixture("nasdaq-status-rss.xml"))));
    }

    [Fact]
    public async Task OfficialClientRejectsListingReplayAndArchivesOnlyStrictNames()
    {
        using var temp = new TemporaryDirectory();
        var report = "NordicEquity-posttrade-2026-08-06T1016";
        var bytes = await File.ReadAllBytesAsync(Fixture("nasdaq-posttrade.csv"));
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("trade-reports", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent($"{{\"message\":null,\"reports\":[\"{report}\"]}}") }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tradereports.nasdaq.com") };
        var client = new NasdaqPostTradeClient(http, new ImmutableArchive(temp.Path));
        Assert.Equal(new[] { report }, await client.ListReportsAsync(CancellationToken.None));
        var archived = await client.DownloadAsync(report, DateTimeOffset.Parse("2026-08-06T16:00:00Z"), CancellationToken.None);
        Assert.Equal(report, archived.Report);

        using var replayHttp = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent($"{{\"message\":null,\"reports\":[\"{report}\",\"{report}\"]}}") }))
        { BaseAddress = http.BaseAddress };
        await Assert.ThrowsAsync<MarketDataException>(() => new NasdaqPostTradeClient(replayHttp, new ImmutableArchive(temp.Path)).ListReportsAsync(CancellationToken.None));
    }

    [Fact]
    public void CollectorHealthFailsClosedOnMissingStaleOrFailedPoll()
    {
        var health = new CollectorHealth(TimeSpan.FromMinutes(2));
        var now = DateTimeOffset.Parse("2026-08-06T10:00:00Z");
        Assert.False(health.IsHealthy(now));
        health.RecordSuccess(now);
        Assert.True(health.IsHealthy(now.AddSeconds(119)));
        Assert.False(health.IsHealthy(now.AddMinutes(2).AddSeconds(1)));
        health.RecordFailure(new InvalidOperationException("feed"), now.AddMinutes(3));
        Assert.False(health.IsHealthy(now.AddMinutes(3)));
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
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
