using AiStocks.Core;

namespace AiStocks.Exhibition.Worker;

public sealed class CredentialHomeFactory(string root, string credentialFile)
{
    public async Task<EphemeralHermesHome> CreateAsync(AgentDefinition agent, CancellationToken cancellationToken)
    {
        if (!ContestContract.IsExactAgent(agent.Id, agent.ModelId))
            throw new InvalidOperationException("Unknown contest agent.");
        var source = Path.GetFullPath(credentialFile);
        var rootPath = Path.GetFullPath(root);
        if (!File.Exists(source)) throw new InvalidOperationException("Copilot credential file is missing.");
        Directory.CreateDirectory(rootPath);
        var home = Path.Combine(rootPath, agent.Id.ToString("N"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Hermes only loads provider credentials from $HERMES_HOME/.env.
            // Keep the mounted source name irrelevant so deployment can use a
            // descriptive secret filename without silently losing credentials.
            var destination = Path.Combine(home, ".env");
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var config = Path.Combine(home, "config.yaml");
            await File.WriteAllTextAsync(config,
                """
                mcp_servers:
                  research:
                    command: "/opt/hermes/.venv/bin/python"
                    args:
                      - "/app/public_https_fetch_mcp.py"
                    env:
                      HOME: "/tmp"
                      PYTHONDONTWRITEBYTECODE: "1"
                    timeout: 30
                    connect_timeout: 20
                    sampling:
                      enabled: false
                """, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new EphemeralHermesHome(home);
        }
        catch
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            throw;
        }
    }
}

public sealed class EphemeralHermesHome(string path) : IAsyncDisposable
{
    public string Path { get; } = path;

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}
