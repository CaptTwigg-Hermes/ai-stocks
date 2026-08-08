using AiStocks.Core;

namespace AiStocks.Core.Tests;

public sealed class DomainContractTests
{
    [Theory]
    [InlineData("1.004", "1.00")]
    [InlineData("1.005", "1.01")]
    [InlineData("-1.005", "-1.01")]
    [InlineData("999999.999", "1000000.00")]
    public void Money_rounds_to_ore_away_from_zero(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected), Money.Round(decimal.Parse(input)));

    [Theory]
    [InlineData("0", "0")]
    [InlineData("0.0004", "0.02")]
    [InlineData("0.01", "0.1")]
    [InlineData("0.25", "0.5")]
    [InlineData("1", "1")]
    [InlineData("2", "1.4142135623730950488016887242")]
    public void Decimal_sqrt_is_deterministic(string input, string expected) =>
        Assert.Equal(decimal.Parse(expected), DecimalMath.Sqrt(decimal.Parse(input)));

    [Fact]
    public void Decimal_sqrt_rejects_negative_values() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DecimalMath.Sqrt(-0.01m));

    [Fact]
    public void Contest_has_exactly_the_four_fixed_isolated_agents()
    {
        Assert.Equal(4, ContestContract.Agents.Count);
        Assert.Equal(
            ["gpt-5.6-sol", "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview"],
            ContestContract.Agents.Select(agent => agent.ModelId));
        Assert.Equal(4, ContestContract.Agents.Select(agent => agent.Id).Distinct().Count());
        Assert.True(ContestContract.IsExactAgent(ContestContract.Agents[0].Id, "gpt-5.6-sol"));
        Assert.False(ContestContract.IsExactAgent(ContestContract.Agents[0].Id, "claude-opus-4.8"));
        Assert.False(ContestContract.IsExactAgent(Guid.NewGuid(), "gpt-5.6-sol"));
    }
}
