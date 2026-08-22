using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportDownloadEnforcesStreamingByteBoundWithMissingOrLyingLength(bool lyingLength)
    {
        using var temp = new TemporaryDirectory();
        var stream = new OversizedReportStream(52_428_800 + 8_192);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            var content = new StreamContent(stream);
            if (lyingLength) content.Headers.ContentLength = 1;
            return new(System.Net.HttpStatusCode.OK) { Content = content };
        }))
        { BaseAddress = new Uri("https://tradereports.nasdaq.com") };

        var exception = await Assert.ThrowsAsync<MarketDataException>(() =>
            new NasdaqPostTradeClient(http, new ImmutableArchive(temp.Path))
                .DownloadAsync(Report, DateTimeOffset.Parse("2026-08-06T10:17:00Z"), CancellationToken.None));

        Assert.Contains("oversized", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(stream.BytesRead, 1, 52_428_801);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
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
    public void PublicRssBestEffortStatusBootstrapsFirdsUniverseWithoutSigningKeys()
    {
        using var temp = new TemporaryDirectory();
        var statePath = Path.Combine(temp.Path, "status.json");
        var machine = NasdaqStatusMachine.LoadPublicRssBestEffort(statePath);
        machine.ApplyRss(Rss("existing-suspension", "Thu, 06 Aug 2026 08:00:00 GMT", "suspension"));

        machine.InitializeBestEffortUniverse(
            ["SE0000108656", "SE0000112233"],
            DateTimeOffset.Parse("2026-08-06T09:00:00Z"));

        Assert.False(machine.IsEligible("SE0000108656"));
        Assert.True(machine.IsEligible("SE0000112233"));
        Assert.Equal("public-rss-best-effort", machine.SignerKeyId);
        Assert.Equal(new string('0', 64), machine.SignerKeySha256);

        var restarted = NasdaqStatusMachine.LoadPublicRssBestEffort(statePath);
        Assert.Equal(InstrumentTradingState.Suspended, restarted.StateOf("SE0000108656"));
        Assert.Equal(InstrumentTradingState.Clear, restarted.StateOf("SE0000112233"));
        Assert.True(restarted.IsFreshAt(DateTimeOffset.Parse("2026-08-06T09:05:00Z"), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void StatusHistoryProjectsSuspensionAndResumptionAtEachTradeTimestamp()
    {
        using var temp = new TemporaryDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = "{\"asOf\":\"2026-08-06T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var machine = new PinnedStatusSeedVerifier("ops", key.ExportSubjectPublicKeyInfo()).Load(payload,
            key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        machine.ApplyRss(Rss("suspend", "Thu, 06 Aug 2026 10:00:00 GMT", "suspension"));
        machine.ApplyRss(Rss("resume", "Thu, 06 Aug 2026 11:00:00 GMT", "resumption"));

        Assert.Equal(InstrumentTradingState.Clear,
            machine.StateAt("SE0000108656", DateTimeOffset.Parse("2026-08-06T09:59:59Z")));
        Assert.Equal(InstrumentTradingState.Suspended,
            machine.StateAt("SE0000108656", DateTimeOffset.Parse("2026-08-06T10:30:00Z")));
        Assert.Equal(InstrumentTradingState.Clear,
            machine.StateAt("SE0000108656", DateTimeOffset.Parse("2026-08-06T11:00:00Z")));
        Assert.Equal(InstrumentTradingState.Unknown,
            machine.StateAt("SE0000108656", DateTimeOffset.Parse("2026-08-06T06:59:59Z")));
    }

    [Fact]
    public void DurableFirdsStateUsesIsinAndOrderBookAndRejectsCursorReplay()
    {
        using var temp = new TemporaryDirectory();
        var store = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json"));
        var fullBytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        store.ApplyFull(new MemoryStream(fullBytes), new DateOnly(2026, 8, 6), new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of01.zip"), Convert.ToHexStringLower(SHA256.HashData(fullBytes)), "full-20260806", 10);
        var snapshot = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json")).LoadVerified();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(("SE0000108656", "ERIC-B"), (snapshot.Instruments[0].Isin, snapshot.Instruments[0].OrderBookId));
        Assert.Equal(10, snapshot.Cursor);
        var delta = File.ReadAllBytes(Fixture("firds-delta.xml"));
        store.ApplyDelta(new MemoryStream(delta), new DateOnly(2026, 8, 6), new Uri("https://firds.esma.europa.eu/firds/DLTINS_E_20260806_01of01.zip"), Convert.ToHexStringLower(SHA256.HashData(delta)), "delta-20260806-11", 11);
        Assert.Empty(store.LoadVerified().Instruments);
        Assert.Throws<MarketDataException>(() => store.ApplyDelta(new MemoryStream(delta), new DateOnly(2026, 8, 6), new Uri("https://firds.esma.europa.eu/firds/DLTINS_E_20260806_01of01.zip"), Convert.ToHexStringLower(SHA256.HashData(delta)), "replay", 11));
    }

    [Fact]
    public void FirdsFullIsMonotonicAndRetainsImmutableVersionHistory()
    {
        using var temp = new TemporaryDirectory();
        var store = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json"));
        var bytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var source = new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of01.zip");
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6), source, hash, "full-10", 10);
        Assert.Throws<MarketDataException>(() => store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6), source, hash, "full-9", 9));
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6), source, hash, "full-20", 20);
        var state = store.LoadVerified();
        Assert.Equal(new long[] { 10, 20 }, state.Versions.Select(x => x.Cursor));
        Assert.All(state.Versions, version => Assert.True(File.Exists(version.RawPath)));
    }

    [Fact]
    public void DurableFirdsStateLoadsChecksumValidLegacyVersionsWithoutKind()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "firds.json");
        var store = new DurableFirdsStore(path);
        var bytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of01.zip"),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "full-10", 10);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var envelope = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var state = envelope["state"]!.AsObject();
        foreach (var version in state["versions"]!.AsArray()) version!.AsObject().Remove("kind");
        envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(state, options)));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope, options));

        var loaded = new DurableFirdsStore(path).LoadVerified();
        Assert.Equal(10, loaded.Cursor);
        Assert.Null(loaded.Versions[0].Kind);
    }

    [Fact]
    public void LegacyFirdsProjectionRejectsContradictoryFullFilenameFamily()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "firds.json");
        var store = new DurableFirdsStore(path);
        var bytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6),
            new Uri("https://firds.esma.europa.eu/firds/DLTINS_E_20260806_01of01.zip"),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), "full-10", 10);
        RewriteAsLegacyFirdsState(path);

        var destination = new DurableFirdsStore(Path.Combine(temp.Path, "nordic.json"),
            FirdsUniverse.NordicExhibition);
        Assert.Throws<MarketDataException>(() => store.ProjectVerifiedTo(destination));
    }

    [Fact]
    public void LegacyFirdsProjectionRejectsMismatchedMultipartIdentity()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "firds.json");
        var store = new DurableFirdsStore(path);
        var bytes = File.ReadAllBytes(Fixture("firds-full.xml"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 6),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of02.zip"), hash, "full-1", 1);
        store.ApplyFullPart(new MemoryStream(bytes), new DateOnly(2026, 8, 6),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_02of03.zip"), hash, "full-2", 2);
        RewriteAsLegacyFirdsState(path);

        var destination = new DurableFirdsStore(Path.Combine(temp.Path, "nordic.json"),
            FirdsUniverse.NordicExhibition);
        Assert.Throws<MarketDataException>(() => store.ProjectVerifiedTo(destination));
    }

    [Fact]
    public void OfficialFirdsZipIsExtractedAndMultipartFullRetainsEveryPartProvenance()
    {
        using var temp = new TemporaryDirectory();
        var store = new DurableFirdsStore(Path.Combine(temp.Path, "firds.json"));
        var zip = ZipXml(File.ReadAllBytes(Fixture("firds-full.xml")));
        var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
        store.ApplyFull(new MemoryStream(zip), new DateOnly(2026, 8, 6),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of02.zip"), hash, "full-1", 1);
        store.ApplyFullPart(new MemoryStream(zip), new DateOnly(2026, 8, 6),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260806_02of02.zip"), hash, "full-2", 2);

        var snapshot = store.LoadVerified();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(2, snapshot.Versions.Count);
        Assert.Throws<MarketDataException>(() => store.ApplyFull(new MemoryStream(zip), new DateOnly(2026, 8, 6),
            new Uri("https://registers.esma.europa.eu/firds/FULINS_E_20260806_01of01.zip"), hash, "fake", 3));
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
    public async Task ActiveSessionDownloadsAreBoundedAndVerifiedRecentArchivesAreSkipped()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var session = StockholmCalendar.GetSession(new DateOnly(2026, 8, 6))!;
        var expected = SessionManifest.ExpectedReports(session);
        var body = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var now = session.Close.AddMinutes(14);
        foreach (var report in expected.Take(8)) archive.Archive(report, body, Source(report), now.AddMinutes(-1));
        var downloads = 0;
        var listing = string.Join(',', expected.Select(x => $"\"{x}\""));
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("trade-reports", StringComparison.Ordinal))
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent($"{{\"message\":null,\"reports\":[{listing}]}}") };
            downloads++;
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }))
        { BaseAddress = new Uri("https://tradereports.nasdaq.com") };

        var result = await new NasdaqCollector(new NasdaqPostTradeClient(http, archive), archive,
                new SessionManifestStore(temp.Path), new CollectorDownloadPolicy(16, TimeSpan.FromHours(6)))
            .CollectOnceAsync(now, CancellationToken.None);

        Assert.Equal(16, downloads);
        Assert.Equal(16, result.Downloaded.Count);
        Assert.DoesNotContain(expected[0], result.Downloaded);
    }

    [Fact]
    public async Task ArchivedReportIsRefetchedOnlyOnControlledConflictCheckSchedule()
    {
        using var temp = new TemporaryDirectory();
        var archive = new ImmutableArchive(temp.Path);
        var report = Report;
        var body = File.ReadAllBytes(Fixture("nasdaq-posttrade.csv"));
        var fetched = DateTimeOffset.Parse("2026-08-06T10:16:00Z");
        archive.Archive(report, body, Source(report), fetched);
        var requests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("trade-reports", StringComparison.Ordinal))
                return new(System.Net.HttpStatusCode.OK) { Content = new StringContent($"{{\"message\":null,\"reports\":[\"{report}\"]}}") };
            requests++;
            return new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }))
        { BaseAddress = new Uri("https://tradereports.nasdaq.com") };
        var collector = new NasdaqCollector(new NasdaqPostTradeClient(http, archive), archive,
            new SessionManifestStore(temp.Path), new CollectorDownloadPolicy(4, TimeSpan.FromHours(6)));

        await collector.CollectOnceAsync(fetched.AddHours(5), CancellationToken.None);
        Assert.Equal(0, requests);
        await collector.CollectOnceAsync(fetched.AddHours(7), CancellationToken.None);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CleanStartAcquiresChecksummedFirdsPlanAndArchivesNasdaqRssProvenance()
    {
        using var temp = new TemporaryDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var seed = "{\"asOf\":\"2026-08-06T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var statuses = new PinnedStatusSeedVerifier("ops", key.ExportSubjectPublicKeyInfo()).Load(seed,
            key.SignData(Encoding.UTF8.GetBytes(seed), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status-state.json"));
        var full = File.ReadAllBytes(Fixture("firds-full.xml"));
        var fullHash = Convert.ToHexStringLower(SHA256.HashData(full));
        var planPath = Path.Combine(temp.Path, "firds-plan.json");
        await File.WriteAllTextAsync(planPath, $$"""
            {"artifacts":[{"kind":"full","sourceUrl":"https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of01.zip","sha256":"{{fullHash}}","version":"full-20260806","cursor":1,"effectiveAt":"2026-08-06"}]}
            """);
        var rss = File.ReadAllBytes(Fixture("nasdaq-status-rss.xml"));
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.Host == "firds.esma.europa.eu"
            ? new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(full) }
            : new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(rss) }));
        var firds = new DurableFirdsStore(Path.Combine(temp.Path, "firds-state.json"));
        var nordicFirds = new DurableFirdsStore(Path.Combine(temp.Path, "firds-nordic-state.json"),
            FirdsUniverse.NordicExhibition);
        var acquisition = new MarketReferenceAcquirer(http, firds, statuses, planPath,
            Path.Combine(temp.Path, "status-rss"), nordicFirds);

        await acquisition.AcquireAsync(DateTimeOffset.Parse("2026-08-06T09:00:00Z"), CancellationToken.None);
        await acquisition.AcquireAsync(DateTimeOffset.Parse("2026-08-06T10:00:00Z"), CancellationToken.None);

        Assert.Single(firds.LoadVerified().Instruments);
        Assert.Single(nordicFirds.LoadVerified().Instruments);
        Assert.Equal(InstrumentTradingState.Suspended, statuses.StateOf("SE0000108656"));
        Assert.Single(statuses.RssArtifacts);
        var artifact = statuses.RssArtifacts[0];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(rss)), artifact.Sha256);
        Assert.True(File.Exists(artifact.RawPath));
    }

    [Fact]
    public async Task CleanStartBestEffortMarksEligibleFirdsSharesClearWithoutSeedFiles()
    {
        using var temp = new TemporaryDirectory();
        var statuses = NasdaqStatusMachine.LoadPublicRssBestEffort(Path.Combine(temp.Path, "status-state.json"));
        var full = File.ReadAllBytes(Fixture("firds-full.xml"));
        var fullHash = Convert.ToHexStringLower(SHA256.HashData(full));
        var planPath = Path.Combine(temp.Path, "firds-plan.json");
        await File.WriteAllTextAsync(planPath, $$"""
            {"artifacts":[{"kind":"full","sourceUrl":"https://firds.esma.europa.eu/firds/FULINS_E_20260806_01of01.zip","sha256":"{{fullHash}}","version":"full-20260806","cursor":1,"effectiveAt":"2026-08-06"}]}
            """);
        var rss = Encoding.UTF8.GetBytes("""
            <rss><channel><link>https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices</link>
              <item><guid>unrelated</guid><title>Weekly Exercise</title><description>Derivatives</description><pubDate>Thu, 06 Aug 2026 08:00:00 GMT</pubDate><link>https://view.news.eu.nasdaq.com/view?id=unrelated</link></item>
            </channel></rss>
            """);
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.Host == "firds.esma.europa.eu"
            ? new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(full) }
            : new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(rss) }));
        var firds = new DurableFirdsStore(Path.Combine(temp.Path, "firds-state.json"));

        await new MarketReferenceAcquirer(http, firds, statuses, planPath, Path.Combine(temp.Path, "status-rss"))
            .AcquireAsync(DateTimeOffset.Parse("2026-08-06T09:00:00Z"), CancellationToken.None);

        Assert.True(statuses.IsEligible("SE0000108656"));
        Assert.Equal(DateTimeOffset.MinValue, statuses.SeedAsOf);
    }

    [Fact]
    public void AcquiredRssSnapshotIgnoresKnownEntriesButAppliesNewerNotice()
    {
        using var temp = new TemporaryDirectory();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var seed = "{\"asOf\":\"2026-08-06T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var statuses = new PinnedStatusSeedVerifier("ops", key.ExportSubjectPublicKeyInfo()).Load(seed,
            key.SignData(Encoding.UTF8.GetBytes(seed), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        statuses.ApplyRss(Rss("old", "Thu, 06 Aug 2026 08:00:00 GMT", "suspension"));
        var cumulative = Encoding.UTF8.GetBytes("""
            <rss><channel><link>https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices</link>
              <item><guid>unrelated</guid><title>Weekly Exercise - Swedish Stock</title><description>Derivatives - Expiration</description><pubDate>Thu, 06 Aug 2026 09:00:00 GMT</pubDate><link>https://view.news.eu.nasdaq.com/view?id=unrelated</link></item>
              <item><guid>old</guid><title>SE0000108656 suspension</title><description>suspension</description><pubDate>Thu, 06 Aug 2026 08:00:00 GMT</pubDate><link>https://api.news.eu.nasdaq.com/news/old</link></item>
              <item><guid>new</guid><title>SE0000108656 resumption</title><description>resumption</description><pubDate>Thu, 06 Aug 2026 10:00:00 GMT</pubDate><link>https://view.news.eu.nasdaq.com/view?id=new</link></item>
            </channel></rss>
            """);
        var hash = Convert.ToHexStringLower(SHA256.HashData(cumulative));
        var path = Path.Combine(temp.Path, hash + ".xml");
        File.WriteAllBytes(path, cumulative);

        statuses.ApplyRssSnapshot(new MemoryStream(cumulative), new Uri("https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices"),
            DateTimeOffset.Parse("2026-08-06T09:01:00Z"), hash, path);

        Assert.Equal(InstrumentTradingState.Clear, statuses.StateOf("SE0000108656"));
        Assert.Equal(2, statuses.Events.Count);
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
        var collector = new NasdaqCollector(new NasdaqPostTradeClient(http, archive), archive, manifests);
        CollectionResult result;
        do
        {
            result = await collector.CollectOnceAsync(session.Close.AddHours(24), CancellationToken.None);
            Assert.InRange(result.Downloaded.Count, 0, CollectorDownloadPolicy.Default.MaximumDownloadsPerPoll);
        } while (result.FinalizedManifest is null);
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
    public void IsinOnlyTradeFailsClosedWhenFirdsHasMultipleOrderBooks()
    {
        var trade = NasdaqCsvParser.Parse(File.ReadAllBytes(Fixture("nasdaq-posttrade.csv")),
            DateTimeOffset.Parse("2026-08-06T16:00:00Z"))[0];
        FirdsInstrument[] instruments =
        [
            new(trade.Isin, "ERIC-A", "5493001KJTIIGC8Y1R12", "Ericsson A", "ESVUFR", "SEK", "XSTO", null, null),
            new(trade.Isin, "ERIC-B", "5493001KJTIIGC8Y1R12", "Ericsson B", "ESVUFR", "SEK", "XSTO", null, null)
        ];

        Assert.Throws<MarketDataException>(() => TradeInstrumentMapper.Resolve(trade, instruments));
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

    [Fact]
    public async Task EcbFxAcquirerPinsOfficialSourceAndArchivesBoundedResponse()
    {
        using var temp = new TemporaryDirectory();
        const string xml = """
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube><Cube time="2026-08-21"><Cube currency="DKK" rate="7.4758"/><Cube currency="SEK" rate="11.0625"/><Cube currency="NOK" rate="10.8675"/><Cube currency="ISK" rate="141.60"/></Cube></Cube></gesmes:Envelope>
            """;
        Uri? requested = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return new(System.Net.HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml)) };
        }));
        var store = new EcbFxStore(temp.Path);

        var snapshot = await new EcbFxAcquirer(http, store).AcquireAsync(
            DateTimeOffset.Parse("2026-08-21T14:10:00Z"), CancellationToken.None);

        Assert.Equal(EcbFxStore.OfficialSource, requested);
        Assert.Equal(7.4758m, snapshot.DkkPerUnit["EUR"]);
        Assert.True(store.Exists);
    }

    private static IReadOnlyList<DateOnly> ExpectedSessionsEnding(DateOnly ending, int count)
    {
        var result = new List<DateOnly>();
        for (var day = ending; result.Count < count; day = day.AddDays(-1)) if (StockholmCalendar.GetSession(day) is not null) result.Add(day);
        result.Reverse(); return result;
    }

    private static void RewriteAsLegacyFirdsState(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var envelope = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        var state = envelope["state"]!.AsObject();
        foreach (var version in state["versions"]!.AsArray()) version!.AsObject().Remove("kind");
        envelope["sha256"] = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(state, options)));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(envelope, options));
    }

    private static Stream Rss(string id, string published, string state) => new MemoryStream(Encoding.UTF8.GetBytes($"<rss><channel><item><guid>{id}</guid><title>SE0000108656 {state}</title><description>{state}</description><pubDate>{published}</pubDate><link>https://api.news.eu.nasdaq.com/news/{id}</link></item></channel></rss>"));
    private static Uri Source(string report) => new($"https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={report}");
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static byte[] ZipXml(byte[] xml)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = archive.CreateEntry("payload.xml").Open()) entry.Write(xml);
        return output.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class OversizedReportStream(long length) : Stream
    {
        private static readonly byte[] Prefix = "\"sep=;\""u8.ToArray();
        private long position;
        public long BytesRead => position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length) return 0;
            var read = (int)Math.Min(count, length - position);
            for (var index = 0; index < read; index++)
            {
                var absolute = position + index;
                buffer[offset + index] = absolute < Prefix.Length ? Prefix[absolute] : (byte)'x';
            }
            position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.ToArray(), 0, buffer.Length));
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aistocks-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
