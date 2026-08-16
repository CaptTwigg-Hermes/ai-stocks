namespace AiStocks.Exhibition.Worker;

public sealed class ExhibitionSchedulerService(
    ExhibitionCycle cycle,
    ExhibitionHealthState health,
    ExhibitionOptions options,
    TimeProvider timeProvider,
    ILogger<ExhibitionSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cycle.RunAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                health.Complete(timeProvider.GetUtcNow(), 4, exception.Message);
                logger.LogError(exception, "Exhibition cycle failed before all agents could run");
            }
            await Task.Delay(options.CycleInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
