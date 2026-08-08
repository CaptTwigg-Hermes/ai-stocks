using AiStocks.Web;

namespace AiStocks.Web.Tests;

internal static class DashboardFixtures
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
    public static DashboardSnapshot Snapshot { get; } = new(
        "running", false, Now,
        [new("gpt-5.6-sol", 1, 31_200m, 4m, [new(Now.AddDays(-1), 30_000m), new(Now, 31_200m)])],
        [new("gpt-5.6-sol", 2_000m, 31_200m, [new("SE0000000001", "ACME", 10, 2_920m, 29_200m)])],
        [new("order-1", "gpt-5.6-sol", "BUY", "ACME", 2, Now)],
        [new("evidence-1", "gpt-5.6-sol", "ACME", "Catalyst", new Uri("https://example.com/report"), Now, Now)],
        [new("fee-1", "gpt-5.6-sol", 1m, "Mini", Now)],
        [new("dividend-1", "gpt-5.6-sol", "ACME", 25m, Now)],
        [new("failure-1", "gpt-5.6-sol", "MARKET_DATA", "Missing quote", Now)],
        [new("audit-1", "owner@example.com", "PAUSE", "technical", Now)]);
}