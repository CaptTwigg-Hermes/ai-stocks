# Acceptance traceability

This matrix is a release gate. A requirement is not complete because code exists; its named executable evidence must pass on the exact candidate tree.

| Contract requirement | Implementation boundary | Executable evidence |
|---|---|---|
| Four exact Copilot models, isolated state, no fallback | Core, Research, Worker | Core/Research orchestration and identity tests; four-model dry run |
| SEK 30,000 per model | Core, Persistence | Seed/invariant and PostgreSQL bootstrap tests |
| XSTO main-market common shares only | MarketData | FIRDS classification and exclusion fixtures |
| Whole shares, no short/leverage, cash allowed | Trading, Persistence | Trading boundaries and concurrent oversell/overspend tests |
| 25% issuer cap and 1% ADV | Trading, MarketData | Golden boundary and 20-session replay tests |
| Official calendar and six runs/session | MarketData, Worker | Full/half-day/holiday scheduling tests |
| 15-minute retries, then missed/no replay | Worker, Persistence | Fake-clock retry and durable claim tests |
| First valid post-decision or next-open trade | MarketData, Trading | Delayed-feed replay and queue tests |
| Atomic, idempotent fills | Persistence | PostgreSQL conflict/race and trigger tests |
| Adverse slippage formula, capped at 1% | Trading | Decimal golden vectors and cap tests |
| Starter-to-Mini permanent fee transition | Trading, Persistence | 50,000 SEK and trades 500/501 tests |
| Dividends and corporate actions | Trading, Persistence | Replay, fraction freeze, idempotency tests |
| Immutable evidence, prompts, responses, facts | Research, Persistence | Tamper, canonical hash, update/delete/truncate tests |
| Final 2026 liquidation and shared ranking | MarketData, Trading, Persistence | Closing-auction and finalizer-race tests |
| No manual rescue or public trading endpoint | Web, negative-capability gate | Route authorization tests and `prove_no_broker.py` |
| Private mobile dashboard and owner controls | Web | Auth, route, render, and mobile browser tests |
| Daily Discord report and narrow alerts | Worker, Operations | Formatter, category, and idempotent delivery tests |
| External PostgreSQL, explicit migration | Persistence, Operations | Clean migration/bootstrap and restricted-role tests |
| Daily encrypted backup and restore rehearsal | Operations, scripts | Real encrypted backup and disposable restore test |
| Hardened Dockge services | Dockerfile, Compose | Compose config, image build, container health and direct-origin probes |
| No brokerage capability | Entire candidate | Source/package/route/process inventory plus network-denied trade probe |

## Launch-only evidence

The following cannot be replaced by fixtures:

1. Twenty complete archived XSTO sessions for every eligible instrument.
2. Archived initial Nasdaq Main Markets RSS snapshot, followed by monotonic live RSS continuity; the private paper-only baseline assumes eligible FIRDS common shares are clear when the snapshot contains no blocking notice.
3. Live Nasdaq post-trade transport/schema smoke from the production network.
4. Successful safe-mode calls to all four exact Copilot model IDs.
5. Production PostgreSQL migration, concurrency, and restricted-role proof.
6. Cloudflare issuer/AUD/JWKS, authorized session, and direct-origin denial.
7. Real Discord delivery to `#ai-stocks`.
8. Encrypted off-host backup and destructive restore into only `ai_stocks_test`.
9. Full simulated trading day, mobile browser suite, and independent exact-tree review.
