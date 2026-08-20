using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AiStocks.Core;

namespace AiStocks.Api;

public sealed class GlobalRaceStore(TimeProvider clock)
{
    public static readonly Guid HumanSandboxRaceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid AiLeagueRaceId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid MixedExhibitionRaceId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public const decimal StartingCashDkk = 100_000m;
    public const string DataMode = "non-production-approved-provider-index-fixture";

    private readonly object sync = new();
    private readonly Dictionary<(Guid RaceId, string Principal), Participant> participants = new();
    private readonly Dictionary<(Guid RaceId, string Principal, string Key), JoinSubmission> joins = new();
    private readonly Dictionary<Guid, List<GlobalLedgerEvent>> ledger = new();
    private readonly Dictionary<Guid, List<GlobalOrder>> orders = new();
    private readonly Dictionary<(Guid ParticipantId, string Key), (string Hash, GlobalOrder Order)> orderKeys = new();
    private readonly Dictionary<(Guid RaceId, string Key), (string Hash, GlobalOrder Order)> aiOrderKeys = new();

    private static readonly GlobalRace[] RaceSeed =
    [
        new(HumanSandboxRaceId, "Human Sandbox", "human_sandbox", "open", StartingCashDkk),
        new(AiLeagueRaceId, "AI League", "ai_league", "open", StartingCashDkk),
        new(MixedExhibitionRaceId, "Mixed Exhibition", "mixed_exhibition", "open", StartingCashDkk)
    ];

    private static readonly GlobalInstrument[] InstrumentSeed =
    [
        new("novo-dk", "NOVO B", "Novo Nordisk A/S", "XCSE", "Denmark", "DKK"),
        new("aapl-us", "AAPL", "Apple Inc.", "XNAS", "United States", "USD"),
        new("asml-nl", "ASML", "ASML Holding N.V.", "XAMS", "Netherlands", "EUR"),
        new("sony-jp", "SONY", "Sony Group Corp.", "XTKS", "Japan", "JPY")
    ];

    public IReadOnlyList<GlobalRace> Races() => RaceSeed;

    public bool HasJoined(string principal, Guid raceId)
    {
        lock (sync) return participants.ContainsKey((raceId, Principal(principal)));
    }

    public GlobalRace Race(Guid raceId) => RaceSeed.SingleOrDefault(item => item.Id == raceId)
        ?? throw new GlobalRaceException("race-not-found", "Race was not found.");

    public GlobalInstrumentList Search(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        return new(InstrumentSeed.Where(item => normalized.Length == 0 ||
                item.Symbol.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Exchange.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Country.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(20).ToArray(), DataMode);
    }

    public GlobalInstrument Instrument(string instrumentId) => InstrumentSeed.SingleOrDefault(item =>
        string.Equals(item.Id, instrumentId, StringComparison.Ordinal))
        ?? throw new GlobalRaceException("instrument-not-found", "Instrument was not found in the approved local index.");

    public GlobalQuote Quote(string instrumentId)
    {
        _ = Instrument(instrumentId);
        return new(instrumentId, null, null, null, false, DataMode,
            "Execution unavailable: no approved quote and FX contract is configured.");
    }

    public JoinSubmission Join(string principal, Guid raceId, string idempotencyKey)
    {
        principal = Principal(principal);
        ValidateKey(idempotencyKey);
        var race = Race(raceId);
        if (race.Kind == "ai_league")
            throw new GlobalRaceException("human-join-not-allowed", "Humans cannot join the AI League.");
        lock (sync)
        {
            var key = (raceId, principal, idempotencyKey);
            if (joins.TryGetValue(key, out var replay)) return replay with { Replayed = true };
            if (participants.TryGetValue((raceId, principal), out var existing))
                throw new GlobalRaceException("already-joined", "Principal already joined this race with another key.");
            var participant = new Participant(Guid.NewGuid(), raceId, principal, "human", HumanAlias(principal), clock.GetUtcNow());
            participants.Add((raceId, principal), participant);
            var initial = new GlobalLedgerEvent(Guid.NewGuid(), participant.Id, "initial_cash", StartingCashDkk,
                clock.GetUtcNow(), $"initial:{participant.Id}");
            ledger.Add(participant.Id, [initial]);
            orders.Add(participant.Id, []);
            var result = new JoinSubmission(participant, false);
            joins.Add(key, result);
            return result;
        }
    }

    public GlobalPortfolio Portfolio(string principal, Guid raceId)
    {
        lock (sync)
        {
            var participant = OwnParticipant(principal, raceId);
            var cash = ledger[participant.Id].Sum(item => item.CashDeltaDkk);
            return new(participant.Id, participant.DisplayName, StartingCashDkk, cash, 0m, cash, [], DataMode);
        }
    }

    public IReadOnlyList<GlobalLedgerEvent> LedgerEvents(Guid participantId)
    {
        lock (sync) return ledger.TryGetValue(participantId, out var events) ? events.ToArray() : [];
    }

    public IReadOnlyList<GlobalLeaderboardEntry> Leaderboard(Guid raceId)
    {
        _ = Race(raceId);
        lock (sync)
        {
            var rows = participants.Values.Where(item => item.RaceId == raceId)
                .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
                .Select((item, index) => new GlobalLeaderboardEntry(index + 1, item.Id, item.DisplayName,
                    item.ParticipantType, StartingCashDkk, 0m)).ToArray();
            return rows;
        }
    }

    public GlobalOrderSubmission SubmitHumanOrder(string principal, Guid raceId, string idempotencyKey,
        GlobalHumanOrderRequest request)
    {
        ValidateKey(idempotencyKey);
        var participant = OwnParticipant(principal, raceId);
        if (request.Side?.Trim().ToLowerInvariant() is not ("buy" or "sell"))
            throw new GlobalRaceException("invalid-side", "Only buy and sell market-order intents are accepted.");
        if (request.Quantity is < 1 or > 100_000)
            throw new GlobalRaceException("invalid-quantity", "Quantity must be between 1 and 100,000 whole shares.");
        var instrument = Instrument(request.InstrumentId);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 500) throw new GlobalRaceException("note-too-long", "Note must not exceed 500 characters.");
        var side = request.Side.Trim().ToLowerInvariant();
        var hash = Hash($"human\n{Principal(principal)}\n{participant.Id}\n{raceId}\n{side}\n{instrument.Id}\n{request.Quantity}\n{note}");
        lock (sync)
        {
            var key = (participant.Id, idempotencyKey);
            if (orderKeys.TryGetValue(key, out var prior))
            {
                if (prior.Hash != hash) throw new GlobalRaceException("idempotency-conflict", "Key was used for another request.");
                return new(prior.Order, true);
            }
            var order = new GlobalOrder(Guid.NewGuid(), raceId, participant.Id, "human", instrument.Id,
                instrument.Symbol, side, request.Quantity, "market", "queued", note, hash, clock.GetUtcNow(), null, null);
            orderKeys.Add(key, (hash, order));
            orders[participant.Id].Add(order);
            return new(order, false);
        }
    }

    public IReadOnlyList<GlobalOrder> Orders(string principal, Guid raceId)
    {
        lock (sync)
        {
            var participant = OwnParticipant(principal, raceId);
            return orders[participant.Id].OrderByDescending(item => item.SubmittedAt).ToArray();
        }
    }

    public GlobalOrder Cancel(string principal, Guid raceId, Guid orderId, string idempotencyKey)
    {
        ValidateKey(idempotencyKey);
        lock (sync)
        {
            var participant = OwnParticipant(principal, raceId);
            var index = orders[participant.Id].FindIndex(item => item.Id == orderId);
            if (index < 0) throw new GlobalRaceException("order-not-found", "Order was not found.");
            var current = orders[participant.Id][index];
            if (current.Status == "cancelled") return current;
            if (current.Status != "queued") throw new GlobalRaceException("order-not-cancellable", "Only queued intents can be cancelled.");
            var cancelled = current with { Status = "cancelled" };
            orders[participant.Id][index] = cancelled;
            return cancelled;
        }
    }

    public GlobalOrderSubmission SubmitAiOrder(Guid raceId, string idempotencyKey, GlobalAiOrderRequest request)
    {
        ValidateKey(idempotencyKey);
        var race = Race(raceId);
        if (race.Kind == "human_sandbox") throw new GlobalRaceException("ai-not-allowed", "AI cannot enter a human sandbox.");
        if (request.Rationale is null || string.IsNullOrWhiteSpace(request.Rationale.Thesis) ||
            request.Rationale.Thesis.Trim().Length > 2_000 || request.Rationale.Confidence is < 0m or > 1m ||
            request.Rationale.Evidence is null || request.Rationale.Evidence.Count == 0 ||
            request.Rationale.Evidence.Count > 20 || request.Rationale.Evidence.Any(item =>
                item.Url is not { Length: <= 2_048 } ||
                !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                item.PublishedAt == default || item.PublishedAt > clock.GetUtcNow() ||
                string.IsNullOrWhiteSpace(item.ExactExcerpt) ||
                item.ExactExcerpt.Length > 2_000 || !ValidSha(item.ContentSha256)))
            throw new GlobalRaceException("invalid-ai-rationale", "AI orders require bounded thesis, confidence, and structured HTTPS evidence.");
        if (!ContestContract.Agents.Any(agent => string.Equals(agent.ModelId, request.ModelId, StringComparison.Ordinal)))
            throw new GlobalRaceException("invalid-model", "AI model identity must be one fixed trusted competitor.");
        var instrument = Instrument(request.InstrumentId);
        if (request.Side?.Trim().ToLowerInvariant() is not ("buy" or "sell") || request.Quantity is < 1 or > 100_000)
            throw new GlobalRaceException("invalid-order", "AI order shape is invalid.");
        var hash = Hash($"ai\n{raceId}\n{request.ModelId}\n{System.Text.Json.JsonSerializer.Serialize(request)}");
        lock (sync)
        {
            var key = (raceId, idempotencyKey);
            if (aiOrderKeys.TryGetValue(key, out var prior))
            {
                if (prior.Hash != hash) throw new GlobalRaceException("idempotency-conflict", "Key was used for another request.");
                return new(prior.Order, true);
            }
            var order = new GlobalOrder(Guid.NewGuid(), raceId, Guid.Empty, "ai", instrument.Id, instrument.Symbol,
                request.Side.Trim().ToLowerInvariant(), request.Quantity, "market", "queued", null, hash,
                clock.GetUtcNow(), null, request.Rationale);
            aiOrderKeys.Add(key, (hash, order));
            return new(order, false);
        }
    }

    private Participant OwnParticipant(string principal, Guid raceId) =>
        participants.TryGetValue((raceId, Principal(principal)), out var participant)
            ? participant : throw new GlobalRaceException("portfolio-not-found", "No portfolio exists for this principal and race.");

    private static string Principal(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new GlobalRaceException("invalid-principal", "Authenticated principal is required.")
        : value.Trim().ToLowerInvariant();
    private static void ValidateKey(string key)
    {
        if (key is not { Length: >= 8 and <= 128 } || key.Any(character => character < '!' || character > '~'))
            throw new GlobalRaceException("invalid-idempotency-key", "Idempotency key must be 8-128 visible ASCII characters.");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HumanAlias(string principal) => $"Human {Hash(principal)[..8]}";
    private static bool ValidSha(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class GlobalRaceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record GlobalRace(Guid Id, string Name, string Kind, string Status, decimal InitialCashDkk);
public sealed record Participant(Guid Id, Guid RaceId, string Principal, string ParticipantType, string DisplayName, DateTimeOffset JoinedAt);
public sealed record JoinSubmission(Participant Participant, bool Replayed);
public sealed record GlobalLedgerEvent(Guid Id, Guid ParticipantId, string EventType, decimal CashDeltaDkk, DateTimeOffset OccurredAt, string Reference);
public sealed record GlobalInstrument(string Id, string Symbol, string Name, string Exchange, string Country, string Currency);
public sealed record GlobalInstrumentList(IReadOnlyList<GlobalInstrument> Items, string DataMode);
public sealed record GlobalQuote(string InstrumentId, decimal? Price, string? Currency, DateTimeOffset? ObservedAt,
    bool Executable, string DataMode, string UnavailableReason);
public sealed record GlobalPortfolio(Guid ParticipantId, string DisplayName, decimal StartingCashDkk, decimal CashDkk,
    decimal HoldingsValueDkk, decimal TotalValueDkk, IReadOnlyList<object> Holdings, string DataMode);
public sealed record GlobalLeaderboardEntry(int Rank, Guid ParticipantId, string DisplayName, string ParticipantType,
    decimal ValueDkk, decimal ReturnPercent);
public sealed record GlobalHumanOrderRequest(string Side, string InstrumentId, int Quantity, string? Note = null);
public sealed record GlobalOrder(Guid Id, Guid RaceId, Guid ParticipantId, string ActorType, string InstrumentId,
    string Symbol, string Side, int Quantity, string OrderType, string Status, string? Note, string RequestHash,
    DateTimeOffset SubmittedAt, decimal? FillPriceDkk, GlobalAiRationale? Rationale);
public sealed record GlobalOrderSubmission(GlobalOrder Order, bool Replayed);
public sealed record GlobalAiOrderRequest(string ModelId, string Side, string InstrumentId, int Quantity, GlobalAiRationale Rationale);
public sealed record GlobalAiRationale(string Thesis, IReadOnlyList<GlobalEvidence> Evidence, decimal Confidence);
public sealed record GlobalEvidence(string Url, DateTimeOffset PublishedAt, string ExactExcerpt, string ContentSha256);
