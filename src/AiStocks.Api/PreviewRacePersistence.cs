using System.Text.Json;
using AiStocks.Core;

namespace AiStocks.Api;

public sealed record PreviewRacePersistedState(long Revision, string Json);

public interface IPreviewRaceStatePersistence
{
    PreviewRacePersistedState? Load();
    PreviewRacePersistedState Save(long? expectedRevision, string json, Guid mutationId);
    bool WasCommitted(Guid mutationId);
}

public sealed class PreviewRacePersistenceException(string message) : InvalidOperationException(message);

public sealed partial class PreviewRaceStore
{
    private const int PersistenceSchemaVersion = 1;
    private const int MaximumPersistedStateCharacters = 4 * 1024 * 1024;

    private T PersistMutation<T>(Func<T> mutation)
    {
        lock (sync)
        {
            if (persistence is null) return mutation();
            RefreshDurableState();
            var before = SerializeState();
            var beforeRevision = persistenceRevision;
            T result;
            try
            {
                result = mutation();
            }
            catch
            {
                RestoreState(before);
                persistenceRevision = beforeRevision;
                throw;
            }

            var after = SerializeState();
            if (JsonEquivalent(before, after)) return result;
            var mutationId = Guid.NewGuid();
            try
            {
                var saved = persistence.Save(persistenceRevision, after, mutationId);
                persistenceRevision = saved.Revision;
                return result;
            }
            catch (PreviewRacePersistenceException)
            {
                PreviewRacePersistedState? durable = null;
                var committed = false;
                try
                {
                    committed = persistence.WasCommitted(mutationId);
                    durable = persistence.Load();
                }
                catch (PreviewRacePersistenceException) { }
                if (committed && durable is not null)
                {
                    RestoreState(durable.Json);
                    persistenceRevision = durable.Revision;
                    return result;
                }
                if (durable is not null)
                {
                    RestoreState(durable.Json);
                    persistenceRevision = durable.Revision;
                }
                else
                {
                    RestoreState(before);
                    persistenceRevision = beforeRevision;
                }
                throw;
            }
        }
    }

    private void RefreshDurableState()
    {
        if (persistence is null) return;
        var durable = persistence.Load();
        if (durable is null)
        {
            if (persistenceRevision is not null)
                throw new PreviewRacePersistenceException("Durable exhibition state disappeared.");
            return;
        }
        if (durable.Revision == persistenceRevision) return;
        RestoreState(durable.Json);
        persistenceRevision = durable.Revision;
    }

    private static bool JsonEquivalent(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void PersistMutationVoid(Action mutation) => PersistMutation(() =>
    {
        mutation();
        return true;
    });

    private string SerializeState()
    {
        var state = new PersistedState(
            PersistenceSchemaVersion,
            aiAccounts.Values.OrderBy(item => item.AgentId).Select(item => new PersistedAiAccount(
                item.AgentId, item.ModelId, item.Status, item.RunId, item.QueuedAt, item.StartedAt,
                item.CompletedAt, item.Error, item.LatestDecision, item.SeenRunIds.Order(StringComparer.Ordinal).ToArray(),
                item.Account.CashDkk, new(item.Account.Holdings, StringComparer.Ordinal),
                new(item.Account.CostBasisDkk, StringComparer.Ordinal), new(item.Account.Marks, StringComparer.Ordinal),
                item.Account.Performance.ToArray())).ToArray(),
            aiRuns.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PersistedAiRun(item.Key, item.Value.AgentId, item.Value.ModelId,
                    item.Value.Fingerprint, item.Value.Decision)).ToArray(),
            aiActivity.ToArray());
        return JsonSerializer.Serialize(state);
    }

    private void RestoreState(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumPersistedStateCharacters)
            throw new PreviewRacePersistenceException("Persisted preview state is empty or oversized.");
        PersistedState state;
        try
        {
            state = JsonSerializer.Deserialize<PersistedState>(json)
                ?? throw new JsonException("Persisted preview state is null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _ = exception;
            throw new PreviewRacePersistenceException("Persisted preview state is malformed.");
        }
        if (state.Accounts is null || state.Runs is null || state.Activity is null ||
            state.Accounts.Any(item => item is null) || state.Runs.Any(item => item is null) ||
            state.Activity.Any(item => item is null) ||
            state.SchemaVersion != PersistenceSchemaVersion || state.Accounts.Length != ContestContract.Agents.Count ||
            state.Runs.Length > MaximumIdempotencyEntries || state.Activity.Length > 100)
            throw new PreviewRacePersistenceException("Persisted preview state violates its bounded schema.");

        var restored = new Dictionary<Guid, AiAccount>();
        foreach (var persisted in state.Accounts)
        {
            if (!ContestContract.IsExactAgent(persisted.AgentId, persisted.ModelId) ||
                !restored.TryAdd(persisted.AgentId, RestoreAccount(persisted)))
                throw new PreviewRacePersistenceException("Persisted preview state contains an invalid agent identity.");
        }
        if (ContestContract.Agents.Any(agent => !restored.ContainsKey(agent.Id)))
            throw new PreviewRacePersistenceException("Persisted preview state omits a fixed agent.");

        var restoredRuns = new Dictionary<string, AiRun>(StringComparer.Ordinal);
        foreach (var run in state.Runs)
            if (!ContestContract.IsExactAgent(run.AgentId, run.ModelId) || !ValidRunId(run.RunId) ||
                !Bounded(run.Fingerprint, 1_000_000) || run.Decision is null ||
                !string.Equals(run.RunId, run.Decision.RunId, StringComparison.Ordinal) ||
                !ValidDecision(run.Decision, run.ModelId) ||
                !restoredRuns.TryAdd(run.RunId, new(run.AgentId, run.ModelId, run.Fingerprint, run.Decision)))
                throw new PreviewRacePersistenceException("Persisted preview state contains an invalid run identity.");

        var activityRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activity in state.Activity)
            if (!ContestContract.IsExactAgent(activity.AgentId, activity.ModelId) || !ValidRunId(activity.RunId) ||
                activity.Status is not ("succeeded" or "failed") || !BoundedOptional(activity.Action, 16) ||
                !BoundedOptional(activity.Reason, 2_000) || !BoundedOptional(activity.Error, 1_000) ||
                (activity.Status == "failed") != !string.IsNullOrWhiteSpace(activity.Error) ||
                activity.OccurredAt == default ||
                (activity.Status == "succeeded" &&
                 (!restoredRuns.TryGetValue(activity.RunId, out var activityRun) ||
                  activityRun.AgentId != activity.AgentId || activityRun.ModelId != activity.ModelId ||
                  activity.Action != activityRun.Decision.Action || activity.Reason != activityRun.Decision.Reason ||
                  activity.OccurredAt != activityRun.Decision.CompletedAt)) ||
                (!restored.TryGetValue(activity.AgentId, out var activityAccount) ||
                 !activityAccount.SeenRunIds.Contains(activity.RunId)) ||
                (activity.Status == "failed" && (activity.Action is not null || activity.Reason is not null)) ||
                !activityRunIds.Add(activity.RunId))
                throw new PreviewRacePersistenceException("Persisted preview activity violates its bounded schema.");

        var missingActivityRuns = restoredRuns.Values
            .Where(run => !activityRunIds.Contains(run.Decision.RunId)).ToArray();
        if (missingActivityRuns.Length > 0 &&
            (state.Activity.Length < 100 ||
             missingActivityRuns.Any(run => run.Decision.CompletedAt > state.Activity.Min(item => item.OccurredAt))))
            throw new PreviewRacePersistenceException("Persisted preview accepted run has no retained or truncated activity record.");

        foreach (var persisted in state.Accounts)
        {
            if (persisted.Status != "pending" && !persisted.SeenRunIds.Contains(persisted.RunId!, StringComparer.Ordinal))
                throw new PreviewRacePersistenceException("Persisted preview account has incoherent run state.");
            if (persisted.Status == "succeeded" &&
                (!restoredRuns.TryGetValue(persisted.RunId!, out var currentRun) ||
                 currentRun.AgentId != persisted.AgentId || currentRun.ModelId != persisted.ModelId))
                throw new PreviewRacePersistenceException("Persisted preview account has no accepted decision for its run.");
            if (persisted.Status == "succeeded" &&
                (persisted.LatestDecision is null || persisted.LatestDecision.RunId != persisted.RunId ||
                 persisted.LatestDecision.CompletedAt != persisted.CompletedAt))
                throw new PreviewRacePersistenceException("Persisted preview account has no latest decision for its run.");
            if (persisted.LatestDecision is not null &&
                (!ValidDecision(persisted.LatestDecision, persisted.ModelId) ||
                 !restoredRuns.TryGetValue(persisted.LatestDecision.RunId, out var accepted) ||
                 accepted.AgentId != persisted.AgentId || accepted.ModelId != persisted.ModelId ||
                 !JsonEquivalent(JsonSerializer.Serialize(persisted.LatestDecision), JsonSerializer.Serialize(accepted.Decision))))
                throw new PreviewRacePersistenceException("Persisted preview account has an incoherent latest decision.");
        }

        var runOwners = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var persisted in state.Accounts)
            foreach (var runId in persisted.SeenRunIds)
                if (!runOwners.TryAdd(runId, persisted.AgentId))
                    throw new PreviewRacePersistenceException("Persisted preview run belongs to multiple accounts.");
        foreach (var run in restoredRuns)
            if (!runOwners.TryGetValue(run.Key, out var owner) || owner != run.Value.AgentId)
                throw new PreviewRacePersistenceException("Persisted preview accepted run has no owning account.");
        foreach (var persisted in state.Accounts)
            if (!ValidAccounting(persisted, restoredRuns.Values.Where(run => run.AgentId == persisted.AgentId)))
                throw new PreviewRacePersistenceException("Persisted preview accounting does not match accepted fills.");

        aiAccounts.Clear();
        foreach (var item in restored) aiAccounts.Add(item.Key, item.Value);
        aiRuns.Clear();
        foreach (var item in restoredRuns) aiRuns.Add(item.Key, item.Value);
        aiActivity.Clear();
        aiActivity.AddRange(state.Activity);
    }

    private static AiAccount RestoreAccount(PersistedAiAccount persisted)
    {
        if (persisted.SeenRunIds is null || persisted.Holdings is null || persisted.CostBasisDkk is null ||
            persisted.Marks is null || persisted.Performance is null || persisted.CashDkk < 0m ||
            persisted.SeenRunIds.Length > MaximumIdempotencyEntries ||
            persisted.Performance.Length > 10_000 || persisted.Holdings.Any(item => item.Value <= 0) ||
            persisted.CostBasisDkk.Any(item => item.Value <= 0m) ||
            persisted.Holdings.Count > 10_000 || persisted.Marks.Count > 10_000 ||
            persisted.Status is not ("pending" or "queued" or "running" or "succeeded" or "failed") ||
            !BoundedOptional(persisted.RunId, 128) || !BoundedOptional(persisted.Error, 1_000) ||
            persisted.SeenRunIds.Any(runId => !ValidRunId(runId)) ||
            persisted.SeenRunIds.Distinct(StringComparer.Ordinal).Count() != persisted.SeenRunIds.Length ||
            persisted.Holdings.Keys.Any(key => !Bounded(key, 128)) ||
            persisted.Marks.Count != persisted.Holdings.Count ||
            persisted.Marks.Keys.Any(key => !persisted.Holdings.ContainsKey(key)) ||
            persisted.Marks.Any(item => !Bounded(item.Key, 128) || item.Value is null ||
                !string.Equals(item.Key, item.Value.Id, StringComparison.Ordinal) || item.Value.Price <= 0m ||
                item.Value.PriceDkk != Money(item.Value.Price * AssumedSekToDkk) ||
                !Bounded(item.Value.Symbol, 64) || !Bounded(item.Value.Name, 512) ||
                !Bounded(item.Value.Exchange, 64) || !Bounded(item.Value.Country, 64) || !Bounded(item.Value.Currency, 16) ||
                item.Value.ExecutedAt is null || item.Value.AvailableAt is null || item.Value.DelayMinutes is null or < 0 ||
                !Bounded(item.Value.Source, 512) || item.Value.ExecutedAt > item.Value.AvailableAt ||
                item.Value.IsPreviewPrice || item.Value.Tradable != false || item.Value.PaperTradable != true) ||
            persisted.Performance.Any(item => item is null || item.At == default || item.ValueDkk < 0m) ||
            persisted.Performance.Zip(persisted.Performance.Skip(1), (left, right) => left.At > right.At).Any(value => value) ||
            !ValidLifecycle(persisted))
            throw new PreviewRacePersistenceException("Persisted preview account violates its bounded schema.");
        if (persisted.Holdings.Keys.Except(persisted.CostBasisDkk.Keys, StringComparer.Ordinal).Any() ||
            persisted.CostBasisDkk.Keys.Except(persisted.Holdings.Keys, StringComparer.Ordinal).Any())
            throw new PreviewRacePersistenceException("Persisted holdings and cost basis disagree.");

        var account = new AiAccount(persisted.AgentId, persisted.ModelId)
        {
            Status = persisted.Status,
            RunId = persisted.RunId,
            QueuedAt = persisted.QueuedAt,
            StartedAt = persisted.StartedAt,
            CompletedAt = persisted.CompletedAt,
            Error = persisted.Error,
            LatestDecision = persisted.LatestDecision
        };
        account.SeenRunIds.UnionWith(persisted.SeenRunIds);
        account.Account.CashDkk = persisted.CashDkk;
        foreach (var item in persisted.Holdings) account.Account.Holdings.Add(item.Key, item.Value);
        foreach (var item in persisted.CostBasisDkk) account.Account.CostBasisDkk.Add(item.Key, item.Value);
        foreach (var item in persisted.Marks) account.Account.Marks.Add(item.Key, item.Value);
        account.Account.Performance.AddRange(persisted.Performance.OrderBy(item => item.At));
        return account;
    }

    private static bool ValidLifecycle(PersistedAiAccount account) => account.Status switch
    {
        "pending" => account.RunId is null && account.QueuedAt is null && account.StartedAt is null &&
                     account.CompletedAt is null && account.Error is null,
        "queued" => ValidRunId(account.RunId) && account.QueuedAt is not null && account.QueuedAt.Value != default &&
                    account.StartedAt is null &&
                    account.CompletedAt is null && account.Error is null,
        "running" => ValidRunId(account.RunId) && account.QueuedAt is not null && account.QueuedAt.Value != default &&
                     account.StartedAt is not null && account.StartedAt.Value != default && account.QueuedAt <= account.StartedAt &&
                     account.CompletedAt is null && account.Error is null,
        "succeeded" => ValidRunId(account.RunId) && account.QueuedAt is not null && account.QueuedAt.Value != default &&
                       account.StartedAt is not null && account.StartedAt.Value != default &&
                       account.CompletedAt is not null && account.CompletedAt.Value != default &&
                       account.QueuedAt <= account.StartedAt && account.StartedAt <= account.CompletedAt && account.Error is null,
        "failed" => ValidRunId(account.RunId) && account.QueuedAt is not null && account.QueuedAt.Value != default &&
                    account.CompletedAt is not null && account.CompletedAt.Value != default && account.QueuedAt <= account.CompletedAt &&
                    (account.StartedAt is null || account.QueuedAt <= account.StartedAt && account.StartedAt <= account.CompletedAt) &&
                    !string.IsNullOrWhiteSpace(account.Error),
        _ => false
    };

    private static bool ValidDecision(AiDecisionDto decision, string expectedModelId) =>
        ValidRunId(decision.RunId) && Bounded(decision.Action, 16) && Bounded(decision.Reason, 2_000) &&
        decision.Action is "hold" or "buy" or "sell" && decision.Quantity >= 0 &&
        (decision.Action == "hold" ? decision.Quantity == 0 && decision.InstrumentId is null
            : decision.Quantity > 0 && Bounded(decision.InstrumentId, 128)) &&
        decision.Confidence is >= 0m and <= 1m && decision.CompletedAt != default && decision.Evidence is not null &&
        decision.Evidence.Count <= 20 && (decision.Action == "hold" || decision.Evidence.Count > 0) &&
        decision.Attestation is not null &&
        decision.Evidence.All(item => item is not null && HttpsUrl(item.Url) &&
            item.PublishedAt != default && item.PublishedAt <= decision.CompletedAt &&
            Bounded(item.ExactExcerpt, 2_000) && Sha256(item.ContentSha256)) &&
        decision.Attestation.RuntimeProvider == "copilot" &&
        decision.Attestation.RuntimeModel == expectedModelId &&
        Sha256(decision.Attestation.ReportSha256) && ValidFill(decision);

    private static bool ValidFill(AiDecisionDto decision)
    {
        var fill = decision.AssumedPaperFill;
        if (decision.Action == "hold") return fill is null;
        if (fill is null || fill.ObservedPriceSek <= 0m || fill.AssumedSekToDkk != AssumedSekToDkk ||
            fill.AssumedSlippagePercent != AssumedSlippagePercent ||
            fill.ObservationExecutedAt == default || fill.ObservationAvailableAt == default || fill.FilledAt == default ||
            fill.ObservationExecutedAt > fill.ObservationAvailableAt ||
            fill.ObservationAvailableAt > decision.CompletedAt || fill.FilledAt < decision.CompletedAt ||
            fill.ExecutionMode != AssumedExecutionMode)
            return false;
        var expectedFillPrice = Money(fill.ObservedPriceSek * AssumedSekToDkk *
            (decision.Action == "buy" ? 1m + (AssumedSlippagePercent / 100m) :
                1m - (AssumedSlippagePercent / 100m)));
        return fill.FillPriceDkk == expectedFillPrice && fill.TotalDkk == Money(expectedFillPrice * decision.Quantity);
    }

    private static bool ValidAccounting(PersistedAiAccount account, IEnumerable<AiRun> acceptedRuns)
    {
        var runs = acceptedRuns.OrderBy(item => item.Decision.CompletedAt).ToArray();
        if (runs.Select(item => item.Decision.CompletedAt).Distinct().Count() != runs.Length ||
            account.Performance.Select(item => item.At).Distinct().Count() != account.Performance.Length ||
            (account.SeenRunIds.Length > 0 && account.Performance.Length == 0) ||
            (account.Performance.Length > 0 && account.Performance[0].ValueDkk != StartingCashDkk) ||
            runs.Any(run => account.Performance.All(point => point.At != run.Decision.CompletedAt)))
            return false;

        var cash = StartingCashDkk;
        var holdings = new Dictionary<string, int>(StringComparer.Ordinal);
        var costBasis = new Dictionary<string, decimal>(StringComparer.Ordinal);
        DateTimeOffset? previousCompletedAt = null;
        foreach (var run in runs)
        {
            var decision = run.Decision;
            if (previousCompletedAt is not null && decision.CompletedAt <= previousCompletedAt) return false;
            previousCompletedAt = decision.CompletedAt;
            if (decision.Action == "hold") continue;
            var instrumentId = decision.InstrumentId!;
            var fill = decision.AssumedPaperFill!;
            holdings.TryGetValue(instrumentId, out var held);
            if (decision.Action == "buy")
            {
                cash = Money(cash - fill.TotalDkk);
                if (cash < 0m) return false;
                holdings[instrumentId] = held + decision.Quantity;
                costBasis[instrumentId] = Money(costBasis.GetValueOrDefault(instrumentId) + fill.TotalDkk);
                continue;
            }
            if (held < decision.Quantity || !costBasis.TryGetValue(instrumentId, out var existingCost)) return false;
            cash = Money(cash + fill.TotalDkk);
            if (held == decision.Quantity)
            {
                holdings.Remove(instrumentId);
                costBasis.Remove(instrumentId);
            }
            else
            {
                holdings[instrumentId] = held - decision.Quantity;
                costBasis[instrumentId] = Money((existingCost / held) * (held - decision.Quantity));
            }
        }
        var currentValue = Money(cash + account.Holdings.Sum(item =>
            item.Value * account.Marks[item.Key].PriceDkk!.Value));
        return cash == account.CashDkk && DictionaryEqual(holdings, account.Holdings) &&
               DictionaryEqual(costBasis, account.CostBasisDkk) &&
               (account.Performance.Length == 0 || account.Performance[^1].ValueDkk == currentValue);
    }

    private static bool DictionaryEqual<T>(IReadOnlyDictionary<string, T> left,
        IReadOnlyDictionary<string, T> right) where T : IEquatable<T> =>
        left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && item.Value.Equals(value));

    private static bool Bounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static bool BoundedOptional(string? value, int maximum) => value is null || value.Length <= maximum;

    private static bool ValidRunId(string? value) => value is { Length: >= 8 and <= 128 } &&
        value.All(character => character is >= '!' and <= '~');

    private static bool Sha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HttpsUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && Bounded(value, 2_048);

    private sealed record PersistedState(int SchemaVersion, PersistedAiAccount[] Accounts,
        PersistedAiRun[] Runs, AiActivityDto[] Activity);

    private sealed record PersistedAiAccount(Guid AgentId, string ModelId, string Status, string? RunId,
        DateTimeOffset? QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Error,
        AiDecisionDto? LatestDecision, string[] SeenRunIds, decimal CashDkk,
        Dictionary<string, int> Holdings, Dictionary<string, decimal> CostBasisDkk,
        Dictionary<string, InstrumentDto> Marks, PerformancePointDto[] Performance);

    private sealed record PersistedAiRun(string RunId, Guid AgentId, string ModelId, string Fingerprint,
        AiDecisionDto Decision);
}
