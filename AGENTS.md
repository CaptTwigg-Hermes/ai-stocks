# AI Stocks engineering rules

- Read `docs/acceptance-contract.md`, `docs/market-data-contract.md`, and `docs/implementation-decisions.md` before changing behavior.
- Paper trading only. Never add broker SDKs, broker credentials, or real-order endpoints.
- Use strict RED-GREEN-REFACTOR. PostgreSQL-backed state transitions require integration tests.
- Use C# `decimal` for money; preserve immutable audit events and transactional invariants.
- Fail closed on stale/missing/unverified market data, malformed model output, evidence failure, or identity mismatch.
- The four model IDs are fixed. Never silently substitute or share competitor state.
- No public mutation API. Viewer endpoints are read-only; owner controls require verified Cloudflare Access identity.
- Production code targets .NET 10 and treats warnings as errors.
- Before source changes, run `graphify query "<task>"` when `graphify-out/graph.json` exists. Run `graphify update .` after source changes.
- Do not commit or release unless the complete launch gate in the acceptance contract passes.

## Repository and worktree hygiene

- `/opt/data/ai-stocks-build/coordinator` is the single working
  checkout and always tracks `origin/main`. Work there unless a
  task genuinely requires parallel isolated checkouts.
- If you create extra worktrees, delete them in the same session.
  Never leave a stale checkout behind: a later agent can mistake
  it for live code and rebuild work that already shipped.
- Before treating any checkout as authoritative, confirm it:
  run `git status -sb` and check it tracks current `origin/main`.
- `git log origin/main..<branch>` overstates divergence when
  history was squashed or rebased. Use `git cherry origin/main
  <branch>` to test patch-equivalence, and compare file contents
  against `origin/main` before concluding work is unmerged.
- Every project in `AiStocks.slnx` is built and tested by CI.
  When adding a project, add it AND its test project to the
  solution, or its tests will silently never run.
- Archived history from the 2026-08-20 cleanup lives in local
  tags `archive/2026-08-20/*` and tarballs under
  `/opt/data/archive/ai-stocks-2026-08-20/`.
