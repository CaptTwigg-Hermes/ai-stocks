using AiStocks.MarketData;

namespace AiStocks.Collector;

public sealed class CollectorWorker(
    NasdaqCollector collector,
    CollectorHealth health,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<CollectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("CollectorPollSeconds", 15);
        if (seconds is < 5 or > 60) throw new InvalidOperationException("CollectorPollSeconds must be between 5 and 60");
        var poll = TimeSpan.FromSeconds(seconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            try
            {
                var result = await collector.CollectOnceAsync(now, stoppingToken).ConfigureAwait(false);
                if (result.Missing.Count > 0) throw new MarketDataException($"Session incomplete: {result.Missing.Count} reports missing");
                health.RecordSuccess(now);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                health.RecordFailure(exception, now);
                logger.LogError(exception, "Nasdaq collector poll failed closed");
            }
            await Task.Delay(poll, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
