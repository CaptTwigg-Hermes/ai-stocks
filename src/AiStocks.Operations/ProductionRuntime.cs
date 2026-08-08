using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiStocks.Core;
using Npgsql;

namespace AiStocks.Operations;

public sealed class PostgresContestOperations(NpgsqlDataSource dataSource)
{
    private static readonly DateTimeOffset FinalizationAt = new(2026, 12, 30, 16, 50, 0, TimeSpan.Zero);

    public async Task ApplyDueCorporateActionsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT action.id,agent.id,action.effective_at
            FROM corporate_actions action CROSS JOIN agents agent
            WHERE action.effective_at<=$1
              AND NOT EXISTS (SELECT FROM corporate_action_applications applied
                              WHERE applied.corporate_action_id=action.id AND applied.agent_id=agent.id)
            ORDER BY action.effective_at,action.id,agent.id
            """, connection);
        command.Parameters.AddWithValue(now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var pending = new List<(Guid ActionId, Guid AgentId, DateTimeOffset EffectiveAt)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            pending.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2)));
        await reader.DisposeAsync().ConfigureAwait(false);
        foreach (var item in pending)
        {
            await using var apply = new NpgsqlCommand("SELECT apply_corporate_action($1,$2,$3,$4,$5)", connection);
            apply.Parameters.AddWithValue(item.ActionId);
            apply.Parameters.AddWithValue(item.AgentId);
            apply.Parameters.AddWithValue(Guid.NewGuid());
            apply.Parameters.AddWithValue(Guid.NewGuid());
            apply.Parameters.AddWithValue(now < item.EffectiveAt ? item.EffectiveAt : now);
            await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> FinalizeIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now < FinalizationAt) return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var state = new NpgsqlCommand("SELECT status::text FROM contest_state WHERE singleton", connection))
            if (StringComparer.Ordinal.Equals((string?)await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "FINISHED")) return false;
        const string request = "{\"session_id\":\"XSTO-2026-12-30\"}";
        await using var command = new NpgsqlCommand("""
            SELECT finalize_contest('XSTO-2026-12-30-final',$1,'XSTO-2026-12-30-final',
              $2::jsonb,canonical_jsonb_sha256($2::jsonb),'2026-12-30T16:50:00Z')
            """, connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(request);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

public sealed class PostgresDailyReportPublisher(
    NpgsqlDataSource dataSource,
    DailyReportService reports,
    AuditedDiscordDelivery delivery)
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    public async Task<bool> PublishIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var local = TimeZoneInfo.ConvertTime(now, Stockholm);
        var day = DateOnly.FromDateTime(local.DateTime);
        if (local.TimeOfDay < new TimeSpan(18, 30, 0)) return false;
        var generatedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(18, 30)), Stockholm.GetUtcOffset(day.ToDateTime(new TimeOnly(18, 30))));
        var key = $"daily:{day:yyyy-MM-dd}";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var eligible = new NpgsqlCommand("""
            SELECT EXISTS(SELECT FROM trading_sessions WHERE session_day=$1)
               AND NOT EXISTS(SELECT FROM delivery_reservations WHERE delivery_key=$2 AND status='SUCCEEDED')
            """, connection))
        {
            eligible.Parameters.AddWithValue(day);
            eligible.Parameters.AddWithValue(key);
            if (await eligible.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) return false;
        }

        var snapshots = await LoadSnapshotsAsync(connection, day, generatedAt, cancellationToken).ConfigureAwait(false);
        var report = reports.Generate(day, generatedAt, snapshots);
        await using (var persist = new NpgsqlCommand("SELECT persist_daily_report($1,$2,$3,$4,$5::sha256_hex)", connection))
        {
            persist.Parameters.AddWithValue(report.Key);
            persist.Parameters.AddWithValue(day);
            persist.Parameters.AddWithValue(generatedAt.ToUniversalTime());
            persist.Parameters.AddWithValue(report.Message);
            persist.Parameters.AddWithValue(report.ContentHash);
            await persist.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await delivery.DeliverAsync(report.Key, report.ContentHash, report.Message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<IReadOnlyList<DailyAgentSnapshot>> LoadSnapshotsAsync(
        NpgsqlConnection connection, DateOnly day, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        var startLocal = day.ToDateTime(TimeOnly.MinValue);
        var start = new DateTimeOffset(startLocal, Stockholm.GetUtcOffset(startLocal));
        await using var command = new NpgsqlCommand("""
            SELECT a.id,a.model_id,b.cash,
              b.cash+COALESCE((SELECT sum(p.quantity*mark.price) FROM positions p
                JOIN LATERAL (SELECT mo.price FROM market_observations mo
                  WHERE mo.instrument_id=p.instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
                    AND mo.retrieved_at<=$2 ORDER BY mo.traded_at DESC,mo.id DESC LIMIT 1) mark ON true
                WHERE p.agent_id=a.id AND p.quantity>0 AND NOT p.frozen),0) AS net_value,
              COALESCE((SELECT string_agg(i.symbol||':'||p.quantity,', ' ORDER BY i.symbol)
                FROM positions p JOIN instruments i ON i.id=p.instrument_id
                WHERE p.agent_id=a.id AND p.quantity>0),'cash only') AS holdings,
              COALESCE((SELECT string_agg(e.event_type,', ' ORDER BY e.occurred_at,e.id)
                FROM ledger_events e WHERE e.agent_id=a.id AND e.occurred_at>=$1 AND e.occurred_at<=$2
                  AND e.event_type IN ('BUY_FILL','SELL_FILL','FINAL_LIQUIDATION')),'none') AS trades,
              COALESCE((SELECT sum(e.fee) FROM ledger_events e WHERE e.agent_id=a.id
                AND e.occurred_at>=$1 AND e.occurred_at<=$2),0) AS fees,
              (SELECT count(*) FROM scheduled_agent_runs r WHERE r.agent_id=a.id AND r.status='MISSED'
                AND r.scheduled_at>=$1 AND r.scheduled_at<=$2) AS missed,
              COALESCE((SELECT NULLIF(o.request_json->>'reason','') FROM orders o WHERE o.agent_id=a.id
                AND o.decision_at<=$2 ORDER BY o.decision_at DESC,o.id DESC LIMIT 1),'no trade rationale') AS rationale
            FROM agents a JOIN account_balances b ON b.agent_id=a.id ORDER BY a.id
            """, connection);
        command.Parameters.AddWithValue(start.ToUniversalTime());
        command.Parameters.AddWithValue(generatedAt.ToUniversalTime());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<DailyAgentSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var net = reader.GetDecimal(3);
            var total = decimal.Round((net / ContestContract.InitialCash - 1m) * 100m, 2, MidpointRounding.AwayFromZero);
            rows.Add(new DailyAgentSnapshot(reader.GetGuid(0), reader.GetString(1), net, total, total,
                reader.GetDecimal(2), reader.GetString(4), reader.GetString(5), reader.GetDecimal(6),
                checked((int)reader.GetInt64(7)), reader.GetString(8)));
        }
        if (rows.Count != ContestContract.Agents.Count) throw new OperationsException("Report snapshot is incomplete.");
        return rows;
    }
}

public sealed partial class HermesDiscordPort : IDiscordPort
{
    private const int MaximumMessageLength = 6000;
    private readonly string executable;
    private readonly string target;

    public HermesDiscordPort(string executable, string target)
    {
        this.executable = Path.GetFullPath(string.IsNullOrWhiteSpace(executable) ? throw new OperationsException("Hermes executable is required.") : executable);
        var validatedTarget = target ?? string.Empty;
        this.target = TargetPattern().IsMatch(validatedTarget) ? validatedTarget : throw new OperationsException("Discord target must be numeric.");
    }

    public async Task<string> SendAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > MaximumMessageLength)
            throw new OperationsException("Discord message length is invalid.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "send", "--to", target, "--json", "--file", "-" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new OperationsException("Hermes delivery process could not start.");
        await process.StandardInput.WriteAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new OperationsException("Hermes delivery timed out.");
        }
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || output.Length > 64_000) throw new OperationsException("Hermes delivery failed safely.");
        return ParseReceipt(output);
    }

    public static string ParseReceipt(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
                !document.RootElement.TryGetProperty("message_id", out var messageId) || messageId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(messageId.GetString()))
                throw new OperationsException("Hermes delivery did not return a durable external receipt.");
            return messageId.GetString()!;
        }
        catch (JsonException exception) { throw new OperationsException("Hermes delivery receipt is invalid.", exception); }
    }

    [GeneratedRegex("^discord:[0-9]+(?::[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();
}

public sealed class OperationsRuntimeService(
    PostgresContestOperations contest,
    PostgresDailyReportPublisher reports,
    TimeProvider timeProvider,
    TimeSpan pollInterval)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (pollInterval < TimeSpan.FromSeconds(1) || pollInterval > TimeSpan.FromMinutes(5))
            throw new OperationsException("Operations poll interval is invalid.");
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            await contest.ApplyDueCorporateActionsAsync(now, cancellationToken).ConfigureAwait(false);
            await contest.FinalizeIfDueAsync(now, cancellationToken).ConfigureAwait(false);
            await reports.PublishIfDueAsync(now, cancellationToken).ConfigureAwait(false);
            await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
