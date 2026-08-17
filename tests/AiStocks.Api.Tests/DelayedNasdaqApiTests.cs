using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiStocks.MarketData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiStocks.Api.Tests;

public sealed class DelayedNasdaqApiTests
{
    [Fact]
    public async Task Exhibition_instruments_are_latest_available_verified_xsto_observations()
    {
        using var data = DelayedNasdaqFixture.Create();
        await using var factory = new DelayedNasdaqApiFactory(data.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");

        var response = await client.GetFromJsonAsync<JsonElement>("/api/v1/instruments?query=eric");

        Assert.Equal("official-nasdaq-xsto-15m-delayed", response.GetProperty("dataMode").GetString());
        var item = Assert.Single(response.GetProperty("items").EnumerateArray());
        Assert.Equal("SE0000108656", item.GetProperty("id").GetString());
        Assert.Equal("ERIC-B", item.GetProperty("symbol").GetString());
        Assert.Equal("Ericsson B", item.GetProperty("name").GetString());
        Assert.Equal("XSTO", item.GetProperty("exchange").GetString());
        Assert.Equal("Sweden", item.GetProperty("country").GetString());
        Assert.Equal("SEK", item.GetProperty("currency").GetString());
        Assert.Equal(91.25m, item.GetProperty("price").GetDecimal());
        Assert.False(item.TryGetProperty("priceDkk", out _));
        Assert.False(item.GetProperty("isPreviewPrice").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T10:01:00Z"), item.GetProperty("executedAt").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T10:16:00Z"), item.GetProperty("availableAt").GetDateTimeOffset());
        Assert.Equal("Nasdaq Nordic MiFID II delayed post-trade", item.GetProperty("source").GetString());
        Assert.Equal(15, item.GetProperty("delayMinutes").GetInt32());
        Assert.False(item.GetProperty("tradable").GetBoolean());
    }

    [Fact]
    public void Query_is_applied_after_the_bounded_newest_report_window_is_aggregated()
    {
        using var data = DelayedNasdaqFixture.Create();
        data.AddDenseNewerReport();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:17:01Z"));

        var response = new DelayedNasdaqInstrumentStore(data.Path, clock).Search("eric");

        var item = Assert.Single(response.Items);
        Assert.Equal("ERIC-B", item.Symbol);
    }

    [Fact]
    public void Archive_scan_verifies_only_the_fixed_newest_report_window()
    {
        using var data = DelayedNasdaqFixture.Create();
        data.AddAdditionalReports(40);
        var verified = 0;
        var store = new DelayedNasdaqInstrumentStore(
            data.Path,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")),
            _ => verified++);

        var response = store.Search(null);

        Assert.NotEmpty(response.Items);
        Assert.Equal(32, verified);
    }

    [Fact]
    public async Task Exhibition_progress_declares_hold_only_and_trade_submission_cannot_mutate_portfolio()
    {
        using var data = DelayedNasdaqFixture.Create();
        await using var factory = new DelayedNasdaqApiFactory(data.Path);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-AI-Exhibition-Key", AiExhibitionApiFactory.Secret);
        var agentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var completedAt = DateTimeOffset.Parse("2026-08-16T10:20:00Z");
        using var queued = await client.PostAsJsonAsync("/internal/preview/ai-status",
            new { runId = "hold-only-run-001", agentId, modelId = "gpt-5.6-sol", status = "queued", error = (string?)null, occurredAt = completedAt.AddSeconds(-2) });
        using var running = await client.PostAsJsonAsync("/internal/preview/ai-status",
            new { runId = "hold-only-run-001", agentId, modelId = "gpt-5.6-sol", status = "running", error = (string?)null, occurredAt = completedAt.AddSeconds(-1) });
        Assert.True(queued.IsSuccessStatusCode);
        Assert.True(running.IsSuccessStatusCode);

        using var rejected = await client.PostAsJsonAsync("/internal/preview/ai-decisions", new
        {
            runId = "hold-only-run-001",
            agentId,
            modelId = "gpt-5.6-sol",
            action = "buy",
            instrumentId = "aapl-us",
            quantity = 1,
            reason = "Must be rejected without mutation.",
            confidence = 0.5m,
            evidence = new[] { new { url = "https://example.com/research", publishedAt = completedAt.AddMinutes(-1), exactExcerpt = "Evidence", contentSha256 = new string('a', 64) } },
            runtimeProvider = "copilot",
            runtimeModel = "gpt-5.6-sol",
            reportSha256 = new string('b', 64),
            completedAt
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hold-only", problem.GetProperty("code").GetString());
        client.DefaultRequestHeaders.Remove("X-AI-Exhibition-Key");
        client.DefaultRequestHeaders.Add("X-Test-User-Email", "viewer@example.com");
        var progress = await client.GetFromJsonAsync<JsonElement>("/api/v1/ai-progress");
        Assert.Equal(DelayedNasdaqInstrumentStore.DataMode, progress.GetProperty("dataMode").GetString());
        Assert.True(progress.GetProperty("holdOnly").GetBoolean());
        var participant = progress.GetProperty("participants").EnumerateArray()
            .Single(item => item.GetProperty("agentId").GetGuid() == agentId);
        var portfolio = participant.GetProperty("portfolio");
        Assert.Equal(DelayedNasdaqInstrumentStore.DataMode, portfolio.GetProperty("dataMode").GetString());
        Assert.Equal(100_000m, portfolio.GetProperty("cashDkk").GetDecimal());
        Assert.Empty(portfolio.GetProperty("holdings").EnumerateArray());

        var leaderboard = await client.GetFromJsonAsync<JsonElement>("/api/v1/leaderboard");
        Assert.Equal(DelayedNasdaqInstrumentStore.DataMode, leaderboard.GetProperty("dataMode").GetString());
    }

    [Fact]
    public void Observation_is_hidden_until_its_verified_available_at()
    {
        using var data = DelayedNasdaqFixture.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T10:15:59Z"));

        var response = new DelayedNasdaqInstrumentStore(data.Path, clock).Search(null);

        Assert.Empty(response.Items);
    }

    [Fact]
    public void Tampered_archive_fails_closed_instead_of_returning_instruments()
    {
        using var data = DelayedNasdaqFixture.Create();
        var report = Directory.EnumerateDirectories(data.Path, "NordicEquity-posttrade-*").Single();
        File.AppendAllText(Path.Combine(report, Path.GetFileName(report) + ".csv"), "tampered");

        Assert.Throws<MarketDataException>(() =>
            new DelayedNasdaqInstrumentStore(data.Path, TimeProvider.System).Search(null));
    }

    [Fact]
    public void Archive_replacement_after_initial_verification_fails_closed()
    {
        using var data = DelayedNasdaqFixture.Create();
        var replaced = false;
        var store = new DelayedNasdaqInstrumentStore(data.Path, TimeProvider.System, report =>
        {
            if (replaced)
            {
                return;
            }

            File.AppendAllText(report.CsvPath, "replaced-after-verification");
            replaced = true;
        });

        var exception = Assert.Throws<MarketDataException>(() => store.Search(null));

        Assert.True(replaced);
        Assert.Contains("changed after checksum verification", exception.Message, StringComparison.Ordinal);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}

internal sealed class DelayedNasdaqApiFactory(string archivePath) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("PREVIEW_MODE", "1");
        builder.UseSetting("AI_EXHIBITION_MODE", "1");
        builder.UseSetting("AI_EXHIBITION_KEY", AiExhibitionApiFactory.Secret);
        builder.UseSetting("AI_EXHIBITION_ARCHIVE_PATH", archivePath);
    }
}

internal sealed class DelayedNasdaqFixture : IDisposable
{
    private DelayedNasdaqFixture(string path) => Path = path;
    public string Path { get; }

    public static DelayedNasdaqFixture Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aistocks-delayed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        WriteFirds(path, extraInstrumentCount: 0);

        const string report = "NordicEquity-posttrade-2026-08-16T1016";
        var csv = Encoding.UTF8.GetBytes("\"sep=;\"\nTrading date and time;Instrument identification code;Price;Price currency;Price notation;Quantity;Venue of execution;Publication date and time;Transaction identification code;Flags\n2026-08-16T10:01:00Z;SE0000108656;91.25;SEK;MONE;100;XSTO;2026-08-16T10:01:01Z;tx-latest;\n2026-08-16T10:02:00Z;SE0000108656;999;USD;MONE;100;XSTO;2026-08-16T10:02:01Z;wrong-currency;\n2026-08-16T10:03:00Z;SE0000108656;998;SEK;MONE;100;XNAS;2026-08-16T10:03:01Z;wrong-venue;\n2026-08-16T10:04:00Z;SE0000108656;997;SEK;PERC;100;XSTO;2026-08-16T10:04:01Z;wrong-notation;\n");
        new ImmutableArchive(path).Archive(report, csv,
            new Uri($"https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={report}"),
            DateTimeOffset.Parse("2026-08-16T10:16:00Z"));
        return new(path);
    }

    public void AddAdditionalReports(int count)
    {
        var csv = Encoding.UTF8.GetBytes("\"sep=;\"\nTrading date and time;Instrument identification code;Price;Price currency;Price notation;Quantity;Venue of execution;Publication date and time;Transaction identification code;Flags\n2026-08-16T10:01:00Z;SE0000108656;91.25;SEK;MONE;100;XSTO;2026-08-16T10:01:01Z;bounded;\n");
        var first = DateTimeOffset.Parse("2026-08-16T10:17:00Z");
        var archive = new ImmutableArchive(Path);
        for (var index = 0; index < count; index++)
        {
            var fetchedAt = first.AddMinutes(index);
            var report = $"NordicEquity-posttrade-{fetchedAt:yyyy-MM-ddTHHmm}";
            archive.Archive(report, csv,
                new Uri($"https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={report}"),
                fetchedAt);
        }
    }

    public void AddDenseNewerReport()
    {
        const int count = 20;
        WriteFirds(Path, count);
        var rows = new StringBuilder("\"sep=;\"\nTrading date and time;Instrument identification code;Price;Price currency;Price notation;Quantity;Venue of execution;Publication date and time;Transaction identification code;Flags\n");
        for (var index = 1; index <= count; index++)
        {
            rows.Append("2026-08-16T10:02:00Z;")
                .Append(Isin(index)).Append(';').Append(100 + index)
                .Append(";SEK;MONE;100;XSTO;2026-08-16T10:02:01Z;dense-")
                .Append(index).Append(";\n");
        }

        const string report = "NordicEquity-posttrade-2026-08-16T1017";
        new ImmutableArchive(Path).Archive(report, Encoding.UTF8.GetBytes(rows.ToString()),
            new Uri($"https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={report}"),
            DateTimeOffset.Parse("2026-08-16T10:17:00Z"));
    }

    private static void WriteFirds(string path, int extraInstrumentCount)
    {
        var xml = new StringBuilder("<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:auth.017.001.02\">");
        AppendFirds(xml, "SE0000108656", "Ericsson B", "ERIC-B");
        for (var index = 1; index <= extraInstrumentCount; index++)
        {
            AppendFirds(xml, Isin(index), $"Dense {index}", $"DENSE-{index:00}");
        }

        xml.Append("</Document>");
        var xmlBytes = Encoding.UTF8.GetBytes(xml.ToString());
        new DurableFirdsStore(System.IO.Path.Combine(path, "firds-state.json")).ApplyFull(
            new MemoryStream(xmlBytes), DateOnly.Parse("2026-08-16"),
            new Uri("https://firds.esma.europa.eu/firds/reference.zip"),
            Convert.ToHexStringLower(SHA256.HashData(xmlBytes)),
            extraInstrumentCount == 0 ? "full-1" : "full-2",
            extraInstrumentCount == 0 ? 1 : 2);
    }

    private static void AppendFirds(StringBuilder xml, string isin, string name, string orderBookId)
    {
        xml.Append("<RefData><FinInstrmGnlAttrbts><Id>").Append(isin)
            .Append("</Id><FullNm>").Append(name)
            .Append("</FullNm><ClssfctnTp>ESVUFR</ClssfctnTp><NtnlCcy>SEK</NtnlCcy></FinInstrmGnlAttrbts>")
            .Append("<Issr>5493001KJTIIGC8Y1R12</Issr><TradgVnRltdAttrbts><Id>XSTO</Id><TradgVnInstrmId>")
            .Append(orderBookId)
            .Append("</TradgVnInstrmId><FrstTradDt>2000-01-01T00:00:00Z</FrstTradDt></TradgVnRltdAttrbts></RefData>");
    }

    private static string Isin(int index) => $"SE{index:0000000000}";

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
