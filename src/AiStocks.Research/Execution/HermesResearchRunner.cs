using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AiStocks.Core;

namespace AiStocks.Research.Execution;

public sealed record ResearchExecutionOptions
{
    public string HermesExecutable { get; init; } = "/opt/hermes/bin/hermes";
    public int MaximumPromptBytes { get; init; } = 64 * 1024;
    public int MaximumOutputBytes { get; init; } = 1024 * 1024;
    public int MaximumErrorBytes { get; init; } = 256 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        System.Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
}

public sealed record InvocationProvenance
{
    public required Guid AgentId { get; init; }
    public required string ModelId { get; init; }
    public required string Provider { get; init; }
    public required string Executable { get; init; }
    public required ImmutableArray<string> Arguments { get; init; }
    public required ImmutableArray<string> EnvironmentVariableNames { get; init; }
    public required string PromptSha256 { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required int ExitCode { get; init; }
    public required string StandardOutputSha256 { get; init; }
    public required string StandardErrorSha256 { get; init; }
}

public sealed record ResearchExecutionResult(
    string StandardOutput,
    string StandardError,
    InvocationProvenance Provenance);

public sealed class ResearchExecutionException : Exception
{
    public ResearchExecutionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public interface IResearchProcessLauncher
{
    IResearchProcess Start(ProcessStartInfo startInfo);
}

public interface IResearchProcess : IAsyncDisposable
{
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

public sealed class SystemResearchProcessLauncher : IResearchProcessLauncher
{
    public IResearchProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new ResearchExecutionException("Hermes process did not start.");
        }

        return new SystemResearchProcess(process);
    }

    private sealed class SystemResearchProcess(Process process) : IResearchProcess
    {
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class HermesResearchRunner
{
    private static readonly ImmutableDictionary<Guid, string> AllowedIdentities =
        ContestContract.Agents.ToImmutableDictionary(agent => agent.Id, agent => agent.ModelId);

    private static readonly ImmutableHashSet<string> AllowedEnvironmentVariables =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "HOME", "HERMES_HOME", "PATH", "LANG", "LC_ALL", "SSL_CERT_FILE", "SSL_CERT_DIR",
            "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "COPILOT_TOKEN", "GH_TOKEN", "GITHUB_TOKEN");

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IResearchProcessLauncher _launcher;
    private readonly ResearchExecutionOptions _options;

    public HermesResearchRunner(IResearchProcessLauncher launcher, ResearchExecutionOptions? options = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _options = options ?? new ResearchExecutionOptions();
        ValidateOptions(_options);
    }

    public async Task<ResearchExecutionResult> RunAsync(
        Guid agentId,
        string modelId,
        string prompt,
        CancellationToken cancellationToken)
    {
        ValidateInvocation(agentId, modelId, prompt);
        var startedAt = DateTimeOffset.UtcNow;
        var promptBytes = StrictUtf8.GetBytes(prompt);
        var promptHash = Sha256(promptBytes);
        var startInfo = BuildStartInfo(modelId, prompt);
        var arguments = startInfo.ArgumentList.ToImmutableArray();
        var environmentNames = startInfo.Environment.Keys.Order(StringComparer.Ordinal).ToImmutableArray();

        IResearchProcess process;
        try
        {
            process = _launcher.Start(startInfo);
        }
        catch (Exception exception) when (exception is not ResearchExecutionException)
        {
            throw new ResearchExecutionException("Hermes process could not be started.", exception);
        }

        await using (process.ConfigureAwait(false))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.Timeout);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, _options.MaximumOutputBytes, deadline.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError, _options.MaximumErrorBytes, deadline.Token);
            var waitTask = process.WaitForExitAsync(deadline.Token);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask, waitTask).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await KillAndDrainAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var message = deadline.IsCancellationRequested
                    ? "Hermes process exceeded its execution deadline."
                    : "Hermes process output was invalid or exceeded its configured bound.";
                throw new ResearchExecutionException(message, exception);
            }

            var stdoutBytes = await stdoutTask.ConfigureAwait(false);
            var stderrBytes = await stderrTask.ConfigureAwait(false);
            string stdout;
            string stderr;
            try
            {
                stdout = StrictUtf8.GetString(stdoutBytes);
                stderr = StrictUtf8.GetString(stderrBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ResearchExecutionException("Hermes emitted non-UTF-8 output.", exception);
            }

            var provenance = new InvocationProvenance
            {
                AgentId = agentId,
                ModelId = modelId,
                Provider = "copilot",
                Executable = startInfo.FileName,
                Arguments = arguments,
                EnvironmentVariableNames = environmentNames,
                PromptSha256 = promptHash,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ExitCode = process.ExitCode,
                StandardOutputSha256 = Sha256(stdoutBytes),
                StandardErrorSha256 = Sha256(stderrBytes)
            };

            if (process.ExitCode != 0)
            {
                throw new ResearchExecutionException($"Hermes exited with code {process.ExitCode}.");
            }

            return new ResearchExecutionResult(stdout, stderr, provenance);
        }
    }

    private ProcessStartInfo BuildStartInfo(string modelId, string prompt)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.HermesExecutable,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelId);
        startInfo.ArgumentList.Add("--provider");
        startInfo.ArgumentList.Add("copilot");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("-z");
        startInfo.ArgumentList.Add(prompt);
        startInfo.Environment.Clear();
        foreach (var pair in _options.Environment)
        {
            if (AllowedEnvironmentVariables.Contains(pair.Key))
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private void ValidateInvocation(Guid agentId, string modelId, string prompt)
    {
        if (!AllowedIdentities.TryGetValue(agentId, out var pinnedModel) || !StringComparer.Ordinal.Equals(pinnedModel, modelId))
        {
            throw new ResearchExecutionException("Agent and model must exactly match one of the four pinned contest identities; fallback is forbidden.");
        }

        if (prompt is null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }

        int promptByteCount;
        try
        {
            promptByteCount = StrictUtf8.GetByteCount(prompt);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ResearchExecutionException("Prompt is not valid Unicode.", exception);
        }

        if (prompt.IndexOf('\0', StringComparison.Ordinal) >= 0 || promptByteCount > _options.MaximumPromptBytes)
        {
            throw new ResearchExecutionException("Prompt is invalid or exceeds its configured UTF-8 byte bound.");
        }
    }

    private async Task KillAndDrainAsync(IResearchProcess process, Task<byte[]> stdout, Task<byte[]> stderr)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // A process can exit between HasExited and Kill.
        }

        using var drain = new CancellationTokenSource(_options.DrainTimeout);
        try
        {
            await process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
            await Task.WhenAll(IgnoreFailure(stdout), IgnoreFailure(stderr)).WaitAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposal below closes both redirected pipes after the bounded drain attempt.
        }
    }

    private static async Task IgnoreFailure(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(81920, maximumBytes + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Process stream exceeded its configured byte bound.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void ValidateOptions(ResearchExecutionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.HermesExecutable) || !Path.IsPathFullyQualified(options.HermesExecutable) ||
            options.MaximumPromptBytes <= 0 ||
            options.MaximumOutputBytes <= 0 || options.MaximumErrorBytes <= 0 ||
            options.Timeout <= TimeSpan.Zero || options.DrainTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
