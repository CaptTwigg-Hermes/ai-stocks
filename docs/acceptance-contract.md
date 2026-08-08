# AI Swedish Paper-Trading Contest — Grill Outcome

## Goal
Four fixed Copilot models independently research public information and compete to maximize separate simulated Swedish-stock portfolios through the final Nasdaq Stockholm trading day of 2026. Paper trading only; the system must contain no real brokerage endpoint, credential, or order capability.

## Competitors and isolation
- Models: `gpt-5.6-sol`, `claude-opus-4.8`, `claude-sonnet-5`, `gemini-3.1-pro-preview`, all through Copilot.
- SEK 30,000 initial cash each; SEK 120,000 total simulated capital.
- Fixed model/version for the contest; outages cause missed runs, never silent substitution.
- Each model sees only its own portfolio, history, performance, private notes, and public web research—not rivals.
- Models independently choose sources and may revise a private, versioned strategy. Shared rules and prompt contract are immutable.

## Universe and risk gates
- Nasdaq Stockholm main-market common shares only; all market-cap segments.
- Worldwide public information may inform research.
- No foreign shares, First North, funds, ETFs, options, derivatives, leverage, or short selling.
- Whole shares only. Cash may be held freely.
- One issuer may be at most 25% of portfolio value when buying; appreciation may carry it above 25%.
- Buy requires 20 trading days of history, valid recent quote/volume, no unresolved warning/suspension, and order value <=1% of 20-day average daily traded value. Sells remain allowed on valid market data.

## Schedule
- Official Nasdaq Stockholm calendar, holidays, and shortened sessions control scheduling.
- Per model/session: one run 60 minutes before open; four evenly spaced while open; one 30 minutes after close.
- Out-of-hours runs may queue orders for next open. A model may cancel/replace its own unexecuted order with a recorded reason.
- Failed run/service retries for up to 15 minutes; then record a missed run with no later replay using newer information.

## Orders and execution
- V1 supports market buy, market sell, hold, and cancel-pending only; no limit or stop orders.
- During-market orders use the first free-feed quote timestamped at or after decision time; decision and execution timestamps are retained. Missing/unverified timestamps reject execution.
- Out-of-hours orders use the first valid quote at the next real opening, never stale closed-market data.
- Simulator enforces cash, holdings, whole-share quantity, position cap, liquidity, quote freshness, and atomic/idempotent execution.
- Once a valid quote triggers execution, a trade cannot be reversed except through an append-only audited data correction.
- Apply adverse slippage to every fill and final liquidation: `max(0.10%, half bid–ask spread) + 0.25% × sqrt(order value / 20-day average daily traded value)`, capped at 1.00%. If a trustworthy bid–ask spread is unavailable, use the 0.10% floor plus the impact term; never infer a zero spread.

## Fees, tax, income, and corporate actions
- Nordnet new-customer `Kom igång`: SEK 0 Stockholm commission while each model's simulated aggregate account capital is below SEK 50,000, for at most 500 stock trades/year.
- Permanently move to Nordnet Mini once capital reaches SEK 50,000 or before trade 501: 0.25% commission, minimum SEK 1 per Nordic order. This boundary follows Nordnet's official wording: free until capital has reached SEK 50,000; maximum 500 stock trades/year.
- Freeze this fee schedule for contest consistency.
- No simulated taxes.
- Verified dividends credit cash on payment date.
- Verified splits adjust quantity/cost basis; cash mergers credit official consideration; stock mergers apply official ratios; suspended shares cannot trade; delisted shares use official proceeds or remain frozen at zero pending reliable settlement.

## Evidence and audit
Every buy/sell must include reason, catalyst, source URLs/publication times, risks, confidence, model ID, full decision timestamp, observed price, and pre-decision portfolio state. Invalid evidence rejects the order. Store immutable/versioned prompts, responses, source references, quotes, orders, rejections, fills, fees, dividends, corporate actions, snapshots, strategy updates, failures, and adjustments.

## Ranking
On the final Nasdaq Stockholm trading day of 2026, rank by net liquidation value: cash plus hypothetical sale value at official closing prices minus simulated slippage and Nordnet commission. Highest SEK total wins.

## Human control
- No manual cancellations, holdings edits, or model rescues.
- Owner may pause the whole system for technical/security reasons.
- Bad-data corrections are append-only, audited, and applied consistently.

## Product and operations
- New standalone repository/app, separate from the legacy dividend screener.
- Own PostgreSQL database, containers, scheduler, fake trading API, mobile-first private dashboard, and Discord delivery.
- TrueNAS deployment behind existing Cloudflare Tunnel/Access.
- Authenticated read-only dashboard: leaderboard/chart, portfolios, queued orders, evidence timeline, fees, dividends, failures, and audit history.
- Owner-only pause/reset controls; no public trading endpoints.
- Trading-day Discord report in `#ai-stocks` around 18:30 Stockholm: leaderboard, daily/total returns, cash, holdings, trades, fees, missed runs, and short rationales.
- Immediate alerts only for system pause, run-wide stale/invalid market data, DB/backup failure, multi-model auth outage, or accounting invariant violation.
- Daily encrypted DB backups with restore tests; retain complete contest history through 2027.

## Launch gate
All must pass: unit tests; PostgreSQL integration tests; delayed-quote/corporate-action replay tests; concurrency and accounting-invariant tests; API authorization and prompt-injection security tests; mobile browser tests; full simulated trading-day dry run for all four models; backup/restore test; independent review; proof no real brokerage integration exists. Any failure blocks launch. Start on the next full Nasdaq Stockholm trading day after production passes, with staging/test trades erased and all four portfolios initialized simultaneously.

## Scope lock
This is the complete approved v1 acceptance contract. Implementation was approved in Discord message `1534975156781322311`; the adverse-slippage rule was separately approved in message `1534980696739676322`. No launch occurs until every gate above passes.
