using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiStocks.Core;
using Npgsql;
using NpgsqlTypes;

namespace AiStocks.Api;

public sealed class PostgresGlobalRaceStore(NpgsqlDataSource dataSource, TimeProvider clock) : IGlobalRaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<GlobalRace> Races()
    {
        using var command = dataSource.CreateCommand("SELECT id,name,kind,status,initial_cash_dkk FROM v2_races ORDER BY kind");
        using var reader = command.ExecuteReader();
        var result = new List<GlobalRace>();
        while (reader.Read()) result.Add(ReadRace(reader));
        return result;
    }

    public bool HasJoined(string principal, Guid raceId)
    {
        principal = NormalizePrincipal(principal);
        using var command = dataSource.CreateCommand(
            "SELECT EXISTS(SELECT 1 FROM v2_participants WHERE race_id=$1 AND principal=$2 AND participant_type='human')");
        command.Parameters.AddWithValue(raceId);
        command.Parameters.AddWithValue(principal);
        return command.ExecuteScalar() is true;
    }

    public GlobalRace Race(Guid raceId)
    {
        using var command = dataSource.CreateCommand(
            "SELECT id,name,kind,status,initial_cash_dkk FROM v2_races WHERE id=$1");
        command.Parameters.AddWithValue(raceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Error("race-not-found", "Race was not found.");
        return ReadRace(reader);
    }

    public GlobalInstrumentList Search(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        using var command = dataSource.CreateCommand("""
            SELECT id,symbol,name,exchange,country,currency::text
            FROM v2_instruments
            WHERE $1='' OR symbol ILIKE '%' || $1 || '%' OR name ILIKE '%' || $1 || '%'
              OR exchange ILIKE '%' || $1 || '%' OR country ILIKE '%' || $1 || '%'
            ORDER BY symbol,id LIMIT 20
            """);
        command.Parameters.AddWithValue(normalized);
        using var reader = command.ExecuteReader();
        var result = new List<GlobalInstrument>();
        while (reader.Read()) result.Add(ReadInstrument(reader));
        return new(result, GlobalRaceStore.DataMode);
    }

    public GlobalInstrument Instrument(string instrumentId)
    {
        using var command = dataSource.CreateCommand(
            "SELECT id,symbol,name,exchange,country,currency::text FROM v2_instruments WHERE id=$1");
        command.Parameters.AddWithValue(instrumentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Error("instrument-not-found", "Instrument was not found in the approved local index.");
        return ReadInstrument(reader);
    }

    public GlobalQuote Quote(string instrumentId)
    {
        _ = Instrument(instrumentId);
        return new(instrumentId, null, null, null, false, GlobalRaceStore.DataMode,
            "Execution unavailable: no approved quote and FX contract is configured.");
    }

    public JoinSubmission Join(string principal, Guid raceId, string idempotencyKey)
    {
        principal = NormalizePrincipal(principal);
        ValidateKey(idempotencyKey);
        var race = Race(raceId);
        if (race.Kind == "ai_league") throw Error("human-join-not-allowed", "Humans cannot join the AI League.");
        var hash = Hash($"join\n{raceId}\n{principal}");
        return EnsureParticipant(Guid.NewGuid(), raceId, principal, "human", HumanAlias(principal), null, idempotencyKey, hash);
    }

    public GlobalPortfolio Portfolio(string principal, Guid raceId)
    {
        var participant = OwnParticipant(principal, raceId);
        using var command = dataSource.CreateCommand("""
            SELECT COALESCE(sum(cash_delta_dkk),0) FROM v2_ledger_events WHERE participant_id=$1
            """);
        command.Parameters.AddWithValue(participant.Id);
        var cash = (decimal)command.ExecuteScalar()!;
        return new(participant.Id, participant.DisplayName, GlobalRaceStore.StartingCashDkk, cash, 0m, cash, [],
            GlobalRaceStore.DataMode);
    }

    public IReadOnlyList<GlobalLedgerEvent> LedgerEvents(Guid participantId)
    {
        using var command = dataSource.CreateCommand("""
            SELECT id,participant_id,event_type,cash_delta_dkk,occurred_at,reference
            FROM v2_ledger_events WHERE participant_id=$1 ORDER BY occurred_at,id
            """);
        command.Parameters.AddWithValue(participantId);
        using var reader = command.ExecuteReader();
        var result = new List<GlobalLedgerEvent>();
        while (reader.Read()) result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.GetDecimal(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5)));
        return result;
    }

    public IReadOnlyList<GlobalLeaderboardEntry> Leaderboard(Guid raceId)
    {
        _ = Race(raceId);
        using var command = dataSource.CreateCommand("""
            SELECT p.id,p.display_name,p.participant_type,COALESCE(sum(l.cash_delta_dkk),0)
            FROM v2_participants p LEFT JOIN v2_ledger_events l ON l.participant_id=p.id
            WHERE p.race_id=$1 GROUP BY p.id,p.display_name,p.participant_type ORDER BY p.display_name,p.id
            """);
        command.Parameters.AddWithValue(raceId);
        using var reader = command.ExecuteReader();
        var result = new List<GlobalLeaderboardEntry>();
        var rank = 0;
        while (reader.Read()) result.Add(new(++rank, reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader.GetDecimal(3), 0m));
        return result;
    }

    public GlobalOrderSubmission SubmitHumanOrder(string principal, Guid raceId, string idempotencyKey,
        GlobalHumanOrderRequest request)
    {
        ValidateKey(idempotencyKey);
        var participant = OwnParticipant(principal, raceId);
        var side = ValidateOrder(request.Side, request.Quantity);
        var instrument = Instrument(request.InstrumentId);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 500) throw Error("note-too-long", "Note must not exceed 500 characters.");
        var hash = Hash($"human\n{NormalizePrincipal(principal)}\n{participant.Id}\n{raceId}\n{side}\n{instrument.Id}\n{request.Quantity}\n{note}");
        return InsertOrder(raceId, participant, "human", null, instrument, side, request.Quantity, note, null,
            idempotencyKey, hash);
    }

    public IReadOnlyList<GlobalOrder> Orders(string principal, Guid raceId)
    {
        var participant = OwnParticipant(principal, raceId);
        using var command = dataSource.CreateCommand(OrderSelect + " WHERE o.participant_id=$1 ORDER BY o.submitted_at DESC,o.id");
        command.Parameters.AddWithValue(participant.Id);
        using var reader = command.ExecuteReader();
        var result = new List<GlobalOrder>();
        while (reader.Read()) result.Add(ReadOrder(reader));
        return result;
    }

    public GlobalOrder Cancel(string principal, Guid raceId, Guid orderId, string idempotencyKey)
    {
        ValidateKey(idempotencyKey);
        var participant = OwnParticipant(principal, raceId);
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        AdvisoryLock(connection, transaction, $"cancel:{participant.Id}:{orderId}");
        var current = FindOrder(connection, transaction, participant.Id, orderId)
            ?? throw Error("order-not-found", "Order was not found.");
        var hash = Hash($"cancel\n{participant.Id}\n{orderId}");
        using (var priorKey = new NpgsqlCommand("""
            SELECT order_id,request_hash FROM v2_order_lifecycle_events
            WHERE participant_id=$1 AND idempotency_key=$2
            """, connection, transaction))
        {
            priorKey.Parameters.AddWithValue(participant.Id);
            priorKey.Parameters.AddWithValue(idempotencyKey);
            using var reader = priorKey.ExecuteReader();
            if (reader.Read() && (reader.GetGuid(0) != orderId || reader.GetString(1) != hash))
                throw Error("idempotency-conflict", "Key was used for another request.");
        }
        if (current.Status == "cancelled")
        {
            transaction.Commit();
            return current;
        }
        using var insert = new NpgsqlCommand("""
            INSERT INTO v2_order_lifecycle_events(id,order_id,participant_id,event_type,idempotency_key,request_hash,occurred_at)
            VALUES($1,$2,$3,'cancelled',$4,$5,$6)
            """, connection, transaction);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(orderId);
        insert.Parameters.AddWithValue(participant.Id);
        insert.Parameters.AddWithValue(idempotencyKey);
        insert.Parameters.AddWithValue(hash);
        insert.Parameters.AddWithValue(clock.GetUtcNow());
        insert.ExecuteNonQuery();
        transaction.Commit();
        return current with { Status = "cancelled" };
    }

    public GlobalOrderSubmission SubmitAiOrder(Guid raceId, string idempotencyKey, GlobalAiOrderRequest request)
    {
        ValidateKey(idempotencyKey);
        var race = Race(raceId);
        if (race.Kind == "human_sandbox") throw Error("ai-not-allowed", "AI cannot enter a human sandbox.");
        ValidateAi(request);
        var instrument = Instrument(request.InstrumentId);
        var side = ValidateOrder(request.Side, request.Quantity);
        var model = request.ModelId.Trim();
        var principal = $"ai:{model.ToLowerInvariant()}";
        var joinKey = $"model:{Hash(model)[..16]}";
        var participant = EnsureParticipant(Guid.NewGuid(), raceId, principal, "ai", model, model, joinKey,
            Hash($"ai-participant\n{raceId}\n{model}")).Participant;
        var hash = Hash($"ai\n{raceId}\n{model}\n{JsonSerializer.Serialize(request, JsonOptions)}");
        return InsertOrder(raceId, participant, "ai", model, instrument, side, request.Quantity, null,
            request.Rationale, idempotencyKey, hash);
    }

    private JoinSubmission EnsureParticipant(Guid id, Guid raceId, string principal, string type, string displayName,
        string? modelId, string key, string hash)
    {
        using var command = dataSource.CreateCommand("""
            SELECT id,race_id,principal,participant_type,display_name,joined_at,replayed
            FROM v2_ensure_participant($1,$2,$3,$4,$5,$6,$7,$8,$9)
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(raceId);
        command.Parameters.AddWithValue(principal);
        command.Parameters.AddWithValue(type);
        command.Parameters.AddWithValue(displayName);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = modelId ?? (object)DBNull.Value });
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(hash);
        command.Parameters.AddWithValue(clock.GetUtcNow());
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw Error("participant-write-failed", "Participant could not be persisted.");
            return new(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)), reader.GetBoolean(6));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw Error("already-joined", "Principal already joined this race with another key.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.RaiseException)
        {
            throw Error(exception.MessageText.Contains("different key", StringComparison.Ordinal) ? "already-joined" :
                "participant-write-failed", exception.MessageText);
        }
    }

    private GlobalOrderSubmission InsertOrder(Guid raceId, Participant participant, string actorType, string? modelId,
        GlobalInstrument instrument, string side, int quantity, string? note, GlobalAiRationale? rationale,
        string key, string hash)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        AdvisoryLock(connection, transaction, $"order:{participant.Id}:{key}");
        using (var existing = new NpgsqlCommand(OrderSelect +
            " WHERE o.race_id=$1 AND o.participant_id=$2 AND o.idempotency_key=$3", connection, transaction))
        {
            existing.Parameters.AddWithValue(raceId);
            existing.Parameters.AddWithValue(participant.Id);
            existing.Parameters.AddWithValue(key);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var prior = ReadOrder(reader);
                if (prior.RequestHash != hash) throw Error("idempotency-conflict", "Key was used for another request.");
                reader.Close();
                transaction.Commit();
                return new(prior, true);
            }
        }
        var id = Guid.NewGuid();
        using var insert = new NpgsqlCommand("""
            INSERT INTO v2_orders(id,race_id,participant_id,actor_type,trusted_model_id,instrument_id,side,quantity,
              order_type,status,note,rationale_json,evidence_json,idempotency_key,request_hash,submitted_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,'market','queued',$9,$10::jsonb,$11::jsonb,$12,$13,$14)
            """, connection, transaction);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(raceId);
        insert.Parameters.AddWithValue(participant.Id);
        insert.Parameters.AddWithValue(actorType);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = modelId ?? (object)DBNull.Value });
        insert.Parameters.AddWithValue(instrument.Id);
        insert.Parameters.AddWithValue(side);
        insert.Parameters.AddWithValue(quantity);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = note ?? (object)DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter
            { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = rationale is null ? DBNull.Value : JsonSerializer.Serialize(rationale, JsonOptions) });
        insert.Parameters.Add(new NpgsqlParameter
            { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = rationale is null ? DBNull.Value : JsonSerializer.Serialize(rationale.Evidence, JsonOptions) });
        insert.Parameters.AddWithValue(key);
        insert.Parameters.AddWithValue(hash);
        var submittedAt = clock.GetUtcNow();
        insert.Parameters.AddWithValue(submittedAt);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new(new(id, raceId, participant.Id, actorType, instrument.Id, instrument.Symbol, side, quantity,
            "market", "queued", note, hash, submittedAt, null, rationale), false);
    }

    private Participant OwnParticipant(string principal, Guid raceId)
    {
        principal = NormalizePrincipal(principal);
        using var command = dataSource.CreateCommand("""
            SELECT id,race_id,principal,participant_type,display_name,joined_at
            FROM v2_participants WHERE race_id=$1 AND principal=$2 AND participant_type='human'
            """);
        command.Parameters.AddWithValue(raceId);
        command.Parameters.AddWithValue(principal);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw Error("portfolio-not-found", "No portfolio exists for this principal and race.");
        return ReadParticipant(reader);
    }

    private static GlobalOrder? FindOrder(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid participantId, Guid orderId)
    {
        using var command = new NpgsqlCommand(OrderSelect +
            " WHERE o.participant_id=$1 AND o.id=$2 FOR UPDATE OF o", connection, transaction);
        command.Parameters.AddWithValue(participantId);
        command.Parameters.AddWithValue(orderId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadOrder(reader) : null;
    }

    private static void AdvisoryLock(NpgsqlConnection connection, NpgsqlTransaction transaction, string key)
    {
        using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction);
        command.Parameters.AddWithValue(key);
        command.ExecuteNonQuery();
    }

    private static GlobalRace ReadRace(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4));

    private static GlobalInstrument ReadInstrument(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5).Trim());

    private static Participant ReadParticipant(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5));

    private static GlobalOrder ReadOrder(NpgsqlDataReader reader)
    {
        var rationale = reader.IsDBNull(15) ? null : JsonSerializer.Deserialize<GlobalAiRationale>(reader.GetString(15), JsonOptions);
        return new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetInt32(7), "market", reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11), null, rationale);
    }

    private static string ValidateOrder(string? side, int quantity)
    {
        var normalized = side?.Trim().ToLowerInvariant();
        if (normalized is not ("buy" or "sell")) throw Error("invalid-side", "Only buy and sell market-order intents are accepted.");
        if (quantity is < 1 or > 100_000) throw Error("invalid-quantity", "Quantity must be between 1 and 100,000 whole shares.");
        return normalized;
    }

    private void ValidateAi(GlobalAiOrderRequest request)
    {
        if (request.Rationale is null || string.IsNullOrWhiteSpace(request.Rationale.Thesis) ||
            request.Rationale.Thesis.Trim().Length > 2_000 || request.Rationale.Confidence is < 0m or > 1m ||
            request.Rationale.Evidence is null || request.Rationale.Evidence.Count == 0 || request.Rationale.Evidence.Count > 20 ||
            request.Rationale.Evidence.Any(item => item.Url is not { Length: <= 2_048 } ||
                !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || item.PublishedAt == default || item.PublishedAt > clock.GetUtcNow() ||
                string.IsNullOrWhiteSpace(item.ExactExcerpt) ||
                item.ExactExcerpt.Length > 2_000 || !ValidSha(item.ContentSha256)))
            throw Error("invalid-ai-rationale", "AI orders require bounded thesis, confidence, and structured HTTPS evidence.");
        if (!ContestContract.Agents.Any(agent => string.Equals(agent.ModelId, request.ModelId, StringComparison.Ordinal)))
            throw Error("invalid-model", "AI model identity must be one fixed trusted competitor.");
    }

    private static string NormalizePrincipal(string value) => string.IsNullOrWhiteSpace(value)
        ? throw Error("invalid-principal", "Authenticated principal is required.") : value.Trim().ToLowerInvariant();

    private static void ValidateKey(string key)
    {
        if (key is not { Length: >= 8 and <= 128 } || key.Any(character => character < '!' || character > '~'))
            throw Error("invalid-idempotency-key", "Idempotency key must be 8-128 visible ASCII characters.");
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HumanAlias(string principal) => $"Human {Hash(principal)[..8]}";
    private static bool ValidSha(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static GlobalRaceException Error(string code, string message) => new(code, message);

    private const string OrderSelect = """
        SELECT o.id,o.race_id,o.participant_id,o.actor_type,o.instrument_id,i.symbol,o.side,o.quantity,
          CASE WHEN EXISTS(SELECT 1 FROM v2_order_lifecycle_events e WHERE e.order_id=o.id AND e.event_type='cancelled')
               THEN 'cancelled' ELSE o.status END,
          o.note,o.request_hash,o.submitted_at,o.trusted_model_id,o.idempotency_key,o.evidence_json,
          CASE WHEN o.rationale_json IS NULL THEN NULL ELSE o.rationale_json::text END
        FROM v2_orders o JOIN v2_instruments i ON i.id=o.instrument_id
        """;
}
