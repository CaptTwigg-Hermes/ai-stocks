using System.Text.Json;
using AiStocks.Persistence;
using Npgsql;

namespace AiStocks.Web;

public sealed class PostgresDashboardFacade(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IDashboardFacade
{
    public async Task<DashboardSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (status, paused) = await StateAsync(connection, cancellationToken).ConfigureAwait(false);
            var portfolios = await PortfoliosAsync(connection, cancellationToken).ConfigureAwait(false);
            var ordered = portfolios.OrderByDescending(row => row.ValueSek).ThenBy(row => row.ModelId, StringComparer.Ordinal).ToArray();
            var leaderboard = ordered.Select(row => new LeaderboardRow(row.ModelId,
                Array.FindIndex(ordered, candidate => candidate.ValueSek == row.ValueSek) + 1,
                row.ValueSek, row.ValueSek / 30000m * 100m - 100m, [])).ToArray();
            return new DashboardSnapshot(status, paused, timeProvider.GetUtcNow(), leaderboard, portfolios,
                await QueuedAsync(connection, cancellationToken).ConfigureAwait(false),
                await EvidenceAsync(connection, cancellationToken).ConfigureAwait(false),
                await FeesAsync(connection, cancellationToken).ConfigureAwait(false),
                await DividendsAsync(connection, cancellationToken).ConfigureAwait(false),
                await FailuresAsync(connection, cancellationToken).ConfigureAwait(false),
                await AuditAsync(connection, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is NpgsqlException or InvalidCastException or JsonException)
        {
            throw new DashboardUnavailableException("PostgreSQL dashboard query failed closed.", exception);
        }
    }

    public async Task<ContestControlResult> ControlAsync(ContestControlCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ActorEmail) || string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128)
            throw new ContestControlRejectedException("Control identity is invalid.");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = command.Action.ToString(),
            actor = command.ActorEmail.Trim().ToLowerInvariant(),
            idempotencyKey = command.IdempotencyKey
        }));
        var json = CanonicalJson.Serialize(document.RootElement);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (command.Action == ContestControlAction.PreStartReset)
            {
                await using var reset = new NpgsqlCommand("SELECT prestart_reset($1,$2,$3,$4::jsonb,canonical_jsonb_sha256($4::jsonb),$5)", connection);
                reset.Parameters.AddWithValue(Guid.NewGuid());
                reset.Parameters.AddWithValue(command.ActorEmail);
                reset.Parameters.AddWithValue(command.IdempotencyKey);
                reset.Parameters.AddWithValue(json);
                reset.Parameters.AddWithValue(timeProvider.GetUtcNow());
                await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (from, to, reason) = command.Action switch
                {
                    ContestControlAction.Start => ("DRAFT", "RUNNING", "owner start"),
                    ContestControlAction.Pause => ("RUNNING", "PAUSED", "owner pause"),
                    ContestControlAction.Resume => ("PAUSED", "RUNNING", "owner resume"),
                    _ => throw new ContestControlRejectedException("Unsupported control action.")
                };
                await using var transition = new NpgsqlCommand("SELECT transition_contest($1,$2::contest_status,$3::contest_status,$4,$5,$6,$7::jsonb,canonical_jsonb_sha256($7::jsonb),$8)", connection);
                transition.Parameters.AddWithValue(Guid.NewGuid());
                transition.Parameters.AddWithValue(from);
                transition.Parameters.AddWithValue(to);
                transition.Parameters.AddWithValue(reason);
                transition.Parameters.AddWithValue(command.ActorEmail);
                transition.Parameters.AddWithValue(command.IdempotencyKey);
                transition.Parameters.AddWithValue(json);
                transition.Parameters.AddWithValue(timeProvider.GetUtcNow());
                await transition.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var state = await StateAsync(connection, cancellationToken).ConfigureAwait(false);
            return new ContestControlResult(state.Status, state.Paused);
        }
        catch (PostgresException exception)
        {
            throw new ContestControlRejectedException(exception.MessageText.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                ? "Control idempotency conflict." : "Control rejected by contest lifecycle.");
        }
        catch (NpgsqlException)
        {
            throw new ContestControlRejectedException("Control persistence is unavailable.");
        }
    }

    private static async Task<(string Status, bool Paused)> StateAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT lower(status::text),status::text='PAUSED' FROM contest_state WHERE singleton", connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Contest state is absent.");
        return (reader.GetString(0), reader.GetBoolean(1));
    }

    private static async Task<IReadOnlyList<PortfolioRow>> PortfoliosAsync(NpgsqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT a.model_id,b.cash,i.isin::text,i.symbol,p.quantity,
                   COALESCE((SELECT mo.price FROM market_observations mo WHERE mo.instrument_id=p.instrument_id AND mo.verified ORDER BY mo.traded_at DESC LIMIT 1),0)
            FROM agents a JOIN account_balances b ON b.agent_id=a.id
            LEFT JOIN positions p ON p.agent_id=a.id AND p.quantity>0
            LEFT JOIN instruments i ON i.id=p.instrument_id ORDER BY a.model_id,i.isin
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var values = new Dictionary<string, (decimal Cash, List<HoldingRow> Holdings)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var model = reader.GetString(0);
            if (!values.TryGetValue(model, out var value)) value = (reader.GetDecimal(1), []);
            if (!reader.IsDBNull(2))
            {
                var quantity = checked((int)reader.GetInt64(4));
                var price = reader.GetDecimal(5);
                value.Holdings.Add(new HoldingRow(reader.GetString(2).Trim(), reader.GetString(3), quantity, price, price * quantity));
            }
            values[model] = value;
        }
        return values.Select(item => new PortfolioRow(item.Key, item.Value.Cash,
            item.Value.Cash + item.Value.Holdings.Sum(x => x.ValueSek), item.Value.Holdings)).OrderBy(x => x.ModelId, StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyList<QueuedOrderRow>> QueuedAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT o.id::text,a.model_id,o.side::text,i.symbol,o.quantity,o.created_at FROM orders o
            JOIN agents a ON a.id=o.agent_id JOIN instruments i ON i.id=o.instrument_id
            WHERE NOT EXISTS(SELECT FROM order_outcomes x WHERE x.order_id=o.id) ORDER BY o.created_at,o.id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<QueuedOrderRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2).ToLowerInvariant(), reader.GetString(3), checked((int)reader.GetInt64(4)), reader.GetFieldValue<DateTimeOffset>(5)));
        return rows;
    }

    private static async Task<IReadOnlyList<EvidenceRow>> EvidenceAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT o.id::text,a.model_id,i.symbol,o.request_json->>'catalyst',e->>'url',(e->>'publishedAt')::timestamptz,o.decision_at
            FROM orders o JOIN agents a ON a.id=o.agent_id JOIN instruments i ON i.id=o.instrument_id
            CROSS JOIN LATERAL jsonb_array_elements(COALESCE(o.request_json->'evidence','[]'::jsonb)) e ORDER BY o.decision_at DESC
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<EvidenceRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), new Uri(reader.GetString(4)), reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6)));
        return rows;
    }

    private static async Task<IReadOnlyList<FeeRow>> FeesAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT f.id::text,a.model_id,f.fee,b.fee_tier::text,f.executed_at FROM fills f JOIN agents a ON a.id=f.agent_id JOIN account_balances b ON b.agent_id=f.agent_id WHERE f.fee>0 ORDER BY f.executed_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<FeeRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3).ToLowerInvariant(), reader.GetFieldValue<DateTimeOffset>(4)));
        return rows;
    }

    private static async Task<IReadOnlyList<DividendRow>> DividendsAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT l.id::text,a.model_id,i.symbol,l.cash_delta,l.occurred_at FROM ledger_events l JOIN agents a ON a.id=l.agent_id JOIN instruments i ON i.id=l.instrument_id WHERE l.event_type='DIVIDEND' ORDER BY l.occurred_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<DividendRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return rows;
    }

    private static async Task<IReadOnlyList<FailureRow>> FailuresAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT r.id::text,a.model_id,r.status::text,COALESCE(r.last_error,'unknown'),COALESCE(r.completed_at,r.next_attempt_at) FROM scheduled_agent_runs r JOIN agents a ON a.id=r.agent_id WHERE r.status IN ('FAILED','MISSED') OR r.last_error IS NOT NULL ORDER BY 5 DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<FailureRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2).ToLowerInvariant(), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return rows;
    }

    private static async Task<IReadOnlyList<AuditRow>> AuditAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT id::text,actor,lower(to_status::text),reason,occurred_at FROM contest_state_events ORDER BY occurred_at DESC", connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return rows;
    }
}
