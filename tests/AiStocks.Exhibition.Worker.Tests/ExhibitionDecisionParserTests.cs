using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionDecisionParserTests
{
    private static readonly AgentDefinition Agent = ContestContract.Agents[0];
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> Observations =
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["SE0000115446"] = DateTimeOffset.Parse("2026-08-16T10:15:00Z")
        };

    [Theory]
    [InlineData("buy", ExhibitionAction.Buy)]
    [InlineData("sell", ExhibitionAction.Sell)]
    public void Parse_AcceptsTradeForCurrentInstrumentWithPositiveWholeQuantityAndEvidence(
        string action, ExhibitionAction expected)
    {
        var json = $$"""
            {"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"{{action}}","instrumentId":"SE0000115446","quantity":2,"reason":"Assumed-fill paper decision","confidence":0.75,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-16T10:00:00Z","exactExcerpt":"Exact public text"}]}
            """;

        var parsed = new ExhibitionDecisionParser().Parse(
            json, Agent, Observations);

        Assert.Equal(expected, parsed.Action);
        Assert.Equal("SE0000115446", parsed.InstrumentId);
        Assert.Equal(2, parsed.Quantity);
        Assert.Single(parsed.Evidence);
    }

    [Fact]
    public void Parse_RejectsDuplicateKeysAndIdentityMismatch()
    {
        var duplicate = $$"""{"agentId":"{{Agent.Id:D}}","agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";
        var mismatch = $$"""{"agentId":"{{ContestContract.Agents[1].Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";
        var parser = new ExhibitionDecisionParser();
        var fixtures = Observations;

        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(duplicate, Agent, fixtures));
        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(mismatch, Agent, fixtures));
    }

    [Fact]
    public void Parse_RejectsTradeOutsideFixtureAndInvalidHoldSemantics()
    {
        var unknownTrade = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"sell","instrumentId":"UNKNOWN","quantity":1,"reason":"sell","confidence":0.5,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-16T10:00:00Z","exactExcerpt":"text"}]}""";
        var invalidHold = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":"SE0000115446","quantity":1,"reason":"wait","confidence":0.5,"evidence":[]}""";
        var parser = new ExhibitionDecisionParser();
        var fixtures = Observations;

        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(unknownTrade, Agent, fixtures));
        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(invalidHold, Agent, fixtures));
    }

    [Theory]
    [InlineData("buy", null, 1, true)]
    [InlineData("buy", "SE0000115446", 0, true)]
    [InlineData("sell", "UNKNOWN", 1, true)]
    [InlineData("sell", "SE0000115446", 1, false)]
    public void Parse_RejectsMalformedBuyAndSellVectors(
        string action, string? instrumentId, int quantity, bool includeEvidence)
    {
        var instrument = instrumentId is null ? "null" : "\"" + instrumentId + "\"";
        var evidence = includeEvidence
            ? "[{\"url\":\"https://example.com/news\",\"publishedAt\":\"2026-08-16T10:00:00Z\",\"exactExcerpt\":\"text\"}]"
            : "[]";
        var json = "{\"agentId\":\"" + Agent.Id.ToString("D") + "\",\"modelId\":\"" + Agent.ModelId +
            "\",\"action\":\"" + action + "\",\"instrumentId\":" + instrument + ",\"quantity\":" + quantity +
            ",\"reason\":\"trade\",\"confidence\":0.5,\"evidence\":" + evidence + "}";

        Assert.Throws<ExhibitionDecisionException>(() =>
            new ExhibitionDecisionParser().Parse(json, Agent, Observations));
    }

    [Theory]
    [InlineData("BUY")]
    [InlineData("Sell")]
    [InlineData("wait")]
    public void Parse_RejectsActionsOutsideExactLowerCaseSchema(string action)
    {
        var json = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"{{action}}","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";

        Assert.Throws<ExhibitionDecisionException>(() =>
            new ExhibitionDecisionParser().Parse(json, Agent, Observations));
    }

    [Fact]
    public void Parse_RejectsTradeEvidencePublishedAfterSelectedObservationWasAvailable()
    {
        var overload = typeof(ExhibitionDecisionParser).GetMethod(nameof(ExhibitionDecisionParser.Parse),
            [typeof(string), typeof(AgentDefinition), typeof(IReadOnlyDictionary<string, DateTimeOffset>)]);
        Assert.NotNull(overload);
        var json = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"buy","instrumentId":"SE0000115446","quantity":1,"reason":"late evidence","confidence":0.5,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-16T10:15:01Z","exactExcerpt":"text"}]}""";
        var observations = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)
        {
            ["SE0000115446"] = DateTimeOffset.Parse("2026-08-16T10:15:00Z")
        };

        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            overload.Invoke(new ExhibitionDecisionParser(), [json, Agent, observations]));

        Assert.IsType<ExhibitionDecisionException>(exception.InnerException);
    }

    [Fact]
    public void Parse_AcceptsTruthfulHoldWithoutFabricatedEvidence()
    {
        var hold = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";

        var parsed = new ExhibitionDecisionParser().Parse(
            hold, Agent, Observations);

        Assert.Equal(ExhibitionAction.Hold, parsed.Action);
        Assert.Empty(parsed.Evidence);
    }
}
