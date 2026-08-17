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
    IReadOnlyList<EvidenceClaim> Evidence);

public sealed class ExhibitionDecisionParser
{
    private static readonly HashSet<string> RootNames =
        ["agentId", "modelId", "action", "instrumentId", "quantity", "reason", "confidence", "evidence"];
    private static readonly HashSet<string> EvidenceNames = ["url", "publishedAt", "exactExcerpt"];

    public ExhibitionDecision Parse(
        string json,
        AgentDefinition expectedAgent,
        IReadOnlyDictionary<string, DateTimeOffset> currentObservations)
    {
        var decision = ParseKnown(json, expectedAgent,
            currentObservations.Keys.ToHashSet(StringComparer.Ordinal));
        if (decision.Action != ExhibitionAction.Hold &&
            decision.InstrumentId is not null &&
            currentObservations.TryGetValue(decision.InstrumentId, out var availableAt) &&
            decision.Evidence.Any(item => item.PublishedAt > availableAt))
            throw Invalid("Trade evidence cannot be published after the selected observation was available.");
        return decision;
    }

    private static ExhibitionDecision ParseKnown(string json, AgentDefinition expectedAgent, IReadOnlySet<string> fixtureInstrumentIds)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = new UTF8Encoding(false, true).GetBytes(json);
        if (bytes.Length is 0 or > 256 * 1024) throw Invalid("Decision JSON is empty or oversized.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            var root = document.RootElement;
            RequireShape(root, RootNames, "decision");
            RejectDuplicates(root);
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
            return new ExhibitionDecision(agentId, expectedAgent.ModelId, action, instrument, quantity, reason, confidence, evidence);
        }
        catch (ExhibitionDecisionException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or EncoderFallbackException)
        {
            throw Invalid("Decision JSON is malformed or contains an invalid field.", exception);
        }
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
