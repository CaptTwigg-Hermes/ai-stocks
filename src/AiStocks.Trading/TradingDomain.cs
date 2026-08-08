using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AiStocks.Core;

namespace AiStocks.Trading;

public sealed class TradingException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed record TradingSession(string Id, DateTimeOffset OpenAt, DateTimeOffset CloseAt)
{
    public bool Contains(DateTimeOffset value) => value >= OpenAt && value <= CloseAt;
}

public sealed record AuditEvent(
    long Sequence, Guid AgentId, string Type, DateTimeOffset OccurredAt, string Reference,
    decimal CashDelta = 0m, InstrumentId? Instrument = null, int QuantityDelta = 0,
    decimal AverageCostAfter = 0m, string Detail = "");

public sealed record PaperOrder(
    Guid Id, OrderDecision Decision, OrderStatus Status, DateTimeOffset CreatedAt,
    Guid? ReplacedBy = null, string? LifecycleReason = null, OrderOutcome? Outcome = null);

public sealed record FrozenEntitlement(
    Guid AgentId, InstrumentId Instrument, decimal FractionalQuantity, string Reference);

public sealed record FinalStanding(
    Guid AgentId, string ModelId, int Rank, decimal NetLiquidationValue,
    IReadOnlyList<OrderOutcome> Liquidations);

public static class ExecutionMath
{
    public static decimal AdverseSlippageRate(VerifiedMarketObservation quote, decimal orderValue)
    {
        if (orderValue < 0m || quote.AverageDailyValue20 <= 0m)
        {
            throw new TradingException("market-data", "Order value and ADV must be valid.");
        }

        var spread = 0.001m;
        if (quote.Bid is not null && quote.Ask is not null)
        {
            spread = decimal.Max(spread, (quote.Ask.Value - quote.Bid.Value) / (2m * quote.Price));
        }

        var impact = 0.0025m * DecimalMath.Sqrt(orderValue / quote.AverageDailyValue20);
        return decimal.Min(0.01m, spread + impact);
    }

    public static decimal ExecutionPrice(OrderSide side, VerifiedMarketObservation quote, decimal orderValue)
    {
        var slippage = AdverseSlippageRate(quote, orderValue);
        var multiplier = side == OrderSide.Buy ? 1m + slippage : 1m - slippage;
        return decimal.Round(quote.Price * multiplier, 4, MidpointRounding.AwayFromZero);
    }
}

public sealed class PaperTradingEngine
{
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private readonly Dictionary<Guid, Account> accounts;
    private readonly Dictionary<Guid, PaperOrder> orders = [];
    private readonly Dictionary<string, Guid> decisionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid AgentId, string Key), (string Hash, Guid OrderId)> lifecycleKeys = [];
    private readonly Dictionary<(Guid AgentId, string Reference), string> actionReferences = [];
    private readonly List<AuditEvent> audit = [];
    private readonly List<FrozenEntitlement> frozenEntitlements = [];
    private readonly List<(DateTimeOffset PausedAt, DateTimeOffset? ResumedAt)> pauses = [];
    private long sequence;
    private string? finalReference;
    private string? finalInput;
    private IReadOnlyList<FinalStanding>? finalStandings;

    private PaperTradingEngine(IEnumerable<AgentDefinition> agents)
    {
        accounts = agents.ToDictionary(agent => agent.Id,
            agent => new Account(agent, ContestContract.InitialCash));
        foreach (var account in accounts.Values.OrderBy(account => account.Agent.Id))
        {
            AddAudit(account.Agent.Id, "INITIAL_CASH", DateTimeOffset.MinValue, "contest", account.Cash);
        }
    }

    public ContestStatus Status { get; private set; } = ContestStatus.Running;
    public IReadOnlyList<AuditEvent> AuditTrail => audit.AsReadOnly();
    public IReadOnlyList<FrozenEntitlement> FrozenEntitlements => frozenEntitlements.AsReadOnly();
    public IReadOnlyList<PaperOrder> Orders => orders.Values.OrderBy(order => order.CreatedAt).ThenBy(order => order.Id).ToArray();

    public static PaperTradingEngine CreateContest() => new(ContestContract.Agents);

    public PortfolioSnapshot Portfolio(Guid agentId, DateTimeOffset? asOf = null)
    {
        var account = GetAccount(agentId);
        return new PortfolioSnapshot(agentId, Money.Round(account.Cash),
            account.Positions.Values.OrderBy(position => position.Instrument.Isin, StringComparer.Ordinal)
                .ThenBy(position => position.Instrument.OrderBookId, StringComparer.Ordinal).ToArray(),
            account.CompletedTradeCount, account.FeeTier, asOf ?? LastAuditAt());
    }

    public OrderOutcome Submit(
        OrderDecision decision,
        VerifiedMarketObservation? quote,
        TradingSession? session,
        IReadOnlyDictionary<InstrumentId, decimal> marks)
    {
        EnsureRunning();
        ValidateDecision(decision);
        if (decisionIds.TryGetValue(decision.DecisionId, out var existingId))
        {
            var existing = orders[existingId];
            if (!string.Equals(existing.Decision.CanonicalRequestSha256, decision.CanonicalRequestSha256,
                    StringComparison.Ordinal))
            {
                throw new TradingException("idempotency", "Decision id payload differs.");
            }

            return existing.Outcome ?? Outcome(existing, existing.Status, "replay", "Order replayed.");
        }

        var order = new PaperOrder(Guid.NewGuid(), decision, OrderStatus.Queued, decision.DecisionAt);
        orders.Add(order.Id, order);
        decisionIds.Add(decision.DecisionId, order.Id);
        AddAudit(decision.AgentId, "ORDER_QUEUED", decision.DecisionAt, decision.DecisionId,
            detail: decision.Action.ToString());
        if (quote is null || session is null || !session.Contains(decision.DecisionAt) ||
            quote.TradedAt < decision.DecisionAt)
        {
            return Outcome(order, OrderStatus.Queued, "queued", "Awaiting first eligible quote.");
        }

        return Fill(order.Id, quote, session, marks);
    }

    public OrderOutcome ExecuteQueued(
        Guid orderId,
        VerifiedMarketObservation quote,
        TradingSession session,
        IReadOnlyDictionary<InstrumentId, decimal> marks)
    {
        EnsureRunning();
        var order = GetQueuedOrder(orderId);
        if (IsDuringPause(quote.TradedAt))
        {
            return Outcome(order, OrderStatus.Queued, "paused-quote", "Quote from paused interval is ineligible.");
        }

        return Fill(orderId, quote, session, marks);
    }

    public OrderOutcome Cancel(Guid agentId, Guid orderId, string reason, string idempotencyKey,
        DateTimeOffset at)
    {
        EnsureRunning();
        ValidateLifecycle(reason, idempotencyKey);
        var hash = $"cancel|{agentId}|{orderId}|{reason}|{at:O}";
        if (ReplayLifecycle(agentId, idempotencyKey, hash) is { } replay)
        {
            return orders[replay].Outcome!;
        }

        var order = GetQueuedOrder(orderId);
        if (order.Decision.AgentId != agentId)
        {
            throw new TradingException("ownership", "Only the owning fixed agent may cancel an order.");
        }

        var outcome = Outcome(order, OrderStatus.Cancelled, "cancelled", reason);
        orders[orderId] = order with { Status = OrderStatus.Cancelled, LifecycleReason = reason, Outcome = outcome };
        lifecycleKeys[(agentId, idempotencyKey)] = (hash, orderId);
        AddAudit(agentId, "ORDER_CANCELLED", at, idempotencyKey, detail: reason);
        return outcome;
    }

    public OrderOutcome Replace(Guid agentId, Guid orderId, OrderDecision replacement, string reason,
        string idempotencyKey, DateTimeOffset at)
    {
        EnsureRunning();
        ValidateLifecycle(reason, idempotencyKey);
        var hash = $"replace|{agentId}|{orderId}|{replacement.CanonicalRequestSha256}|{reason}|{at:O}";
        if (ReplayLifecycle(agentId, idempotencyKey, hash) is { } replay)
        {
            return orders[replay].Outcome!;
        }

        var original = GetQueuedOrder(orderId);
        if (original.Decision.AgentId != agentId || replacement.AgentId != agentId)
        {
            throw new TradingException("ownership", "Replacement agent mismatch.");
        }
        if (replacement.DecisionAt < original.Decision.DecisionAt)
        {
            throw new TradingException("replacement-time", "Replacement precedes original decision.");
        }
        ValidateDecision(replacement);
        if (decisionIds.ContainsKey(replacement.DecisionId))
        {
            throw new TradingException("idempotency", "Replacement decision id exists.");
        }

        var replacementOrder = new PaperOrder(Guid.NewGuid(), replacement, OrderStatus.Queued, at);
        var replacementOutcome = Outcome(replacementOrder, OrderStatus.Queued, "queued", "Replacement queued.");
        replacementOrder = replacementOrder with { Outcome = replacementOutcome };
        orders.Add(replacementOrder.Id, replacementOrder);
        decisionIds.Add(replacement.DecisionId, replacementOrder.Id);
        var originalOutcome = Outcome(original, OrderStatus.Replaced, "replaced", reason);
        orders[orderId] = original with
        {
            Status = OrderStatus.Replaced,
            ReplacedBy = replacementOrder.Id,
            LifecycleReason = reason,
            Outcome = originalOutcome
        };
        lifecycleKeys[(agentId, idempotencyKey)] = (hash, replacementOrder.Id);
        AddAudit(agentId, "ORDER_REPLACED", at, idempotencyKey, detail: reason);
        return replacementOutcome;
    }

    public void Pause(DateTimeOffset at, string reason)
    {
        if (Status != ContestStatus.Running || string.IsNullOrWhiteSpace(reason))
        {
            throw new TradingException("pause-state", "Contest can only pause from running state with a reason.");
        }
        Status = ContestStatus.Paused;
        pauses.Add((at, null));
        AddAudit(Guid.Empty, "SYSTEM_PAUSED", at, $"pause:{sequence + 1}", detail: reason);
    }

    public void Resume(DateTimeOffset at, string reason)
    {
        if (Status != ContestStatus.Paused || string.IsNullOrWhiteSpace(reason) || at <= pauses[^1].PausedAt)
        {
            throw new TradingException("pause-state", "Contest can only resume after its active pause.");
        }
        pauses[^1] = (pauses[^1].PausedAt, at);
        Status = ContestStatus.Running;
        AddAudit(Guid.Empty, "SYSTEM_RESUMED", at, $"resume:{sequence + 1}", detail: reason);
    }

    public void ApplyDividend(Guid agentId, InstrumentId instrument, decimal perShare,
        DateTimeOffset ownershipClose, DateOnly exDate, DateTimeOffset paymentAt, string reference)
    {
        EnsureRunning();
        if (perShare < 0m || DateOnly.FromDateTime(ownershipClose.Date) >= exDate ||
            DateOnly.FromDateTime(paymentAt.Date) < exDate)
            throw new TradingException("corporate-action", "Dividend dates or amount are invalid.");
        var payload = $"dividend|{instrument}|{perShare}|{ownershipClose:O}|{exDate}|{paymentAt:O}";
        if (!BeginAction(agentId, reference, payload)) return;
        var quantity = QuantityAt(agentId, instrument, ownershipClose);
        var cash = Money.Round(perShare * quantity);
        var account = GetAccount(agentId);
        account.Cash = Money.Round(account.Cash + cash);
        AddAudit(agentId, "DIVIDEND", paymentAt, reference, cash, instrument,
            detail: $"entitled quantity {quantity}");
    }

    public void ApplySplit(Guid agentId, InstrumentId instrument, int numerator, int denominator,
        DateTimeOffset at, string reference)
    {
        EnsureRunning();
        if (numerator <= 0 || denominator <= 0)
            throw new TradingException("corporate-action", "Split ratio is invalid.");
        var payload = $"split|{instrument}|{numerator}|{denominator}|{at:O}";
        if (!BeginAction(agentId, reference, payload)) return;
        var account = GetAccount(agentId);
        if (!account.Positions.TryGetValue(instrument, out var position))
        {
            AddAudit(agentId, "SPLIT", at, reference, instrument: instrument);
            return;
        }
        var exact = position.Quantity * (decimal)numerator / denominator;
        var whole = decimal.ToInt32(decimal.Floor(exact));
        var fraction = exact - whole;
        var totalCost = position.AverageCost * position.Quantity;
        if (whole == 0) account.Positions.Remove(instrument);
        else account.Positions[instrument] = new Position(instrument, whole,
            decimal.Round(totalCost / exact, 4, MidpointRounding.AwayFromZero));
        if (fraction > 0m)
            frozenEntitlements.Add(new FrozenEntitlement(agentId, instrument, fraction, reference));
        AddAudit(agentId, "SPLIT", at, reference, instrument: instrument,
            quantityDelta: whole - position.Quantity,
            averageCostAfter: whole == 0 ? 0m : account.Positions[instrument].AverageCost);
    }

    public void ApplyStockMerger(Guid agentId, InstrumentId oldInstrument, InstrumentId newInstrument,
        int numerator, int denominator, DateTimeOffset at, string reference)
    {
        EnsureRunning();
        if (numerator <= 0 || denominator <= 0 || oldInstrument == newInstrument)
            throw new TradingException("corporate-action", "Stock merger ratio is invalid.");
        var payload = $"stock-merger|{oldInstrument}|{newInstrument}|{numerator}|{denominator}|{at:O}";
        if (!BeginAction(agentId, reference, payload)) return;
        var account = GetAccount(agentId);
        if (!account.Positions.TryGetValue(oldInstrument, out var old)) return;
        var exact = old.Quantity * (decimal)numerator / denominator;
        var whole = decimal.ToInt32(decimal.Floor(exact));
        var fraction = exact - whole;
        var totalCost = old.AverageCost * old.Quantity;
        account.Positions.Remove(oldInstrument);
        if (whole > 0)
        {
            var existing = account.Positions.GetValueOrDefault(newInstrument);
            var combinedQuantity = whole + (existing?.Quantity ?? 0);
            var combinedCost = totalCost + (existing?.AverageCost ?? 0m) * (existing?.Quantity ?? 0);
            account.Positions[newInstrument] = new Position(newInstrument, combinedQuantity,
                decimal.Round(combinedCost / combinedQuantity, 4, MidpointRounding.AwayFromZero));
        }
        if (fraction > 0m)
            frozenEntitlements.Add(new FrozenEntitlement(agentId, newInstrument, fraction, reference));
        AddAudit(agentId, "STOCK_MERGER_REMOVE", at, reference, instrument: oldInstrument,
            quantityDelta: -old.Quantity);
        AddAudit(agentId, "STOCK_MERGER_ADD", at, reference, instrument: newInstrument,
            quantityDelta: whole, averageCostAfter: whole == 0 ? 0m : account.Positions[newInstrument].AverageCost);
    }

    public void ApplyCashMerger(Guid agentId, InstrumentId instrument, decimal perShare,
        DateTimeOffset at, string reference)
    {
        EnsureRunning();
        if (perShare < 0m) throw new TradingException("corporate-action", "Cash consideration is invalid.");
        var payload = $"cash-merger|{instrument}|{perShare}|{at:O}";
        if (!BeginAction(agentId, reference, payload)) return;
        SettlePosition(agentId, instrument, perShare, at, reference, "CASH_MERGER");
    }

    public void ApplyDelisting(Guid agentId, InstrumentId instrument, decimal? officialProceeds,
        DateTimeOffset at, string reference)
    {
        EnsureRunning();
        if (officialProceeds < 0m) throw new TradingException("corporate-action", "Delisting proceeds are invalid.");
        var payload = $"delisting|{instrument}|{officialProceeds}|{at:O}";
        if (!BeginAction(agentId, reference, payload)) return;
        var account = GetAccount(agentId);
        if (officialProceeds is null)
        {
            account.Frozen.Add(instrument);
            AddAudit(agentId, "DELISTING_FROZEN", at, reference, instrument: instrument,
                detail: "zero pending reliable settlement");
        }
        else SettlePosition(agentId, instrument, officialProceeds.Value, at, reference, "DELISTING_SETTLED");
    }

    public void SettleDelisting(Guid agentId, InstrumentId instrument, decimal officialProceeds,
        DateTimeOffset at, string reference)
    {
        EnsureRunning();
        if (officialProceeds < 0m || !IsFrozen(agentId, instrument))
            throw new TradingException("corporate-action", "No frozen delisting can be settled.");
        var payload = $"delisting-settlement|{instrument}|{officialProceeds}|{at:O}";
        if (!BeginAction(agentId, reference, payload)) return;
        SettlePosition(agentId, instrument, officialProceeds, at, reference, "DELISTING_SETTLED");
        GetAccount(agentId).Frozen.Remove(instrument);
    }

    public bool IsFrozen(Guid agentId, InstrumentId instrument) => GetAccount(agentId).Frozen.Contains(instrument);

    public IReadOnlyList<FinalStanding> FinalLiquidation(
        IReadOnlyDictionary<InstrumentId, VerifiedMarketObservation> closingQuotes,
        TradingSession session, DateTimeOffset finalizedAt, string reference)
    {
        var input = string.Join(";", closingQuotes.OrderBy(item => item.Key.Isin, StringComparer.Ordinal)
            .ThenBy(item => item.Key.OrderBookId, StringComparer.Ordinal)
            .Select(item => $"{item.Key}|{item.Value.Price}|{item.Value.TradedAt:O}|{item.Value.RawSha256}")) +
            $"|{session.Id}|{session.OpenAt:O}|{session.CloseAt:O}|{finalizedAt:O}|{reference}";
        if (finalStandings is not null)
        {
            if (!string.Equals(finalReference, reference, StringComparison.Ordinal) ||
                !string.Equals(finalInput, input, StringComparison.Ordinal))
                throw new TradingException("final-conflict", "Final liquidation input differs.");
            return finalStandings;
        }
        EnsureRunning();
        if (session.CloseAt.Date != ContestContract.FinalTradingDate.ToDateTime(TimeOnly.MinValue).Date)
            throw new TradingException("final-date", "Liquidation is only valid on the final XSTO trading day.");
        if (string.IsNullOrWhiteSpace(reference) || finalizedAt < session.CloseAt)
            throw new TradingException("final-input", "Final liquidation reference or timestamp is invalid.");

        // Validate every required quote before mutating any account.
        foreach (var account in accounts.Values)
            foreach (var position in account.Positions.Values)
            {
                if (account.Frozen.Contains(position.Instrument)) continue;
                if (!closingQuotes.TryGetValue(position.Instrument, out var quote))
                    throw new TradingException("closing-quote", "Official closing quote is missing.");
                ValidateClosingQuote(position.Instrument, quote, session, finalizedAt);
            }

        var values = new List<(Account Account, decimal Value, IReadOnlyList<OrderOutcome> Outcomes)>();
        foreach (var account in accounts.Values.OrderBy(item => item.Agent.Id))
        {
            var markedCapital = account.Cash + account.Positions.Values
                .Where(position => !account.Frozen.Contains(position.Instrument))
                .Sum(position => closingQuotes[position.Instrument].Price * position.Quantity);
            if (account.FeeTier == FeeTier.Mini || markedCapital >= 50_000m || account.CompletedTradeCount >= 500)
                account.FeeTier = FeeTier.Mini;
            var outcomes = new List<OrderOutcome>();
            foreach (var position in account.Positions.Values.OrderBy(item => item.Instrument.Isin,
                         StringComparer.Ordinal).ThenBy(item => item.Instrument.OrderBookId, StringComparer.Ordinal).ToArray())
            {
                if (account.Frozen.Contains(position.Instrument))
                {
                    AddAudit(account.Agent.Id, "FINAL_FROZEN_ZERO", session.CloseAt, reference,
                        instrument: position.Instrument, detail: "pending reliable settlement");
                    continue;
                }
                var quote = closingQuotes[position.Instrument];
                var raw = quote.Price * position.Quantity;
                var execution = ExecutionMath.ExecutionPrice(OrderSide.Sell, quote, raw);
                var gross = Money.Round(execution * position.Quantity);
                var fee = account.FeeTier == FeeTier.Starter ? 0m :
                    decimal.Max(1m, Money.Round(gross * 0.0025m));
                var proceeds = gross - fee;
                account.Cash = Money.Round(account.Cash + proceeds);
                account.Positions.Remove(position.Instrument);
                account.CompletedTradeCount++;
                var outcome = new OrderOutcome(Guid.NewGuid(), OrderStatus.Filled, "final-liquidation",
                    "Hypothetical final sale.", execution, fee, quote.TradedAt);
                outcomes.Add(outcome);
                AddAudit(account.Agent.Id, "FINAL_LIQUIDATION", quote.TradedAt, reference, proceeds,
                    position.Instrument, -position.Quantity, detail: $"gross={gross};fee={fee}");
            }
            values.Add((account, Money.Round(account.Cash), outcomes));
        }

        var ordered = values.OrderByDescending(item => item.Value)
            .ThenBy(item => item.Account.Agent.Id).ToArray();
        var standings = new List<FinalStanding>(ordered.Length);
        decimal? previousValue = null;
        var rank = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            if (previousValue != ordered[index].Value) rank = index + 1;
            previousValue = ordered[index].Value;
            standings.Add(new FinalStanding(ordered[index].Account.Agent.Id,
                ordered[index].Account.Agent.ModelId, rank, ordered[index].Value, ordered[index].Outcomes));
        }
        Status = ContestStatus.Finished;
        finalReference = reference;
        finalInput = input;
        finalStandings = new ReadOnlyCollection<FinalStanding>(standings);
        AddAudit(Guid.Empty, "CONTEST_FINISHED", finalizedAt, reference,
            detail: "shared-rank final liquidation complete");
        return finalStandings;
    }

    public void ApplyCorrection(Guid agentId, string reference, decimal cashDelta,
        InstrumentId? instrument, int quantityDelta, decimal averageCostAfter, DateTimeOffset at,
        string reason, int completedTradeCountDelta = 0)
    {
        EnsureRunning();
        if (string.IsNullOrWhiteSpace(reason) || completedTradeCountDelta < 0)
        {
            throw new TradingException("correction", "Audited correction input is invalid.");
        }
        var account = GetAccount(agentId);
        var resultingCash = Money.Round(account.Cash + cashDelta);
        var existing = instrument is null ? null : account.Positions.GetValueOrDefault(instrument);
        var resultingQuantity = (existing?.Quantity ?? 0) + quantityDelta;
        if (resultingCash < 0m || resultingQuantity < 0 ||
            (resultingQuantity > 0 && averageCostAfter < 0m))
            throw new TradingException("correction", "Correction creates invalid accounting state.");
        var payload = $"{cashDelta}|{instrument}|{quantityDelta}|{averageCostAfter}|{completedTradeCountDelta}|{at:O}|{reason}";
        if (!BeginAction(agentId, reference, payload)) return;
        account.Cash = resultingCash;
        account.CompletedTradeCount += completedTradeCountDelta;
        if (instrument is not null && quantityDelta != 0)
        {
            if (resultingQuantity == 0) account.Positions.Remove(instrument);
            else account.Positions[instrument] = new Position(instrument, resultingQuantity, averageCostAfter);
        }
        AddAudit(agentId, "CORRECTION", at, reference, cashDelta, instrument, quantityDelta,
            averageCostAfter, reason);
    }

    private OrderOutcome Fill(Guid orderId, VerifiedMarketObservation quote, TradingSession session,
        IReadOnlyDictionary<InstrumentId, decimal> marks)
    {
        var order = GetQueuedOrder(orderId);
        var decision = order.Decision;
        var instrument = decision.Instrument!;
        try
        {
            ValidateQuote(decision, quote, session);
        }
        catch (TradingException error)
        {
            throw Reject(order, error.Code, error.Message);
        }
        var account = GetAccount(decision.AgentId);
        if (account.Frozen.Contains(instrument))
            throw Reject(order, "frozen", "Instrument is frozen pending reliable settlement.");
        var side = decision.Action == DecisionAction.Buy ? OrderSide.Buy : OrderSide.Sell;
        var raw = quote.Price * decision.Quantity;
        if (side == OrderSide.Buy && quote.CompleteHistorySessions < ContestContract.RequiredHistorySessions)
            throw Reject(order, "history", "Twenty complete sessions are required.");
        if (side == OrderSide.Buy && raw > quote.AverageDailyValue20 * ContestContract.MaximumAdvParticipation)
            throw Reject(order, "liquidity", "Order exceeds one percent of ADV.");

        var markedCapital = account.Cash;
        foreach (var position in account.Positions.Values)
        {
            if (!marks.TryGetValue(position.Instrument, out var mark) || mark <= 0m)
                throw Reject(order, "marks", "Verified mark missing for held instrument.");
            markedCapital += mark * position.Quantity;
        }
        if (account.FeeTier == FeeTier.Mini || markedCapital >= 50_000m || account.CompletedTradeCount >= 500)
            account.FeeTier = FeeTier.Mini;

        var execution = ExecutionMath.ExecutionPrice(side, quote, raw);
        var gross = Money.Round(execution * decision.Quantity);
        var fee = account.FeeTier == FeeTier.Starter ? 0m : decimal.Max(1m, Money.Round(gross * 0.0025m));
        if (side == OrderSide.Buy)
        {
            var total = gross + fee;
            if (total > account.Cash) throw Reject(order, "cash", "Insufficient cash.");
            var existing = account.Positions.GetValueOrDefault(instrument);
            var resultingQuantity = (existing?.Quantity ?? 0) + decision.Quantity;
            var targetValue = account.Positions.Values
                .Where(position => string.Equals(position.Instrument.IssuerKey, instrument.IssuerKey,
                    StringComparison.Ordinal) && position.Instrument != instrument)
                .Sum(position => marks[position.Instrument] * position.Quantity) +
                quote.Price * resultingQuantity;
            var postCapital = markedCapital + raw - total;
            if (postCapital <= 0m || targetValue / postCapital > ContestContract.MaximumIssuerWeight)
                throw Reject(order, "concentration", "Issuer exceeds twenty-five percent.");
            var oldCost = (existing?.AverageCost ?? 0m) * (existing?.Quantity ?? 0);
            var average = decimal.Round((oldCost + total) / resultingQuantity, 4,
                MidpointRounding.AwayFromZero);
            account.Cash = Money.Round(account.Cash - total);
            account.Positions[instrument] = new Position(instrument, resultingQuantity, average);
            AddAudit(account.Agent.Id, "FILL_BUY", quote.TradedAt, decision.DecisionId, -total,
                instrument, decision.Quantity, average);
        }
        else
        {
            var existing = account.Positions.GetValueOrDefault(instrument);
            if (existing is null || existing.Quantity < decision.Quantity)
                throw Reject(order, "holdings", "Insufficient holdings; short sales are forbidden.");
            var remaining = existing.Quantity - decision.Quantity;
            account.Cash = Money.Round(account.Cash + gross - fee);
            if (remaining == 0) account.Positions.Remove(instrument);
            else account.Positions[instrument] = existing with { Quantity = remaining };
            AddAudit(account.Agent.Id, "FILL_SELL", quote.TradedAt, decision.DecisionId, gross - fee,
                instrument, -decision.Quantity, existing.AverageCost);
        }
        account.CompletedTradeCount++;
        var outcome = new OrderOutcome(order.Id, OrderStatus.Filled, "filled", "Order filled.",
            execution, fee, quote.TradedAt);
        orders[order.Id] = order with { Status = OrderStatus.Filled, Outcome = outcome };
        return outcome;
    }

    private void ValidateDecision(OrderDecision decision)
    {
        if (!ContestContract.IsExactAgent(decision.AgentId, decision.ExactModelId))
            throw new TradingException("identity", "Fixed model identity mismatch.");
        if (decision.Action is not (DecisionAction.Buy or DecisionAction.Sell))
            throw new TradingException("action", "Only market buy and sell create orders.");
        if (decision.Instrument is null || decision.Instrument.Mic != "XSTO" ||
            string.IsNullOrWhiteSpace(decision.Instrument.Isin) || string.IsNullOrWhiteSpace(decision.Instrument.OrderBookId))
            throw new TradingException("instrument", "Only identified XSTO instruments are eligible.");
        if (decision.Quantity <= 0) throw new TradingException("quantity", "Quantity must be positive whole shares.");
        if (decision.ObservedPrice is null || decision.ObservedPrice <= 0m)
            throw new TradingException("observed-price", "Observed price is required.");
        if (decision.Confidence is < 0m or > 1m || string.IsNullOrWhiteSpace(decision.Reason) ||
            string.IsNullOrWhiteSpace(decision.Catalyst) || decision.Risks.Count == 0 || decision.Evidence.Count == 0 ||
            !Sha256.IsMatch(decision.CanonicalRequestSha256))
            throw new TradingException("evidence", "Complete decision evidence is required.");
        foreach (var evidence in decision.Evidence)
        {
            if (evidence.FinalUrl.Scheme is not ("http" or "https") ||
                evidence.PublishedAt > decision.DecisionAt || evidence.RetrievedAt > decision.DecisionAt ||
                evidence.RetrievedAt < evidence.PublishedAt || !Sha256.IsMatch(evidence.ContentSha256) ||
                string.IsNullOrWhiteSpace(evidence.ExactExcerpt))
                throw new TradingException("evidence", "Evidence provenance is invalid.");
        }
    }

    private static void ValidateQuote(OrderDecision decision, VerifiedMarketObservation quote,
        TradingSession session)
    {
        if (quote.Instrument != decision.Instrument) throw new TradingException("instrument", "Quote identity mismatch.");
        if (quote.Price <= 0m || quote.Quantity <= 0 || quote.AverageDailyValue20 <= 0m ||
            quote.RetrievedAt < quote.TradedAt || quote.RetrievedAt - quote.TradedAt < TimeSpan.FromMinutes(15) ||
            quote.RetrievedAt - quote.TradedAt > TimeSpan.FromMinutes(20) ||
            quote.SessionId != session.Id || !session.Contains(quote.TradedAt) || !Sha256.IsMatch(quote.RawSha256))
            throw new TradingException("market-data", "Quote provenance is invalid.");
        if (quote.TradedAt < decision.DecisionAt)
            throw new TradingException("quote-time", "Quote precedes eligible decision time.");
        if (quote.HasWarning || quote.IsSuspended)
            throw new TradingException("warning", "Instrument is warned or suspended.");
        if ((quote.Bid is null) != (quote.Ask is null) ||
            quote.Bid is not null && (quote.Bid <= 0m || quote.Ask < quote.Bid))
            throw new TradingException("spread", "Bid/ask provenance is invalid.");

    }

    private static void ValidateClosingQuote(InstrumentId instrument, VerifiedMarketObservation quote,
        TradingSession session, DateTimeOffset finalizedAt)
    {
        if (quote.Instrument != instrument || quote.Price <= 0m || quote.Quantity <= 0 ||
            quote.AverageDailyValue20 <= 0m || quote.SessionId != session.Id ||
            quote.TradedAt != session.CloseAt || quote.RetrievedAt - quote.TradedAt < TimeSpan.FromMinutes(15) ||
            quote.RetrievedAt - quote.TradedAt > TimeSpan.FromMinutes(20) ||
            quote.RetrievedAt > finalizedAt || !Sha256.IsMatch(quote.RawSha256) ||
            quote.HasWarning || quote.IsSuspended || (quote.Bid is null) != (quote.Ask is null) ||
            quote.Bid is not null && (quote.Bid <= 0m || quote.Ask < quote.Bid))
            throw new TradingException("closing-quote", "Official closing-auction quote is invalid.");
    }

    private TradingException Reject(PaperOrder order, string code, string message)
    {
        var outcome = Outcome(order, OrderStatus.Rejected, code, message);
        orders[order.Id] = order with { Status = OrderStatus.Rejected, Outcome = outcome };
        AddAudit(order.Decision.AgentId, "ORDER_REJECTED", order.Decision.DecisionAt,
            order.Decision.DecisionId, detail: code);
        return new TradingException(code, message);
    }

    private PaperOrder GetQueuedOrder(Guid orderId)
    {
        if (!orders.TryGetValue(orderId, out var order)) throw new TradingException("order", "Order not found.");
        if (order.Status != OrderStatus.Queued) throw new TradingException("order-state", "Order is no longer queued.");
        return order;
    }

    private Account GetAccount(Guid agentId) => accounts.TryGetValue(agentId, out var account)
        ? account : throw new TradingException("identity", "Unknown fixed agent.");

    private void EnsureRunning()
    {
        if (Status != ContestStatus.Running) throw new TradingException("paused", "System is not running.");
    }

    private bool IsDuringPause(DateTimeOffset at) => pauses.Any(interval =>
        at >= interval.PausedAt && (interval.ResumedAt is null || at < interval.ResumedAt));

    private static void ValidateLifecycle(string reason, string key)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 4000 ||
            string.IsNullOrWhiteSpace(key) || key.Length > 100)
            throw new TradingException("lifecycle", "Lifecycle reason and idempotency key are required.");
    }

    private Guid? ReplayLifecycle(Guid agentId, string key, string hash)
    {
        if (!lifecycleKeys.TryGetValue((agentId, key), out var existing)) return null;
        if (!string.Equals(existing.Hash, hash, StringComparison.Ordinal))
            throw new TradingException("idempotency", "Lifecycle idempotency conflict.");
        return existing.OrderId;
    }

    private bool BeginAction(Guid agentId, string reference, string payload)
    {
        GetAccount(agentId);
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 100)
            throw new TradingException("reference", "Action reference is required.");
        var key = (agentId, reference);
        if (!actionReferences.TryGetValue(key, out var existing))
        {
            actionReferences.Add(key, payload);
            return true;
        }
        if (!string.Equals(existing, payload, StringComparison.Ordinal))
            throw new TradingException("reference-conflict", "Action reference payload differs.");
        return false;
    }

    private int QuantityAt(Guid agentId, InstrumentId instrument, DateTimeOffset at) => audit
        .Where(item => item.AgentId == agentId && item.Instrument == instrument && item.OccurredAt <= at)
        .Sum(item => item.QuantityDelta);

    private void SettlePosition(Guid agentId, InstrumentId instrument, decimal perShare,
        DateTimeOffset at, string reference, string eventType)
    {
        var account = GetAccount(agentId);
        if (!account.Positions.TryGetValue(instrument, out var position)) return;
        var cash = Money.Round(perShare * position.Quantity);
        account.Cash = Money.Round(account.Cash + cash);
        account.Positions.Remove(instrument);
        AddAudit(agentId, eventType, at, reference, cash, instrument, -position.Quantity);
    }

    private OrderOutcome Outcome(PaperOrder order, OrderStatus status, string code, string message) =>
        new(order.Id, status, code, message);

    private DateTimeOffset LastAuditAt() => audit.Count == 0 ? DateTimeOffset.MinValue : audit[^1].OccurredAt;

    private void AddAudit(Guid agentId, string type, DateTimeOffset at, string reference,
        decimal cashDelta = 0m, InstrumentId? instrument = null, int quantityDelta = 0,
        decimal averageCostAfter = 0m, string detail = "") =>
        audit.Add(new AuditEvent(++sequence, agentId, type, at, reference, Money.Round(cashDelta),
            instrument, quantityDelta, averageCostAfter, detail));

    private sealed class Account(AgentDefinition agent, decimal cash)
    {
        public AgentDefinition Agent { get; } = agent;
        public decimal Cash { get; set; } = cash;
        public Dictionary<InstrumentId, Position> Positions { get; } = [];
        public HashSet<InstrumentId> Frozen { get; } = [];
        public int CompletedTradeCount { get; set; }
        public FeeTier FeeTier { get; set; } = FeeTier.Starter;
    }
}
