"""Production-shaped scheduler → Hermes → paper-trading worker runtime."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import replace
from datetime import UTC, datetime
from decimal import Decimal
from typing import Any

from sqlalchemy import select
from sqlalchemy.orm import Session

from .calendar import STOCKHOLM, session_for
from .market import MarketProvider, Quote
from .models import AgentRun, SystemState
from .orchestration import AgentOrchestrator
from .research import ResearchVerificationError, ResearchVerifier
from .runner import AgentContext, Decision, HermesRunner
from .trading import Evidence, OrderRequest, Source, TradingError, TradingService


class WorkerRuntime:
    """Run one durable orchestration claim at a time without an HTTP mutation surface."""

    def __init__(
        self,
        session: Session,
        *,
        runner: HermesRunner,
        market: MarketProvider,
        clock: Callable[[], datetime] = lambda: datetime.now(UTC),
        research_verifier: ResearchVerifier | None = None,
    ) -> None:
        self.session = session
        self.runner = runner
        self.market = market
        self.clock = clock
        self.research_verifier = research_verifier or ResearchVerifier()
        self.trading = TradingService(session, market, auto_commit=False)
        self.queued_trading = TradingService(session, market)
        self.orchestrator = AgentOrchestrator(
            session,
            runner=runner,
            context_provider=self.context_for,
            decision_handler=self.handle_decision,
        )

    def tick(self, now: datetime | None = None) -> str | None:
        now = now or self.clock()
        if now.tzinfo is None or now.utcoffset() is None:
            raise ValueError("worker clock must be timezone aware")
        state = self.session.get(SystemState, 1)
        if state is None or state.paused or state.contest_status != "RUNNING":
            self.session.rollback()
            return None
        local_day = now.astimezone(STOCKHOLM).date()
        try:
            trading_session = session_for(local_day)
        except ValueError:
            self.session.rollback()
            return None
        if trading_session is None:
            self.session.rollback()
            return None
        self.queued_trading.execute_queued(now)
        self.orchestrator.ensure_session(trading_session)
        return self.orchestrator.run_next(now.astimezone(UTC))

    def context_for(self, agent_id: str) -> AgentContext:
        portfolio = self.trading.portfolio(agent_id)
        runs = list(
            self.session.scalars(
                select(AgentRun)
                .where(AgentRun.agent_id == agent_id, AgentRun.status == "OK")
                .order_by(AgentRun.ended_at.desc(), AgentRun.id.desc())
                .limit(50)
            )
        )
        history = tuple(
            {
                "run_key": run.run_key,
                "ended_at": _iso(run.ended_at),
                "decision": run.decision_json,
            }
            for run in reversed(runs)
        )
        private_notes = ""
        for run in runs:
            decision = run.decision_json or {}
            update = decision.get("strategy_update")
            if isinstance(update, str) and update.strip():
                private_notes = update.strip()
                break
        return AgentContext(
            agent_id=agent_id,
            portfolio=_portfolio_json(portfolio.cash, portfolio.holdings),
            history=history,
            private_notes=private_notes,
            research_data=(),
        )

    def handle_decision(
        self,
        agent_id: str,
        run_key: str,
        decision: Decision,
        context: AgentContext,
    ) -> None:
        if decision.action == "hold":
            return
        if decision.action == "cancel_pending":
            if not decision.pending_order_id:
                raise TradingError("cancel decision has no pending order")
            self.trading.cancel_order(
                agent_id,
                decision.pending_order_id,
                decision.reason,
                decision.decision_at,
                run_key,
            )
            return
        if decision.symbol is None or decision.quantity is None:
            raise TradingError("trade decision is incomplete")
        observed_at = self.clock()
        try:
            verified_research = self.research_verifier.verify(
                decision.evidence, observed_at=observed_at
            )
        except ResearchVerificationError as exc:
            raise TradingError(f"independent research verification failed: {exc}") from exc
        context = replace(context, research_data=verified_research)
        _require_verified_research(decision, context)
        quote = self._observed_quote(decision.symbol, decision.decision_at, observed_at)
        evidence = Evidence(
            reason=decision.reason,
            catalyst=decision.catalyst or "No catalyst supplied",
            sources=[
                Source(url=source.url, published_at=source.published_at)
                for source in decision.evidence
            ],
            risks=list(decision.risks),
            confidence=Decimal(str(decision.confidence)),
            model_id=decision.model_id,
            decision_at=decision.decision_at,
            observed_price=quote.price if quote is not None else None,
            observed_portfolio=dict(context.portfolio),
            research_verification=[dict(record) for record in verified_research],
        )
        self.trading.submit(
            OrderRequest(
                decision_id=run_key,
                agent_id=agent_id,
                symbol=decision.symbol,
                side="BUY" if decision.action == "buy" else "SELL",
                quantity=decision.quantity,
                evidence=evidence,
            ),
            observed_at=observed_at,
        )

    def _observed_quote(
        self, symbol: str, decision_at: datetime, observed_at: datetime
    ) -> Quote | None:
        if observed_at.tzinfo is None or observed_at.utcoffset() is None:
            raise TradingError("worker observation clock must be timezone aware")
        if self.market.session_containing(decision_at) is not None:
            return self.market.first_quote_at_or_after(symbol, decision_at, as_of=observed_at)
        return self.market.latest_quote_at_or_before(symbol, decision_at, as_of=observed_at)


def _require_verified_research(decision: Decision, context: AgentContext) -> None:
    verified: set[tuple[str, datetime, str]] = set()
    for raw in context.research_data:
        if not isinstance(raw, dict) or raw.get("verified") is not True:
            continue
        url = raw.get("url")
        claim = raw.get("claim")
        checksum = raw.get("content_sha256")
        try:
            published_at = datetime.fromisoformat(str(raw["published_at"]).replace("Z", "+00:00"))
            observed_at = datetime.fromisoformat(str(raw["observed_at"]).replace("Z", "+00:00"))
        except (KeyError, ValueError):
            continue
        if (
            not isinstance(url, str)
            or not isinstance(claim, str)
            or not isinstance(checksum, str)
            or len(checksum) != 64
            or any(character not in "0123456789abcdef" for character in checksum)
            or published_at.tzinfo is None
            or observed_at.tzinfo is None
            or published_at > observed_at
            or (
                raw.get("verification_mode") != "independent_fetch"
                and observed_at > decision.decision_at
            )
        ):
            continue
        verified.add((url, published_at, claim))
    for source in decision.evidence:
        if (source.url, source.published_at, source.claim) not in verified:
            raise TradingError("decision source is not bound to a verified research observation")


def _portfolio_json(cash: Decimal, holdings: dict[str, int]) -> dict[str, Any]:
    return {"cash": f"{cash:.2f}", "holdings": dict(sorted(holdings.items()))}


def _iso(value: datetime) -> str:
    if value.tzinfo is None or value.utcoffset() is None:
        value = value.replace(tzinfo=UTC)
    return value.astimezone(UTC).isoformat()
