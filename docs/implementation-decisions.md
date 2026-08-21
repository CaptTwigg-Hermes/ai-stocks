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

- Universe: [ESMA FIRDS full and delta instrument-reference files](https://registers.esma.europa.eu/publication/searchRegister?core=esma_registers_firds_files). Include active XSTO equity instruments whose CFI identifies common shares; retain effective-dated versions and checksums.
- Execution: [Nasdaq Nordic MiFID II delayed post-trade reports](https://tradereports.nasdaq.com/shares/trade-reports/post-trade) already approved by the market-data contract.
- Official closing price: the XSTO closing-auction price represented by authoritative `PATS` post-trade rows. If no unambiguous closing-auction price exists, finalization fails closed pending an audited correction; it never substitutes a website quote.
- Warning, observation, suspension, and resumption events: [Nasdaq Europe Main Markets Notices RSS](https://api.news.eu.nasdaq.com/news/rss/mainMarketNotices). For this private paper-only deployment, active eligible FIRDS common shares start clear and the archived public RSS snapshot plus subsequent notices override that baseline. Unknown or explicitly blocked state is ineligible. This best-effort mode is not suitable for real-money or regulated execution because a finite RSS window may omit an older active status.
- Corporate actions: Nasdaq official notices plus the already-required second source or audited owner normalization.

- Nasdaq's public website screener API is not a production dependency. [Nasdaq's legal terms](https://www.nasdaq.com/legal) prohibit automated or manual capture without prior written permission. The implementation may use only sources whose publication mechanism permits the intended automated use.
- The local Nordic assumed-fill exhibition is an explicit preview-only profile. It keeps strict XSTO state and rules physically separate, derives a separate Nordic FIRDS state only from checksum-verified retained raw versions, uses venue/ISIN/order-book identity and exact venue/currency pairs, and binds archived ECB informational DKK rates to v2 fills. It is not approved for public or commercial redistribution.
