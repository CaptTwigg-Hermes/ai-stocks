using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class ExhibitionOptionsTests
{
    [Fact]
    public void Validate_RejectsShortInternalKeyAndUnsafeIntervals()
    {
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test"),
            InternalKey = "short",
            CopilotCredentialFile = "/run/secrets/copilot.json",
            HermesHomeRoot = "/dev/shm/aistocks-exhibition",
            CycleInterval = TimeSpan.FromSeconds(1),
            HttpTimeout = TimeSpan.FromHours(1)
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("32", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///tmp/search")]
    [InlineData("http://user:password@searxng:8080")]
    public void Validate_RejectsUnsafeSearchOrigins(string value)
    {
        var options = new ExhibitionOptions
        {
            ApiBaseUrl = new Uri("https://api.example.test"),
            InternalKey = new string('k', 32),
            CopilotCredentialFile = "/run/secrets/copilot.json",
            SearchBaseUrl = new Uri(value)
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
