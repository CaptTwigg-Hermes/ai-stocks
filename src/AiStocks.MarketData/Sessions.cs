using System.Globalization;
using System.Text.RegularExpressions;

namespace AiStocks.MarketData;

public static partial class NasdaqReportName
{
    [GeneratedRegex("^NordicEquity-posttrade-(2026-\\d{2}-\\d{2}T\\d{4})$")]
    private static partial Regex Pattern();

    public static DateTimeOffset ParseTimestamp(string report)
    {
        var match = Pattern().Match(report);
        if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd'T'HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new MarketDataException("Invalid Nasdaq report name");
        var day = DateOnly.FromDateTime(local);
        return StockholmCalendar.Local(day, local.Hour, local.Minute);
    }

    public static void Validate(string report) => _ = ParseTimestamp(report);
}

public static class SessionManifest
{
    public static IReadOnlyList<string> ExpectedReports(TradingSession session)
    {
        var names = new List<string>();
        for (var cursor = session.Open.AddMinutes(15); cursor <= session.Close.AddMinutes(15); cursor = cursor.AddMinutes(1))
            names.Add($"NordicEquity-posttrade-{cursor:yyyy-MM-dd'T'HHmm}");
        return names;
    }

    public static void ValidateComplete(TradingSession session, IReadOnlyDictionary<string, string> reports)
    {
        var expected = ExpectedReports(session);
        if (reports.Count != expected.Count || expected.Any(x => !reports.TryGetValue(x, out var hash) ||
            hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new MarketDataException("Session manifest is incomplete or invalid");
    }
}

public sealed record SessionTradedValue(DateOnly Session, decimal TradedValue, bool Complete, string ManifestChecksum = "legacy");

public static class AverageDailyValue
{
    public static decimal Calculate20(IEnumerable<SessionTradedValue> sessions)
    {
        var ordered = sessions.OrderBy(x => x.Session).ToArray();
        if (ordered.Length != 20 || ordered.Any(x => !x.Complete || x.TradedValue < 0 || string.IsNullOrWhiteSpace(x.ManifestChecksum)) ||
            ordered.Select(x => x.Session).Distinct().Count() != 20 || ordered.Any(x => StockholmCalendar.GetSession(x.Session) is null))
            throw new MarketDataException("ADV requires exactly 20 manifest-bound complete XSTO sessions");
        var expected = new List<DateOnly>();
        for (var day = ordered[^1].Session; expected.Count < 20; day = day.AddDays(-1))
            if (StockholmCalendar.GetSession(day) is not null) expected.Add(day);
        expected.Reverse();
        if (!ordered.Select(x => x.Session).SequenceEqual(expected))
            throw new MarketDataException("ADV sessions must be consecutive expected XSTO sessions");
        return ordered.Sum(x => x.TradedValue) / 20m;
    }
}
