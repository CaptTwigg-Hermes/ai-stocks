using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionDecisionParserTests
{
    private static readonly AgentDefinition Agent = ContestContract.Agents[0];

    [Fact]
    public void Parse_RejectsNonHoldDecisionEvenForKnownInstrument()
    {
        var json = $$"""
            {"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"buy","instrumentId":"SE0000115446","quantity":2,"reason":"Fixture-only paper decision","confidence":0.75,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-16T10:00:00Z","exactExcerpt":"Exact public text"}]}
            """;

        Assert.Throws<ExhibitionDecisionException>(() =>
            new ExhibitionDecisionParser().Parse(json, Agent, new HashSet<string>(StringComparer.Ordinal) { "SE0000115446" }));
    }

    [Fact]
    public void Parse_RejectsDuplicateKeysAndIdentityMismatch()
    {
        var duplicate = $$"""{"agentId":"{{Agent.Id:D}}","agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";
        var mismatch = $$"""{"agentId":"{{ContestContract.Agents[1].Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";
        var parser = new ExhibitionDecisionParser();
        var fixtures = new HashSet<string>(StringComparer.Ordinal) { "SE0000115446" };

        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(duplicate, Agent, fixtures));
        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(mismatch, Agent, fixtures));
    }

    [Fact]
    public void Parse_RejectsTradeOutsideFixtureAndInvalidHoldSemantics()
    {
        var unknownTrade = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"sell","instrumentId":"UNKNOWN","quantity":1,"reason":"sell","confidence":0.5,"evidence":[{"url":"https://example.com/news","publishedAt":"2026-08-16T10:00:00Z","exactExcerpt":"text"}]}""";
        var invalidHold = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":"SE0000115446","quantity":1,"reason":"wait","confidence":0.5,"evidence":[]}""";
        var parser = new ExhibitionDecisionParser();
        var fixtures = new HashSet<string>(StringComparer.Ordinal) { "SE0000115446" };

        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(unknownTrade, Agent, fixtures));
        Assert.Throws<ExhibitionDecisionException>(() => parser.Parse(invalidHold, Agent, fixtures));
    }

    [Fact]
    public void Parse_AcceptsTruthfulHoldWithoutFabricatedEvidence()
    {
        var hold = $$"""{"agentId":"{{Agent.Id:D}}","modelId":"{{Agent.ModelId}}","action":"hold","instrumentId":null,"quantity":0,"reason":"wait","confidence":0.5,"evidence":[]}""";

        var parsed = new ExhibitionDecisionParser().Parse(
            hold, Agent, new HashSet<string>(StringComparer.Ordinal) { "SE0000115446" });

        Assert.Equal(ExhibitionAction.Hold, parsed.Action);
        Assert.Empty(parsed.Evidence);
    }
}
