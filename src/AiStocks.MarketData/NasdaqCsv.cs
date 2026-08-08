using System.Globalization;
using System.Text;

namespace AiStocks.MarketData;

public sealed record NasdaqTrade(
    DateTimeOffset ExecutedAt,
    string Isin,
    decimal Price,
    string Currency,
    string PriceNotation,
    long Quantity,
    string Venue,
    DateTimeOffset PublishedAt,
    string TransactionId,
    string Flags,
    DateTimeOffset FetchedAt);

public static class NasdaqCsvParser
{
    private static readonly string[] Required =
    [
        "Trading date and time", "Instrument identification code", "Price", "Price currency",
        "Price notation", "Quantity", "Venue of execution", "Publication date and time",
        "Transaction identification code", "Flags"
    ];

    public static IReadOnlyList<NasdaqTrade> Parse(byte[] bytes, DateTimeOffset fetchedAt)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new MarketDataException("Nasdaq CSV is not UTF-8", exception); }
        text = text.TrimStart('\uFEFF');
        var records = ParseRecords(text);
        if (records.Count < 2 || records[0].Count != 1 || !string.Equals(records[0][0], "sep=;", StringComparison.OrdinalIgnoreCase))
            throw new MarketDataException("Nasdaq separator declaration is invalid");
        var header = records[1];
        if (header.Count != header.Distinct(StringComparer.Ordinal).Count() || Required.Any(x => !header.Contains(x, StringComparer.Ordinal)))
            throw new MarketDataException("Nasdaq CSV schema is invalid");
        var index = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);
        var result = new List<NasdaqTrade>();
        foreach (var fields in records.Skip(2))
        {
            if (fields.Count == 1 && fields[0].Length == 0) continue;
            if (fields.Count != header.Count) throw new MarketDataException("Nasdaq CSV row width is invalid");
            try
            {
                var executed = ParseTime(fields[index["Trading date and time"]]);
                var published = ParseTime(fields[index["Publication date and time"]]);
                var delay = published - executed;
                var price = decimal.Parse(fields[index["Price"]], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                var quantity = long.Parse(fields[index["Quantity"]], NumberStyles.None, CultureInfo.InvariantCulture);
                var id = fields[index["Transaction identification code"]].Trim();
                if (price <= 0 || quantity <= 0 || id.Length == 0 || delay < TimeSpan.FromMinutes(15) || delay > TimeSpan.FromMinutes(20) || fetchedAt < published)
                    throw new MarketDataException("Nasdaq trade row violates value or delay constraints");
                result.Add(new(executed, fields[index["Instrument identification code"]], price,
                    fields[index["Price currency"]], fields[index["Price notation"]], quantity,
                    fields[index["Venue of execution"]], published, id, fields[index["Flags"]], fetchedAt));
            }
            catch (MarketDataException) { throw; }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            { throw new MarketDataException("Nasdaq trade row is malformed", exception); }
        }
        return result;
    }

    private static DateTimeOffset ParseTime(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var result) ||
            !(value.EndsWith('Z') || value.LastIndexOf('+') > 9 || value.LastIndexOf('-') > 9))
            throw new MarketDataException("Nasdaq timestamp must contain an offset");
        return result;
    }

    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == ';') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear(); records.Add(row); row = [];
            }
            else field.Append(c);
        }
        if (quoted) throw new MarketDataException("Nasdaq CSV contains an unterminated quote");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); records.Add(row); }
        return records;
    }
}

public static class NasdaqTradeSelection
{
    public static NasdaqTrade FirstEligible(IEnumerable<NasdaqTrade> rows, string isin, DateTimeOffset decisionAt, TradingSession session, NasdaqStatusMachine statuses)
    {
        if (!statuses.IsEligible(isin)) throw new MarketDataException("Instrument status is unknown or ineligible");
        return FirstEligible(rows, isin, decisionAt, session);
    }

    public static NasdaqTrade FirstEligible(IEnumerable<NasdaqTrade> rows, string isin, DateTimeOffset decisionAt, TradingSession session)
    {
        return rows.Where(x => x.Isin == isin && x.Venue == "XSTO" && x.Currency == "SEK" && x.PriceNotation == "MONE" &&
                x.ExecutedAt >= decisionAt && session.Contains(x.ExecutedAt))
            .OrderBy(x => x.ExecutedAt).ThenBy(x => x.TransactionId, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new MarketDataException("No eligible post-decision XSTO trade");
    }

    public static decimal ClosingAuctionPrice(IEnumerable<NasdaqTrade> rows, string isin, TradingSession session)
    {
        var prices = rows.Where(x => x.Isin == isin && x.Venue == "XSTO" && x.Currency == "SEK" && x.PriceNotation == "MONE" &&
                session.Contains(x.ExecutedAt) && x.Flags.Split(',', ' ', ';').Contains("PATS", StringComparer.Ordinal))
            .Select(x => x.Price).Distinct().ToArray();
        return prices.Length == 1 ? prices[0] : throw new MarketDataException("Closing auction PATS price is missing or ambiguous");
    }
}
