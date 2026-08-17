using System.Text.Json;
using AiStocks.Core;

namespace AiStocks.Exhibition.Worker;

public static class ExhibitionPromptBuilder
{
    public static string Build(AgentDefinition agent, string runId, string instrumentsJson, string progressJson)
    {
        if (!ContestContract.IsExactAgent(agent.Id, agent.ModelId)) throw new InvalidOperationException("Unknown contest agent.");
        using var instruments = StrictJson.Parse(instrumentsJson, 2 * 1024 * 1024);
        if (instruments.RootElement.ValueKind != JsonValueKind.Object ||
            !instruments.RootElement.TryGetProperty("items", out var instrumentItems) || instrumentItems.ValueKind != JsonValueKind.Array ||
            !instruments.RootElement.TryGetProperty("dataMode", out var dataMode) || dataMode.GetString() != ExhibitionDataContract.DataMode)
            throw new InvalidOperationException("Instrument response must contain official delayed Nasdaq XSTO data.");
        using var progress = StrictJson.Parse(progressJson, 2 * 1024 * 1024);
        if (progress.RootElement.ValueKind != JsonValueKind.Object ||
            !progress.RootElement.TryGetProperty("participants", out var agents) || agents.ValueKind != JsonValueKind.Array ||
            !progress.RootElement.TryGetProperty("dataMode", out var progressDataMode) || progressDataMode.GetString() != ExhibitionDataContract.DataMode ||
            !progress.RootElement.TryGetProperty("isNonLive", out var isNonLive) || !isNonLive.GetBoolean() ||
            !progress.RootElement.TryGetProperty("strictContest", out var strictContest) || strictContest.GetBoolean() ||
            !progress.RootElement.TryGetProperty("holdOnly", out var holdOnly) || !holdOnly.GetBoolean())
            throw new InvalidOperationException("AI progress response must contain an exact HOLD-only delayed-data snapshot.");
        var participants = agents.EnumerateArray().ToArray();
        if (participants.Any(item =>
                item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("portfolio", out var participantPortfolio) ||
                participantPortfolio.ValueKind != JsonValueKind.Object ||
                !participantPortfolio.TryGetProperty("dataMode", out var portfolioDataMode) ||
                portfolioDataMode.ValueKind != JsonValueKind.String ||
                portfolioDataMode.GetString() != ExhibitionDataContract.DataMode))
            throw new InvalidOperationException("Every AI portfolio must use the exact delayed-data mode.");
        var matches = participants.Where(item =>
            item.TryGetProperty("agentId", out var id) && id.ValueKind == JsonValueKind.String &&
            StringComparer.Ordinal.Equals(id.GetString(), agent.Id.ToString("D"))).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("portfolio", out var portfolio))
            throw new InvalidOperationException("AI progress must contain exactly one portfolio for the requested fixed agent.");

        return $$"""
            OFFICIAL NASDAQ XSTO DELAYED-DATA EXHIBITION. Data is official Nasdaq XSTO, at least 15-minute delayed, non-live, paper-only, and HOLD-only. No brokerage, real orders, or real money.
            You are fixed agent {{agent.Id:D}} using exact model {{agent.ModelId}} via provider copilot. No fallback or substitution is allowed.
            Immutable idempotent run ID: {{runId}}
            You may research only public HTTPS web sources. Do not seek rival state. The only portfolio context below is your own.

            OFFICIAL DELAYED INSTRUMENT OBSERVATIONS (context only; they are not tradable):
            {{instrumentItems.GetRawText()}}

            YOUR OWN PORTFOLIO:
            {{portfolio.GetRawText()}}

            Return exactly one JSON object and no markdown or commentary. It must have exactly these properties and types:
            {"agentId":"exact fixed GUID","modelId":"exact fixed model ID","action":"hold","instrumentId":null,"quantity":0,"reason":"non-empty string","confidence":0.0,"evidence":[{"url":"absolute HTTPS URL","publishedAt":"offset-bearing ISO-8601 timestamp","exactExcerpt":"exact visible source text"}]}
            Rules: action must be hold, instrumentId must be null, and quantity must be 0. Any other action is rejected before API submission. hold may use an empty evidence array when no source can be independently verified. confidence is 0..1. Every supplied evidence claim will be independently fetched and verified; fabrication rejects the decision. Prefer a truthful hold with [] over invented, inaccessible, or unverifiable evidence.
            Example shape only (replace identity and values with the required real values):
            {"agentId":"{{agent.Id:D}}","modelId":"{{agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"No independently verified delayed-data opportunity","confidence":0.5,"evidence":[]}
            """;
    }
}

internal static class ExhibitionDataContract
{
    internal const string DataMode = "official-nasdaq-xsto-15m-delayed";
    internal const string Source = "Nasdaq Nordic MiFID II delayed post-trade";
}

internal static class StrictJson
{
    internal static JsonDocument Parse(string json, int maximumBytes)
    {
        if (json is null || System.Text.Encoding.UTF8.GetByteCount(json) is 0 || System.Text.Encoding.UTF8.GetByteCount(json) > maximumBytes)
            throw new InvalidOperationException("JSON response is empty or oversized.");
        var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        RejectDuplicates(document.RootElement);
        return document;
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException($"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
