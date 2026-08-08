using AiStocks.Core;
using AiStocks.Research.Decisions;

namespace AiStocks.Research.Tests;

public sealed class DecisionJsonParserTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly StrictDecisionJsonParser _parser = new();

    [Fact]
    public void Parse_AcceptsCompleteBoundedDecisionAndPreservesIdentity()
    {
        var decision = _parser.Parse(ValidJson(), AgentId, "gpt-5.6-sol");

        Assert.Equal("decision-1", decision.DecisionId);
        Assert.Equal(AgentId, decision.AgentId);
        Assert.Equal("gpt-5.6-sol", decision.ModelId);
        Assert.Equal(DecisionAction.Buy, decision.Action);
        Assert.Equal("SE0000000001", decision.Instrument!.Isin);
        Assert.Equal(10, decision.Quantity);
        Assert.Equal(123.45m, decision.ObservedPrice);
        Assert.Equal(0.75m, decision.Confidence);
        Assert.Single(decision.Evidence);
        Assert.Equal("Exact catalyst text", decision.Evidence[0].ExactExcerpt);
    }

    [Fact]
    public void Parse_RejectsDuplicateKeysAtAnyDepth()
    {
        var duplicateTop = ValidJson().Replace("\"decisionId\":\"decision-1\"", "\"decisionId\":\"decision-1\",\"decisionId\":\"other\"");
        var duplicateNested = ValidJson().Replace("\"isin\":\"SE0000000001\"", "\"isin\":\"SE0000000001\",\"isin\":\"SE0000000002\"");

        Assert.Throws<DecisionValidationException>(() => _parser.Parse(duplicateTop, AgentId, "gpt-5.6-sol"));
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(duplicateNested, AgentId, "gpt-5.6-sol"));
    }

    [Theory]
    [InlineData("\"confidence\":0.75", "\"confidence\":-0.01")]
    [InlineData("\"confidence\":0.75", "\"confidence\":1.01")]
    [InlineData("\"quantity\":10", "\"quantity\":0")]
    [InlineData("\"observedPrice\":123.45", "\"observedPrice\":0")]
    [InlineData("\"risks\":[\"Currency risk\"]", "\"risks\":[]")]
    [InlineData("\"evidence\":[{", "\"evidence\":[] , \"ignored\":[{")]
    public void Parse_RejectsOutOfRangeOrMissingTradeEvidence(string original, string replacement)
    {
        var json = ValidJson().Replace(original, replacement);
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(json, AgentId, "gpt-5.6-sol"));
    }

    [Fact]
    public void Parse_RejectsWrongAgentOrModelIdentity()
    {
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson().Replace(AgentId.ToString(), Guid.NewGuid().ToString()), AgentId, "gpt-5.6-sol"));
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson().Replace("gpt-5.6-sol", "claude-opus-4.8"), AgentId, "gpt-5.6-sol"));
    }

    [Fact]
    public void Parse_RejectsUnknownFieldsTrailingJsonAndOversizedInput()
    {
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson().Replace("\"decisionId\"", "\"unknown\":1,\"decisionId\""), AgentId, "gpt-5.6-sol"));
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson() + "{}", AgentId, "gpt-5.6-sol"));
        var parser = new StrictDecisionJsonParser(new DecisionJsonLimits { MaximumJsonBytes = 10 });
        Assert.Throws<DecisionValidationException>(() => parser.Parse(ValidJson(), AgentId, "gpt-5.6-sol"));
    }

    [Fact]
    public void Parse_RejectsInvalidTimestampsHashesUrisAndExcessiveFields()
    {
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson().Replace("2026-08-08T10:00:00Z", "2026-08-08"), AgentId, "gpt-5.6-sol"));
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson().Replace(new string('a', 64), "abc"), AgentId, "gpt-5.6-sol"));
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(ValidJson().Replace("https://example.com/news", "http://example.com/news"), AgentId, "gpt-5.6-sol"));
        var parser = new StrictDecisionJsonParser(new DecisionJsonLimits { MaximumReasonCharacters = 3 });
        Assert.Throws<DecisionValidationException>(() => parser.Parse(ValidJson(), AgentId, "gpt-5.6-sol"));
    }

    [Theory]
    [InlineData("https://singlelabel/news")]
    [InlineData("https://example.com:8443/news")]
    [InlineData("https://127.0.0.1/news")]
    [InlineData("https://example.com/news#fragment")]
    public void Parse_RejectsEvidenceUrlsThatCannotBePublicPinnedHttps(string url)
    {
        var json = ValidJson().Replace("https://example.com/news", url);
        Assert.Throws<DecisionValidationException>(() => _parser.Parse(json, AgentId, "gpt-5.6-sol"));
    }

    [Fact]
    public void Parse_CancelPendingRequiresExplicitOrderIdentity()
    {
        var pendingOrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var json = ValidJson()
            .Replace("\"action\":\"buy\"", "\"action\":\"cancelPending\"")
            .Replace("\"instrument\":{\"isin\":\"SE0000000001\",\"orderBookId\":\"123\",\"mic\":\"XSTO\"}", "\"instrument\":null")
            .Replace("\"quantity\":10", "\"quantity\":0")
            .Replace("\"observedPrice\":123.45", "\"observedPrice\":null")
            .Replace("\"pendingOrderId\":null", "\"pendingOrderId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"")
            .Replace("\"evidence\":[{\"url\":\"https://example.com/news\",\"publishedAt\":\"2026-08-08T09:00:00Z\",\"exactExcerpt\":\"Exact catalyst text\"}]", "\"evidence\":[]");

        var decision = _parser.Parse(json, AgentId, "gpt-5.6-sol");

        Assert.Equal(DecisionAction.CancelPending, decision.Action);
        Assert.Equal(pendingOrderId, decision.PendingOrderId);
    }

    [Fact]
    public void Parse_AllowsHoldWithoutInstrumentEvidenceOrPositiveQuantity()
    {
        var json = ValidJson()
            .Replace("\"action\":\"buy\"", "\"action\":\"hold\"")
            .Replace("\"instrument\":{\"isin\":\"SE0000000001\",\"orderBookId\":\"123\",\"mic\":\"XSTO\"}", "\"instrument\":null")
            .Replace("\"quantity\":10", "\"quantity\":0")
            .Replace("\"observedPrice\":123.45", "\"observedPrice\":null")
            .Replace("\"evidence\":[{\"url\":\"https://example.com/news\",\"publishedAt\":\"2026-08-08T09:00:00Z\",\"exactExcerpt\":\"Exact catalyst text\"}]", "\"evidence\":[]");

        Assert.Equal(DecisionAction.Hold, _parser.Parse(json, AgentId, "gpt-5.6-sol").Action);
    }

    private static string ValidJson() => $$"""
    {
      "decisionId":"decision-1",
      "agentId":"{{AgentId}}",
      "modelId":"gpt-5.6-sol",
      "action":"buy",
      "instrument":{"isin":"SE0000000001","orderBookId":"123","mic":"XSTO"},
      "quantity":10,
      "decisionAt":"2026-08-08T10:00:00Z",
      "observedPrice":123.45,
      "pendingOrderId":null,
      "reason":"Strong verified setup",
      "catalyst":"Published contract win",
      "risks":["Currency risk"],
      "confidence":0.75,
      "evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-08T09:00:00Z","exactExcerpt":"Exact catalyst text"}],
      "canonicalRequestSha256":"{{new string('a', 64)}}"
    }
    """;
}
