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
        var trade = NasdaqTradeSelection.FirstEligible(rows, "SE0000108656", DateTimeOffset.Parse("2026-08-06T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-06T16:00:00Z"), session, statuses);
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
    public void CsvAcceptsOfficialNearEqualPublicationTimestampAfterFeedDelay()
    {
        const string csv = """
            "sep=;"
            Trading date and time;Instrument identification code;Price;Missing Price;Price currency;Price notation;Quantity;Venue of execution;Trading system;Publication date and time;Venue of publication;Transaction identification code;Flags
            2026-08-07T07:15:00.017Z;SE0000108656;96.90;;SEK;MONE;10;XSTO;CLOB;2026-08-07T07:15:00.016Z;XSTO;official-row;---
            """;

        var rows = NasdaqCsvParser.Parse(Encoding.UTF8.GetBytes(csv), DateTimeOffset.Parse("2026-08-07T07:30:00.017Z"));

        Assert.Single(rows);
        var early = Assert.Single(NasdaqCsvParser.Parse(
            Encoding.UTF8.GetBytes(csv), DateTimeOffset.Parse("2026-08-07T07:29:59Z")));
        Assert.Equal(DateTimeOffset.Parse("2026-08-07T07:30:00.017Z"), early.AvailableAt);
    }

    [Fact]
    public void CsvAcceptsCapturedReportRetrievedJustBeforePerTradeDelay()
    {
        const string csv = """
            "sep=;"
            Trading date and time;Instrument identification code;Price;Missing Price;Price currency;Price notation;Quantity;Venue of execution;Trading system;Publication date and time;Venue of publication;Transaction identification code;Flags
            2026-08-10T07:17:59.991Z;SE0000108656;96.90;;SEK;MONE;10;XSTO;CLOB;2026-08-10T07:17:59.991Z;XSTO;000113444;---
            """;
        var fetchedAt = DateTimeOffset.Parse("2026-08-10T07:32:13.229699Z");

        var trade = Assert.Single(NasdaqCsvParser.Parse(Encoding.UTF8.GetBytes(csv), fetchedAt));

        Assert.Equal(fetchedAt, trade.FetchedAt);
    }

    [Fact]
    public void TradeSelectionWaitsUntilTheFullDelayedAvailabilityTime()
    {
        const string csv = """
            "sep=;"
            Trading date and time;Instrument identification code;Price;Missing Price;Price currency;Price notation;Quantity;Venue of execution;Trading system;Publication date and time;Venue of publication;Transaction identification code;Flags
            2026-08-10T07:17:59.991Z;SE0000108656;96.90;;SEK;MONE;10;XSTO;CLOB;2026-08-10T07:17:59.991Z;XSTO;000113444;---
            """;
        var rows = NasdaqCsvParser.Parse(Encoding.UTF8.GetBytes(csv), DateTimeOffset.Parse("2026-08-10T07:32:13.229699Z"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new TemporaryDirectory();
        var payload = "{\"asOf\":\"2026-08-10T07:00:00Z\",\"states\":{\"SE0000108656\":\"Clear\"}}";
        var statuses = new PinnedStatusSeedVerifier("test-key", key.ExportSubjectPublicKeyInfo()).Load(payload,
            key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256), Path.Combine(temp.Path, "status.json"));
        var decisionAt = DateTimeOffset.Parse("2026-08-10T07:17:00Z");
        var availableAt = DateTimeOffset.Parse("2026-08-10T07:32:59.991Z");
        var session = StockholmCalendar.GetSession(new DateOnly(2026, 8, 10))!;

        Assert.Throws<MarketDataException>(() => NasdaqTradeSelection.FirstEligible(
            rows, "SE0000108656", decisionAt, availableAt.AddTicks(-1), session, statuses));
        Assert.Equal("000113444", NasdaqTradeSelection.FirstEligible(
            rows, "SE0000108656", decisionAt, availableAt, session, statuses).TransactionId);
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
    public void ArchiveReplayVerifiesEveryMetadataCsvPair()
    {
        using var temp = new TemporaryDirectory();
        var report = "NordicEquity-posttrade-2026-08-06T1016";
        var source = new Uri("https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName=" + report);
        new ImmutableArchive(temp.Path).Archive(report, File.ReadAllBytes(Fixture("nasdaq-posttrade.csv")), source,
            DateTimeOffset.Parse("2026-08-06T16:00:00Z"));

        var result = NasdaqArchiveReplay.Replay(temp.Path);

        Assert.Equal(1, result.Reports);
        Assert.Equal(4, result.Rows);
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
    public void FirdsFullAndDeltaAreEffectiveDatedAndChecksumBound()
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
    public void NordicExhibitionFirdsKeepsOnlyPrimaryVenueCurrencyCommonShares()
    {
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02">
              <RefData><FinInstrmGnlAttrbts><Id>SE0000108656</Id><FullNm>Ericsson B</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5493001KJTIIGC8Y1R12</Issr><TradgVnRltdAttrbts><Id>XSTO</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>DK0010181676</Id><FullNm>Carlsberg A A/S</FullNm><ClssfctnTp>ESEUFN</ClssfctnTp><NtnlCcy>DKK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5299001O0WJQYB5GYZ19</Issr><TradgVnRltdAttrbts><Id>XCSE</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>FI0009800395</Id><FullNm>Raisio Plc K</FullNm><ClssfctnTp>ESETFR</ClssfctnTp><NtnlCcy>EUR</NtnlCcy></FinInstrmGnlAttrbts><Issr>74370083282NHIP4QD02</Issr><TradgVnRltdAttrbts><Id>XHEL</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>NO0003399917</Id><FullNm>Odfjell SE B</FullNm><ClssfctnTp>ESNUFR</ClssfctnTp><NtnlCcy>NOK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5967007LIEEXZXJ8QG45</Issr><TradgVnRltdAttrbts><Id>ONSE</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>IS0000000040</Id><FullNm>Iceland Common</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>ISK</NtnlCcy></FinInstrmGnlAttrbts><Issr>529900T8BM49AURSDO55</Issr><TradgVnRltdAttrbts><Id>XICE</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>SE0000000001</Id><FullNm>Wrong currency</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>EUR</NtnlCcy></FinInstrmGnlAttrbts><Issr>529900T8BM49AURSDO56</Issr><TradgVnRltdAttrbts><Id>XSTO</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>SE0000000002</Id><FullNm>First North</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts><Issr>529900T8BM49AURSDO57</Issr><TradgVnRltdAttrbts><Id>FNSE</Id></TradgVnRltdAttrbts></RefData>
            </Document>
            """;
        var parser = new FirdsUniverseParser(FirdsUniverse.NordicExhibition);

        var instruments = parser.ParseFull(new MemoryStream(Encoding.UTF8.GetBytes(xml)), FullDay);

        Assert.Equal(["ONSE", "XCSE", "XHEL", "XICE", "XSTO"],
            instruments.Select(item => item.Venue).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void NordicExhibitionFirdsKeysCrossListingsByVenue()
    {
        const string full = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02">
              <RefData><FinInstrmGnlAttrbts><Id>SE0000000001</Id><FullNm>Dual Listed</FullNm><ClssfctnTp>ESEUFN</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts><Issr>54930000000000000001</Issr><TradgVnRltdAttrbts><Id>XSTO</Id><TradgVnInstrmId>DUAL</TradgVnInstrmId><FrstTradDt>2020-01-01T00:00:00Z</FrstTradDt></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>SE0000000001</Id><FullNm>Dual Listed</FullNm><ClssfctnTp>ESEUFN</ClssfctnTp><NtnlCcy>DKK</NtnlCcy></FinInstrmGnlAttrbts><Issr>54930000000000000001</Issr><TradgVnRltdAttrbts><Id>XCSE</Id><TradgVnInstrmId>DUAL</TradgVnInstrmId><FrstTradDt>2020-01-01T00:00:00Z</FrstTradDt></TradgVnRltdAttrbts></RefData>
            </Document>
            """;
        const string delta = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02">
              <TermntdRcrd><FinInstrmGnlAttrbts><Id>SE0000000001</Id><FullNm>Dual Listed</FullNm><ClssfctnTp>ESEUFN</ClssfctnTp><NtnlCcy>DKK</NtnlCcy></FinInstrmGnlAttrbts><Issr>54930000000000000001</Issr><TradgVnRltdAttrbts><Id>XCSE</Id><TradgVnInstrmId>DUAL</TradgVnInstrmId><FrstTradDt>2020-01-01T00:00:00Z</FrstTradDt></TradgVnRltdAttrbts></TermntdRcrd>
            </Document>
            """;
        var parser = new FirdsUniverseParser(FirdsUniverse.NordicExhibition);

        var instruments = parser.ParseFull(new MemoryStream(Encoding.UTF8.GetBytes(full)), new DateOnly(2026, 8, 21));
        Assert.Equal(2, instruments.Count);

        var afterDelete = parser.ApplyDelta(instruments,
            new MemoryStream(Encoding.UTF8.GetBytes(delta)), new DateOnly(2026, 8, 21));
        var remaining = Assert.Single(afterDelete);
        Assert.Equal("XSTO", remaining.Venue);
    }

    [Fact]
    public void EcbFxArchiveProvidesChecksumVerifiedDkkCrossRates()
    {
        using var temp = new TemporaryDirectory();
        var fetchedAt = DateTimeOffset.Parse("2026-08-21T14:10:00Z");
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <Cube><Cube time="2026-08-21">
                <Cube currency="DKK" rate="7.4758"/><Cube currency="SEK" rate="11.0625"/>
                <Cube currency="NOK" rate="10.8675"/><Cube currency="ISK" rate="141.60"/>
              </Cube></Cube>
            </gesmes:Envelope>
            """;
        var store = new EcbFxStore(temp.Path);

        store.Archive(Encoding.UTF8.GetBytes(xml), EcbFxStore.OfficialSource, fetchedAt);
        var snapshot = store.LoadVerified(fetchedAt.AddMinutes(1));

        Assert.Equal(new DateOnly(2026, 8, 21), snapshot.ReferenceDate);
        Assert.Equal(1m, snapshot.DkkPerUnit["DKK"]);
        Assert.Equal(7.4758m, snapshot.DkkPerUnit["EUR"]);
        Assert.Equal(7.4758m / 11.0625m, snapshot.DkkPerUnit["SEK"]);
        Assert.Equal(7.4758m / 10.8675m, snapshot.DkkPerUnit["NOK"]);
        Assert.Equal(7.4758m / 141.60m, snapshot.DkkPerUnit["ISK"]);
        Assert.Equal(fetchedAt, snapshot.AvailableAt);
        File.AppendAllText(snapshot.RawPath, "tamper");
        Assert.Throws<MarketDataException>(() => store.LoadVerified(fetchedAt.AddMinutes(2)));
    }

    [Fact]
    public void EcbFxArchiveRejectsReferenceDateAndAcquisitionTimeRegression()
    {
        using var temp = new TemporaryDirectory();
        const string current = """
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <Cube><Cube time="2026-08-21"><Cube currency="DKK" rate="7.4758"/><Cube currency="SEK" rate="11.0625"/><Cube currency="NOK" rate="10.8675"/><Cube currency="ISK" rate="141.60"/></Cube></Cube>
            </gesmes:Envelope>
            """;
        var store = new EcbFxStore(temp.Path);
        var firstFetchedAt = DateTimeOffset.Parse("2026-08-21T14:10:00Z");
        store.Archive(Encoding.UTF8.GetBytes(current), EcbFxStore.OfficialSource, firstFetchedAt);

        var older = current.Replace("2026-08-21", "2026-08-20", StringComparison.Ordinal);

        Assert.Throws<MarketDataException>(() => store.Archive(Encoding.UTF8.GetBytes(older),
            EcbFxStore.OfficialSource, firstFetchedAt.AddHours(1)));
        Assert.Throws<MarketDataException>(() => store.Archive(Encoding.UTF8.GetBytes(current),
            EcbFxStore.OfficialSource, firstFetchedAt));
        Assert.Equal(new DateOnly(2026, 8, 21), store.LoadVerified(firstFetchedAt.AddHours(2)).ReferenceDate);
    }

    [Fact]
    public void NordicCorporateActionStateIsFreshBoundedAndAppendOnly()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "actions");
        Directory.CreateDirectory(input);
        var actionPath = Path.Combine(input, "split.json");
        File.WriteAllText(actionPath, """
            {
              "schemaVersion":"1",
              "venue":"XCSE",
              "isin":"DK0010181676",
              "orderBookId":"CARL-A",
              "actionType":"SPLIT",
              "effectiveAt":"2026-08-22T00:00:00Z"
            }
            """);
        var store = new UnsupportedCorporateActionStore(temp.Path);
        var refreshedAt = DateTimeOffset.Parse("2026-08-21T14:10:00Z");

        var snapshot = store.RefreshFromDirectory(input, refreshedAt);

        Assert.Single(snapshot.Actions);
        Assert.True(store.IsBlocked("XCSE", "DK0010181676", "CARL-A", refreshedAt.AddMinutes(1)));
        Assert.Throws<MarketDataException>(() => store.LoadVerified(refreshedAt.AddMinutes(6)));
        File.Delete(actionPath);
        Assert.Throws<MarketDataException>(() =>
            store.RefreshFromDirectory(input, refreshedAt.AddMinutes(1)));
    }

    [Fact]
    public void NordicCorporateActionInputRejectsBytesBeyondTheReadBound()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "actions");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "oversized.json"), new byte[1_048_577]);

        var exception = Assert.Throws<MarketDataException>(() =>
            new UnsupportedCorporateActionStore(temp.Path).RefreshFromDirectory(
                input, DateTimeOffset.Parse("2026-08-21T14:10:00Z")));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NordicCorporateActionInputRejectsSymbolicLinks()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "actions");
        Directory.CreateDirectory(input);
        var target = Path.Combine(temp.Path, "target.json");
        File.WriteAllText(target, "{}");
        File.CreateSymbolicLink(Path.Combine(input, "linked.json"), target);

        Assert.Throws<MarketDataException>(() =>
            new UnsupportedCorporateActionStore(temp.Path).RefreshFromDirectory(
                input, DateTimeOffset.Parse("2026-08-21T14:10:00Z")));
    }

    [Fact]
    public void NordicCorporateActionStateRejectsSymbolicLinks()
    {
        using var temp = new TemporaryDirectory();
        var target = Path.Combine(temp.Path, "target-state.json");
        File.WriteAllText(target, "{}");
        File.CreateSymbolicLink(
            Path.Combine(temp.Path, "nordic-unsupported-corporate-actions.json"), target);

        Assert.Throws<MarketDataException>(() =>
            new UnsupportedCorporateActionStore(temp.Path).LoadVerified(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NordicCorporateActionRawArchiveRejectsSymbolicLinks()
    {
        using var temp = new TemporaryDirectory();
        var input = Path.Combine(temp.Path, "actions");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "action.json"), """
            {"schemaVersion":"1","venue":"XCSE","isin":"DK0010181676","orderBookId":"CARL-A","actionType":"SPLIT","effectiveAt":"2026-08-18T00:00:00Z"}
            """);
        var store = new UnsupportedCorporateActionStore(temp.Path);
        var now = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
        store.RefreshFromDirectory(input, now);
        var raw = Directory.EnumerateFiles(
            Path.Combine(temp.Path, "nordic-unsupported-corporate-actions.json.raw"), "*.json").Single();
        var target = Path.Combine(temp.Path, "target-raw.json");
        File.Copy(raw, target);
        File.Delete(raw);
        File.CreateSymbolicLink(raw, target);

        Assert.Throws<MarketDataException>(() => store.LoadVerified(now));
    }

    [Fact]
    public void MarketReferencePrimaryHandlerDoesNotFollowRedirects()
    {
        using var handler = Assert.IsType<HttpClientHandler>(MarketReferenceAcquirer.CreatePrimaryHandler());

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(System.Net.DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public void NordicProjectionReplaysVerifiedRawFirdsWithoutWeakeningStockholmState()
    {
        using var temp = new TemporaryDirectory();
        const string xml = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02">
              <RefData><FinInstrmGnlAttrbts><Id>SE0000108656</Id><FullNm>Ericsson B</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5493001KJTIIGC8Y1R12</Issr><TradgVnRltdAttrbts><Id>XSTO</Id></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>DK0010181676</Id><FullNm>Carlsberg A A/S</FullNm><ClssfctnTp>ESEUFN</ClssfctnTp><NtnlCcy>DKK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5299001O0WJQYB5GYZ19</Issr><TradgVnRltdAttrbts><Id>XCSE</Id></TradgVnRltdAttrbts></RefData>
            </Document>
            """;
        var bytes = Encoding.UTF8.GetBytes(xml);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var store = new DurableFirdsStore(Path.Combine(temp.Path, "firds-state.json"));
        store.ApplyFull(new MemoryStream(bytes), new DateOnly(2026, 8, 8),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260808_01of01.zip"),
            sha256, "full-2026-08-08-1", 1);

        var stockholm = store.LoadVerified();
        var nordicStore = new DurableFirdsStore(Path.Combine(temp.Path, "firds-nordic-state.json"),
            FirdsUniverse.NordicExhibition);
        var nordic = store.ProjectVerifiedTo(nordicStore);

        Assert.Single(stockholm.Instruments);
        Assert.True(nordicStore.Exists);
        Assert.Equal(2, nordic.Instruments.Count);
        Assert.Contains(nordic.Instruments, item => item.Venue == "XCSE" && item.Currency == "DKK");
    }

    [Fact]
    public void NordicProjectionReplacesRemovedListingsOnANewerFullSnapshot()
    {
        using var temp = new TemporaryDirectory();
        const string first = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02">
              <RefData><FinInstrmGnlAttrbts><Id>SE0000108656</Id><FullNm>Ericsson B</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5493001KJTIIGC8Y1R12</Issr><TradgVnRltdAttrbts><Id>XSTO</Id><TradgVnInstrmId>ERIC-B</TradgVnInstrmId></TradgVnRltdAttrbts></RefData>
              <RefData><FinInstrmGnlAttrbts><Id>DK0010181676</Id><FullNm>Carlsberg A A/S</FullNm><ClssfctnTp>ESEUFN</ClssfctnTp><NtnlCcy>DKK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5299001O0WJQYB5GYZ19</Issr><TradgVnRltdAttrbts><Id>XCSE</Id><TradgVnInstrmId>CARL-A</TradgVnInstrmId></TradgVnRltdAttrbts></RefData>
            </Document>
            """;
        const string second = """
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:auth.017.001.02">
              <RefData><FinInstrmGnlAttrbts><Id>SE0000108656</Id><FullNm>Ericsson B</FullNm><ClssfctnTp>ESEUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts><Issr>5493001KJTIIGC8Y1R12</Issr><TradgVnRltdAttrbts><Id>XSTO</Id><TradgVnInstrmId>ERIC-B</TradgVnInstrmId></TradgVnRltdAttrbts></RefData>
            </Document>
            """;
        var source = new DurableFirdsStore(Path.Combine(temp.Path, "strict.json"));
        var destination = new DurableFirdsStore(Path.Combine(temp.Path, "nordic.json"),
            FirdsUniverse.NordicExhibition);
        var firstBytes = Encoding.UTF8.GetBytes(first);
        source.ApplyFull(new MemoryStream(firstBytes), new DateOnly(2026, 8, 20),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260820_01of01.zip"),
            Convert.ToHexStringLower(SHA256.HashData(firstBytes)), "full-2026-08-20", 1);
        Assert.Equal(2, source.ProjectVerifiedTo(destination).Instruments.Count);

        var secondBytes = Encoding.UTF8.GetBytes(second);
        source.ApplyFull(new MemoryStream(secondBytes), new DateOnly(2026, 8, 21),
            new Uri("https://firds.esma.europa.eu/firds/FULINS_E_20260821_01of01.zip"),
            Convert.ToHexStringLower(SHA256.HashData(secondBytes)), "full-2026-08-21", 2);
        var projected = source.ProjectVerifiedTo(destination);

        Assert.Single(projected.Instruments);
        Assert.DoesNotContain(projected.Instruments, item => item.Isin == "DK0010181676");
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
