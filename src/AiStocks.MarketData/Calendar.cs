using System.Security.Cryptography;

namespace AiStocks.MarketData;

public sealed class MarketDataException(string message, Exception? inner = null) : Exception(message, inner);

public enum SessionKind { Full, Half }

public sealed record TradingSession(DateOnly Day, DateTimeOffset Open, DateTimeOffset Close, SessionKind Kind)
{
    public bool Contains(DateTimeOffset value) => value >= Open && value <= Close;
}

public static class StockholmCalendar
{
    public const string HolidaySha256 = "867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395";
    public const string TradingHoursSha256 = "f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395";
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private static readonly HashSet<DateOnly> Closed =
    [
        new(2026, 1, 1), new(2026, 1, 6), new(2026, 4, 3), new(2026, 4, 6),
        new(2026, 5, 1), new(2026, 5, 14), new(2026, 6, 19), new(2026, 12, 24),
        new(2026, 12, 25), new(2026, 12, 31)
    ];
    private static readonly HashSet<DateOnly> HalfDays =
    [
        new(2026, 1, 5), new(2026, 4, 2), new(2026, 4, 30), new(2026, 5, 13), new(2026, 10, 30)
    ];

    public static void VerifyPinnedArtifacts(string repositoryRoot)
    {
        Verify(Path.Combine(repositoryRoot, "docs", "nasdaq-holiday-schedule-2026.xlsx"), HolidaySha256);
        Verify(Path.Combine(repositoryRoot, "docs", "nasdaq-trading-hours.html"), TradingHoursSha256);
    }

    public static TradingSession? GetSession(DateOnly day)
    {
        if (day.Year != 2026 || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || Closed.Contains(day)) return null;
        var kind = HalfDays.Contains(day) ? SessionKind.Half : SessionKind.Full;
        return new(day, Local(day, 9, 0), Local(day, kind == SessionKind.Half ? 13 : 17, kind == SessionKind.Half ? 0 : 30), kind);
    }

    public static IReadOnlyList<DateTimeOffset> SixRunTimes(TradingSession session)
    {
        var duration = session.Close - session.Open;
        return new[]
        {
            session.Open.AddHours(-1), session.Open + duration / 5, session.Open + duration * 2 / 5,
            session.Open + duration * 3 / 5, session.Open + duration * 4 / 5, session.Close.AddMinutes(30)
        };
    }

    public static TradingSession FinalSession2026()
    {
        for (var day = new DateOnly(2026, 12, 31); day.Year == 2026; day = day.AddDays(-1))
            if (GetSession(day) is { } session) return session;
        throw new MarketDataException("No final XSTO session exists");
    }

    public static DateTimeOffset Local(DateOnly day, int hour, int minute)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local) || Zone.IsAmbiguousTime(local)) throw new MarketDataException("Ambiguous Stockholm timestamp");
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    private static void Verify(string path, string expected)
    {
        if (!File.Exists(path)) throw new MarketDataException($"Missing pinned artifact: {Path.GetFileName(path)}");
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
            throw new MarketDataException($"Pinned artifact checksum mismatch: {Path.GetFileName(path)}");
    }
}
