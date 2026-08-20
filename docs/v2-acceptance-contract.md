# AI Stocks global human + AI platform — v2 acceptance contract

## Authority and status

This document is the authoritative product and architecture contract for the global v2 platform layer. It does not amend, replace, launch, migrate, or retire the Swedish 2026 championship defined by [`acceptance-contract.md`](acceptance-contract.md). The legacy championship keeps its own models, capital, universe, market-data rules, ledger, deployment profile, and launch gate.

V2 is paper trading only. It is **not production-ready** and must not be represented as globally executable until every v2 deployment gate below has passed on the exact release candidate and the production market-data approval required by [`v2-market-data-provider-contract.md`](v2-market-data-provider-contract.md) exists and has been exercised.

## Product modes

Every competition, portfolio, order intent, decision, fill, ledger event, note, ranking snapshot, and audit event has exactly one immutable `mode` from this closed set:

| Mode | Participants | Decision authority | Evidence rule |
|---|---|---|---|
| `human_sandbox` | Humans only | An authenticated human may submit paper-order intents for a portfolio they own. | A human note is optional. Absence of a note never invalidates an otherwise valid intent. |
| `ai_league` | AI participants only | The runner identity bound to the registered AI participant may submit paper-order intents. Humans may observe and perform separately authorized operational pause actions, but may not trade or rescue an AI portfolio. | Every AI decision, including hold, requires the complete AI decision record below. |
| `mixed_exhibition` | Registered humans and registered AI participants | Each actor may submit intents only for its own portfolio; actor type is retained and displayed. | Human notes are optional; AI rationale, evidence, and model identity are mandatory. |

Modes never share portfolios, orders, cash, positions, private notes, runner context, idempotency namespaces, or mutable competition state. Data may appear together only in an explicitly labelled cross-mode read projection whose records retain their source mode and competition identifiers. A mode change creates a new competition; it never converts existing state.

`mixed_exhibition` is an exhibition, not proof of fair human-versus-AI comparison. The UI and API must label its participant type and market-data status without ambiguity.

## Competition, portfolio, and money invariants

- Every new v2 participant portfolio starts exactly once with **100,000.00 DKK** in an append-only ledger. Replays must return the original initialization and may not credit cash again.
- DKK is the base currency for cash, portfolio value, return, leaderboard ordering, and displayed totals. Source-currency amounts remain immutable alongside DKK conversions; DKK values never replace them.
- A leaderboard row must be reproducible from ledger events, positions, approved instrument quotes, and the exact event-time FX observations used. It records the quote IDs, FX observation IDs and conversion path, source and availability timestamps, provider approval version, valuation timestamp, and calculation version.
- Missing, stale, invalid, unapproved, or temporally unavailable quote or FX input makes the affected order ineligible and the affected valuation explicitly `UNAVAILABLE`. The system must not use the last displayed value, zero, a fixture, a manually entered rate, or a different provider as fallback. An unavailable valuation is not ordered as if it were current.
- Corrections are append-only, reasoned, attributable, and linked to superseded facts. No balance, position, fill, FX fact, or historical ranking is edited in place.

## Global instrument universe

V2 has no country or exchange hard-code. Its eligible universe is the effective-dated set of **active common-stock listings explicitly supported by the approved, locally indexed production provider**. “Global” means provider-supported markets can be admitted through the same contract; it does not mean every country, venue, or security is available.

The executor accepts only an exact instrument revision already present in the approved local index for the decision time, venue, trading currency, and provider approval version. Arbitrary symbols, search results, user-supplied instrument metadata, and on-demand unindexed provider responses are never execution authority. Funds, ETFs/ETPs, derivatives, rights, warrants, preferred shares, debt, cryptoassets, and any instrument not positively classified as a common stock are rejected. Depositary receipts and synthetic or fractional representations are rejected unless a later contract version expressly admits them.

Provider rights, quote semantics, calendars, corporate actions, FX, identifiers, retention, and redistribution must satisfy the separate v2 provider contract. The implemented local indexed seed/test provider is test/demo input only and is not an approved production source.

## Human and AI decision records

A human intent stores authenticated actor ID, portfolio ID, instrument revision, side/quantity, client decision timestamp, accepted timestamp, and idempotency key. It may include a bounded plain-text note. A human is never required or encouraged to manufacture AI-style evidence, and a human note is never labelled as verified evidence.

Every AI decision, including hold, is rejected before persistence unless it contains:

1. the registered participant ID and exact model identity: provider, model ID, model/version when exposed, runner build, prompt-contract version, and invocation/run ID;
2. a bounded rationale, thesis/catalyst or explicit hold basis, material risks, confidence, decision timestamp, and intended action;
3. evidence references with canonical URL or approved immutable source ID, title, publisher, publication time, retrieval time, content hash, and a claim-to-evidence mapping;
4. the immutable pre-decision portfolio snapshot, market snapshot IDs, and FX snapshot IDs actually presented to the model; and
5. a validated schema version and attestation binding the response, identity, prompt, snapshots, and evidence.

A model mismatch, missing field, malformed output, unverified evidence, evidence published or available after the decision, stale snapshot, or failed attestation rejects the AI decision; it is retained only as a bounded failed-run audit record and creates no order. No model substitution is implicit. A valid hold retains the full required identity, rationale, evidence, attestation, run status, and timestamps but creates no order. A technical failed/no-decision run retains model identity, run status, timestamps, and bounded error, and is never presented as a valid AI decision.

## UI, API, authorization, and mutation authority

- The UI is a separate untrusted client. It has no database, provider, runner, executor, ledger, broker, or service credentials and contains no authoritative validation or accounting logic. It reads and submits only through the versioned API and clearly displays mode, participant type, data timestamp/currency, delay/staleness, and paper-only status.
- The API authenticates every request and performs server-side object authorization on every read, export, note, order-intent, cancellation-intent, stream, and subscription. Possession or guessing of a competition, portfolio, order, event, participant, or note ID grants no access.
- Authorization is checked against the authenticated principal, immutable mode, competition membership, participant/runner binding, portfolio ownership, and operation at the time of use. List and realtime paths apply the same row/object filter as detail paths. Cross-mode and cross-owner access fails closed without revealing object existence, except for deliberately published leaderboard fields.
- The public/API process has no SQL permission to insert, update, or delete fills, balances, positions, ledger events, valuation facts, or ranking facts. It may append validated user-owned notes and immutable command intents within its grant.
- The executor is the sole component allowed to transition order intents and request accounting mutations. The ledger is the sole authority allowed to atomically append fills/accounting events and derive cash and positions. Database grants and constraints enforce this separation; no UI, API, AI runner, human, administrator console, migration-at-runtime path, or provider adapter can directly mutate portfolio state.
- Every command is idempotent within its mode, competition, actor, and portfolio scope. The executor rechecks authorization binding, lifecycle state, instrument eligibility, quote/FX freshness, cash, holdings, and all risk rules in the same serialized transaction that appends the ledger result.
- Pause, correction, and administrative operations are separate, least-privilege, audited capabilities. Pause prevents new execution; it does not rewrite history. Corrections use the append-only ledger correction protocol and cannot impersonate a participant decision.

## Negative capability: no broker

The v2 candidate must contain no broker SDK or dependency, broker credential or environment read, brokerage endpoint/URL, account-link flow, real-order route, subprocess or plugin capable of placing a real order, or network permission to a broker execution service. All order objects and fills are explicitly simulated. “Paper”, “sandbox”, or an owner-only route is not sufficient if the code can reach a brokerage capability.

The API and executor accept only internal paper intents. Provider adapters are read-only market/reference-data clients. A network-denied executable negative-capability probe plus source, dependency, route, environment, and process inventories must prove this on each candidate.

## Deployment gates

V2 remains blocked from production execution until all of the following are approved and versioned for the intended production use:

1. a provider/legal record covering automated access, all admitted venues, quote and trade semantics, service levels, retention, storage, display and redistribution rights;
2. an effective-dated common-stock index and identifier/change process;
3. quote freshness/delay, session calendar, venue/time-zone, halt/status, and closing-price rules;
4. corporate-action coverage and deterministic normalization/correction rules;
5. event-time FX sources, pairs/triangulation, freshness, outage handling, and DKK conversion/audit rules;
6. provider outage, revision, replay, rate-limit, integrity, and provenance behavior; and
7. production credentials, network policy, least-privilege roles, monitoring, backup/restore, and incident runbooks.

Approval on paper is insufficient. The exact production adapter and deployment network must successfully acquire, index, replay, and exercise representative data for every admitted venue/currency and each required lifecycle event. Until then only seed/test demonstrations may run, labelled non-production and with no production-readiness claim.

The v2 deployment must be isolated from the legacy Swedish championship: separate mode/configuration, credentials, provider approval manifest, database roles and state, routes/hostname, workers, backups, and readiness signal. A v2 failure or reset must not mutate, pause, expose, or erase the legacy contest.

## Verification and release gate

Every item must pass on the exact immutable candidate; any failure, skip, missing prerequisite, or stale approval blocks deployment:

- contract/schema tests for the closed mode enum, 100,000.00 DKK idempotent initialization, actor-type evidence differences, AI identity/attestation rejection, and mode isolation;
- unit and real PostgreSQL integration tests proving serialized/idempotent executor-ledger transitions, immutable correction history, and database denial for UI/API/runner/provider roles;
- adversarial API tests for cross-owner, cross-participant, cross-competition, and cross-mode IDs across detail, list, export, note, command, and realtime paths;
- provider contract tests and production-network exercises for index revisions, common-stock classification, identifiers, quote delay/freshness, calendars, halts, corporate actions, FX, checksums, revisions, outage, and rate limits;
- stale/missing/tampered/unapproved quote and FX tests proving zero fills, zero ledger mutation, no fixture/provider fallback, and an explicit unavailable valuation;
- deterministic DKK replay tests reproducing fills and leaderboard rows from immutable quote/FX IDs, including conversion direction/triangulation and unavailable rows;
- UI/API separation tests proving no secrets or direct data/execution access in the UI and accurate paper/mode/actor/currency/freshness labels;
- the executable no-broker proof described above under network denial;
- migration from an empty database, upgrade rehearsal, concurrency/load tests, backup plus destructive restore into an isolated test target, monitoring/alert exercises, and rollback rehearsal;
- an end-to-end dry run for each enabled mode and every admitted venue/currency, followed by independent security, accounting, provider-rights, and exact-tree review; and
- isolation tests proving v2 start, stop, pause, reset, restore, and failure cannot affect the Swedish 2026 championship.

Readiness and liveness are separate. Readiness stays closed when any required provider approval, index revision, quote/FX/calendar/corporate-action feed, database invariant, or verification evidence is absent or expired. No skipped test or local seed/test-provider result counts as production evidence.
