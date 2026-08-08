using System.Xml;
using System.Xml.Linq;

namespace AiStocks.MarketData;

public sealed record FirdsInstrument(string Isin, string IssuerId, string Name, string Cfi, string Currency, string Venue,
    DateOnly? FirstTradeDate, DateOnly? TerminationDate);

public sealed class FirdsUniverseParser
{
    public IReadOnlyList<FirdsInstrument> ParseFull(Stream xml, DateOnly effectiveAt) =>
        Read(xml).Where(x => x.Operation is not Operation.Delete && IsEligible(x.Instrument, effectiveAt))
            .Select(x => x.Instrument).GroupBy(x => x.Isin, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderBy(x => x.Isin, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<FirdsInstrument> ApplyDelta(IEnumerable<FirdsInstrument> current, Stream deltaXml, DateOnly effectiveAt)
    {
        var result = current.ToDictionary(x => x.Isin, StringComparer.Ordinal);
        foreach (var change in Read(deltaXml))
        {
            if (change.Operation == Operation.Delete || !IsEligible(change.Instrument, effectiveAt)) result.Remove(change.Instrument.Isin);
            else result[change.Instrument.Isin] = change.Instrument;
        }
        return result.Values.OrderBy(x => x.Isin, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<Change> Read(Stream xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, MaxCharactersInDocument = 1_000_000_000 };
        using var reader = XmlReader.Create(xml, settings);
        var found = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName is not ("NewRcrd" or "ModfdRcrd" or "TermntdRcrd")) continue;
            found = true;
            var operation = reader.LocalName == "TermntdRcrd" ? Operation.Delete : Operation.Upsert;
            using var subtree = reader.ReadSubtree();
            var element = XElement.Load(subtree, LoadOptions.None);
            yield return new(operation, Parse(element));
        }
        if (!found) throw new MarketDataException("FIRDS file contains no instrument records");
    }

    private static FirdsInstrument Parse(XElement record)
    {
        string Value(string name) => record.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
        DateOnly? Date(string name)
        {
            var value = Value(name);
            return DateOnly.TryParse(value, out var result) ? result : null;
        }
        var general = record.Descendants().FirstOrDefault(x => x.Name.LocalName == "FinInstrmGnlAttrbts")
            ?? throw new MarketDataException("FIRDS general attributes are missing");
        string General(string name) => general.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
        var venueAttributes = record.Descendants().FirstOrDefault(x => x.Name.LocalName == "TradgVnRltdAttrbts")
            ?? throw new MarketDataException("FIRDS venue attributes are missing");
        var venue = venueAttributes.Elements().FirstOrDefault(x => x.Name.LocalName == "Id")?.Value.Trim() ?? string.Empty;
        var item = new FirdsInstrument(General("Id"), Value("Issr"), General("FullNm"), General("ClssfctnTp"), General("NtnlCcy"), venue,
            Date("FrstTradDt"), Date("TermntnDt"));
        if (item.Isin.Length != 12 || item.Cfi.Length != 6 || item.Venue.Length != 4)
            throw new MarketDataException("FIRDS identity is malformed");
        return item;
    }

    private static bool IsEligible(FirdsInstrument item, DateOnly at) =>
        item.IssuerId.Length == 20 && item.Venue == "XSTO" && item.Currency == "SEK" &&
        item.Cfi.StartsWith("ES", StringComparison.Ordinal) &&
        (item.FirstTradeDate is null || item.FirstTradeDate <= at) && (item.TerminationDate is null || item.TerminationDate > at);

    private enum Operation { Upsert, Delete }
    private sealed record Change(Operation Operation, FirdsInstrument Instrument);
}
