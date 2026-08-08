using AiStocks.MarketData;

namespace AiStocks.Collector;

public sealed class CollectorWorker(
    MarketReferenceAcquirer referenceAcquirer,
    NasdaqCollector collector,
    PostgresCollectorPersistence persistence,
    CollectorHealth health,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<CollectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("COLLECTOR_POLL_SECONDS", 15);
        if (seconds is < 5 or > 60) throw new InvalidOperationException("CollectorPollSeconds must be between 5 and 60");
        var poll = TimeSpan.FromSeconds(seconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            try
            {
                await persistence.PollStartedAsync(now, stoppingToken).ConfigureAwait(false);
                await referenceAcquirer.AcquireAsync(now, stoppingToken).ConfigureAwait(false);
                var result = await collector.CollectOnceAsync(now, stoppingToken).ConfigureAwait(false);
                if (result.Missing.Count > 0) throw new MarketDataException($"Session incomplete: {result.Missing.Count} reports missing");
                await persistence.PersistAsync(result, now, stoppingToken).ConfigureAwait(false);
                health.RecordSuccess(now);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                try { await persistence.PollFailedAsync(now, exception, stoppingToken).ConfigureAwait(false); }
                catch (Exception durableException) { logger.LogError(durableException, "Failed to persist collector failure state"); }
                health.RecordFailure(exception, now);
                logger.LogError(exception, "Nasdaq collector poll failed closed");
            }
            await Task.Delay(poll, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
