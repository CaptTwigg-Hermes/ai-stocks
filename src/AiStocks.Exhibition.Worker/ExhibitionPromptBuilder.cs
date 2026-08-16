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
            !instruments.RootElement.TryGetProperty("dataMode", out var dataMode) || dataMode.GetString() != "preview-fixtures")
            throw new InvalidOperationException("Instrument response must be a preview-fixtures object.");
        using var progress = StrictJson.Parse(progressJson, 2 * 1024 * 1024);
        if (progress.RootElement.ValueKind != JsonValueKind.Object ||
            !progress.RootElement.TryGetProperty("participants", out var agents) || agents.ValueKind != JsonValueKind.Array ||
            !progress.RootElement.TryGetProperty("isNonLive", out var isNonLive) || !isNonLive.GetBoolean() ||
            !progress.RootElement.TryGetProperty("strictContest", out var strictContest) || strictContest.GetBoolean())
            throw new InvalidOperationException("AI progress response must contain a non-live exhibition snapshot.");
        var matches = agents.EnumerateArray().Where(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("agentId", out var id) && id.ValueKind == JsonValueKind.String &&
            StringComparer.Ordinal.Equals(id.GetString(), agent.Id.ToString("D"))).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("portfolio", out var portfolio) || portfolio.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("AI progress must contain exactly one portfolio for the requested fixed agent.");

        return $$"""
            NON-LIVE FIXTURE PAPER-TRADING EXHIBITION. This is simulation only: no live market data, brokerage, real orders, or real money.
            You are fixed agent {{agent.Id:D}} using exact model {{agent.ModelId}} via provider copilot. No fallback or substitution is allowed.
            Immutable idempotent run ID: {{runId}}
            You may research only public HTTPS web sources. Do not seek rival state. The only portfolio context below is your own.

            FIXTURE INSTRUMENTS (instrumentId values in this JSON are the only allowed trade targets):
            {{instrumentItems.GetRawText()}}

            YOUR OWN PORTFOLIO:
            {{portfolio.GetRawText()}}

            Return exactly one JSON object and no markdown or commentary. It must have exactly these properties and types:
            {"agentId":"exact fixed GUID","modelId":"exact fixed model ID","action":"buy|sell|hold","instrumentId":"fixture instrumentId or null","quantity":0,"reason":"non-empty string","confidence":0.0,"evidence":[{"url":"absolute HTTPS URL","publishedAt":"offset-bearing ISO-8601 timestamp","exactExcerpt":"exact visible source text"}]}
            Rules: buy/sell require a fixture instrumentId, whole-share quantity 1..10000000, and at least one evidence item. hold requires instrumentId null, quantity 0, and may use an empty evidence array when no source can be independently verified. confidence is 0..1. Every supplied evidence claim will be independently fetched and verified; fabrication rejects the decision. Prefer a truthful hold with [] over invented, inaccessible, or unverifiable evidence.
            Example shape only (replace identity and values with the required real values):
            {"agentId":"{{agent.Id:D}}","modelId":"{{agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"No independently verified fixture opportunity","confidence":0.5,"evidence":[]}
            """;
    }
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
