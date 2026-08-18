using System.Security.Cryptography;
using AiStocks.MarketData;

namespace AiStocks.Api;

public sealed class DelayedNasdaqInstrumentStore
{
    public const string DataMode = "official-nasdaq-xsto-15m-delayed";
    private const int MaximumVerifiedReports = 32;
    private static readonly TimeSpan MaximumObservationAge = TimeSpan.FromHours(72);
    private readonly string archivePath;
    private readonly TimeProvider clock;
    private readonly Action<ArchivedReport>? afterVerification;

    public DelayedNasdaqInstrumentStore(string archivePath, TimeProvider clock)
        : this(archivePath, clock, null)
    {
    }

    internal DelayedNasdaqInstrumentStore(
        string archivePath,
        TimeProvider clock,
        Action<ArchivedReport>? afterVerification)
    {
        this.archivePath = Path.GetFullPath(archivePath);
        this.clock = clock;
        this.afterVerification = afterVerification;
    }

    public InstrumentListDto Search(string? query)
    {
        var asOf = clock.GetUtcNow();
        var archive = new ImmutableArchive(archivePath);
        var firds = new DurableFirdsStore(Path.Combine(archivePath, "firds-state.json")).LoadVerified();
        var reportNames = Directory.EnumerateDirectories(
                archivePath,
                "NordicEquity-posttrade-*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderDescending(StringComparer.Ordinal)
            .Take(MaximumVerifiedReports)
            .ToArray();
        if (reportNames.Length == 0)
            throw new MarketDataException("AI exhibition archive contains no verified Nasdaq reports");

        var observations = new Dictionary<(string Isin, string OrderBookId),
            (NasdaqTrade Trade, FirdsInstrument Instrument, string Source)>();
        foreach (var reportName in reportNames)
        {
            var report = archive.Verify(reportName);
            afterVerification?.Invoke(report);
            var bytes = ReadVerifiedBytes(report);
            foreach (var trade in NasdaqCsvParser.Parse(bytes, report.FetchedAt))
            {
                if (trade.AvailableAt > asOf || asOf - trade.AvailableAt > MaximumObservationAge ||
                    trade.Venue != "XSTO" || trade.Currency != "SEK" || trade.PriceNotation != "MONE")
                    continue;
                var instrument = TradeInstrumentMapper.TryResolve(trade, firds.Instruments);
                if (instrument is null || instrument.Currency != "SEK" || instrument.Venue != "XSTO")
                    continue;
                var key = (instrument.Isin, instrument.OrderBookId);
                if (!observations.TryGetValue(key, out var current) || trade.ExecutedAt > current.Trade.ExecutedAt ||
                    (trade.ExecutedAt == current.Trade.ExecutedAt &&
                        StringComparer.Ordinal.Compare(trade.TransactionId, current.Trade.TransactionId) > 0))
                    observations[key] = (trade, instrument, "Nasdaq Nordic MiFID II delayed post-trade");
            }
        }

        var normalized = query?.Trim() ?? string.Empty;
        var items = observations.Values
            .Where(item => normalized.Length == 0 ||
                item.Instrument.OrderBookId.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Instrument.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Instrument.Isin.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Instrument.OrderBookId, StringComparer.Ordinal)
            .Take(20)
            .Select(item => new InstrumentDto(item.Instrument.Isin, item.Instrument.OrderBookId,
                item.Instrument.Name, "XSTO", "Sweden", "SEK", item.Trade.Price, PriceDkk: null,
                IsPreviewPrice: false, item.Trade.ExecutedAt, item.Trade.AvailableAt, item.Source,
                DelayMinutes: 15, Tradable: false, PaperTradable: true))
            .ToArray();
        return new(items, DataMode);
    }

    private static byte[] ReadVerifiedBytes(ArchivedReport report)
    {
        try
        {
            var bytes = File.ReadAllBytes(report.CsvPath);
            var actual = SHA256.HashData(bytes);
            var expected = Convert.FromHexString(report.Sha256);
            if (bytes.LongLength != report.Bytes || !CryptographicOperations.FixedTimeEquals(actual, expected))
                throw new MarketDataException("Archive changed after checksum verification");
            return bytes;
        }
        catch (MarketDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new MarketDataException("Archive entry changed after checksum verification", exception);
        }
    }
}
