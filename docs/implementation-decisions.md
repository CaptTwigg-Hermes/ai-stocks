# Approved implementation decisions

The acceptance and market-data contracts remain authoritative.
This document resolves implementation details that they left open.

- The C# deliverable is a complete .NET 10 replacement.
- Equal final values receive the same rank.
- `observed_price` is the latest verified price available before the decision.
- Quotes from a paused interval are never eligible. Pending orders can use only the first valid post-resume quote.
- Reset is pre-launch only. It clears staging projections while preserving immutable audit facts.
- Fractional corporate-action entitlements remain frozen until verified official cash-in-lieu terms are normalized through an audited correction.
- Positions use weighted-average cost.
- Dividend entitlement is based on verified ownership at the close preceding the official ex-date.
- Dashboard viewers use an explicit Cloudflare Access email allowlist.
- Publication times must be independently verified from the fetched source or its trusted metadata.

## Free authoritative source stack

- Universe: ESMA FIRDS full and delta instrument-reference files. Include active XSTO equity instruments whose CFI identifies common shares; retain effective-dated versions and checksums.
- Execution: Nasdaq Nordic MiFID II delayed post-trade reports already approved by the market-data contract.
- Official closing price: the XSTO closing-auction price represented by authoritative `PATS` post-trade rows. If no unambiguous closing-auction price exists, finalization fails closed pending an audited correction; it never substitutes a website quote.
- Warning, observation, suspension, and resumption events: Nasdaq Europe Main Markets Notices RSS. State is append-only and must be seeded by a reviewed signed snapshot before launch. Unknown state is ineligible.
- Corporate actions: Nasdaq official notices plus the already-required second source or audited owner normalization.

Nasdaq's public website screener API is not a production dependency. Its legal terms prohibit automated or manual capture without prior written permission. The implementation may use only sources whose publication mechanism permits the intended automated use.
