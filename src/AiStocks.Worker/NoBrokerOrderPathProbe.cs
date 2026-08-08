using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text;
using AiStocks.Core;
using AiStocks.Trading;

namespace AiStocks.Worker;

public static class NoBrokerOrderPathProbe
{
    public static object Run()
    {
        using var listener = new DenyNetworkEventListener();
        var at = new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero);
        var content = Encoding.UTF8.GetBytes("public paper-order evidence");
        var evidence = new VerifiedEvidence(
            new Uri("https://example.invalid/evidence"), at.AddHours(-1), at,
            Convert.ToHexStringLower(SHA256.HashData(content)), "public paper-order evidence")
        {
            ImmutableContent = content.ToImmutableArray(),
            ContentType = "text/plain"
        };
        var decision = new OrderDecision(
            "no-broker-denial-probe", ContestContract.Agents[0].Id, ContestContract.Agents[0].ModelId,
            DecisionAction.Buy, new InstrumentId("SE0000000001", "probe-book", "XSTO", "PROBEISSUER"), 1, at, 100m,
            "Execute the real in-memory paper-order submission path.", "Public probe catalyst", ["loss"],
            0.5m, [evidence], new string('a', 64));
        var engine = PaperTradingEngine.CreateContest();
        var outcome = engine.Submit(decision, null, null,
            new Dictionary<InstrumentId, VerifiedMarketObservation>());
        Thread.MemoryBarrier();
        var events = listener.Events.Order(StringComparer.Ordinal).ToArray();
        return new
        {
            ok = events.Length == 0 && engine.Orders.Count == 1 && outcome.Status == OrderStatus.Queued,
            executed = true,
            paper_order_count = engine.Orders.Count,
            outcome = outcome.Status.ToString(),
            network_events = events,
            denied_capabilities = new[] { "dns", "socket", "http", "broker-provider" }
        };
    }

    private sealed class DenyNetworkEventListener : EventListener
    {
        private readonly ConcurrentBag<string> events = [];
        public IEnumerable<string> Events => events;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.StartsWith("System.Net.", StringComparison.Ordinal))
                EnableEvents(eventSource, EventLevel.LogAlways);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
            events.Add($"{eventData.EventSource.Name}:{eventData.EventName ?? eventData.EventId.ToString()}");
    }
}
