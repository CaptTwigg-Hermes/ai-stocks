using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AiStocks.Core;
using AiStocks.Research.Decisions;

namespace AiStocks.Exhibition.Worker;

public enum ExhibitionAction { Buy, Sell, Hold }

public sealed record ExhibitionDecision(
    Guid AgentId,
    string ModelId,
    ExhibitionAction Action,
    string? InstrumentId,
    int Quantity,
    string Reason,
    decimal Confidence,
    IReadOnlyList<EvidenceClaim> Evidence,
    StrategyUpdate? StrategyUpdate = null);

public sealed class ExhibitionDecisionParser
{
    private static readonly HashSet<string> RootNames =
        ["agentId", "modelId", "action", "instrumentId", "quantity", "reason", "confidence", "evidence"];
    private static readonly HashSet<string> StrategyNames =
        ["philosophy", "researchPlan", "entryRules", "exitRules", "riskRules", "activeTheses", "lessons", "journalNote"];
    private static readonly HashSet<string> ThesisNames = ["thesis", "invalidation"];
    private static readonly HashSet<string> EvidenceNames = ["url", "publishedAt", "exactExcerpt"];

    public ExhibitionDecision Parse(
        string json,
        AgentDefinition expectedAgent,
        IReadOnlyDictionary<string, DateTimeOffset> currentObservations) =>
        Parse(json, expectedAgent, currentObservations.ToDictionary(
            item => item.Key, item => new DelayedObservation(0m, item.Value), StringComparer.Ordinal));

    public ExhibitionDecision Parse(
        string json,
        AgentDefinition expectedAgent,
        IReadOnlyDictionary<string, DelayedObservation> currentObservations)
    {
        var decision = ParseKnown(json, expectedAgent,
            currentObservations.Keys.ToHashSet(StringComparer.Ordinal));
        if (decision.Action != ExhibitionAction.Hold &&
            decision.InstrumentId is not null &&
            currentObservations.TryGetValue(decision.InstrumentId, out var observation) &&
            decision.Evidence.Any(item => item.PublishedAt > observation.AvailableAt))
            throw Invalid("Trade evidence cannot be published after the selected observation was available.");
        return decision;
    }

    private static ExhibitionDecision ParseKnown(string json, AgentDefinition expectedAgent, IReadOnlySet<string> fixtureInstrumentIds)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = new UTF8Encoding(false, true).GetBytes(json);
        if (bytes.Length is 0 or > 256 * 1024) throw Invalid("Decision JSON is empty or oversized.");
        bytes = ExtractSingleDecisionObject(json, bytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            var root = document.RootElement;
            RejectDuplicates(root);
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("decision must be an object.");
            var rootProperties = root.EnumerateObject().ToArray();
            if (rootProperties.Length is not (8 or 9) ||
                rootProperties.Any(property => !RootNames.Contains(property.Name) && property.Name != "strategyUpdate") ||
                RootNames.Any(name => !root.TryGetProperty(name, out _)))
                throw Invalid("decision must contain exactly the documented properties.");
            var agentIdText = String(root, "agentId", 36);
            if (!Guid.TryParseExact(agentIdText, "D", out var agentId) || agentId != expectedAgent.Id ||
                !StringComparer.Ordinal.Equals(String(root, "modelId", 128), expectedAgent.ModelId))
                throw Invalid("Decision identity does not exactly match the fixed agent/model.");
            var actionText = String(root, "action", 8);
            var action = actionText switch
            {
                "buy" => ExhibitionAction.Buy,
                "sell" => ExhibitionAction.Sell,
                "hold" => ExhibitionAction.Hold,
                _ => throw Invalid("action must be exactly buy, sell, or hold.")
            };
            var instrument = NullableString(root.GetProperty("instrumentId"), "instrumentId", 128);
            if (!root.GetProperty("quantity").TryGetInt32(out var quantity) || quantity is < 0 or > 10_000_000)
                throw Invalid("quantity is outside its safe bound.");
            var reason = String(root, "reason", 8_000);
            if (string.IsNullOrWhiteSpace(reason)) throw Invalid("reason is required.");
            if (!root.GetProperty("confidence").TryGetDecimal(out var confidence) || confidence is < 0m or > 1m)
                throw Invalid("confidence must be between zero and one.");
            var evidence = ParseEvidence(root.GetProperty("evidence"));
            if (action == ExhibitionAction.Hold)
            {
                if (instrument is not null || quantity != 0)
                    throw Invalid("hold requires null instrumentId and zero quantity.");
            }
            else if (instrument is null || !fixtureInstrumentIds.Contains(instrument) || quantity <= 0 || evidence.Count == 0)
                throw Invalid("buy and sell require a current instrumentId, positive whole quantity, and verified evidence.");
            var strategyUpdate = root.TryGetProperty("strategyUpdate", out var strategyElement)
                ? ParseStrategyUpdate(strategyElement)
                : null;
            return new ExhibitionDecision(agentId, expectedAgent.ModelId, action, instrument, quantity, reason, confidence, evidence, strategyUpdate);
        }
        catch (ExhibitionDecisionException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or EncoderFallbackException or InvalidDataException)
        {
            throw Invalid("Decision JSON is malformed or contains an invalid field.", exception);
        }
    }

    private static StrategyUpdate ParseStrategyUpdate(JsonElement element)
    {
        RequireShape(element, StrategyNames, "strategyUpdate");
        var thesesElement = element.GetProperty("activeTheses");
        if (thesesElement.ValueKind != JsonValueKind.Array) throw Invalid("activeTheses must be an array.");
        var theses = new List<StrategyThesis>();
        foreach (var item in thesesElement.EnumerateArray())
        {
            if (theses.Count >= 8) throw Invalid("activeTheses exceeds its item bound.");
            RequireShape(item, ThesisNames, "active thesis");
            theses.Add(new StrategyThesis(String(item, "thesis", 1_000), String(item, "invalidation", 1_000)));
        }
        var update = new StrategyUpdate(
            String(element, "philosophy", 2_000),
            ParseStringList(element.GetProperty("researchPlan"), "researchPlan"),
            ParseStringList(element.GetProperty("entryRules"), "entryRules"),
            ParseStringList(element.GetProperty("exitRules"), "exitRules"),
            ParseStringList(element.GetProperty("riskRules"), "riskRules"),
            theses,
            ParseStringList(element.GetProperty("lessons"), "lessons"),
            String(element, "journalNote", 1_000));
        StrategyMemoryStore.ValidateUpdate(update);
        return update;
    }

    private static IReadOnlyList<string> ParseStringList(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Array) throw Invalid($"{name} must be an array.");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (values.Count >= 8 || item.ValueKind != JsonValueKind.String)
                throw Invalid($"{name} exceeds its item bound or contains a non-string.");
            var value = item.GetString()!;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 500 || value.Contains('\0', StringComparison.Ordinal))
                throw Invalid($"{name} contains an invalid item.");
            values.Add(value);
        }
        return values;
    }

    private static byte[] ExtractSingleDecisionObject(string response, byte[] responseBytes)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < response.Length && char.IsWhiteSpace(response[firstNonWhitespace]))
            firstNonWhitespace++;
        if (firstNonWhitespace < response.Length && response[firstNonWhitespace] == '{')
            return responseBytes;

        var objectStart = response.IndexOf('{', StringComparison.Ordinal);
        if (objectStart < 0) throw Invalid("Decision response does not contain a JSON object.");
        var prefix = response.AsSpan(0, objectStart);
        if (Encoding.UTF8.GetByteCount(prefix) > 4 * 1024 || prefix.Contains('}') ||
            prefix.Contains('\0') || prefix.Contains('`'))
            throw Invalid("Decision response has an invalid leading envelope.");
        foreach (var character in prefix)
            if (char.IsControl(character) && !char.IsWhiteSpace(character))
                throw Invalid("Decision response has an invalid leading envelope.");
        return new UTF8Encoding(false, true).GetBytes(response[objectStart..]);
    }

    private static List<EvidenceClaim> ParseEvidence(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw Invalid("evidence must be an array.");
        var result = new List<EvidenceClaim>();
        foreach (var item in element.EnumerateArray())
        {
            if (result.Count >= 20) throw Invalid("evidence exceeds its item bound.");
            RequireShape(item, EvidenceNames, "evidence item");
            var urlText = String(item, "url", 2_048);
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment) || url.Port != 443 ||
                IPAddress.TryParse(url.Host, out _))
                throw Invalid("evidence URL must be a public-host HTTPS URL without user info or fragment.");
            var publishedText = String(item, "publishedAt", 64);
            if (!DateTimeOffset.TryParseExact(publishedText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var publishedAt) &&
                !DateTimeOffset.TryParse(publishedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out publishedAt))
                throw Invalid("publishedAt must be offset-bearing ISO-8601.");
            if (!publishedText.EndsWith('Z') && !(publishedText.Length >= 6 && (publishedText[^6] is '+' or '-')))
                throw Invalid("publishedAt must include an offset.");
            var excerpt = String(item, "exactExcerpt", 4_000);
            if (string.IsNullOrWhiteSpace(excerpt)) throw Invalid("exactExcerpt is required.");
            result.Add(new EvidenceClaim(url, publishedAt, excerpt));
        }
        return result;
    }

    private static void RequireShape(JsonElement element, HashSet<string> exactNames, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid($"{name} must be an object.");
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != exactNames.Count || properties.Any(property => !exactNames.Contains(property.Name)))
            throw Invalid($"{name} must contain exactly the documented properties.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid($"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static string String(JsonElement parent, string name, int maximum) =>
        NullableString(parent.GetProperty(name), name, maximum) ?? throw Invalid($"{name} must be a string.");

    private static string? NullableString(JsonElement element, string name, int maximum)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw Invalid($"{name} must be a string or null.");
        var value = element.GetString()!;
        if (value.Length == 0 || value.Length > maximum || value.Contains('\0', StringComparison.Ordinal))
            throw Invalid($"{name} is outside its character bound.");
        return value;
    }

    private static ExhibitionDecisionException Invalid(string message, Exception? inner = null) => new(message, inner);
}

public sealed class ExhibitionDecisionException(string message, Exception? inner = null) : Exception(message, inner);
