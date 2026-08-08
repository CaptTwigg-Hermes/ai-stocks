using System.Security.Cryptography;
using System.Text.Json;

namespace AiStocks.MarketData;

public sealed record InstrumentSessionObservation(string Isin, string OrderBookId, DateOnly Session, decimal TradedValue,
    int UsableTradeCount, string ManifestSha256, string? RawReport, string? RawSha256);

public sealed class DurableObservationStore
{
    private readonly string _path;
    private readonly ImmutableArchive _archive;
    private readonly SessionManifestStore _manifests;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DurableObservationStore(string path, ImmutableArchive archive, SessionManifestStore manifests)
    { _path = Path.GetFullPath(path); _archive = archive; _manifests = manifests; }

    public void Record(InstrumentSessionObservation observation)
    {
        if (observation.TradedValue < 0 || observation.UsableTradeCount < 0 || string.IsNullOrWhiteSpace(observation.Isin) || string.IsNullOrWhiteSpace(observation.OrderBookId))
            throw new MarketDataException("Observation aggregate is invalid");
        var session = StockholmCalendar.GetSession(observation.Session) ?? throw new MarketDataException("Observation is not an XSTO session");
        var manifest = _manifests.Verify(session);
        if (manifest.Sha256 != observation.ManifestSha256) throw new MarketDataException("Observation manifest provenance mismatch");
        if (observation.UsableTradeCount == 0)
        {
            if (observation.TradedValue != 0 || observation.RawReport is not null || observation.RawSha256 is not null)
                throw new MarketDataException("Zero-trade session must be explicit and have no fabricated raw row");
        }
        else
        {
            if (observation.TradedValue <= 0 || observation.RawReport is null || observation.RawSha256 is null)
                throw new MarketDataException("Usable observation raw provenance is missing");
            var manifestReport = manifest.Manifest.Reports.SingleOrDefault(x => x.Report == observation.RawReport);
            var archived = _archive.Verify(observation.RawReport);
            if (manifestReport is null || manifestReport.Sha256 != observation.RawSha256 || archived.Sha256 != observation.RawSha256)
                throw new MarketDataException("Observation raw hash is not bound to the complete archive manifest");
        }
        var values = LoadInternal().Where(x => (x.Isin, x.OrderBookId, x.Session) != (observation.Isin, observation.OrderBookId, observation.Session)).Append(observation)
            .OrderBy(x => x.Isin, StringComparer.Ordinal).ThenBy(x => x.OrderBookId, StringComparer.Ordinal).ThenBy(x => x.Session).ToArray();
        Persist(values);
    }

    public IReadOnlyList<InstrumentSessionObservation> LoadVerified() => LoadInternal();

    private IReadOnlyList<InstrumentSessionObservation> LoadInternal()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(_path), JsonOptions) ?? throw new JsonException();
            var actual = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Observations, JsonOptions)));
            if (actual != envelope.Sha256) throw new MarketDataException("Observation state checksum mismatch");
            return envelope.Observations;
        }
        catch (MarketDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException)
        { throw new MarketDataException("Observation state is malformed", exception); }
    }

    private void Persist(IReadOnlyList<InstrumentSessionObservation> observations)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(observations, JsonOptions)));
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(new Envelope(hash, observations), JsonOptions));
    }
    private sealed record Envelope(string Sha256, IReadOnlyList<InstrumentSessionObservation> Observations);
}

public sealed record ReadinessResult(bool Ready, IReadOnlyList<string> Failures);

public sealed class MarketDataReadinessGate(DurableFirdsStore firds, NasdaqStatusMachine statuses,
    SessionManifestStore manifests, DurableObservationStore observations)
{
    public ReadinessResult Evaluate(DateOnly asOf)
    {
        var failures = new List<string>();
        FirdsSnapshot snapshot;
        IReadOnlyList<InstrumentSessionObservation> values;
        try { snapshot = firds.LoadVerified(); }
        catch (MarketDataException exception) { return new(false, [exception.Message]); }
        try { values = observations.LoadVerified(); }
        catch (MarketDataException exception) { return new(false, [exception.Message]); }
        if (snapshot.Instruments.Count == 0) failures.Add("FIRDS eligible universe is empty");
        var expected = ExpectedSessions(asOf);
        var verifiedManifests = new Dictionary<DateOnly, VerifiedSessionManifest>();
        foreach (var day in expected)
        {
            try { verifiedManifests[day] = manifests.Verify(StockholmCalendar.GetSession(day)!); }
            catch (MarketDataException exception) { failures.Add($"{day}: {exception.Message}"); }
        }
        foreach (var instrument in snapshot.Instruments)
        {
            if (statuses.StateOf(instrument.Isin) == InstrumentTradingState.Unknown)
            { failures.Add($"{instrument.Isin}/{instrument.OrderBookId}: signed status is unknown"); continue; }
            if (!statuses.IsEligible(instrument.Isin)) continue;
            var history = values.Where(x => x.Isin == instrument.Isin && x.OrderBookId == instrument.OrderBookId && expected.Contains(x.Session)).OrderBy(x => x.Session).ToArray();
            if (history.Length != 20 || !history.Select(x => x.Session).SequenceEqual(expected))
            { failures.Add($"{instrument.Isin}/{instrument.OrderBookId}: 20 consecutive observations are missing"); continue; }
            if (history.Any(x => !verifiedManifests.TryGetValue(x.Session, out var manifest) || manifest.Sha256 != x.ManifestSha256))
                failures.Add($"{instrument.Isin}/{instrument.OrderBookId}: observation manifest binding failed");
            if (!history.Any(x => x.UsableTradeCount > 0)) failures.Add($"{instrument.Isin}/{instrument.OrderBookId}: no usable observations");
            try { _ = AverageDailyValue.Calculate20(history.Select(x => new SessionTradedValue(x.Session, x.TradedValue, true, x.ManifestSha256))); }
            catch (MarketDataException exception) { failures.Add($"{instrument.Isin}/{instrument.OrderBookId}: {exception.Message}"); }
        }
        return new(failures.Count == 0, failures);
    }

    private static IReadOnlyList<DateOnly> ExpectedSessions(DateOnly asOf)
    {
        var days = new List<DateOnly>();
        for (var day = asOf; days.Count < 20; day = day.AddDays(-1)) if (StockholmCalendar.GetSession(day) is not null) days.Add(day);
        days.Reverse(); return days;
    }
}

public sealed class ConfiguredMarketDataReadiness(
    string archivePath, string firdsStatePath, string observationStatePath, string seedPayloadPath,
    string seedSignaturePath, string pinnedPublicKeyPath, string pinnedKeyId)
{
    public ReadinessResult Evaluate(DateOnly asOf)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pinnedKeyId)) throw new MarketDataException("Pinned status signer key identity is not configured");
            var archive = new ImmutableArchive(archivePath);
            var manifests = new SessionManifestStore(archivePath);
            var verifier = new PinnedStatusSeedVerifier(pinnedKeyId, File.ReadAllBytes(pinnedPublicKeyPath));
            var statuses = verifier.Load(File.ReadAllText(seedPayloadPath), File.ReadAllBytes(seedSignaturePath), Path.Combine(archivePath, "status-state.json"));
            var firds = new DurableFirdsStore(firdsStatePath);
            var observations = new DurableObservationStore(observationStatePath, archive, manifests);
            return new MarketDataReadinessGate(firds, statuses, manifests, observations).Evaluate(asOf);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MarketDataException or CryptographicException)
        { return new(false, [exception.Message]); }
    }
}
