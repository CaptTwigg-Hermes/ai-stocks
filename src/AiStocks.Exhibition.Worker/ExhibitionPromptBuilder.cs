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
            !progress.RootElement.TryGetProperty("executionMode", out var executionMode) || executionMode.GetString() != ExhibitionDataContract.ExecutionMode ||
            !progress.RootElement.TryGetProperty("isNonLive", out var isNonLive) || isNonLive.ValueKind != JsonValueKind.True ||
            !progress.RootElement.TryGetProperty("strictContest", out var strictContest) || strictContest.ValueKind != JsonValueKind.False ||
            !progress.RootElement.TryGetProperty("holdOnly", out var holdOnly) || holdOnly.ValueKind != JsonValueKind.False ||
            !progress.RootElement.TryGetProperty("assumedFills", out var assumedFills) || assumedFills.ValueKind != JsonValueKind.True ||
            !progress.RootElement.TryGetProperty("assumedSekToDkk", out var assumedSekToDkk) ||
            !assumedSekToDkk.TryGetDecimal(out var fx) || fx != ExhibitionDataContract.AssumedSekToDkk ||
            !progress.RootElement.TryGetProperty("assumedSlippagePercent", out var assumedSlippage) ||
            !assumedSlippage.TryGetDecimal(out var slippage) || slippage != ExhibitionDataContract.AssumedSlippagePercent)
            throw new InvalidOperationException("AI progress response must contain the exact assumed-fill exhibition contract.");
        var participants = agents.EnumerateArray().ToArray();
        if (participants.Any(item =>
                item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("portfolio", out var participantPortfolio) ||
                participantPortfolio.ValueKind != JsonValueKind.Object ||
                !participantPortfolio.TryGetProperty("dataMode", out var portfolioDataMode) ||
                portfolioDataMode.ValueKind != JsonValueKind.String ||
                portfolioDataMode.GetString() != ExhibitionDataContract.DataMode ||
                !participantPortfolio.TryGetProperty("executionMode", out var portfolioExecutionMode) ||
                portfolioExecutionMode.ValueKind != JsonValueKind.String ||
                portfolioExecutionMode.GetString() != ExhibitionDataContract.ExecutionMode))
            throw new InvalidOperationException("Every AI portfolio must use the exact delayed-data and assumed-fill execution modes.");
        var matches = participants.Where(item =>
            item.TryGetProperty("agentId", out var id) && id.ValueKind == JsonValueKind.String &&
            StringComparer.Ordinal.Equals(id.GetString(), agent.Id.ToString("D"))).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("portfolio", out var portfolio))
            throw new InvalidOperationException("AI progress must contain exactly one portfolio for the requested fixed agent.");

        return $$"""
            OFFICIAL NASDAQ XSTO DELAYED-DATA ASSUMED-FILL EXHIBITION. Inputs are official Nasdaq XSTO observations, at least 15-minute delayed and non-live. This is a separate assumed-fill paper exhibition, not the strict contest. No brokerage or real orders exist; no real money is used.
            You are fixed agent {{agent.Id:D}} using exact model {{agent.ModelId}} via provider copilot. No fallback or substitution is allowed.
            Immutable idempotent run ID: {{runId}}
            You may research only public HTTPS web sources. Before deciding, you MUST use web_search to investigate at least three currently observed issuers (or every issuer when fewer than three are shown). Search for issuer news, results, guidance, corporate actions, and material announcements. Do not claim that research is unavailable without first calling web_search. Do not seek rival state. The only portfolio context below is your own.
            Paper execution assumptions: fixed 0.65 DKK/SEK conversion and 1% adverse slippage. A buy order may cost at most 10,000 DKK after assumed FX/slippage. A marked position may be at most 25,000 DKK after the buy.

            OFFICIAL DELAYED INSTRUMENT OBSERVATIONS (eligible only for assumed paper fills):
            {{instrumentItems.GetRawText()}}

            YOUR OWN PORTFOLIO:
            {{portfolio.GetRawText()}}

            Return exactly one JSON object and no markdown or commentary. It must have exactly these properties and types:
            {"agentId":"exact fixed GUID","modelId":"exact fixed model ID","action":"buy|sell|hold","instrumentId":"current instrument ID or null","quantity":1,"reason":"non-empty string","confidence":0.0,"evidence":[{"url":"absolute HTTPS URL","publishedAt":"offset-bearing ISO-8601 timestamp","exactExcerpt":"exact visible source text"}]}
            Rules: action must be exactly lower-case buy, sell, or hold. BUY/SELL must use a current instrument ID from the observations, a whole positive quantity, and at least one independently verifiable HTTPS evidence item published no later than that selected observation's availableAt. HOLD requires null instrumentId and quantity 0 and may use an empty evidence array. confidence is 0..1. Every supplied evidence claim will be independently fetched and verified; fabrication rejects the decision. Prefer a truthful hold with [] over invented, inaccessible, or unverifiable evidence.
            Example shape only (replace identity and values with the required real values):
            {"agentId":"{{agent.Id:D}}","modelId":"{{agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"No independently verified delayed-data opportunity","confidence":0.5,"evidence":[]}
            """;
    }
}

internal static class ExhibitionDataContract
{
    internal const string DataMode = "official-nasdaq-xsto-15m-delayed";
    internal const string ExecutionMode = "assumed-delayed-paper-fills-v1";
    internal const string Source = "Nasdaq Nordic MiFID II delayed post-trade";
    internal const decimal AssumedSekToDkk = 0.65m;
    internal const decimal AssumedSlippagePercent = 1m;
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
