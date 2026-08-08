using AiStocks.Core;

namespace AiStocks.Worker.Orchestration;

public sealed record TradingSession(DateOnly Day, DateTimeOffset OpenAt, DateTimeOffset CloseAt)
{
    public TimeSpan Duration => CloseAt - OpenAt;
}

public sealed record RunWindow(
    string RunKey,
    Guid AgentId,
    string ModelId,
    int Sequence,
    DateTimeOffset ScheduledAt,
    DateTimeOffset DeadlineAt);

public static class RunSchedule
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<RunWindow> Create(TradingSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.CloseAt <= session.OpenAt)
            throw new ArgumentException("Session close must follow open.", nameof(session));

        var durationTicks = session.Duration.Ticks;
        var times = new[]
        {
            session.OpenAt.AddHours(-1),
            session.OpenAt.AddTicks(durationTicks / 5),
            session.OpenAt.AddTicks(durationTicks * 2 / 5),
            session.OpenAt.AddTicks(durationTicks * 3 / 5),
            session.OpenAt.AddTicks(durationTicks * 4 / 5),
            session.CloseAt.AddMinutes(30)
        };

        return ContestContract.Agents
            .SelectMany(agent => times.Select((scheduledAt, index) => new RunWindow(
                $"{session.Day:yyyy-MM-dd}:{agent.Id:N}:{index + 1}", agent.Id, agent.ModelId,
                index + 1, scheduledAt, scheduledAt.Add(RetryWindow))))
            .OrderBy(window => window.ScheduledAt)
            .ThenBy(window => window.AgentId)
            .ToArray();
    }
}

public interface IDurableRunSchedulePort
{
    Task<int> EnsureAtomicallyAsync(IReadOnlyList<RunWindow> windows, CancellationToken cancellationToken);
}

public sealed class DurableScheduleRegistrar(IDurableRunSchedulePort port)
{
    public Task<int> EnsureSessionAsync(TradingSession session, CancellationToken cancellationToken) =>
        port.EnsureAtomicallyAsync(RunSchedule.Create(session), cancellationToken);
}

public enum RunAttemptOutcome { Succeeded, Failed, Missed }

public sealed record ClaimedRun(RunWindow Run, string ClaimToken);

public sealed record RunCompletion(
    RunWindow Run,
    string ClaimToken,
    RunAttemptOutcome Outcome,
    DateTimeOffset CompletedAt,
    string? Reason,
    DateTimeOffset? RetryAt,
    AgentRunResult? Result);

public interface IRunStore
{
    Task<ClaimedRun?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task CompleteAsync(RunCompletion completion, CancellationToken cancellationToken);
}

public sealed record AgentContext(Guid AgentId);
public sealed record AgentRunRequest(Guid AgentId, string ModelId, string RunKey, DateTimeOffset DecisionAt, AgentContext Context);
public sealed record AgentRunResult(bool Ok, string? Decision, string? Error)
{
    public static AgentRunResult Success(string decision) => new(true, decision, null);
    public static AgentRunResult Failure(string error) => new(false, null, error);
}

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

public interface IAgentDecisionPort
{
    // The implementation must check contest state and persist the decision in one transaction.
    Task<bool> TryAcceptWhileRunningAsync(RunWindow run, AgentRunResult result, CancellationToken cancellationToken);
}

public interface IContestPausePort
{
    Task<bool> IsPausedAsync(CancellationToken cancellationToken);
}

public interface IAgentContextPort
{
    Task<AgentContext> LoadIsolatedAsync(Guid agentId, CancellationToken cancellationToken);
}

public sealed class DurableOrchestrator
{
    private readonly IRunStore store;
    private readonly IAgentRunner runner;
    private readonly IAgentDecisionPort decisions;
    private readonly IContestPausePort pause;
    private readonly IClock clock;
    private readonly TimeSpan retryDelay;
    private readonly IAgentContextPort contexts;

    public DurableOrchestrator(
        IRunStore store,
        IAgentRunner runner,
        IAgentDecisionPort decisions,
        IContestPausePort pause,
        IClock clock,
        TimeSpan? retryDelay = null,
        IAgentContextPort? contexts = null)
    {
        this.store = store;
        this.runner = runner;
        this.decisions = decisions;
        this.pause = pause;
        this.clock = clock;
        this.retryDelay = retryDelay ?? TimeSpan.FromMinutes(1);
        this.contexts = contexts ?? new MinimalIsolatedContextPort();
        if (this.retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));
    }

    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var claimed = await store.ClaimNextAsync(now, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
            return false;

        var run = claimed.Run;
        if (now >= run.DeadlineAt)
        {
            await FinishAsync(claimed, RunAttemptOutcome.Missed, "retry_window_expired", null, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var canonical = ContestContract.Agents.SingleOrDefault(x => x.Id == run.AgentId);
        if (canonical is null || !StringComparer.Ordinal.Equals(canonical.ModelId, run.ModelId))
        {
            await FinishAsync(claimed, RunAttemptOutcome.Missed, "agent_model_identity_mismatch", null, null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (await pause.IsPausedAsync(cancellationToken).ConfigureAwait(false))
        {
            await RetryOrMissAsync(claimed, "system_paused", null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        AgentContext context;
        AgentRunResult result;
        try
        {
            context = await contexts.LoadIsolatedAsync(run.AgentId, cancellationToken).ConfigureAwait(false);
            if (context.AgentId != run.AgentId)
            {
                await FinishAsync(claimed, RunAttemptOutcome.Missed, "context_agent_identity_mismatch", null, null, cancellationToken).ConfigureAwait(false);
                return true;
            }
            result = await runner.RunAsync(
                new AgentRunRequest(run.AgentId, run.ModelId, run.RunKey, run.ScheduledAt, context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RetryOrMissAsync(claimed, $"runner_error:{exception.GetType().Name}", null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (await pause.IsPausedAsync(cancellationToken).ConfigureAwait(false))
        {
            await RetryOrMissAsync(claimed, "paused_during_run", result, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!result.Ok || result.Decision is null)
        {
            await RetryOrMissAsync(claimed, result.Error ?? "runner_failed", result, cancellationToken).ConfigureAwait(false);
            return true;
        }

        try
        {
            var accepted = await decisions.TryAcceptWhileRunningAsync(run, result, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                await RetryOrMissAsync(claimed, "paused_during_commit", result, cancellationToken).ConfigureAwait(false);
                return true;
            }
            await FinishAsync(claimed, RunAttemptOutcome.Succeeded, null, null, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RetryOrMissAsync(claimed, $"decision_error:{exception.GetType().Name}", result, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private Task RetryOrMissAsync(ClaimedRun claimed, string reason, AgentRunResult? result, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (now >= claimed.Run.DeadlineAt)
            return FinishAsync(claimed, RunAttemptOutcome.Missed, "retry_window_expired", null, result, cancellationToken);
        var retryAt = now.Add(retryDelay);
        if (retryAt > claimed.Run.DeadlineAt)
            retryAt = claimed.Run.DeadlineAt;
        return FinishAsync(claimed, RunAttemptOutcome.Failed, reason, retryAt, result, cancellationToken);
    }

    private Task FinishAsync(
        ClaimedRun claimed,
        RunAttemptOutcome outcome,
        string? reason,
        DateTimeOffset? retryAt,
        AgentRunResult? result,
        CancellationToken cancellationToken) =>
        store.CompleteAsync(new(claimed.Run, claimed.ClaimToken, outcome, clock.UtcNow, reason, retryAt, result), cancellationToken);

    private sealed class MinimalIsolatedContextPort : IAgentContextPort
    {
        public Task<AgentContext> LoadIsolatedAsync(Guid agentId, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentContext(agentId));
    }
}

public sealed record QueuedExecution(string OrderId, DateTimeOffset DecisionAt, Guid AgentId);

public interface IQueuedExecutionPort
{
    Task<IReadOnlyList<QueuedExecution>> LoadReadyAsync(CancellationToken cancellationToken);
    Task ExecuteAsync(QueuedExecution order, CancellationToken cancellationToken);
}

public sealed class QueuedExecutionCoordinator(IQueuedExecutionPort port)
{
    public async Task ExecuteAllAsync(CancellationToken cancellationToken)
    {
        var orders = await port.LoadReadyAsync(cancellationToken).ConfigureAwait(false);
        foreach (var order in orders.OrderBy(x => x.DecisionAt).ThenBy(x => x.OrderId, StringComparer.Ordinal))
            await port.ExecuteAsync(order, cancellationToken).ConfigureAwait(false);
    }
}
