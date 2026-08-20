using AiStocks.Collector;

namespace AiStocks.Persistence.Tests;

/// <summary>
/// The collector reports readiness only when a poll succeeded recently.
/// That freshness window must be wide enough for a poll to actually
/// finish: a real warmup cycle downloads and persists a full session and
/// takes several minutes, so a fixed 2-minute window can never be
/// satisfied and the container stays permanently unhealthy even though
/// collection is succeeding.
/// </summary>
public sealed class CollectorReadinessWindowTests
{
    [Fact]
    public void Window_is_derived_from_the_configured_poll_interval()
    {
        var window = PostgresCollectorReadiness.StalenessWindow(pollSeconds: 15);

        Assert.True(
            window >= TimeSpan.FromMinutes(10),
            $"a 15s poll cadence must tolerate slow cycles, got {window}");
    }

    [Fact]
    public void Window_scales_with_a_slower_poll_interval()
    {
        var fast = PostgresCollectorReadiness.StalenessWindow(pollSeconds: 15);
        var slow = PostgresCollectorReadiness.StalenessWindow(pollSeconds: 60);

        Assert.True(slow > fast, "a slower cadence must widen the window");
    }

    [Fact]
    public void Window_never_collapses_below_a_full_cycle()
    {
        foreach (var seconds in new[] { 5, 15, 30, 60 })
        {
            var window = PostgresCollectorReadiness.StalenessWindow(seconds);
            Assert.True(
                window > TimeSpan.FromSeconds(seconds * 4),
                $"window {window} is too tight for a {seconds}s cadence");
        }
    }
}
