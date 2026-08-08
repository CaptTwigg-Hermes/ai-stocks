# AI Stocks engineering rules

- Read `docs/acceptance-contract.md` before changing behavior.
- Paper trading only. Never add broker SDKs, broker credentials, or real-order endpoints.
- Use strict RED-GREEN-REFACTOR. PostgreSQL-backed state transitions require integration tests.
- Use `Decimal` for money; preserve immutable audit events and transactional invariants.
- Fail closed on stale/missing/unverified market data, malformed model output, evidence failure, or identity mismatch.
- The four model IDs are fixed. Never silently substitute or share competitor state.
- No public mutation API. Viewer endpoints are read-only; owner controls require verified Cloudflare Access identity.
- Before source changes, run `graphify query "<task>"` when `graphify-out/graph.json` exists. Run `graphify update .` after source changes.
- Do not commit or release unless the complete launch gate in the acceptance contract passes.
