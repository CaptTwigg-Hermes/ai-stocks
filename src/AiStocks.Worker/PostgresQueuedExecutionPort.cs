using AiStocks.Worker.Orchestration;
using Npgsql;

namespace AiStocks.Worker;

public sealed class PostgresQueuedExecutionPort(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IQueuedExecutionPort
{
    public async Task<IReadOnlyList<QueuedExecution>> LoadReadyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT o.id,o.decision_at,o.agent_id
            FROM orders o
            WHERE NOT EXISTS (SELECT FROM order_outcomes outcome WHERE outcome.order_id=o.id)
              AND EXISTS (
                SELECT FROM market_observations mo
                JOIN trading_sessions session ON session.session_id=mo.session_id
                WHERE mo.instrument_id=o.instrument_id AND mo.verified AND NOT mo.warning AND NOT mo.suspended
                  AND mo.traded_at>=o.decision_at AND mo.traded_at BETWEEN session.opens_at AND session.closes_at
                  AND mo.retrieved_at-mo.traded_at BETWEEN interval '15 minutes' AND interval '20 minutes'
                  AND mo.retrieved_at<=$1)
            ORDER BY o.decision_at,o.id
            """, connection);
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<QueuedExecution>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new QueuedExecution(reader.GetGuid(0).ToString("D"), reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2)));
        return result;
    }

    public async Task ExecuteAsync(QueuedExecution order, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(order.OrderId, out var orderId)) throw new InvalidOperationException("Queued order identity is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT execute_queued_order($1,$2)", connection);
        command.Parameters.AddWithValue(orderId);
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.MessageText.Contains("account", StringComparison.OrdinalIgnoreCase) ||
            exception.MessageText.Contains("negative", StringComparison.OrdinalIgnoreCase) ||
            exception.MessageText.Contains("projection", StringComparison.OrdinalIgnoreCase))
        {
            await using var alertConnection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var alert = new NpgsqlCommand(
                "SELECT enqueue_immediate_alert('AccountingInvariantViolation',$1,$2,$3)", alertConnection);
            alert.Parameters.AddWithValue("queued execution blocked by an accounting invariant");
            alert.Parameters.AddWithValue($"accounting:{orderId:D}");
            alert.Parameters.AddWithValue(timeProvider.GetUtcNow());
            await alert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
