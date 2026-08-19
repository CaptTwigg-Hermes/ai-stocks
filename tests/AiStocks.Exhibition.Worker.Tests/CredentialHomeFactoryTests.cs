using AiStocks.Core;
using AiStocks.Exhibition.Worker;

namespace AiStocks.Exhibition.Worker.Tests;

public sealed class CredentialHomeFactoryTests
{
    [Fact]
    public async Task CreateAsync_CopiesOnlyCredentialWithOwnerOnlyModeIntoDistinctEphemeralAgentHomes()
    {
        var root = Path.Combine(Path.GetTempPath(), "exhibition-credential-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "copilot.json");
        await File.WriteAllTextAsync(source, "secret");
        var homes = Path.Combine(root, "homes");
        try
        {
            await using var first = await new CredentialHomeFactory(homes, source).CreateAsync(ContestContract.Agents[0], CancellationToken.None);
            await using var second = await new CredentialHomeFactory(homes, source).CreateAsync(ContestContract.Agents[1], CancellationToken.None);

            Assert.NotEqual(first.Path, second.Path);
            var copied = Directory.GetFiles(first.Path);
            Assert.Equal(2, copied.Length);
            var credential = Assert.Single(copied, path => Path.GetFileName(path) == ".env");
            Assert.Equal("secret", await File.ReadAllTextAsync(credential));
            var config = Assert.Single(copied, path => Path.GetFileName(path) == "config.yaml");
            var configText = await File.ReadAllTextAsync(config);
            Assert.Contains("mcp_servers:", configText, StringComparison.Ordinal);
            Assert.Contains("research:", configText, StringComparison.Ordinal);
            Assert.Contains("/app/public_https_fetch_mcp.py", configText, StringComparison.Ordinal);
            Assert.Contains("sampling:", configText, StringComparison.Ordinal);
            Assert.Contains("enabled: false", configText, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credential));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(config));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
