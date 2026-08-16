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
            var copied = Assert.Single(Directory.GetFiles(first.Path));
            Assert.Equal(".env", Path.GetFileName(copied));
            Assert.Equal("secret", await File.ReadAllTextAsync(copied));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copied));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
