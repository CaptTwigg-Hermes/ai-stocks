using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiStocks.Core;

namespace AiStocks.Research.Decisions;

public sealed record DecisionJsonLimits
{
    public int MaximumJsonBytes { get; init; } = 256 * 1024;
    public int MaximumDecisionIdCharacters { get; init; } = 128;
    public int MaximumReasonCharacters { get; init; } = 8_000;
    public int MaximumCatalystCharacters { get; init; } = 4_000;
    public int MaximumRiskCharacters { get; init; } = 2_000;
    public int MaximumRisks { get; init; } = 20;
    public int MaximumEvidenceItems { get; init; } = 20;
    public int MaximumExcerptCharacters { get; init; } = 4_000;
    public int MaximumQuantity { get; init; } = 10_000_000;
    public decimal MaximumObservedPrice { get; init; } = 100_000_000m;
    public int MaximumDepth { get; init; } = 16;
}

public sealed record EvidenceClaim(Uri Url, DateTimeOffset PublishedAt, string ExactExcerpt);

public sealed record ResearchDecisionDraft(
    string DecisionId,
    Guid AgentId,
    string ModelId,
    DecisionAction Action,
    InstrumentId? Instrument,
    int Quantity,
    DateTimeOffset DecisionAt,
    decimal? ObservedPrice,
    string Reason,
    string Catalyst,
    IReadOnlyList<string> Risks,
    decimal Confidence,
    IReadOnlyList<EvidenceClaim> Evidence,
    string CanonicalRequestSha256);

public sealed class DecisionValidationException : Exception
{
    public DecisionValidationException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed partial class StrictDecisionJsonParser
{
    private static readonly HashSet<string> RootProperties =
    [
        "decisionId", "agentId", "modelId", "action", "instrument", "quantity", "decisionAt",
        "observedPrice", "reason", "catalyst", "risks", "confidence", "evidence", "canonicalRequestSha256"
    ];
    private static readonly HashSet<string> InstrumentProperties = ["isin", "orderBookId", "mic"];
    private static readonly HashSet<string> EvidenceProperties = ["url", "publishedAt", "exactExcerpt"];
    private readonly DecisionJsonLimits _limits;

    public StrictDecisionJsonParser(DecisionJsonLimits? limits = null)
    {
        _limits = limits ?? new DecisionJsonLimits();
        ValidateLimits(_limits);
    }

    public ResearchDecisionDraft Parse(string json, Guid expectedAgentId, string expectedModelId)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        byte[] utf8;
        try { utf8 = new UTF8Encoding(false, true).GetBytes(json); }
        catch (EncoderFallbackException exception) { throw new DecisionValidationException("Decision JSON is not valid UTF-8.", exception); }
        if (utf8.Length == 0 || utf8.Length > _limits.MaximumJsonBytes)
            throw new DecisionValidationException("Decision JSON exceeds its configured byte bound or is empty.");

        try
        {
            RejectDuplicateKeysAndTrailingValues(utf8);
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = _limits.MaximumDepth
            });
            var root = document.RootElement;
            RequireObjectShape(root, RootProperties, "decision");

            var decisionId = RequiredBoundedString(root, "decisionId", 1, _limits.MaximumDecisionIdCharacters);
            var agentId = ParseGuid(RequiredString(root, "agentId"), "agentId");
            var modelId = RequiredBoundedString(root, "modelId", 1, 128);
            if (agentId != expectedAgentId || !StringComparer.Ordinal.Equals(modelId, expectedModelId))
                throw new DecisionValidationException("Decision agent/model identity does not match the invocation identity.");

            var action = ParseAction(RequiredString(root, "action"));
            var instrument = ParseInstrument(root.GetProperty("instrument"));
            var quantity = RequiredInt32(root, "quantity");
            var decisionAt = ParseTimestamp(RequiredString(root, "decisionAt"), "decisionAt");
            var observedPrice = OptionalDecimal(root.GetProperty("observedPrice"), "observedPrice");
            var reason = RequiredBoundedString(root, "reason", 1, _limits.MaximumReasonCharacters);
            var catalyst = RequiredBoundedString(root, "catalyst", 1, _limits.MaximumCatalystCharacters);
            var risks = ParseRisks(root.GetProperty("risks"));
            var confidence = RequiredDecimal(root, "confidence");
            if (confidence is < 0m or > 1m) throw new DecisionValidationException("confidence must be between 0 and 1 inclusive.");
            var evidence = ParseEvidence(root.GetProperty("evidence"));
            var requestHash = RequiredString(root, "canonicalRequestSha256");
            if (!Sha256Regex().IsMatch(requestHash)) throw new DecisionValidationException("canonicalRequestSha256 must be 64 lowercase hexadecimal characters.");

            ValidateActionFields(action, instrument, quantity, observedPrice, risks, evidence);
            return new ResearchDecisionDraft(decisionId, agentId, modelId, action, instrument, quantity,
                decisionAt, observedPrice, reason, catalyst, risks.AsReadOnly(), confidence, evidence.AsReadOnly(), requestHash);
        }
        catch (DecisionValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new DecisionValidationException("Decision JSON is malformed or has an invalid field type.", exception);
        }
    }

    private void RejectDuplicateKeysAndTrailingValues(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = _limits.MaximumDepth
        });
        var objectKeys = new Stack<HashSet<string>>();
        var rootValues = 0;
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && reader.CurrentDepth == 0)
                rootValues++;
            if (reader.TokenType == JsonTokenType.StartObject)
                objectKeys.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                objectKeys.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;
                if (objectKeys.Count == 0 || !objectKeys.Peek().Add(name))
                    throw new DecisionValidationException($"Duplicate JSON property '{name}' is forbidden.");
            }
        }
        if (rootValues != 1 || reader.BytesConsumed != utf8.Length)
            throw new DecisionValidationException("Decision JSON must contain exactly one root value and no trailing data.");
    }

    private InstrumentId? ParseInstrument(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        RequireObjectShape(element, InstrumentProperties, "instrument");
        var isin = RequiredBoundedString(element, "isin", 12, 12);
        if (!IsinRegex().IsMatch(isin)) throw new DecisionValidationException("instrument.isin is invalid.");
        var orderBook = RequiredBoundedString(element, "orderBookId", 1, 64);
        var mic = RequiredBoundedString(element, "mic", 4, 4);
        if (!StringComparer.Ordinal.Equals(mic, "XSTO")) throw new DecisionValidationException("Only XSTO instruments are accepted.");
        return new InstrumentId(isin, orderBook, mic);
    }

    private List<string> ParseRisks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new DecisionValidationException("risks must be an array.");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (values.Count >= _limits.MaximumRisks) throw new DecisionValidationException("risks exceeds its item bound.");
            values.Add(BoundedString(item, "risk", 1, _limits.MaximumRiskCharacters));
        }
        return values;
    }

    private List<EvidenceClaim> ParseEvidence(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new DecisionValidationException("evidence must be an array.");
        var values = new List<EvidenceClaim>();
        foreach (var item in element.EnumerateArray())
        {
            if (values.Count >= _limits.MaximumEvidenceItems) throw new DecisionValidationException("evidence exceeds its item bound.");
            RequireObjectShape(item, EvidenceProperties, "evidence item");
            var urlText = RequiredBoundedString(item, "url", 1, 2_048);
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || !StringComparer.OrdinalIgnoreCase.Equals(url.Scheme, Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo) || url.Port != 443 ||
                !string.IsNullOrEmpty(url.Fragment) || url.Host.Contains('*', StringComparison.Ordinal) ||
                (url.HostNameType == UriHostNameType.Dns && !url.Host.Contains('.', StringComparison.Ordinal)) ||
                (IPAddress.TryParse(url.Host, out var literal) && !Evidence.EvidenceVerifier.IsPublicAddress(literal)))
                throw new DecisionValidationException("Evidence URL must be an absolute HTTPS URL without user information.");
            var publishedAt = ParseTimestamp(RequiredString(item, "publishedAt"), "evidence.publishedAt");
            var excerpt = RequiredBoundedString(item, "exactExcerpt", 1, _limits.MaximumExcerptCharacters);
            values.Add(new EvidenceClaim(url, publishedAt, excerpt));
        }
        return values;
    }

    private static void ValidateActionFields(DecisionAction action, InstrumentId? instrument, int quantity,
        decimal? observedPrice, IReadOnlyCollection<string> risks, IReadOnlyCollection<EvidenceClaim> evidence)
    {
        var trade = action is DecisionAction.Buy or DecisionAction.Sell;
        if (trade && (instrument is null || quantity <= 0 || observedPrice is null or <= 0 || risks.Count == 0 || evidence.Count == 0))
            throw new DecisionValidationException("Buy/sell decisions require instrument, positive quantity/price, risks, and evidence.");
        if (!trade && (quantity != 0 || observedPrice is not null))
            throw new DecisionValidationException("Non-trade decisions require zero quantity and null observedPrice.");
    }

    private static DecisionAction ParseAction(string value) => value switch
    {
        "buy" => DecisionAction.Buy,
        "sell" => DecisionAction.Sell,
        "hold" => DecisionAction.Hold,
        "cancelPending" => DecisionAction.CancelPending,
        _ => throw new DecisionValidationException("action is not one of buy, sell, hold, cancelPending.")
    };

    private static void RequireObjectShape(JsonElement element, HashSet<string> required, string field)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new DecisionValidationException($"{field} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name)) throw new DecisionValidationException($"Unknown {field} property '{property.Name}'.");
            seen.Add(property.Name);
        }
        if (seen.Count != required.Count) throw new DecisionValidationException($"{field} is missing one or more required properties.");
    }

    private static string RequiredString(JsonElement parent, string name) => BoundedString(parent.GetProperty(name), name, 0, int.MaxValue);
    private static string RequiredBoundedString(JsonElement parent, string name, int min, int max) => BoundedString(parent.GetProperty(name), name, min, max);
    private static string BoundedString(JsonElement element, string name, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.String) throw new DecisionValidationException($"{name} must be a string.");
        var value = element.GetString()!;
        if (value.Length < min || value.Length > max || value.IndexOf('\0', StringComparison.Ordinal) >= 0)
            throw new DecisionValidationException($"{name} is outside its character bound.");
        return value;
    }

    private int RequiredInt32(JsonElement parent, string name)
    {
        if (!parent.GetProperty(name).TryGetInt32(out var value) || value < 0 || value > _limits.MaximumQuantity)
            throw new DecisionValidationException($"{name} is outside its integer range.");
        return value;
    }
    private static decimal RequiredDecimal(JsonElement parent, string name)
    {
        if (!parent.GetProperty(name).TryGetDecimal(out var value)) throw new DecisionValidationException($"{name} must be a decimal number.");
        return value;
    }
    private decimal? OptionalDecimal(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (!element.TryGetDecimal(out var value) || value <= 0 || value > _limits.MaximumObservedPrice)
            throw new DecisionValidationException($"{name} is outside its decimal range.");
        return value;
    }
    private static Guid ParseGuid(string value, string name) => Guid.TryParseExact(value, "D", out var parsed) ? parsed : throw new DecisionValidationException($"{name} must be a canonical GUID.");
    private static DateTimeOffset ParseTimestamp(string value, string name)
    {
        if (!TimestampRegex().IsMatch(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new DecisionValidationException($"{name} must be an offset-bearing ISO-8601 timestamp.");
        return parsed;
    }

    private static void ValidateLimits(DecisionJsonLimits limits)
    {
        if (limits.MaximumJsonBytes <= 0 || limits.MaximumDecisionIdCharacters <= 0 || limits.MaximumReasonCharacters <= 0 ||
            limits.MaximumCatalystCharacters <= 0 || limits.MaximumRiskCharacters <= 0 || limits.MaximumRisks <= 0 ||
            limits.MaximumEvidenceItems <= 0 || limits.MaximumExcerptCharacters <= 0 || limits.MaximumQuantity <= 0 ||
            limits.MaximumObservedPrice <= 0 || limits.MaximumDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256Regex();
    [GeneratedRegex("^[A-Z]{2}[A-Z0-9]{9}[0-9]$", RegexOptions.CultureInvariant)] private static partial Regex IsinRegex();
    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T.+(?:Z|[+-]\\d{2}:\\d{2})$", RegexOptions.CultureInvariant)] private static partial Regex TimestampRegex();
}
