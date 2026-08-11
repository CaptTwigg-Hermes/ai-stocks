namespace AiStocks.Api;

public sealed class PreviewRaceStore(TimeProvider clock)
{
    public const decimal StartingCashDkk = 100_000m;
    public const string DataMode = "preview-fixtures";
    public const int MaximumIdempotencyEntries = 1_000;

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

        lock (sync)
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
            var total = Money(instrument.PriceDkk * request.Quantity);
            account.Holdings.TryGetValue(instrument.Id, out var held);
            if (side == "buy")
            {
                if (account.CashDkk < total)
                    throw new PreviewOrderException("insufficient-cash", "The paper account has insufficient DKK cash.");
                account.CashDkk = Money(account.CashDkk - total);
                account.Holdings[instrument.Id] = held + request.Quantity;
            }
            else
            {
                if (held < request.Quantity)
                    throw new PreviewOrderException("insufficient-holdings", "The paper account does not hold enough shares.");
                account.CashDkk = Money(account.CashDkk + total);
                if (held == request.Quantity) account.Holdings.Remove(instrument.Id);
                else account.Holdings[instrument.Id] = held - request.Quantity;
            }

            var order = new PreviewOrderDto(Guid.NewGuid(), side, instrument.Id, instrument.Symbol,
                request.Quantity, instrument.PriceDkk, total, "filled", note, clock.GetUtcNow());
            account.Orders.Add(order);
            account.Idempotency.Add(idempotencyKey, new(fingerprint, order));
            if (account.Orders.Count > 100) account.Orders.RemoveRange(0, account.Orders.Count - 100);
            return new(order, false);
        }
    }

    private Account AccountFor(string identity)
    {
        if (accounts.TryGetValue(identity, out var account)) return account;
        var displayName = identity.Split('@', 2)[0].Replace('.', ' ').Replace('-', ' ').Trim();
        account = new(string.IsNullOrWhiteSpace(displayName) ? "You" : ToTitleCase(displayName));
        accounts.Add(identity, account);
        return account;
    }

    private static PreviewPortfolioDto PortfolioFor(Account account)
    {
        var holdings = account.Holdings.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item =>
            {
                var instrument = Instruments.Single(candidate => candidate.Id == item.Key);
                return new PreviewHoldingDto(instrument.Id, instrument.Symbol, instrument.Name, item.Value,
                    instrument.PriceDkk, Money(instrument.PriceDkk * item.Value));
            }).ToArray();
        var holdingsValue = Money(holdings.Sum(item => item.ValueDkk));
        var total = Money(account.CashDkk + holdingsValue);
        return new(account.DisplayName, StartingCashDkk, account.CashDkk, holdingsValue, total,
            Percent((total - StartingCashDkk) / StartingCashDkk * 100m), holdings, DataMode);
    }

    private static InstrumentDto Instrument(string id, string symbol, string name, string exchange,
        string country, string currency, decimal price, decimal fxToDkk) =>
        new(id, symbol, name, exchange, country, currency, price, Money(price * fxToDkk), true);

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static decimal Percent(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static string ToTitleCase(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

    private sealed class Account(string displayName)
    {
        public string DisplayName { get; } = displayName;
        public decimal CashDkk { get; set; } = StartingCashDkk;
        public Dictionary<string, int> Holdings { get; } = new(StringComparer.Ordinal);
        public List<PreviewOrderDto> Orders { get; } = [];
        public Dictionary<string, IdempotencyEntry> Idempotency { get; } = new(StringComparer.Ordinal);
    }

    private sealed record IdempotencyEntry(string Fingerprint, PreviewOrderDto Order);
}

public sealed record PreviewSubmission(PreviewOrderDto Order, bool Replayed);

public sealed class PreviewOrderException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
