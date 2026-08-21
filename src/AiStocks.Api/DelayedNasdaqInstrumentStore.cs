using System.Security.Cryptography;
using AiStocks.Core;
using AiStocks.MarketData;

namespace AiStocks.Api;

public sealed class DelayedNasdaqInstrumentStore
{
    public const string DataMode = "official-nasdaq-xsto-15m-delayed";
    public const string NordicDataMode = "official-nasdaq-nordic-15m-delayed-ecb-fx";

    private const int MaximumVerifiedReports = 32;
    private static readonly TimeSpan MaximumObservationAge = TimeSpan.FromHours(72);
    private static readonly TimeSpan MaximumStatusAge = TimeSpan.FromMinutes(10);
    private static readonly IReadOnlyDictionary<string, string> Countries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XSTO"] = "Sweden",
            ["XCSE"] = "Denmark",
            ["XHEL"] = "Finland",
            ["ONSE"] = "Norway",
            ["XICE"] = "Iceland"
        };
    private readonly string archivePath;
    private readonly TimeProvider clock;
    private readonly Action<ArchivedReport>? afterVerification;
    private readonly FirdsUniverse universe;

    public DelayedNasdaqInstrumentStore(string archivePath, TimeProvider clock)
        : this(archivePath, clock, null, FirdsUniverse.StockholmContest)
    {
    }

    public DelayedNasdaqInstrumentStore(string archivePath, TimeProvider clock, FirdsUniverse universe)
        : this(archivePath, clock, null, universe)
    {
    }

    internal DelayedNasdaqInstrumentStore(
        string archivePath,
        TimeProvider clock,
        Action<ArchivedReport>? afterVerification,
        FirdsUniverse universe = FirdsUniverse.StockholmContest)
    {
        this.archivePath = Path.GetFullPath(archivePath);
        this.clock = clock;
        this.afterVerification = afterVerification;
        this.universe = universe;
    }

    public InstrumentListDto Search(string? query)
    {
        var snapshot = CurrentSnapshot();
        var normalized = query?.Trim() ?? string.Empty;
        var filtered = snapshot.Items
            .Where(item => normalized.Length == 0 ||
                item.Symbol.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var items = snapshot.DataMode == NordicDataMode && normalized.Length == 0
            ? BalancedNordicPublicSnapshot(filtered, 20)
            : filtered.Take(20).ToArray();
        return new InstrumentListDto(items, snapshot.DataMode);
    }

    internal static IReadOnlyList<InstrumentDto> BalancedNordicPublicSnapshot(
        IReadOnlyList<InstrumentDto> instruments, int maximum)
    {
        var venues = new[] { "XSTO", "XCSE", "XHEL", "ONSE", "XICE" };
        var queues = venues.ToDictionary(venue => venue,
            venue => new Queue<InstrumentDto>(instruments
                .Where(item => item.Exchange == venue)
                .OrderBy(item => item.Symbol, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)), StringComparer.Ordinal);
        var selected = new List<InstrumentDto>(maximum);
        while (selected.Count < maximum && queues.Values.Any(queue => queue.Count > 0))
        {
            foreach (var venue in venues)
            {
                if (selected.Count == maximum) break;
                if (queues[venue].TryDequeue(out var item)) selected.Add(item);
            }
        }
        return selected;
    }

    public InstrumentListDto CurrentSnapshot()
    {
        var asOf = clock.GetUtcNow();
        var archive = new ImmutableArchive(archivePath);
        var firdsPath = Path.Combine(archivePath,
            universe == FirdsUniverse.NordicExhibition ? "firds-nordic-state.json" : "firds-state.json");
        var firds = new DurableFirdsStore(firdsPath, universe).LoadVerified();
        var fx = universe == FirdsUniverse.NordicExhibition
            ? new EcbFxStore(archivePath).LoadVerified(asOf)
            : null;
        var statuses = universe == FirdsUniverse.NordicExhibition
            ? NasdaqStatusMachine.LoadPublicRssBestEffort(Path.Combine(archivePath, "status-state.json"))
            : null;
        var blockedCorporateActions = universe == FirdsUniverse.NordicExhibition
            ? new UnsupportedCorporateActionStore(archivePath).LoadVerified(asOf).Actions
                .Select(action => (action.Venue, action.Isin, action.OrderBookId)).ToHashSet()
            : new HashSet<(string Venue, string Isin, string OrderBookId)>();
        if (statuses is not null && !statuses.IsFreshAt(asOf, MaximumStatusAge))
            throw new MarketDataException("Nordic instrument status state is missing or stale");
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

        var observations = new Dictionary<(string Venue, string Isin, string OrderBookId),
            (NasdaqTrade Trade, FirdsInstrument Instrument, string Source)>();
        foreach (var reportName in reportNames)
        {
            var report = archive.Verify(reportName);
            afterVerification?.Invoke(report);
            var bytes = ReadVerifiedBytes(report);
            foreach (var trade in NasdaqCsvParser.Parse(bytes, report.FetchedAt))
            {
                if (trade.AvailableAt > asOf || asOf - trade.AvailableAt > MaximumObservationAge ||
                    trade.PriceNotation != "MONE")
                    continue;
                var instrument = TradeInstrumentMapper.TryResolve(trade, firds.Instruments);
                if (instrument is null || trade.Currency != instrument.Currency || trade.Venue != instrument.Venue ||
                    (statuses is not null && !statuses.IsEligible(instrument.Isin)) ||
                    blockedCorporateActions.Contains((trade.Venue, trade.Isin, instrument.OrderBookId)) ||
                    (universe == FirdsUniverse.StockholmContest &&
                        (instrument.Currency != "SEK" || instrument.Venue != "XSTO")))
                    continue;
                var key = (instrument.Venue, instrument.Isin, instrument.OrderBookId);
                if (!observations.TryGetValue(key, out var current) || trade.ExecutedAt > current.Trade.ExecutedAt ||
                    (trade.ExecutedAt == current.Trade.ExecutedAt &&
                        StringComparer.Ordinal.Compare(trade.TransactionId, current.Trade.TransactionId) > 0))
                    observations[key] = (trade, instrument, "Nasdaq Nordic MiFID II delayed post-trade");
            }
        }

        var items = observations.Values
            .OrderBy(item => item.Instrument.OrderBookId, StringComparer.Ordinal)
            .Select(item =>
            {
                if (fx is null)
                    return new InstrumentDto(item.Instrument.Isin, item.Instrument.OrderBookId,
                        item.Instrument.Name, "XSTO", "Sweden", "SEK", item.Trade.Price, PriceDkk: null,
                        IsPreviewPrice: false, item.Trade.ExecutedAt, item.Trade.AvailableAt, item.Source,
                        DelayMinutes: 15, Tradable: false, PaperTradable: true);
                if (!fx.DkkPerUnit.TryGetValue(item.Instrument.Currency, out var fxToDkk) ||
                    !Countries.TryGetValue(item.Instrument.Venue, out var country))
                    throw new MarketDataException("Nordic observation lacks authoritative FX or venue identity");
                var instrumentId = $"{item.Instrument.Venue}:{item.Instrument.Isin}:{item.Instrument.OrderBookId}";
                return new InstrumentDto(instrumentId, item.Instrument.OrderBookId,
                    item.Instrument.Name, item.Instrument.Venue, country, item.Instrument.Currency,
                    item.Trade.Price, item.Trade.Price * fxToDkk, IsPreviewPrice: false,
                    item.Trade.ExecutedAt, item.Trade.AvailableAt,
                    item.Source, DelayMinutes: 15, Tradable: false, PaperTradable: true,
                    FxToDkk: fxToDkk, FxReferenceDate: fx.ReferenceDate, FxAvailableAt: fx.AvailableAt,
                    FxSource: MarketDataProvenance.EcbInformationalReferenceRates, FxSha256: fx.Sha256);
            })
            .ToArray();
        return new(items, universe == FirdsUniverse.NordicExhibition ? NordicDataMode : DataMode);
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
