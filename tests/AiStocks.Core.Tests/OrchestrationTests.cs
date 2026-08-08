using AiStocks.Core;
using AiStocks.Operations;
using AiStocks.Worker;
using AiStocks.Worker.Orchestration;

namespace AiStocks.Core.Tests;

public sealed class OrchestrationTests
{
    private static readonly DateTimeOffset Open = new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScheduleCreatesSixOrderedWindowsForEveryExactAgentModelPair()
    {
        var windows = RunSchedule.Create(new TradingSession(new DateOnly(2026, 8, 6), Open, Open.AddHours(8.5)));

        Assert.Equal(24, windows.Count);
        foreach (var agent in ContestContract.Agents)
        {
            var own = windows.Where(x => x.AgentId == agent.Id).ToArray();
            Assert.Equal(6, own.Length);
            Assert.All(own, x => Assert.Equal(agent.ModelId, x.ModelId));
            Assert.Equal(Open.AddHours(-1), own[0].ScheduledAt);
            Assert.Equal(Open.AddHours(9), own[5].ScheduledAt);
            Assert.All(own, x => Assert.Equal(TimeSpan.FromMinutes(15), x.DeadlineAt - x.ScheduledAt));
        }

        Assert.Equal(windows.OrderBy(x => x.ScheduledAt).ThenBy(x => x.AgentId), windows);
        Assert.Equal(
            new[] { Open.AddMinutes(102), Open.AddMinutes(204), Open.AddMinutes(306), Open.AddMinutes(408) },
            windows.Where(x => x.AgentId == ContestContract.Agents[0].Id).Skip(1).Take(4).Select(x => x.ScheduledAt));
    }

    [Fact]
    public async Task ScheduleRegistrationUsesOneIdempotentDurableBatch()
    {
        var port = new FakeSchedulePort();
        var session = new TradingSession(new(2026, 8, 6), Open, Open.AddHours(8.5));

        var inserted = await new DurableScheduleRegistrar(port).EnsureSessionAsync(session, default);

        Assert.Equal(24, inserted);
        var batch = Assert.Single(port.Batches);
        Assert.Equal(24, batch.Count);
        Assert.Equal(24, batch.Select(x => x.RunKey).Distinct().Count());
    }

    [Fact]
    public void RecoveryCreatesEveryTradingSessionFromContestStartThroughToday()
    {
        var started = AiStocks.MarketData.StockholmCalendar.Local(new DateOnly(2026, 8, 6), 10, 0);
        var now = AiStocks.MarketData.StockholmCalendar.Local(new DateOnly(2026, 8, 10), 12, 0);

        var windows = WorkerScheduleRecovery.Create(started, now);

        Assert.Equal(72, windows.Count); // Thursday, Friday, and Monday; weekend is excluded.
        Assert.Equal(new[] { new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7), new DateOnly(2026, 8, 10) },
            windows.Select(x => DateOnly.ParseExact(x.RunKey[..10], "yyyy-MM-dd")).Distinct());
    }

    [Fact]
    public async Task OrchestratorRetriesInsideBoundThenRecordsMissedWithoutReplay()
    {
        var clock = new FakeClock(Open.AddHours(-1));
        var run = RunSchedule.Create(new TradingSession(new(2026, 8, 6), Open, Open.AddHours(8.5)))[0];
        var store = new FakeRunStore(run);
        var runner = new FakeRunner(_ => AgentRunResult.Failure("invalid"));
        var sut = new DurableOrchestrator(store, runner, new FakeDecisionPort(), new FakePausePort(), clock, TimeSpan.FromMinutes(5));

        await sut.TickAsync(default);
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        await sut.TickAsync(default);
        clock.UtcNow = run.DeadlineAt;
        await sut.TickAsync(default);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        await sut.TickAsync(default);

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal(new[] { RunAttemptOutcome.Failed, RunAttemptOutcome.Failed, RunAttemptOutcome.Missed }, store.Outcomes);
        Assert.Equal("retry_window_expired", store.Completions[^1].Reason);
    }

    [Fact]
    public async Task OrchestratorPassesOnlyClaimedAgentsContextToExactModel()
    {
        var run = RunSchedule.Create(new TradingSession(new(2026, 8, 6), Open, Open.AddHours(8.5)))[0];
        var clock = new FakeClock(run.ScheduledAt);
        var store = new FakeRunStore(run);
        var decisions = new FakeDecisionPort();
        var runner = new FakeRunner(request => AgentRunResult.Success("hold"));
        var sut = new DurableOrchestrator(store, runner, decisions, new FakePausePort(), clock);

        await sut.TickAsync(default);

        Assert.Single(runner.Requests);
        Assert.Equal(run.AgentId, runner.Requests[0].AgentId);
        Assert.Equal(run.ModelId, runner.Requests[0].ModelId);
        Assert.Equal(run.AgentId, runner.Requests[0].Context.AgentId);
        Assert.Single(decisions.Accepted);
        Assert.Equal(RunAttemptOutcome.Succeeded, store.Outcomes.Single());
    }

    [Fact]
    public async Task OrchestratorFailsClosedOnAgentModelMismatch()
    {
        var valid = RunSchedule.Create(new TradingSession(new(2026, 8, 6), Open, Open.AddHours(8.5)))[0];
        var invalid = valid with { ModelId = ContestContract.Agents[1].ModelId };
        var runner = new FakeRunner(_ => AgentRunResult.Success("hold"));
        var store = new FakeRunStore(invalid);
        var sut = new DurableOrchestrator(store, runner, new FakeDecisionPort(), new FakePausePort(), new FakeClock(invalid.ScheduledAt));

        await sut.TickAsync(default);

        Assert.Empty(runner.Requests);
        Assert.Equal(RunAttemptOutcome.Missed, store.Outcomes.Single());
        Assert.Equal("agent_model_identity_mismatch", store.Completions.Single().Reason);
    }

    [Fact]
    public async Task PauseThatRacesAgentCompletionPreventsDecisionSideEffects()
    {
        var run = RunSchedule.Create(new TradingSession(new(2026, 8, 6), Open, Open.AddHours(8.5)))[0];
        var pause = new FakePausePort(false, true);
        var decisions = new FakeDecisionPort();
        var store = new FakeRunStore(run);
        var sut = new DurableOrchestrator(store, new FakeRunner(_ => AgentRunResult.Success("buy")), decisions, pause, new FakeClock(run.ScheduledAt));

        await sut.TickAsync(default);

        Assert.Empty(decisions.Accepted);
        Assert.Equal(RunAttemptOutcome.Failed, store.Outcomes.Single());
        Assert.Equal("paused_during_run", store.Completions.Single().Reason);
    }

    [Fact]
    public async Task PauseThatRacesFinalCommitIsRejectedByAtomicDecisionPort()
    {
        var run = RunSchedule.Create(new TradingSession(new(2026, 8, 6), Open, Open.AddHours(8.5)))[0];
        var decisions = new FakeDecisionPort(accept: false);
        var store = new FakeRunStore(run);
        var sut = new DurableOrchestrator(store, new FakeRunner(_ => AgentRunResult.Success("buy")), decisions,
            new FakePausePort(false, false), new FakeClock(run.ScheduledAt));

        await sut.TickAsync(default);

        Assert.Empty(decisions.Accepted);
        Assert.Equal(RunAttemptOutcome.Failed, store.Outcomes.Single());
        Assert.Equal("paused_during_commit", store.Completions.Single().Reason);
    }

    [Fact]
    public async Task QueuedOrdersExecuteStrictlyByDecisionTimeThenStableId()
    {
        var port = new FakeQueuedOrderPort(
            new QueuedExecution("b", Open, ContestContract.Agents[0].Id),
            new QueuedExecution("a", Open, ContestContract.Agents[1].Id),
            new QueuedExecution("c", Open.AddMinutes(1), ContestContract.Agents[2].Id));

        await new QueuedExecutionCoordinator(port).ExecuteAllAsync(default);

        Assert.Equal(new[] { "a", "b", "c" }, port.Executed);
    }

    [Fact]
    public async Task FailingQueuedOrderDoesNotPoisonLaterOrders()
    {
        var port = new FakeQueuedOrderPort(
            [new QueuedExecution("a", Open, ContestContract.Agents[0].Id),
             new QueuedExecution("b", Open.AddMinutes(1), ContestContract.Agents[1].Id)],
            "a");

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => new QueuedExecutionCoordinator(port).ExecuteAllAsync(default));

        Assert.Single(failure.InnerExceptions);
        Assert.Equal(new[] { "a", "b" }, port.Executed);
    }

    [Fact]
    public void DailyReportRequiresStockholm1830AllFourAgentsAndEveryRequiredField()
    {
        var service = new DailyReportService();
        var generatedAt = new DateTimeOffset(2026, 8, 6, 18, 30, 0, TimeSpan.FromHours(2));
        var rows = ContestContract.Agents.Select((agent, i) => new DailyAgentSnapshot(
            agent.Id, agent.ModelId, 30_100m - i, 10m - i, 100m - i, 20_000m, "ERIC B:10", "BUY ERIC B", 1.25m, i, "quality catalyst")).ToArray();

        var report = service.Generate(new DateOnly(2026, 8, 6), generatedAt, rows);

        Assert.Contains("18:30 Stockholm", report.Message, StringComparison.Ordinal);
        foreach (var field in new[] { "daily", "total", "cash", "holdings", "trades", "fees", "missed", "rationale" })
            Assert.Contains(field, report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, report.Rows.Count);
        Assert.Throws<OperationsException>(() => service.Generate(new(2026, 8, 6), generatedAt.AddMinutes(1), rows));
        Assert.Throws<OperationsException>(() => service.Generate(new(2026, 8, 6), generatedAt, rows[..3]));
    }

    [Fact]
    public async Task DeliveryIsIdempotentAndEveryAttemptIsAudited()
    {
        var store = new FakeDeliveryStore();
        var discord = new FakeDiscord();
        var service = new AuditedDiscordDelivery(store, discord, new FakeClock(Open));

        var first = await service.DeliverAsync("daily:2026-08-06", "hash", "message", default);
        var second = await service.DeliverAsync("daily:2026-08-06", "hash", "message", default);

        Assert.Equal(first, second);
        Assert.Single(discord.Messages);
        Assert.Single(store.Audits);
        Assert.Equal(DeliveryStatus.Succeeded, store.Audits[0].Status);
        await Assert.ThrowsAsync<OperationsException>(() => service.DeliverAsync("daily:2026-08-06", "different", "message", default));
    }

    [Fact]
    public void ImmediateAlertsAreRestrictedToApprovedRunWideConditions()
    {
        Assert.Equal(5, Enum.GetValues<ImmediateAlertKind>().Length);
        Assert.Throws<OperationsException>(() => ImmediateAlert.Create((ImmediateAlertKind)999, "detail", "key"));
        Assert.Equal(ImmediateAlertKind.SystemPause, ImmediateAlert.Create(ImmediateAlertKind.SystemPause, "maintenance", "pause-1").Kind);
    }

    [Fact]
    public void ExplicitCommandsDoNotCollapseMigrationBootstrapAndPreflight()
    {
        Assert.Equal(OperationsCommand.Preflight, OperationsCommandParser.Parse(["preflight"]));
        Assert.Equal(OperationsCommand.Migrate, OperationsCommandParser.Parse(["migrate"]));
        Assert.Equal(OperationsCommand.Bootstrap, OperationsCommandParser.Parse(["bootstrap"]));
        Assert.Throws<OperationsException>(() => OperationsCommandParser.Parse([]));
        Assert.Throws<OperationsException>(() => OperationsCommandParser.Parse(["migrate-and-bootstrap"]));
    }

    [Fact]
    public async Task BootstrapInitializesExactlyFourAgentsInOneAtomicBatchAtSameInstant()
    {
        var clock = new FakeClock(Open);
        var port = new FakeBootstrapPort();

        await new ContestBootstrapper(port, clock).BootstrapAsync(default);

        var batch = Assert.Single(port.Batches);
        Assert.Equal(4, batch.Count);
        Assert.Equal(4, batch.Select(x => x.AgentId).Distinct().Count());
        Assert.All(batch, x => Assert.Equal(30_000m, x.Cash));
        Assert.Single(batch.Select(x => x.InitializedAt).Distinct());
    }

    [Fact]
    public async Task HealthIsLiveButReadinessFailsClosedUntilEveryDependencyIsReady()
    {
        var checks = new FakeReadinessPort(database: true, migrations: true, marketData: false, agents: true);
        var health = new OperationsHealth(checks);

        Assert.True(health.IsLive());
        var notReady = await health.ReadinessAsync(default);
        Assert.False(notReady.Ready);
        Assert.Contains("market-data", notReady.Failures);
        checks.MarketData = true;
        Assert.True((await health.ReadinessAsync(default)).Ready);
    }

    [Fact]
    public void BackupRequiresEncryptedOutputAndRestoreCanTargetOnlyDedicatedTestDatabase()
    {
        var backup = BackupRestoreCommands.ValidateBackup(new(
            new Uri("postgresql://db/ai_stocks"), "/backups/day.dump.enc", "/run/secrets/backup-passphrase"));
        Assert.True(backup.Encrypted);
        Assert.Throws<OperationsException>(() => BackupRestoreCommands.ValidateBackup(new(new Uri("postgresql://db/ai_stocks"), "/backups/day.dump", "/key")));

        var restore = BackupRestoreCommands.ValidateRestore(new(
            new Uri("postgresql://db/ai_stocks_test"), "/backups/day.dump.enc", "/run/secrets/backup-passphrase"));
        Assert.Equal("ai_stocks_test", restore.DatabaseName);
        Assert.Throws<OperationsException>(() => BackupRestoreCommands.ValidateRestore(new(new Uri("postgresql://db/ai_stocks"), "/backups/day.dump.enc", "/key")));
        Assert.Throws<OperationsException>(() => BackupRestoreCommands.ValidateRestore(new(new Uri("postgresql://db/postgres"), "/backups/day.dump.enc", "/key")));
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; set; } = now; }

    private sealed class FakeRunStore(params RunWindow[] runs) : IRunStore
    {
        private readonly Queue<RunWindow> queue = new(runs);
        public List<RunCompletion> Completions { get; } = [];
        public List<RunAttemptOutcome> Outcomes => Completions.Select(x => x.Outcome).ToList();
        public Task<ClaimedRun?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(queue.Count == 0 ? null : new ClaimedRun(queue.Dequeue(), Guid.NewGuid().ToString("N")));
        public Task CompleteAsync(RunCompletion completion, CancellationToken cancellationToken) { Completions.Add(completion); if (completion.Outcome == RunAttemptOutcome.Failed && completion.RetryAt is not null) queue.Enqueue(completion.Run); return Task.CompletedTask; }
    }

    private sealed class FakeRunner(Func<AgentRunRequest, AgentRunResult> execute) : IAgentRunner
    {
        public List<AgentRunRequest> Requests { get; } = [];
        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken cancellationToken) { Requests.Add(request); return Task.FromResult(execute(request)); }
    }

    private sealed class FakeSchedulePort : IDurableRunSchedulePort
    {
        public List<IReadOnlyList<RunWindow>> Batches { get; } = [];
        public Task<int> EnsureAtomicallyAsync(IReadOnlyList<RunWindow> windows, CancellationToken cancellationToken)
        {
            Batches.Add(windows);
            return Task.FromResult(windows.Count);
        }
    }

    private sealed class FakeDecisionPort(bool accept = true) : IAgentDecisionPort
    {
        public List<(RunWindow Run, AgentRunResult Result)> Accepted { get; } = [];
        public Task<bool> TryAcceptWhileRunningAsync(RunWindow run, AgentRunResult result, CancellationToken cancellationToken)
        {
            if (accept) Accepted.Add((run, result));
            return Task.FromResult(accept);
        }
    }

    private sealed class FakePausePort(params bool[] states) : IContestPausePort
    {
        private readonly Queue<bool> states = new(states.Length == 0 ? [false] : states);
        public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => Task.FromResult(states.Count > 1 ? states.Dequeue() : states.Peek());
    }

    private sealed class FakeQueuedOrderPort : IQueuedExecutionPort
    {
        private readonly List<QueuedExecution> orders;
        private readonly HashSet<string> failures;
        public FakeQueuedOrderPort(params QueuedExecution[] orders) : this(orders, []) { }
        public FakeQueuedOrderPort(IEnumerable<QueuedExecution> orders, params string[] failures) =>
            (this.orders, this.failures) = ([.. orders], [.. failures]);
        public List<string> Executed { get; } = [];
        public Task<IReadOnlyList<QueuedExecution>> LoadReadyAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QueuedExecution>>(orders);
        public Task ExecuteAsync(QueuedExecution order, CancellationToken cancellationToken)
        {
            Executed.Add(order.OrderId);
            return failures.Contains(order.OrderId)
                ? Task.FromException(new InvalidOperationException("synthetic order failure"))
                : Task.CompletedTask;
        }
    }

    private sealed class FakeDeliveryStore : IDeliveryAuditPort
    {
        private readonly Dictionary<string, DeliveryAudit> completed = [];
        public List<DeliveryAudit> Audits { get; } = [];
        public Task<DeliveryReservation> ReserveAsync(string key, string contentHash, CancellationToken cancellationToken)
        {
            if (completed.TryGetValue(key, out var audit))
                return Task.FromResult(audit.ContentHash == contentHash ? DeliveryReservation.AlreadyCompleted(audit) : DeliveryReservation.Conflict());
            return Task.FromResult(DeliveryReservation.Acquired("lease-1"));
        }
        public Task BeginSendAsync(string key, string contentHash, string leaseToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordAsync(DeliveryAudit audit, string leaseToken, CancellationToken cancellationToken) { Audits.Add(audit); if (audit.Status == DeliveryStatus.Succeeded) completed[audit.Key] = audit; return Task.CompletedTask; }
    }

    private sealed class FakeDiscord : IDiscordPort
    {
        public List<string> Messages { get; } = [];
        public Task<string> SendAsync(string message, CancellationToken cancellationToken) { Messages.Add(message); return Task.FromResult("receipt-1"); }
    }

    private sealed class FakeBootstrapPort : IContestBootstrapPort
    {
        public List<IReadOnlyList<InitialAgentState>> Batches { get; } = [];
        public Task InitializeAtomicallyAsync(IReadOnlyList<InitialAgentState> agents, CancellationToken cancellationToken) { Batches.Add(agents); return Task.CompletedTask; }
    }

    private sealed class FakeReadinessPort(bool database, bool migrations, bool marketData, bool agents) : IReadinessPort
    {
        public bool MarketData { get; set; } = marketData;
        public Task<bool> DatabaseAsync(CancellationToken cancellationToken) => Task.FromResult(database);
        public Task<bool> MigrationsAsync(CancellationToken cancellationToken) => Task.FromResult(migrations);
        public Task<bool> MarketDataAsync(CancellationToken cancellationToken) => Task.FromResult(MarketData);
        public Task<bool> FourAgentsAsync(CancellationToken cancellationToken) => Task.FromResult(agents);
    }
}
