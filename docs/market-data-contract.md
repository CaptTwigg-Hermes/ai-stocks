# Market-data contract

Verified 2026-08-06. Production fails closed if these rules cannot be met.

## Execution authority

Primary source: Nasdaq Nordic MiFID II delayed post-trade files.

- Landing page: `https://tradereports.nasdaq.com/shares/trade-reports/post-trade`
- File listing: `https://tradereports.nasdaq.com/api/regulatory/trade-reports?type=POST_TRADE&assetClass=EQUITY`
- Download: `https://tradereports.nasdaq.com/api/regulatory/trade-report/download?type=POST_TRADE&assetClass=EQUITY&fileName={fileName}`

The official page states that files are CSV, generated every minute,
delayed 15 minutes, retained for 48 hours, and free for
non-commercial use. `ai_stocks.nasdaq_acquisition` strictly validates
the listing/report names, retrieves only from these fixed endpoints,
and writes each raw CSV plus retrieval time, source URL, byte count,
and SHA-256 into an immutable per-report archive. Existing checksum or
metadata mismatches fail closed. A live smoke on 2026-08-06 listed
1,684 reports and archived the latest 7,044-byte CSV with checksum
`ed90a1363684e991ba787667e08528dfb43b905736d1ec0417e23727f9c6791a`.
That smoke proves transport/schema compatibility only, not 20-session
readiness.

Only execute from a row with:

- mapped allowed ISIN and XSTO execution venue;
- UTC trading timestamp at or after the decision timestamp;
- positive price and quantity;
- SEK monetary price notation;
- timestamp inside the pinned Nasdaq Stockholm session;
- no unresolved suspension, corporate action, or data warning.

Never replace the trading timestamp with retrieval, publication,
file-generation, or chart-bucket time. No matching post-decision
trade means no fill.

For this private paper-only deployment, active eligible FIRDS common
shares begin clear and archived Nasdaq Main Markets RSS notices override
that baseline. This is a deliberate convenience trade-off: the finite
public RSS window may omit an older active status. It must never be reused
for real-money or regulated execution.

## Universe and reference data

Nasdaq's website screener and instrument endpoints are technically
available but its general legal terms restrict automated capture.
They are not authorized production dependencies without written
permission. Until permission exists, use a reviewed, checksummed
universe artifact derived from official Nasdaq list/change files.
Track instruments by ISIN plus XSTO order-book identity. ESMA's
official FIRDS full files do not publish a separate venue instrument ID, so
this deployment uses the ISIN itself as the deterministic XSTO identity.

Allowed: Nasdaq Stockholm Large, Mid, and Small Cap common shares.
Reject preference shares, SDB/SDR depositary receipts, SPACs, funds,
ETFs/ETPs, rights, warrants, and every non-common-share instrument.

## Calendar

Pin the official yearly Nasdaq holiday XLSX:
`https://www.nasdaq.com/docs/2025/10/07/holiday-schedule-for-web-2026.xlsx`

Repository artifact: `docs/nasdaq-holiday-schedule-2026.xlsx`
SHA-256: `867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395`

The official trading-hours page gives Stockholm Main Market equity
hours as 09:00–17:30 Europe/Stockholm and half-day equity hours as
09:00–13:00. A source capture retrieved on 2026-08-06 is pinned at
`docs/nasdaq-trading-hours.html` with SHA-256
`f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395`.
The 2026 artifact marks Jan 5, Apr 2, Apr 30, May 13, and Oct 30 as
equity half-days. Dec 31 is closed, making Dec 30 the final 2026
equity trading day. `ai_stocks.calendar` verifies both pinned source
hashes before readiness can pass and derives all six run times from
the actual session length.

Never infer a session from weekdays alone.

## Twenty-session liquidity warm-up

The official delayed files are retained for only 48 hours. Before the
launch gate can pass, staging must archive and aggregate at least 20
complete Stockholm trading sessions for every eligible ISIN. Staging
orders/trades are erased at launch; verified raw market observations
and checksums remain as reference data. No model may buy an instrument
without 20 complete sessions. This makes the next-full-session start
rule apply only after market-data readiness is verified.

## Separate assumed-fill exhibition

The Development/Testing AI exhibition may show autonomous paper
BUY/SELL decisions before strict readiness, but it is not the contest
and does not weaken the twenty-session gate above. Its market input is
still checksum-verified official delayed XSTO data. Only execution is
synthetic, under `assumed-delayed-paper-fills-v1`:

- fixed `0.65 DKK/SEK` conversion;
- `1%` adverse slippage on buys and sells;
- maximum assumed buy total of `10,000 DKK`;
- maximum marked position after a buy of `25,000 DKK`;
- whole shares, available cash, existing inventory, and no shorts;
- exact binding to the delayed price and availability timestamp shown
  to the worker, with rejection if the API snapshot has advanced;
- a maximum observation age of 72 hours, allowing the closed-market
  weekend while rejecting older retained reports;
- no broker, real order, measured ADV20, or verified liquidity claim.

The API is the execution authority. The mode requires
`PREVIEW_MODE=1` and `AI_EXHIBITION_MODE=1`, remains non-live and
paper-only, and must be labelled **ASSUMED FILLS** in the UI. Missing,
stale, invalid, or tampered observations fail closed without fixture
fallback. Production still aborts if either preview mode is enabled.

## Corporate actions

Use issuer disclosures distributed through Nasdaq's official RSS:
`https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices`

Disclosures are evidence, not normalized instructions. No automatic
cash/share mutation without dual-source agreement or owner-approved,
append-only audited normalization. Yahoo may detect disagreements but
must never mutate a portfolio.

## Fallbacks

Nordnet exposes useful delayed prices (`delay: 900`) and millisecond
trade timestamps, but unattended reuse permission has not been
verified. It is disabled for production execution until written
permission exists. Yahoo Finance is reconciliation-only.

If the Nasdaq CSV feed is unavailable or invalid, do not execute.
Record the failure and alert according to the acceptance contract.
