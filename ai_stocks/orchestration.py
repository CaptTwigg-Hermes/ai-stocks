"""Database-backed scheduler-to-runner-to-trading orchestration."""

from __future__ import annotations

from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from uuid import uuid4

from sqlalchemy import or_, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from .calendar import TradingSession
from .models import Agent, AgentRun, ScheduledAgentRun
from .runner import AgentContext, Decision, HermesRunner, RunnerResult
from .schedule import RunWindow, build_run_windows

ContextProvider = Callable[[str], AgentContext]
DecisionHandler = Callable[[str, str, Decision, AgentContext], None]


class AgentOrchestrator:
    """Durably claim fixed-model windows and retain one immutable record per attempt."""

    def __init__(
        self,
        session: Session,
        *,
        runner: HermesRunner,
        context_provider: ContextProvider,
        decision_handler: DecisionHandler,
        retry_delay: timedelta = timedelta(minutes=1),
        claim_lease: timedelta = timedelta(minutes=6),
    ) -> None:
        if retry_delay <= timedelta(0) or claim_lease <= timedelta(0):
            raise ValueError("retry delay and claim lease must be positive")
        self.session = session
        self.runner = runner
        self.context_provider = context_provider
        self.decision_handler = decision_handler
        self.retry_delay = retry_delay
        self.claim_lease = claim_lease

    def ensure_session(self, trading_session: TradingSession) -> int:
        inserted = 0
        for window in build_run_windows(trading_session):
            if self.session.get(ScheduledAgentRun, window.id) is not None:
                continue
            self.session.add(self._scheduled(window))
            try:
                self.session.commit()
                inserted += 1
            except IntegrityError:
                self.session.rollback()
        return inserted

    def run_next(self, now: datetime) -> str | None:
        now = _aware(now, "orchestrator clock")
        scheduled = self._claim(now)
        if scheduled is None:
            return None
        run_key = scheduled.run_key
        if now >= _utc(scheduled.deadline_at):
            self._record_missed(scheduled, now, "retry_window_expired")
            return run_key

        agent = self.session.get(Agent, scheduled.agent_id)
        if agent is None or agent.model_id != scheduled.model_id:
            self._record_missed(scheduled, now, "agent_model_identity_mismatch")
            return run_key

        try:
            context = self.context_provider(scheduled.agent_id)
        except Exception as exc:
            self._record_failure(
                scheduled, now, None, f"context_error: {type(exc).__name__}: {exc}"
            )
            return run_key
        if context.agent_id != scheduled.agent_id:
            self._record_missed(scheduled, now, "context_agent_identity_mismatch")
            return run_key

        result = self.runner.run(
            model_id=scheduled.model_id,
            context=context,
            decision_at=_utc(scheduled.scheduled_at),
        )
        if not result.ok or result.decision is None:
            self._record_failure(scheduled, now, result, result.error or "runner_failed")
            return run_key

        handler_error = None
        try:
            with self.session.begin_nested():
                self.decision_handler(scheduled.agent_id, run_key, result.decision, context)
        except Exception as exc:
            handler_error = f"trading_error: {type(exc).__name__}: {exc}"
        if handler_error is not None:
            self._record_failure(scheduled, now, result, handler_error)
        else:
            self._record_success(scheduled, now, result)
        return run_key

    def _claim(self, now: datetime) -> ScheduledAgentRun | None:
        statement = (
            select(ScheduledAgentRun)
            .where(
                ScheduledAgentRun.scheduled_at <= now,
                or_(
                    ScheduledAgentRun.status == "PENDING",
                    (ScheduledAgentRun.status == "CLAIMED") & (ScheduledAgentRun.lease_until < now),
                ),
                ScheduledAgentRun.next_attempt_at <= now,
            )
            .order_by(ScheduledAgentRun.scheduled_at, ScheduledAgentRun.agent_id)
            .limit(1)
        )
        if self.session.bind and self.session.bind.dialect.name == "postgresql":
            statement = statement.with_for_update(skip_locked=True)
        row = self.session.scalar(statement)
        if row is None:
            self.session.rollback()
            return None
        row.status = "CLAIMED"
        row.claim_token = str(uuid4())
        row.lease_until = now + self.claim_lease
        self.session.commit()
        return row

    def _record_success(
        self, scheduled: ScheduledAgentRun, now: datetime, result: RunnerResult
    ) -> None:
        scheduled.attempt_count += 1
        self.session.add(self._audit(scheduled, now, result, "OK"))
        scheduled.status = "COMPLETED"
        scheduled.claim_token = None
        scheduled.lease_until = None
        self.session.commit()

    def _record_failure(
        self,
        scheduled: ScheduledAgentRun,
        now: datetime,
        result: RunnerResult | None,
        error: str,
    ) -> None:
        scheduled.attempt_count += 1
        retry_at = min(now + self.retry_delay, _utc(scheduled.deadline_at))
        self.session.add(self._audit(scheduled, now, result, "ERROR", error, retry_at))
        scheduled.status = "PENDING"
        scheduled.next_attempt_at = retry_at
        scheduled.claim_token = None
        scheduled.lease_until = None
        self.session.commit()

    def _record_missed(self, scheduled: ScheduledAgentRun, now: datetime, reason: str) -> None:
        scheduled.attempt_count += 1
        self.session.add(self._audit(scheduled, now, None, "MISSED", missed_reason=reason))
        scheduled.status = "MISSED"
        scheduled.claim_token = None
        scheduled.lease_until = None
        self.session.commit()

    @staticmethod
    def _scheduled(window: RunWindow) -> ScheduledAgentRun:
        return ScheduledAgentRun(
            run_key=window.id,
            agent_id=window.agent_id,
            model_id=window.model_id,
            scheduled_at=window.scheduled_at.astimezone(UTC),
            deadline_at=window.deadline_at.astimezone(UTC),
            status="PENDING",
            attempt_count=0,
            next_attempt_at=window.scheduled_at.astimezone(UTC),
        )

    @staticmethod
    def _audit(
        scheduled: ScheduledAgentRun,
        now: datetime,
        result: RunnerResult | None,
        status: str,
        validation_error: str | None = None,
        retry_at: datetime | None = None,
        missed_reason: str | None = None,
    ) -> AgentRun:
        decision = result.decision if result else None
        sources = (
            [
                {
                    "url": source.url,
                    "published_at": source.published_at.isoformat(),
                    "claim": source.claim,
                }
                for source in decision.evidence
            ]
            if decision
            else []
        )
        decision_json = None
        if decision:
            decision_json = {
                "schema_version": decision.schema_version,
                "model_id": decision.model_id,
                "decision_at": decision.decision_at.isoformat(),
                "action": decision.action,
                "symbol": decision.symbol,
                "quantity": decision.quantity,
                "pending_order_id": decision.pending_order_id,
                "reason": decision.reason,
                "catalyst": decision.catalyst,
                "risks": list(decision.risks),
                "confidence": decision.confidence,
                "strategy_update": decision.strategy_update,
            }
        return AgentRun(
            run_key=scheduled.run_key,
            attempt=scheduled.attempt_count,
            agent_id=scheduled.agent_id,
            model_id=scheduled.model_id,
            prompt_version=result.prompt_contract_version if result else "1",
            prompt=result.prompt if result else "",
            raw_response=result.stdout if result else "",
            stderr=result.stderr if result else "",
            command_json=list(result.command) if result else [],
            sources=sources,
            provenance_json={
                "provider": result.provider if result else "copilot",
                "model_id": scheduled.model_id,
                "scheduled_at": _utc(scheduled.scheduled_at).isoformat(),
                "deadline_at": _utc(scheduled.deadline_at).isoformat(),
                "returncode": result.returncode if result else None,
                "timed_out": result.timed_out if result else False,
                "output_exceeded": result.output_exceeded if result else False,
            },
            decision_json=decision_json,
            validation_error=validation_error,
            retry_at=retry_at,
            missed_reason=missed_reason,
            started_at=now,
            ended_at=now,
            status=status,
        )


def _aware(value: datetime, label: str) -> datetime:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError(f"{label} must be timezone-aware")
    return value.astimezone(UTC)


def _utc(value: datetime) -> datetime:
    # SQLite drops timezone offsets; scheduled values are always stored as UTC.
    return value.replace(tzinfo=UTC) if value.tzinfo is None else value.astimezone(UTC)
