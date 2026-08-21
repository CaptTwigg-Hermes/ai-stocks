using AiStocks.Core;

using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiStocks.Api;

public sealed partial class PreviewRaceStore
{
    public const decimal StartingCashDkk = 100_000m;
    public const string DataMode = "preview-fixtures";
    public const string AssumedExecutionMode = "assumed-delayed-paper-fills-v1";
    public const string NordicAssumedExecutionMode = "assumed-delayed-paper-fills-v2";
    public const decimal AssumedSekToDkk = 0.65m;
    public const decimal AssumedSlippagePercent = 1m;
    public const decimal MaximumAssumedOrderDkk = 10_000m;
    public const decimal MaximumAssumedPositionDkk = 25_000m;
    public const int MaximumIdempotencyEntries = 1_000;
    private static readonly Regex LowerSha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private static readonly InstrumentDto[] Instruments =
    [
        Instrument("aapl-us", "AAPL", "Apple Inc.", "NASDAQ", "United States", "USD", 217.35m, 6.45m),
        Instrument("msft-us", "MSFT", "Microsoft Corp.", "NASDAQ", "United States", "USD", 418.79m, 6.45m),
        Instrument("tsla-us", "TSLA", "Tesla Inc.", "NASDAQ", "United States", "USD", 329.65m, 6.45m),
        Instrument("nvda-us", "NVDA", "NVIDIA Corp.", "NASDAQ", "United States", "USD", 182.12m, 6.45m),
        Instrument("novo-dk", "NOVO B", "Novo Nordisk A/S", "Nasdaq Copenhagen", "Denmark", "DKK", 470.20m, 1m),
        Instrument("maersk-dk", "MAERSK B", "A.P. Møller - Mærsk A/S", "Nasdaq Copenhagen", "Denmark", "DKK", 13_225m, 1m),
        Instrument("asml-nl", "ASML", "ASML Holding N.V.", "Euronext Amsterdam", "Netherlands", "EUR", 892.40m, 7.46m),
        Instrument("sap-de", "SAP", "SAP SE", "Xetra", "Germany", "EUR", 238.70m, 7.46m),
        Instrument("sony-jp", "SONY", "Sony Group Corp.", "Tokyo", "Japan", "JPY", 3_741m, 0.043m),
        Instrument("shop-ca", "SHOP", "Shopify Inc.", "Toronto", "Canada", "CAD", 153.20m, 4.70m)
    ];

    private readonly object sync = new();
    private readonly Dictionary<string, Account> accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, AiAccount> aiAccounts = ContestContract.Agents
        .ToDictionary(agent => agent.Id, agent => new AiAccount(agent.Id, agent.ModelId));
    private readonly Dictionary<string, AiRun> aiRuns = new(StringComparer.Ordinal);
    private readonly List<AiActivityDto> aiActivity = [];
    private readonly TimeProvider clock;
    private readonly IPreviewRaceStatePersistence? persistence;
    private readonly string? persistedDataMode;
    private readonly string? persistedExecutionMode;
    private long? persistenceRevision;

    public PreviewRaceStore(TimeProvider clock, IPreviewRaceStatePersistence? persistence = null,
        string? persistedDataMode = null, string? persistedExecutionMode = null)
    {
        this.clock = clock;
        this.persistence = persistence;
        this.persistedDataMode = persistedDataMode ?? DelayedNasdaqInstrumentStore.DataMode;
        this.persistedExecutionMode = persistedExecutionMode ?? AssumedExecutionMode;
        if (!(((this.persistedDataMode == DataMode ||
                this.persistedDataMode == DelayedNasdaqInstrumentStore.DataMode) &&
               this.persistedExecutionMode == AssumedExecutionMode) ||
              (this.persistedDataMode == DelayedNasdaqInstrumentStore.NordicDataMode &&
               this.persistedExecutionMode == NordicAssumedExecutionMode)))
            throw new ArgumentException("Unsupported preview data and execution mode pair.");
        var persisted = persistence?.Load();
        if (persisted is null) return;
        RestoreState(persisted.Json);
        persistenceRevision = persisted.Revision;
    }

    public InstrumentListDto Search(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var matches = Instruments
            .Where(item => normalized.Length == 0 ||
                item.Symbol.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Exchange.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.Country.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToArray();
        return new(matches, DataMode);
    }

    public PreviewPortfolioDto Portfolio(string identity)
    {
        lock (sync)
        {
            return PortfolioFor(AccountFor(identity));
        }
    }

    public OrderListDto Orders(string identity)
    {
        lock (sync)
        {
            return new(AccountFor(identity).Orders.OrderByDescending(item => item.FilledAt).Take(20).ToArray());
        }
    }

    public PreviewLeaderboardDto Leaderboard(string identity)
    {
        lock (sync)
        {
            var human = PortfolioFor(AccountFor(identity));
            var rows = new List<(string Name, string Type, decimal Value)>
            {
                (human.DisplayName, "human", human.TotalValueDkk),
                ("GPT-5.6 Sol", "ai", 101_240m),
                ("Claude Sonnet 5", "ai", 100_120m),
                ("Claude Opus 4.8", "ai", 99_580m),
                ("Gemini 3.1 Pro", "ai", 98_920m)
            };
            var ordered = rows.OrderByDescending(item => item.Value).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
            var ranked = ordered
                .Select(item => new PreviewLeaderboardEntryDto(
                    1 + ordered.Count(candidate => candidate.Value > item.Value),
                    item.Name, item.Type, Money(item.Value),
                    Percent((item.Value - StartingCashDkk) / StartingCashDkk * 100m)))
                .ToArray();
            return new(ranked, DataMode);
        }
    }

    public AiProgressDto AiProgress(string dataMode = DataMode)
    {
        lock (sync)
        {
            RefreshDurableState();
            var participants = aiAccounts.Values.OrderBy(account => account.ModelId, StringComparer.Ordinal)
                .Select(account => new AiProgressAgentDto(account.AgentId, account.ModelId, account.ModelId,
                    account.Status, account.RunId, account.QueuedAt, account.StartedAt, account.CompletedAt,
                    account.Error, PortfolioFor(account.Account, dataMode), account.LatestDecision)).ToArray();
            return new(participants,
                aiActivity.OrderByDescending(item => item.OccurredAt).Take(100).ToArray(),
                dataMode, IsNonLive: true, StrictContest: false, HoldOnly: true,
                Performance: BuildPerformance(participants));
        }
    }

    public AiProgressDto AiProgress(InstrumentListDto snapshot)
    {
        var instruments = Snapshot(snapshot, clock.GetUtcNow());
        lock (sync)
        {
            RefreshDurableState();
            var participants = aiAccounts.Values.OrderBy(account => account.ModelId, StringComparer.Ordinal)
                .Select(account => new AiProgressAgentDto(account.AgentId, account.ModelId, account.ModelId,
                    account.Status, account.RunId, account.QueuedAt, account.StartedAt, account.CompletedAt,
                    account.Error, PortfolioFor(account.Account, snapshot.DataMode, instruments), account.LatestDecision)).ToArray();
            var nordic = snapshot.DataMode == DelayedNasdaqInstrumentStore.NordicDataMode;
            return new(participants,
                aiActivity.OrderByDescending(item => item.OccurredAt).Take(100).ToArray(), snapshot.DataMode,
                IsNonLive: true, StrictContest: false, HoldOnly: false,
                ExecutionMode: nordic ? NordicAssumedExecutionMode : AssumedExecutionMode,
                AssumedFills: true, AssumedSekToDkk: nordic ? null : AssumedSekToDkk,
                AssumedSlippagePercent, Performance: BuildPerformance(participants),
                AssumedFxToDkk: nordic ? FxMap(instruments) : null);
        }
    }

    public PreviewLeaderboardDto AiLeaderboard(string dataMode = DataMode)
    {
        lock (sync)
        {
            RefreshDurableState();
            var rows = aiAccounts.Values.Select(account =>
                (account.ModelId, TotalValueDkk: PortfolioFor(account.Account).TotalValueDkk)).ToArray();
            var ordered = rows.OrderByDescending(row => row.TotalValueDkk)
                .ThenBy(row => row.ModelId, StringComparer.Ordinal).ToArray();
            return new(ordered.Select(row => new PreviewLeaderboardEntryDto(
                1 + ordered.Count(candidate => candidate.TotalValueDkk > row.TotalValueDkk), row.ModelId, "ai",
                row.TotalValueDkk, Percent((row.TotalValueDkk - StartingCashDkk) / StartingCashDkk * 100m))).ToArray(), dataMode);
        }
    }

    public PreviewLeaderboardDto AiLeaderboard(InstrumentListDto snapshot)
    {
        var instruments = Snapshot(snapshot, clock.GetUtcNow());
        lock (sync)
        {
            RefreshDurableState();
            var rows = aiAccounts.Values.Select(account =>
                (account.ModelId, TotalValueDkk: PortfolioFor(account.Account, snapshot.DataMode, instruments).TotalValueDkk)).ToArray();
            var ordered = rows.OrderByDescending(row => row.TotalValueDkk)
                .ThenBy(row => row.ModelId, StringComparer.Ordinal).ToArray();
            return new(ordered.Select(row => new PreviewLeaderboardEntryDto(
                1 + ordered.Count(candidate => candidate.TotalValueDkk > row.TotalValueDkk), row.ModelId, "ai",
                row.TotalValueDkk, Percent((row.TotalValueDkk - StartingCashDkk) / StartingCashDkk * 100m))).ToArray(),
                snapshot.DataMode);
        }
    }

    public AiDecisionSubmission SubmitAi(AiDecisionRequestDto request)
    {
        ValidateAi(request);
        if (!string.Equals(request.Action.Trim(), "hold", StringComparison.OrdinalIgnoreCase))
            throw new PreviewOrderException("delayed-snapshot-required", "Trades require one immutable current delayed snapshot.");
        var fingerprint = JsonSerializer.Serialize(request);
        return PersistMutation<AiDecisionSubmission>(() =>
        {
            if (aiRuns.TryGetValue(request.RunId, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new PreviewOrderException("run-id-conflict", "runId was already used for a different decision.");
                return new(prior.Decision, true);
            }
            if (aiRuns.Count >= MaximumIdempotencyEntries)
                throw new PreviewOrderException("run-capacity", "The fixture decision capacity has been reached.");

            var account = aiAccounts[request.AgentId];
            if (!string.Equals(account.RunId, request.RunId, StringComparison.Ordinal))
                throw new PreviewOrderException("run-decision-conflict", "The decision runId must match the agent's current run.");
            if (account.Status != "running")
                throw new PreviewOrderException("decision-status-conflict", "A fixture decision may complete only a running run.");
            if (account.StartedAt is null || request.CompletedAt <= account.StartedAt)
                throw new PreviewOrderException("stale-decision", "Decision completion must follow the current run's start time.");
            var action = request.Action.Trim().ToLowerInvariant();
            var decision = new AiDecisionDto(request.RunId, action, request.InstrumentId, request.Quantity,
                request.Reason.Trim(), request.Confidence, request.Evidence,
                new(request.RuntimeProvider, request.RuntimeModel, request.ReportSha256), request.CompletedAt);
            account.Status = "succeeded";
            account.RunId = request.RunId;
            account.QueuedAt ??= request.CompletedAt;
            account.StartedAt ??= request.CompletedAt;
            account.CompletedAt = request.CompletedAt;
            account.Error = null;
            account.LatestDecision = decision;
            account.SeenRunIds.Add(request.RunId);
            AddAiActivity(new(request.RunId, request.AgentId, request.ModelId, "succeeded", action,
                request.Reason.Trim(), null, request.CompletedAt));
            aiRuns.Add(request.RunId, new(request.AgentId, request.ModelId, fingerprint, decision));
            RecordPerformance(account.Account, request.CompletedAt, PortfolioFor(account.Account).TotalValueDkk);
            return new(decision, false);
        });
    }

    public AiDecisionSubmission SubmitAi(AiDecisionRequestDto request, InstrumentListDto snapshot)
    {
        ValidateAi(request);
        var instruments = Snapshot(snapshot, request.CompletedAt);
        var fingerprint = JsonSerializer.Serialize(request);
        return PersistMutation<AiDecisionSubmission>(() =>
        {
            if (aiRuns.TryGetValue(request.RunId, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new PreviewOrderException("run-id-conflict", "runId was already used for a different decision.");
                return new(prior.Decision, true);
            }
            if (aiRuns.Count >= MaximumIdempotencyEntries)
                throw new PreviewOrderException("run-capacity", "The fixture decision capacity has been reached.");

            var account = aiAccounts[request.AgentId];
            ValidateCurrentRun(account, request);
            var action = request.Action.Trim().ToLowerInvariant();
            PortfolioFor(account.Account, snapshot.DataMode, instruments);
            AiAssumedPaperFillDto? fill = null;
            if (action != "hold")
            {
                var matches = instruments.Where(item => string.Equals(item.Id, request.InstrumentId, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    throw new PreviewOrderException("instrument-not-found", "Instrument must be one exact current delayed item.");
                var instrument = matches[0];
                var nordic = snapshot.DataMode == DelayedNasdaqInstrumentStore.NordicDataMode;
                if (instrument.Tradable != false || instrument.PaperTradable != true ||
                    instrument.ExecutedAt is null || instrument.AvailableAt is null ||
                    instrument.Price <= 0m || string.IsNullOrWhiteSpace(instrument.Source))
                    throw new PreviewOrderException("invalid-observation", "Instrument is not a verified delayed observation.");

                decimal fxToDkk;
                string executionMode;
                if (nordic)
                {
                    if (instrument.PriceDkk is null or <= 0m || instrument.FxToDkk is null or <= 0m ||
                        instrument.FxReferenceDate is null || instrument.FxAvailableAt is null ||
                        instrument.FxSource != MarketDataProvenance.EcbInformationalReferenceRates ||
                        !LowerSha256.IsMatch(instrument.FxSha256 ?? string.Empty))
                        throw new PreviewOrderException("invalid-fx", "Instrument lacks verified current DKK FX.");
                    if (request.ObservedPrice != instrument.Price || request.ObservedCurrency != instrument.Currency ||
                        request.ObservedVenue != instrument.Exchange || request.ObservedFxToDkk != instrument.FxToDkk ||
                        request.ObservationExecutedAt != instrument.ExecutedAt ||
                        request.ObservationAvailableAt != instrument.AvailableAt ||
                        request.FxReferenceDate != instrument.FxReferenceDate ||
                        request.FxAvailableAt != instrument.FxAvailableAt ||
                        request.FxSource != instrument.FxSource || request.FxSha256 != instrument.FxSha256)
                        throw new PreviewOrderException("observation-mismatch",
                            "Trade must bind to the exact delayed observation and FX supplied to the worker.");
                    fxToDkk = instrument.FxToDkk.Value;
                    executionMode = NordicAssumedExecutionMode;
                }
                else
                {
                    if (instrument.Currency != "SEK" || instrument.Exchange != "XSTO" ||
                        request.ObservedPriceSek != instrument.Price ||
                        request.ObservationAvailableAt != instrument.AvailableAt)
                        throw new PreviewOrderException("observation-mismatch",
                            "Trade must bind to the exact delayed observation supplied to the worker.");
                    fxToDkk = AssumedSekToDkk;
                    executionMode = AssumedExecutionMode;
                }
                if (request.CompletedAt < instrument.AvailableAt.Value)
                    throw new PreviewOrderException("observation-not-available", "The delayed observation was not available when the decision completed.");
                if (request.Evidence.Any(item => item.PublishedAt > instrument.AvailableAt.Value))
                    throw new PreviewOrderException("evidence-lookahead", "Trade evidence cannot postdate the selected observation's availability.");

                account.Account.Holdings.TryGetValue(instrument.Id, out var held);
                var markPriceDkk = CurrentPriceDkk(instrument, snapshot.DataMode);
                var fillPrice = Money(markPriceDkk * (action == "buy" ? 1.01m : 0.99m));
                var total = Money(fillPrice * request.Quantity);
                if (action == "buy" && total > MaximumAssumedOrderDkk)
                    throw new PreviewOrderException("maximum-order-total", "Assumed buy order total cannot exceed DKK 10,000.");
                if (action == "buy")
                {
                    if (account.Account.CashDkk < total)
                        throw new PreviewOrderException("insufficient-cash", "The paper account has insufficient DKK cash.");
                    if (Money((held + request.Quantity) * markPriceDkk) > MaximumAssumedPositionDkk)
                        throw new PreviewOrderException("maximum-position-value", "The resulting marked position cannot exceed DKK 25,000.");
                    account.Account.CashDkk = Money(account.Account.CashDkk - total);
                    account.Account.Holdings[instrument.Id] = held + request.Quantity;
                    account.Account.CostBasisDkk[instrument.Id] = Money(
                        account.Account.CostBasisDkk.GetValueOrDefault(instrument.Id) + total);
                }
                else
                {
                    if (held < request.Quantity)
                        throw new PreviewOrderException("insufficient-holdings", "The paper account does not hold enough shares.");
                    account.Account.CashDkk = Money(account.Account.CashDkk + total);
                    var averageCost = account.Account.CostBasisDkk.GetValueOrDefault(instrument.Id) / held;
                    if (held == request.Quantity)
                    {
                        account.Account.Holdings.Remove(instrument.Id);
                        account.Account.CostBasisDkk.Remove(instrument.Id);
                    }
                    else
                    {
                        account.Account.Holdings[instrument.Id] = held - request.Quantity;
                        account.Account.CostBasisDkk[instrument.Id] = Money(averageCost * (held - request.Quantity));
                    }
                }
                fill = new(instrument.Price, fxToDkk, AssumedSlippagePercent, fillPrice, total,
                    instrument.ExecutedAt.Value, instrument.AvailableAt.Value, clock.GetUtcNow(), executionMode,
                    ObservedPrice: nordic ? instrument.Price : null,
                    ObservedCurrency: nordic ? instrument.Currency : null,
                    ObservedVenue: nordic ? instrument.Exchange : null,
                    FxToDkk: nordic ? fxToDkk : null,
                    FxReferenceDate: nordic ? instrument.FxReferenceDate : null,
                    FxAvailableAt: nordic ? instrument.FxAvailableAt : null,
                    FxSource: nordic ? instrument.FxSource : null,
                    FxSha256: nordic ? instrument.FxSha256 : null);
            }

            RefreshPersistedMarks(account.Account, instruments, snapshot.DataMode);
            var decision = new AiDecisionDto(request.RunId, action, request.InstrumentId, request.Quantity,
                request.Reason.Trim(), request.Confidence, request.Evidence,
                new(request.RuntimeProvider, request.RuntimeModel, request.ReportSha256), request.CompletedAt, fill);
            CompleteAiDecision(account, request, decision, fingerprint, action);
            RecordPerformance(account.Account, request.CompletedAt,
                PortfolioFor(account.Account, snapshot.DataMode, instruments).TotalValueDkk);
            return new(decision, false);
        });
    }

    public void UpdateAiStatus(AiStatusRequestDto request)
    {
        if (request.RunId is null || request.RunId.Length is < 8 or > 128 ||
            request.RunId.Any(character => character is < '!' or > '~'))
            throw new PreviewOrderException("invalid-run-id", "runId must contain 8-128 visible ASCII characters.");
        if (!ContestContract.IsExactAgent(request.AgentId, request.ModelId))
            throw new PreviewOrderException("agent-model-mismatch", "agentId and modelId must exactly match ContestContract.Agents.");
        var status = request.Status?.Trim().ToLowerInvariant();
        if (status is not ("queued" or "running" or "failed"))
            throw new PreviewOrderException("invalid-status", "status must be queued, running, or failed.");
        if (request.OccurredAt == default || request.Error?.Length > 1_000 ||
            (status == "failed" && string.IsNullOrWhiteSpace(request.Error)) ||
            (status != "failed" && request.Error is not null))
            throw new PreviewOrderException("invalid-status-detail", "Status time and bounded failure detail must match the status.");

        PersistMutationVoid(() =>
        {
            var account = aiAccounts[request.AgentId];
            var sameRun = string.Equals(account.RunId, request.RunId, StringComparison.Ordinal);
            if (sameRun && string.Equals(account.Status, status, StringComparison.Ordinal) &&
                StatusDetailsMatch(account, status, request))
                return;
            if (sameRun && account.Status is "succeeded" or "failed")
                throw new PreviewOrderException("terminal-status-conflict", "A terminal fixture run cannot transition to another status.");
            if (!sameRun && status != "queued")
                throw new PreviewOrderException("run-status-conflict", "running and failed status must follow queued for the same runId.");
            if (!sameRun && account.SeenRunIds.Contains(request.RunId))
                throw new PreviewOrderException("stale-status", "A completed or superseded runId cannot become current again.");
            if (!sameRun && account.Status is "queued" or "running")
                throw new PreviewOrderException("active-run-conflict", "A new run cannot replace an active queued or running run.");
            var latestAt = LatestStatusAt(account);
            if ((!sameRun || status != account.Status) && latestAt is not null && request.OccurredAt <= latestAt)
                throw new PreviewOrderException("stale-status", "Status events must be newer than the current lifecycle state.");
            if (sameRun && status == "queued")
                throw new PreviewOrderException("status-transition-conflict", "queued cannot replace an active run state.");
            if (sameRun && account.Status == "running" && status != "failed")
                throw new PreviewOrderException("status-transition-conflict", "running may transition only to failed or a submitted decision.");
            if (sameRun && account.Status == "queued" && status is not ("running" or "failed"))
                throw new PreviewOrderException("status-transition-conflict", "queued may transition only to running or failed.");
            if (status == "queued")
            {
                if (account.SeenRunIds.Count >= MaximumIdempotencyEntries)
                    throw new PreviewOrderException("run-capacity", "The fixture run capacity has been reached.");
                account.SeenRunIds.Add(request.RunId);
                account.RunId = request.RunId;
                account.QueuedAt = request.OccurredAt;
                account.StartedAt = null;
                account.CompletedAt = null;
                account.Error = null;
                if (account.Account.Performance.Count == 0)
                    RecordPerformance(account.Account, request.OccurredAt, StartingCashDkk);
            }
            else if (status == "running")
            {
                account.StartedAt = request.OccurredAt;
                account.Error = null;
            }
            else
            {
                account.CompletedAt = request.OccurredAt;
                account.Error = request.Error!.Trim();
                AddAiActivity(new(request.RunId, request.AgentId, request.ModelId, "failed", null, null,
                    account.Error, request.OccurredAt));
                RecordPerformance(account.Account, request.OccurredAt,
                    account.Account.Performance.LastOrDefault()?.ValueDkk ?? StartingCashDkk);
            }
            account.Status = status;
        });
    }

    private static void ValidateCurrentRun(AiAccount account, AiDecisionRequestDto request)
    {
        if (!string.Equals(account.RunId, request.RunId, StringComparison.Ordinal))
            throw new PreviewOrderException("run-decision-conflict", "The decision runId must match the agent's current run.");
        if (account.Status != "running")
            throw new PreviewOrderException("decision-status-conflict", "A fixture decision may complete only a running run.");
        if (account.StartedAt is null || request.CompletedAt <= account.StartedAt)
            throw new PreviewOrderException("stale-decision", "Decision completion must follow the current run's start time.");
    }

    private void CompleteAiDecision(AiAccount account, AiDecisionRequestDto request, AiDecisionDto decision,
        string fingerprint, string action)
    {
        account.Status = "succeeded";
        account.RunId = request.RunId;
        account.QueuedAt ??= request.CompletedAt;
        account.StartedAt ??= request.CompletedAt;
        account.CompletedAt = request.CompletedAt;
        account.Error = null;
        account.LatestDecision = decision;
        account.SeenRunIds.Add(request.RunId);
        AddAiActivity(new(request.RunId, request.AgentId, request.ModelId, "succeeded", action,
            request.Reason.Trim(), null, request.CompletedAt));
        aiRuns.Add(request.RunId, new(request.AgentId, request.ModelId, fingerprint, decision));
    }

    private static IReadOnlyDictionary<string, decimal> FxMap(IReadOnlyCollection<InstrumentDto> instruments)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var instrument in instruments)
        {
            _ = CurrentPriceDkk(instrument, DelayedNasdaqInstrumentStore.NordicDataMode);
            var fxToDkk = instrument.FxToDkk!.Value;
            if (result.TryGetValue(instrument.Currency, out var existing) && existing != fxToDkk)
                throw new PreviewOrderException("inconsistent-fx", "Nordic snapshot contains conflicting DKK FX.");
            result[instrument.Currency] = fxToDkk;
        }
        return result;
    }

    private InstrumentDto[] Snapshot(InstrumentListDto snapshot, DateTimeOffset asOf)
    {
        if (snapshot.DataMode is not (DelayedNasdaqInstrumentStore.DataMode or DelayedNasdaqInstrumentStore.NordicDataMode))
            throw new PreviewOrderException("invalid-data-mode", "Assumed fills require an official delayed Nasdaq data mode.");
        if (snapshot.DataMode != persistedDataMode)
            throw new PreviewOrderException("exhibition-mode-mismatch",
                "The delayed snapshot does not match the configured exhibition mode.");
        var instruments = snapshot.Items?.ToArray()
            ?? throw new PreviewOrderException("invalid-snapshot", "A current delayed snapshot is required.");
        if (snapshot.DataMode == DelayedNasdaqInstrumentStore.NordicDataMode)
            foreach (var instrument in instruments) _ = CurrentPriceDkk(instrument, snapshot.DataMode, asOf);
        return instruments;
    }

    private static void RefreshPersistedMarks(Account account, IReadOnlyCollection<InstrumentDto> current,
        string dataMode)
    {
        foreach (var stale in account.Marks.Keys.Except(account.Holdings.Keys, StringComparer.Ordinal).ToArray())
            account.Marks.Remove(stale);
        foreach (var holding in account.Holdings)
        {
            var instrument = current.SingleOrDefault(item => item.Id == holding.Key)
                ?? throw new PreviewOrderException("stale-portfolio-mark",
                    "AI exhibition portfolio cannot be persisted without a current delayed observation.");
            account.Marks[holding.Key] = instrument with
            {
                PriceDkk = Money(CurrentPriceDkk(instrument, dataMode))
            };
        }
    }

    private static decimal CurrentPriceDkk(InstrumentDto instrument, string dataMode,
        DateTimeOffset? asOf = null)
    {
        if (instrument.Price <= 0m || instrument.ExecutedAt is null || instrument.AvailableAt is null ||
            string.IsNullOrWhiteSpace(instrument.Source))
            throw new PreviewOrderException("invalid-observation",
                "Portfolio marks require a verified current delayed observation.");
        if (dataMode == DelayedNasdaqInstrumentStore.DataMode)
        {
            if (!ValidStockholmIdentity(instrument.Id) || instrument.Exchange != "XSTO" ||
                instrument.Currency != "SEK" || instrument.FxToDkk is not null ||
                instrument.FxReferenceDate is not null || instrument.FxAvailableAt is not null ||
                instrument.FxSource is not null || instrument.FxSha256 is not null)
                throw new PreviewOrderException("invalid-observation",
                    "Stockholm marks must retain the strict XSTO/SEK identity and conversion contract.");
            return instrument.Price * AssumedSekToDkk;
        }
        if (dataMode != DelayedNasdaqInstrumentStore.NordicDataMode ||
            !ValidNordicIdentity(instrument.Id, instrument.Exchange, instrument.Currency) ||
            instrument.PriceDkk is null or <= 0m || instrument.FxToDkk is null or <= 0m ||
            instrument.PriceDkk != instrument.Price * instrument.FxToDkk.Value ||
            instrument.FxReferenceDate is null || instrument.FxAvailableAt is null ||
            asOf is not null && !ValidFxTiming(instrument.FxReferenceDate, instrument.FxAvailableAt, asOf.Value) ||
            instrument.FxSource != MarketDataProvenance.EcbInformationalReferenceRates ||
            !LowerSha256.IsMatch(instrument.FxSha256 ?? string.Empty))
            throw new PreviewOrderException("invalid-fx",
                "Nordic marks require canonical identity and complete verified DKK FX provenance.");
        return instrument.PriceDkk.Value;
    }

    private static bool StatusDetailsMatch(AiAccount account, string status, AiStatusRequestDto request) => status switch
    {
        "queued" => account.QueuedAt == request.OccurredAt && request.Error is null,
        "running" => account.StartedAt == request.OccurredAt && request.Error is null,
        "failed" => account.CompletedAt == request.OccurredAt &&
            string.Equals(account.Error, request.Error?.Trim(), StringComparison.Ordinal),
        _ => false
    };

    private static DateTimeOffset? LatestStatusAt(AiAccount account) =>
        account.CompletedAt ?? account.StartedAt ?? account.QueuedAt;

    private void AddAiActivity(AiActivityDto item)
    {
        aiActivity.Add(item);
        if (aiActivity.Count > 100) aiActivity.RemoveRange(0, aiActivity.Count - 100);
    }

    private static void ValidateAi(AiDecisionRequestDto request)
    {
        if (request.RunId is null || request.RunId.Length is < 8 or > 128 ||
            request.RunId.Any(character => character is < '!' or > '~'))
            throw new PreviewOrderException("invalid-run-id", "runId must contain 8-128 visible ASCII characters.");
        if (!ContestContract.IsExactAgent(request.AgentId, request.ModelId))
            throw new PreviewOrderException("agent-model-mismatch", "agentId and modelId must exactly match ContestContract.Agents.");
        var action = request.Action?.Trim().ToLowerInvariant();
        if (action is not ("buy" or "sell" or "hold"))
            throw new PreviewOrderException("invalid-action", "action must be buy, sell, or hold.");
        if (request.Reason is null || request.Reason.Trim().Length is < 1 or > 2_000)
            throw new PreviewOrderException("invalid-reason", "reason must contain 1-2,000 characters.");
        if (request.Confidence is < 0m or > 1m)
            throw new PreviewOrderException("invalid-confidence", "confidence must be between 0 and 1.");
        if (request.Evidence is null || request.Evidence.Count > 20 ||
            (action is "buy" or "sell" && request.Evidence.Count < 1) ||
            request.Evidence.Any(evidence =>
                !Uri.TryCreate(evidence.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                evidence.PublishedAt == default || string.IsNullOrWhiteSpace(evidence.ExactExcerpt) ||
                evidence.ExactExcerpt.Length > 2_000 || !LowerSha256.IsMatch(evidence.ContentSha256 ?? string.Empty)))
            throw new PreviewOrderException("invalid-evidence", "Trades require 1-20 and holds allow 0-20 verified HTTPS evidence items.");
        if (!string.Equals(request.RuntimeProvider, "copilot", StringComparison.Ordinal) ||
            !string.Equals(request.RuntimeModel, request.ModelId, StringComparison.Ordinal) ||
            !LowerSha256.IsMatch(request.ReportSha256 ?? string.Empty) || request.CompletedAt == default)
            throw new PreviewOrderException("invalid-attestation", "Runtime provider/model, report SHA-256, and completion time are required.");
        if (action == "hold" && (request.InstrumentId is not null || request.Quantity != 0))
            throw new PreviewOrderException("invalid-hold", "hold requires null instrumentId and zero quantity.");
        if (action is "buy" or "sell" && (string.IsNullOrWhiteSpace(request.InstrumentId) || request.Quantity is < 1 or > 100_000))
            throw new PreviewOrderException("invalid-trade", "buy and sell require an instrumentId and quantity between 1 and 100,000.");
    }

    public PreviewSubmission Submit(string identity, string idempotencyKey, HumanOrderRequestDto request)
    {
        if (idempotencyKey.Length is < 8 or > 128 || idempotencyKey.Any(character => character > 127 || char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new PreviewOrderException("invalid-idempotency-key", "Idempotency-Key must contain 8-128 visible ASCII characters.");
        if (request.Quantity is < 1 or > 100_000)
            throw new PreviewOrderException("invalid-quantity", "Quantity must be between 1 and 100,000.");
        var side = request.Side?.Trim().ToLowerInvariant();
        if (side is not ("buy" or "sell"))
            throw new PreviewOrderException("invalid-side", "Side must be buy or sell.");
        var instrument = Instruments.SingleOrDefault(item => string.Equals(item.Id, request.InstrumentId, StringComparison.Ordinal));
        if (instrument is null)
            throw new PreviewOrderException("instrument-not-found", "Instrument is not available in preview data.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 500)
            throw new PreviewOrderException("note-too-long", "Human notes may contain at most 500 characters.");
        var fingerprint = $"{side}\n{instrument.Id}\n{request.Quantity}\n{note}";

        return PersistMutation<PreviewSubmission>(() =>
        {
            var account = AccountFor(identity);
            if (account.Idempotency.TryGetValue(idempotencyKey, out var prior))
            {
                if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new PreviewOrderException("idempotency-conflict", "Idempotency-Key was already used for a different order.");
                return new(prior.Order, true);
            }
            if (account.Idempotency.Count >= MaximumIdempotencyEntries)
                throw new PreviewOrderException("idempotency-capacity",
                    "The volatile preview account reached its idempotency capacity; restart it before submitting new orders.");
            var total = Money(instrument.PriceDkk!.Value * request.Quantity);
            account.Holdings.TryGetValue(instrument.Id, out var held);
            if (side == "buy")
            {
                if (account.CashDkk < total)
                    throw new PreviewOrderException("insufficient-cash", "The paper account has insufficient DKK cash.");
                account.CashDkk = Money(account.CashDkk - total);
                account.Holdings[instrument.Id] = held + request.Quantity;
                account.CostBasisDkk[instrument.Id] = Money(
                    account.CostBasisDkk.GetValueOrDefault(instrument.Id) + total);
            }
            else
            {
                if (held < request.Quantity)
                    throw new PreviewOrderException("insufficient-holdings", "The paper account does not hold enough shares.");
                account.CashDkk = Money(account.CashDkk + total);
                var averageCost = account.CostBasisDkk.GetValueOrDefault(instrument.Id) / held;
                if (held == request.Quantity)
                {
                    account.Holdings.Remove(instrument.Id);
                    account.CostBasisDkk.Remove(instrument.Id);
                }
                else
                {
                    account.Holdings[instrument.Id] = held - request.Quantity;
                    account.CostBasisDkk[instrument.Id] = Money(averageCost * (held - request.Quantity));
                }
            }

            var order = new PreviewOrderDto(Guid.NewGuid(), side, instrument.Id, instrument.Symbol,
                request.Quantity, instrument.PriceDkk.Value, total, "filled", note, clock.GetUtcNow());
            account.Orders.Add(order);
            account.Idempotency.Add(idempotencyKey, new(fingerprint, order));
            if (account.Orders.Count > 100) account.Orders.RemoveRange(0, account.Orders.Count - 100);
            return new(order, false);
        });
    }

    private Account AccountFor(string identity)
    {
        if (accounts.TryGetValue(identity, out var account)) return account;
        var displayName = identity.Split('@', 2)[0].Replace('.', ' ').Replace('-', ' ').Trim();
        account = new(string.IsNullOrWhiteSpace(displayName) ? "You" : ToTitleCase(displayName));
        accounts.Add(identity, account);
        return account;
    }

    private static PreviewPortfolioDto PortfolioFor(Account account, string dataMode = DataMode)
    {
        var holdings = account.Holdings.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
            {
                var instrument = Instruments.Single(candidate => candidate.Id == item.Key);
                return Holding(account, instrument, item.Value, instrument.PriceDkk!.Value);
            }).ToArray();
        var holdingsValue = Money(holdings.Sum(item => item.ValueDkk));
        var total = Money(account.CashDkk + holdingsValue);
        return new(account.DisplayName, StartingCashDkk, account.CashDkk, holdingsValue, total,
            Percent((total - StartingCashDkk) / StartingCashDkk * 100m), holdings, dataMode);
    }

    private static PreviewPortfolioDto PortfolioFor(Account account, string dataMode,
        IReadOnlyCollection<InstrumentDto> current)
    {
        var holdings = account.Holdings.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
            {
                var instrument = current.SingleOrDefault(candidate => candidate.Id == item.Key)
                    ?? throw new PreviewOrderException("stale-portfolio-mark",
                        "AI exhibition portfolio cannot be valued without a current delayed observation.");
                var priceDkk = Money(CurrentPriceDkk(instrument, dataMode));
                return Holding(account, instrument, item.Value, priceDkk);
            }).ToArray();
        var holdingsValue = Money(holdings.Sum(item => item.ValueDkk));
        var total = Money(account.CashDkk + holdingsValue);
        var executionMode = dataMode == DelayedNasdaqInstrumentStore.NordicDataMode
            ? NordicAssumedExecutionMode
            : AssumedExecutionMode;
        return new(account.DisplayName, StartingCashDkk, account.CashDkk, holdingsValue, total,
            Percent((total - StartingCashDkk) / StartingCashDkk * 100m), holdings, dataMode,
            executionMode);
    }

    private static InstrumentDto Instrument(string id, string symbol, string name, string exchange,
        string country, string currency, decimal price, decimal fxToDkk) =>
        new(id, symbol, name, exchange, country, currency, price, Money(price * fxToDkk), true);

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static decimal Percent(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static PreviewHoldingDto Holding(Account account, InstrumentDto instrument, int quantity, decimal priceDkk)
    {
        var value = Money(priceDkk * quantity);
        var cost = account.CostBasisDkk.GetValueOrDefault(instrument.Id);
        var average = quantity == 0 ? 0m : Money(cost / quantity);
        var gain = Money(value - cost);
        var gainPercent = cost == 0m ? 0m : Percent(gain / cost * 100m);
        return new(instrument.Id, instrument.Symbol, instrument.Name, quantity, priceDkk, value,
            average, cost, gain, gainPercent);
    }

    private static void RecordPerformance(Account account, DateTimeOffset at, decimal valueDkk)
    {
        var point = new PerformancePointDto(at, Money(valueDkk));
        if (account.Performance.Count > 0 && account.Performance[^1].At == at)
            account.Performance[^1] = point;
        else account.Performance.Add(point);
    }

    private IReadOnlyList<PerformanceSeriesDto> BuildPerformance(IReadOnlyList<AiProgressAgentDto> participants)
    {
        var asOf = clock.GetUtcNow();
        var models = participants.Select(participant =>
        {
            var account = aiAccounts[participant.AgentId].Account;
            var points = account.Performance.ToList();
            if (points.Count == 0) points.Add(new(asOf, participant.Portfolio.TotalValueDkk));
            else if (asOf > points[^1].At) points.Add(new(asOf, participant.Portfolio.TotalValueDkk));
            return new PerformanceSeriesDto(participant.ModelId, participant.DisplayName, "model", points);
        }).ToArray();
        var times = models.SelectMany(series => series.Points).Select(point => point.At)
            .Distinct().Order().ToArray();
        decimal ValueAt(PerformanceSeriesDto series, DateTimeOffset at) =>
            series.Points.LastOrDefault(point => point.At <= at)?.ValueDkk ?? StartingCashDkk;
        var starting = times.Select(at => new PerformancePointDto(at, StartingCashDkk)).ToArray();
        var average = times.Select(at => new PerformancePointDto(at,
            Money(models.Average(series => ValueAt(series, at))))).ToArray();
        return [..models,
            new("starting-cash", "Starting cash", "benchmark", starting),
            new("ai-field-average", "AI field average", "benchmark", average)];
    }

    private static string ToTitleCase(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

    private sealed class Account(string displayName)
    {
        public string DisplayName { get; } = displayName;
        public decimal CashDkk { get; set; } = StartingCashDkk;
        public Dictionary<string, int> Holdings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, decimal> CostBasisDkk { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, InstrumentDto> Marks { get; } = new(StringComparer.Ordinal);
        public List<PerformancePointDto> Performance { get; } = [];
        public List<PreviewOrderDto> Orders { get; } = [];
        public Dictionary<string, IdempotencyEntry> Idempotency { get; } = new(StringComparer.Ordinal);
    }

    private sealed record IdempotencyEntry(string Fingerprint, PreviewOrderDto Order);
    private sealed record AiRun(Guid AgentId, string ModelId, string Fingerprint, AiDecisionDto Decision);

    private sealed class AiAccount(Guid agentId, string modelId)
    {
        public Guid AgentId { get; } = agentId;
        public string ModelId { get; } = modelId;
        public string Status { get; set; } = "pending";
        public string? RunId { get; set; }
        public DateTimeOffset? QueuedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Error { get; set; }
        public Account Account { get; } = new(modelId);
        public AiDecisionDto? LatestDecision { get; set; }
        public HashSet<string> SeenRunIds { get; } = new(StringComparer.Ordinal);
    }
}

public sealed record PreviewSubmission(PreviewOrderDto Order, bool Replayed);
public sealed record AiDecisionSubmission(AiDecisionDto Decision, bool Replayed);

public sealed class PreviewOrderException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
