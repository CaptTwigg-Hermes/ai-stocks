# Graph Report - ai-stocks  (2026-08-07)

## Corpus Check
- 66 files · ~41,963 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 580 nodes · 2058 edges · 29 communities (23 shown, 6 thin omitted)
- Extraction: 88% EXTRACTED · 12% INFERRED · 0% AMBIGUOUS · INFERRED: 247 edges (avg confidence: 0.57)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 22|Community 22]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 26|Community 26]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]

## God Nodes (most connected - your core abstractions)
1. `TradingService` - 83 edges
2. `TradingError` - 45 edges
3. `Quote` - 38 edges
4. `LedgerEvent` - 38 edges
5. `ContestOperations` - 37 edges
6. `session_for()` - 35 edges
7. `SessionWindow` - 34 edges
8. `Agent` - 33 edges
9. `OrderRequest` - 33 edges
10. `AgentOrchestrator` - 32 edges

## Surprising Connections (you probably didn't know these)
- `test_full_and_half_day_session_boundaries_are_exact()` --calls--> `session_for()`  [EXTRACTED]
  tests/test_nasdaq_market.py → ai_stocks/calendar.py
- `test_startup_fails_closed_without_access_configuration()` --calls--> `create_app()`  [EXTRACTED]
  tests/test_app_and_day.py → ai_stocks/app.py
- `test_bootstrap_is_complete_idempotent_and_refuses_partial_state()` --calls--> `bootstrap()`  [EXTRACTED]
  tests/test_bootstrap.py → ai_stocks/bootstrap.py
- `test_weekends_holidays_start_rule_and_final_session_fail_closed()` --calls--> `session_for()`  [EXTRACTED]
  tests/test_calendar.py → ai_stocks/calendar.py
- `_window()` --calls--> `session_for()`  [EXTRACTED]
  tests/test_nasdaq_market.py → ai_stocks/calendar.py

## Import Cycles
- None detected.

## Communities (29 total, 6 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.17
Nodes (4): money(), TradingError, TradingService, Decimal

### Community 1 - "Community 1"
Cohesion: 0.13
Nodes (24): MarketProvider, Quote, SessionWindow, _Archive, _aware(), _Instrument, InstrumentStatus, MarketDataError (+16 more)

### Community 2 - "Community 2"
Cohesion: 0.08
Nodes (37): create_app(), AccessConfig, AccessJWTValidator, AuthenticationError, _normal_email(), An Access assertion could not be authenticated or locally authorized., _validated_emails(), _validated_origin() (+29 more)

### Community 3 - "Community 3"
Cohesion: 0.12
Nodes (50): bootstrap(), BootstrapError, _is_complete(), main(), One-time, idempotent initialization of the immutable contest state., Base, Agent, CorporateAction (+42 more)

### Community 4 - "Community 4"
Cohesion: 0.14
Nodes (13): AI Swedish Paper-Trading Contest — Grill Outcome, Competitors and isolation, Evidence and audit, Fees, tax, income, and corporate actions, Goal, Human control, Launch gate, Orders and execution (+5 more)

### Community 5 - "Community 5"
Cohesion: 0.11
Nodes (40): session_for(), six_run_times(), AgentRun, ScheduledAgentRun, AgentOrchestrator, _aware(), Database-backed scheduler-to-runner-to-trading orchestration., Durably claim fixed-model windows and retain one immutable record per attempt. (+32 more)

### Community 6 - "Community 6"
Cohesion: 0.25
Nodes (7): Calendar, Corporate actions, Execution authority, Fallbacks, Market-data contract, Twenty-session liquidity warm-up, Universe and reference data

### Community 7 - "Community 7"
Cohesion: 0.25
Nodes (7): AI Stocks, Backups, Destructive restore rehearsal, Dockge preparation, Explicit migration and bootstrap, Production topology, Stop and incident handling

### Community 9 - "Community 9"
Cohesion: 0.62
Nodes (6): restore-test.sh script, assert_test_database(), cleanup(), clear_test_database(), fail(), pg_tool()

### Community 17 - "Community 17"
Cohesion: 0.12
Nodes (20): FakeMarket, AgentContext, Decision, State for exactly one competitor; callers must create one context per agent., _iso(), _portfolio_json(), Production-shaped scheduler → Hermes → paper-trading worker runtime., Run one durable orchestration claim at a time without an HTTP mutation surface. (+12 more)

### Community 18 - "Community 18"
Cohesion: 0.09
Nodes (39): contest_final_session(), next_full_session(), Pinned Nasdaq Stockholm equity calendar for the 2026 contest., SessionKind, TradingSession, verify_source_artifacts(), collect_once(), CollectionResult (+31 more)

### Community 19 - "Community 19"
Cohesion: 0.13
Nodes (17): AccessIdentity, DeliveryError, DeliveryReceipt, HermesDiscordDelivery, Discord delivery through the already-configured Hermes gateway credentials., SeriousAlertKind, ContestStateEvent, CriticalAlert (+9 more)

### Community 20 - "Community 20"
Cohesion: 0.70
Nodes (4): AST, dotted_name(), inventory(), main()

### Community 22 - "Community 22"
Cohesion: 0.83
Nodes (3): _create_sqlite_agent_run_triggers(), downgrade(), upgrade()

### Community 25 - "Community 25"
Cohesion: 0.13
Nodes (32): build_prompt(), DecisionValidationError, _evidence(), _exact_keys(), execute_process(), _json(), _kill_process_group(), _optional_string() (+24 more)

### Community 26 - "Community 26"
Cohesion: 0.11
Nodes (16): _normalize(), _PinnedHTTPSConnection, Pinned-IP, independently verified public research evidence., ResearchVerificationError, ResearchVerifier, _VisibleText, EvidenceSource, HTMLParser (+8 more)

### Community 28 - "Community 28"
Cohesion: 0.18
Nodes (21): main(), check_heartbeat(), HealthError, heartbeat_path(), main(), Container liveness heartbeats for non-HTTP services., write_heartbeat(), _aware() (+13 more)

## Knowledge Gaps
- **29 isolated node(s):** `ai-stocks`, `entrypoint.sh script`, `migrate.sh script`, `AI Stocks engineering rules`, `Production topology` (+24 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **6 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `TradingService` connect `Community 0` to `Community 1`, `Community 2`, `Community 3`, `Community 5`, `Community 17`, `Community 19`?**
  _High betweenness centrality (0.079) - this node is a cross-community bridge._
- **Why does `session_for()` connect `Community 5` to `Community 1`, `Community 18`, `Community 19`, `Community 17`?**
  _High betweenness centrality (0.026) - this node is a cross-community bridge._
- **Why does `TradingError` connect `Community 0` to `Community 1`, `Community 3`, `Community 5`, `Community 17`, `Community 25`?**
  _High betweenness centrality (0.023) - this node is a cross-community bridge._
- **Are the 13 inferred relationships involving `TradingService` (e.g. with `MarketProvider` and `Quote`) actually correct?**
  _`TradingService` has 13 INFERRED edges - model-reasoned connections that need verification._
- **Are the 30 inferred relationships involving `Decimal` (e.g. with `create_app()` and `run_deterministic_day()`) actually correct?**
  _`Decimal` has 30 INFERRED edges - model-reasoned connections that need verification._
- **Are the 13 inferred relationships involving `TradingError` (e.g. with `MarketProvider` and `Quote`) actually correct?**
  _`TradingError` has 13 INFERRED edges - model-reasoned connections that need verification._
- **Are the 15 inferred relationships involving `Quote` (e.g. with `_Archive` and `_Instrument`) actually correct?**
  _`Quote` has 15 INFERRED edges - model-reasoned connections that need verification._