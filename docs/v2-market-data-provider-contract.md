# AI Stocks global v2 market-data and provider contract

## Authority, scope, and current status

This document is the authoritative market/reference-data contract for the v2 modes defined in [`v2-acceptance-contract.md`](v2-acceptance-contract.md). It does not alter the Swedish 2026 championship or its source-specific [`market-data-contract.md`](market-data-contract.md).

No production provider is approved by this document, and no provider name is implied. The repository's implemented **local indexed seed/test provider** may be used for deterministic tests and explicitly labelled local demonstrations only. It is not evidence of source rights, global coverage, production quote semantics, calendars, corporate actions, FX, redistribution rights, or production execution readiness. Production paper execution remains blocked until the approval and exercise gates below pass.

## Approved indexed provider

“Approved indexed provider” means one provider configuration whose immutable approval manifest has been accepted for the exact deployment and data uses. The manifest is deny-by-default and includes:

- unique provider and approval-version IDs, effective interval, approvers, contract/entitlement reference, environments, and credential owner;
- permitted automated acquisition, storage, retention, derived calculations, UI display, API exposure, audit retention, and redistribution for each data class;
- admitted countries, MICs/venues, segments, security types, currencies, and symbol/identifier namespaces;
- endpoint allowlist, authentication method, rate limits, expected delay, timestamp semantics, revision/cancellation semantics, service windows, and integrity mechanism;
- non-null per-venue quote freshness limits, non-null FX freshness limits, and calendar/status/corporate-action coverage; and
- raw-data retention, provenance, incident, revocation, and migration requirements.

Only data acquired under an active manifest through the pinned adapter and endpoint allowlist can become authoritative. Contract expiry, entitlement loss, manifest mismatch, unknown venue/currency, adapter/version mismatch, or revoked approval closes readiness and execution. An operator flag cannot waive this boundary.

The application executes only against the local effective-dated index produced from approved provider bytes. It never executes from an arbitrary symbol lookup, user payload, UI metadata, search result, or unindexed live response. Index publication is an explicit checksummed ingestion step; it is not a side effect of order submission.

## Instrument index and eligibility

Each instrument revision has an immutable internal ID and records at minimum:

- provider ID, approval version, provider instrument/listing ID, ISIN when assigned, ticker, MIC/venue, segment, issuer identity, country, trading currency, and exchange time zone;
- positive classification as a common stock from provider fields and the classification-rule version;
- listing/status effective-from and effective-to instants, trading/settlement state, and the source record ID/hash; and
- predecessor/successor relationships for symbol, venue, identifier, issuer, or currency changes.

Eligibility at decision and execution time requires an active revision, admitted MIC/segment/currency, positive common-stock classification, open approved session, and clear trade status. Ambiguous, absent, conflicting, expired, future-effective, or unclassified records fail closed.

“Global” is architecture scope, not universal availability. Only common-stock listings positively present in the current approved index are supported. Funds, ETFs/ETPs, preferred stock, debt, derivatives, rights, warrants, cryptoassets, depositary receipts, synthetic/fractional representations, and unknown types are excluded. Delisting, merger, symbol/venue migration, suspension, halt, or currency change creates an effective-dated state transition; it never rewrites the old revision.

## Immutable acquisition and provenance

The collector stores the exact raw response before projection, subject to approved retention rights, with provider/endpoint ID, request parameters excluding secrets, retrieval start/end, HTTP status, provider publication/version time, byte count, media/schema version, and cryptographic hash. Normalized records link to raw hashes and record the parser/build, approval manifest, normalization rule, ingest transaction, and any provider revision/cancellation.

Acquisition is append-only and idempotent by provider identity/version/hash. Same identity with different bytes, checksum failure, partial response, pagination gap, sequence gap, clock ambiguity, schema drift, duplicate conflict, or non-monotonic revision quarantines the affected dataset and closes its readiness. Secrets and prohibited raw content are never exposed through audit or UI.

Provider corrections append new facts linked to the superseded fact. They do not mutate a historical quote, FX observation, fill input, valuation, or audit record. Recalculation creates a versioned correction under the ledger contract.

## Quote and execution contract

Every quote/trade observation eligible for paper execution records:

- immutable observation ID, instrument revision ID, provider/approval version, source record/hash, venue, source currency, and price fields used;
- provider event time, provider publication time when supplied, platform availability time, retrieval time, and their time zones/UTC normalization;
- quote/trade condition, session, delay class, correction/cancellation state, and status/quality flags; and
- the applicable non-null freshness-policy ID and maximum age from the approval manifest.

The provider approval must define which field is executable (for example a quote side, midpoint, last trade, auction price, or deterministic delayed-paper rule) and how side, spread, delay, corrections, auctions, halts, and crossed/invalid markets are handled. The application does not invent or silently substitute semantics.

For an order intent accepted at `decision_at` and evaluated at `executor_at`, an observation is eligible only when all of these are true:

1. it belongs to the exact indexed instrument revision, approved venue, provider, and approval version;
2. its event and availability timestamps are unambiguous, and `available_at <= executor_at`;
3. the information was not available to the participant after `decision_at` while represented as pre-decision context;
4. the provider-specific execution rule selects it deterministically for the intent, including any required first eligible observation at or after `decision_at`;
5. `0 <= executor_at - available_at <= max_quote_age` under the active policy, and any declared provider delay is within the approved bound;
6. the approved session is open for the selected execution rule and the instrument status is clear; and
7. price, currency, size/condition fields, source hash, sequence/revision state, and dataset readiness are valid.

A missing field or eligible observation, stale age (strictly greater than the approved maximum), future observation, closed/unknown session, halt, unapproved delayed class, invalid/non-positive price, currency mismatch, checksum/revision conflict, or unavailable provider causes a recorded rejection with **no fill and no ledger mutation**. The system never falls back to a displayed/cache value, seed/fixture value, manual value, another provider, previous close, or zero.

## Calendar and instrument status

Every admitted venue has an approved, versioned exchange calendar containing IANA time zone, regular and shortened sessions, holidays, auctions relevant to the execution rule, daylight-saving behavior, and effective dates. Weekday inference is prohibited. Missing, stale, conflicting, or uncovered calendar intervals close execution for that venue.

Approved status data must cover listing activation, suspension/halt, resume, observation/warning states relevant to eligibility, delisting, and venue closure with effective times and provenance. Unknown state is ineligible. A resume permits only observations generated and made available under the approved post-resume rule.

## FX and DKK audit contract

All cash, return, valuation, and leaderboard totals use DKK as base currency while retaining original currency amounts. For every non-DKK monetary event or valuation, the system stores an immutable FX conversion record containing:

- FX observation ID, provider/approval version, base and quote currency, rate, direction, source/hash, event time, publication time, availability time, retrieval time, and freshness-policy ID;
- target event/valuation ID and timestamp, original amount/currency, exact decimal formula and rounding version, resulting DKK amount, and calculation version; and
- for approved triangulation only, the ordered currency path and every immutable leg ID, timestamp, rate, direction, and intermediate unrounded value.

An FX observation is eligible only if it was available at the conversion evaluation time, is not newer than the event under the approved event-time rule, has a positive finite decimal rate, uses an admitted pair/path, and satisfies `0 <= evaluation_at - available_at <= max_fx_age` from the active non-null approval policy. Interpolation, inferred parity, UI/browser conversion, floating-point arithmetic, manual rates, fixture rates, and fallback to another source or previous rate are prohibited.

Missing, stale, future, invalid, unapproved, or directionally ambiguous FX fails closed: a trade requiring that conversion receives no fill or ledger mutation; a leaderboard/valuation row becomes explicitly `UNAVAILABLE`. The last known DKK value may be shown only as historical and must never be timestamped or ranked as current.

Leaderboard replay must reproduce every DKK value from ledger facts plus the exact quote and FX IDs without network access. Provider revisions never rewrite a published ranking; they produce an attributable calculation/correction version.

## Corporate actions and closing valuations

The approved production contract must cover effective-dated dividends, splits/consolidations, rights effects, mergers, spin-offs, symbol/venue/currency changes, and delistings for every admitted instrument, including announcement, ex/record/pay/effective dates and correction/cancellation semantics. Provider notices are evidence, not direct accounting commands.

Only the normalized corporate-action service may submit a versioned action command; only the executor/ledger may apply it. Ambiguous, incomplete, conflicting, missing, or stale action data freezes affected execution and valuation until an approved append-only normalization/correction is available. No guessed ratio, cash amount, successor, or FX rate is permitted.

Each admitted venue must also have an approved closing-valuation rule. Missing or ambiguous official close yields `UNAVAILABLE`; it never falls back to a website quote, last cached intraday value, or zero.

## API, UI, and redistribution boundary

Provider credentials and unrestricted/raw datasets remain collector-side. The UI never calls the provider. The API exposes only fields and audiences expressly permitted by the approval manifest, after object authorization and with required attribution, delay, timestamp, and currency labels. Download, export, cache, browser storage, realtime fan-out, and public leaderboard fields each require explicit redistribution/display permission; internal acquisition rights do not imply them.

An overbroad query, entitlement error, attempted unsupported export, or uncertainty about rights is denied and audited. Logs, errors, evidence, and tests do not leak provider credentials or prohibited payloads.

## Readiness and outage behavior

Readiness is computed independently for each provider approval version, venue, instrument-index revision, calendar interval, quote stream, status stream, corporate-action stream, currency pair/path, and redistribution surface. Liveness does not imply readiness.

Orders are admitted only when every dependency required for that exact instrument, event, and DKK accounting path is ready. Dataset age, sequence continuity, source clocks, manifest expiry, and collector heartbeat are checked at execution—not only startup. Provider outage or partial impairment closes only boundaries proven independent; uncertainty closes the whole affected chain. Queued intents do not execute later using information that violates their original temporal rule.

There is no automatic provider fallback. Adding or changing a provider, endpoint, field meaning, parser, venue, currency, freshness limit, calendar, FX path, or redistribution audience requires a new approval version, contract tests, replay, and production exercise before readiness can open.

## Production approval and exercise gate

Before any production v2 paper fill or current leaderboard is enabled, all of the following evidence must exist for the exact candidate and deployment:

1. approved legal/entitlement and redistribution matrix for every data class and audience;
2. pinned adapter, endpoint allowlist, schema/parser, credential scope, manifest, and effective-dated common-stock index;
3. successful production-network acquisition with immutable raw/hash provenance and deterministic replay;
4. representative fixtures and live exercises for each admitted venue, time zone, session type, delay/quote condition, halt/resume, identifier revision, currency, FX direction/path, and corporate-action type;
5. measured source delay and freshness enforcement, including boundary, stale, missing, future, tampered, partial, revised, rate-limited, and outage cases;
6. executable proof that every failure above produces no fill/ledger mutation and no provider/fixture/manual fallback, while valuation becomes explicitly unavailable;
7. offline reproduction of DKK fills and leaderboard values from exact quote/FX IDs;
8. least-privilege, secret, object-authorization, retention/deletion, export, attribution, and redistribution tests;
9. monitoring/alert, manifest-expiry, provider-revocation, incident, backup/restore, and adapter rollback rehearsals; and
10. independent provider-rights, security, temporal-integrity, and accounting review pinned to the exact tree and approval version.

Tests using the local indexed seed/test provider satisfy deterministic development coverage only. They cannot satisfy live acquisition, rights, coverage, redistribution, service, or production-network items. A skipped item, unsupported venue/currency, expired approval, or unexercised lifecycle keeps production execution and current DKK ranking blocked.
