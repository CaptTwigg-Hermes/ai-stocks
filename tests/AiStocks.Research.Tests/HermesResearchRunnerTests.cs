using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using AiStocks.Research.Execution;

namespace AiStocks.Research.Tests;

public sealed class HermesResearchRunnerTests
{
    private static readonly Guid AgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task RunAsync_UsesExactIsolatedHermesInvocationAndCapturesImmutableProvenance()
    {
        var process = new FakeProcess("{\"ok\":true}", "diagnostic", 0);
        var launcher = new CapturingLauncher(process);
        var runner = new HermesResearchRunner(launcher, new ResearchExecutionOptions
        {
            HermesExecutable = "/trusted/hermes",
            Timeout = TimeSpan.FromSeconds(2),
            DrainTimeout = TimeSpan.FromSeconds(1),
            Environment = new Dictionary<string, string>
            {
                ["HOME"] = "/agent",
                ["PATH"] = "/trusted/bin",
                ["LD_PRELOAD"] = "/evil.so",
                ["COPILOT_TOKEN"] = "secret"
            }
        });

        var result = await runner.RunAsync(AgentId, "gpt-5.6-sol", "bounded prompt", CancellationToken.None);

        Assert.Equal("{\"ok\":true}", result.StandardOutput);
        Assert.Equal("diagnostic", result.StandardError);
        Assert.NotNull(launcher.StartInfo);
        Assert.False(launcher.StartInfo!.UseShellExecute);
        Assert.True(launcher.StartInfo.RedirectStandardOutput);
        Assert.True(launcher.StartInfo.RedirectStandardError);
        Assert.Equal(new[]
        {
            "-m", "gpt-5.6-sol", "--provider", "copilot", "-t", "web", "--safe-mode", "-z", "bounded prompt"
        }, launcher.StartInfo.ArgumentList);
        Assert.Equal("/agent", launcher.StartInfo.Environment["HOME"]);
        Assert.Equal("/trusted/bin", launcher.StartInfo.Environment["PATH"]);
        Assert.Equal("secret", launcher.StartInfo.Environment["COPILOT_TOKEN"]);
        Assert.False(launcher.StartInfo.Environment.ContainsKey("LD_PRELOAD"));
        Assert.Equal(AgentId, result.Provenance.AgentId);
        Assert.Equal("gpt-5.6-sol", result.Provenance.ModelId);
        Assert.Equal("copilot", result.Provenance.Provider);
        Assert.Equal(64, result.Provenance.PromptSha256.Length);
        Assert.Equal(result.Provenance.Arguments, result.Provenance.Arguments.ToImmutableArray());
    }

    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("claude-opus-4.8")]
    [InlineData("claude-sonnet-5")]
    [InlineData("gemini-3.1-pro-preview")]
    public async Task RunAsync_AllowsOnlyTheFourPinnedModels(string model)
    {
        var runner = new HermesResearchRunner(new CapturingLauncher(new FakeProcess("{}", "", 0)), TestOptions());
        var id = model switch
        {
            "gpt-5.6-sol" => Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "claude-opus-4.8" => Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "claude-sonnet-5" => Guid.Parse("33333333-3333-3333-3333-333333333333"),
            _ => Guid.Parse("44444444-4444-4444-4444-444444444444")
        };

        await runner.RunAsync(id, model, "prompt", CancellationToken.None);
    }

    [Fact]
    public async Task RunAsync_RejectsUnknownOrMismatchedIdentityBeforeStarting()
    {
        var launcher = new CapturingLauncher(new FakeProcess("{}", "", 0));
        var runner = new HermesResearchRunner(launcher, TestOptions());

        await Assert.ThrowsAsync<ResearchExecutionException>(() =>
            runner.RunAsync(AgentId, "gpt-5.6", "prompt", CancellationToken.None));
        await Assert.ThrowsAsync<ResearchExecutionException>(() =>
            runner.RunAsync(Guid.NewGuid(), "gpt-5.6-sol", "prompt", CancellationToken.None));
        Assert.Null(launcher.StartInfo);
    }

    [Fact]
    public async Task RunAsync_RejectsOversizedPrompt()
    {
        var runner = new HermesResearchRunner(new CapturingLauncher(new FakeProcess("{}", "", 0)),
            TestOptions() with { MaximumPromptBytes = 4 });

        await Assert.ThrowsAsync<ResearchExecutionException>(() =>
            runner.RunAsync(AgentId, "gpt-5.6-sol", "12345", CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_KillsTreeAndDrainsWhenTimedOut()
    {
        var process = new FakeProcess("partial", "error", 0, neverExit: true);
        var runner = new HermesResearchRunner(new CapturingLauncher(process),
            TestOptions() with { Timeout = TimeSpan.FromMilliseconds(30), DrainTimeout = TimeSpan.FromSeconds(1) });

        await Assert.ThrowsAsync<ResearchExecutionException>(() =>
            runner.RunAsync(AgentId, "gpt-5.6-sol", "prompt", CancellationToken.None));

        Assert.True(process.KillTreeCalled);
        Assert.True(process.OutputDisposed);
        Assert.True(process.ErrorDisposed);
    }

    [Fact]
    public async Task RunAsync_KillsProcessWhenOutputExceedsBound()
    {
        var process = new FakeProcess("123456", "", 0, neverExit: true);
        var runner = new HermesResearchRunner(new CapturingLauncher(process),
            TestOptions() with { MaximumOutputBytes = 5, Timeout = TimeSpan.FromMilliseconds(30) });

        await Assert.ThrowsAsync<ResearchExecutionException>(() =>
            runner.RunAsync(AgentId, "gpt-5.6-sol", "prompt", CancellationToken.None));

        Assert.True(process.KillTreeCalled);
    }

    private static ResearchExecutionOptions TestOptions() => new()
    {
        Timeout = TimeSpan.FromSeconds(2),
        DrainTimeout = TimeSpan.FromSeconds(1),
        Environment = new Dictionary<string, string> { ["HOME"] = "/tmp", ["PATH"] = "/bin" }
    };

    private sealed class CapturingLauncher(IResearchProcess process) : IResearchProcessLauncher
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public IResearchProcess Start(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class FakeProcess : IResearchProcess
    {
        private readonly bool _neverExit;
        private readonly int _exitCode;
        public FakeProcess(string stdout, string stderr, int exitCode, bool neverExit = false)
        {
            StandardOutput = new TrackingStream(Encoding.UTF8.GetBytes(stdout), () => OutputDisposed = true);
            StandardError = new TrackingStream(Encoding.UTF8.GetBytes(stderr), () => ErrorDisposed = true);
            _exitCode = exitCode;
            _neverExit = neverExit;
        }
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }
        public int ExitCode => _exitCode;
        public bool HasExited => !_neverExit || KillTreeCalled;
        public bool KillTreeCalled { get; private set; }
        public bool OutputDisposed { get; private set; }
        public bool ErrorDisposed { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _neverExit && !KillTreeCalled ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : Task.CompletedTask;
        public void Kill(bool entireProcessTree) { KillTreeCalled = entireProcessTree; }
        public ValueTask DisposeAsync()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingStream(byte[] bytes, Action disposed) : MemoryStream(bytes)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) disposed();
        }
    }
}
