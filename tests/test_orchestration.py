import json
from datetime import UTC, date, datetime, timedelta
from decimal import Decimal

from sqlalchemy import create_engine, delete, select
from sqlalchemy.orm import Session

from ai_stocks.calendar import session_for
from ai_stocks.db import Base
from ai_stocks.models import Agent, AgentRun, LedgerEvent, ScheduledAgentRun
from ai_stocks.orchestration import AgentOrchestrator
from ai_stocks.runner import AgentContext, HermesRunner, ProcessCapture
from ai_stocks.schedule import AGENT_BY_MODEL, FIXED_MODELS, build_run_windows

NOW = datetime(2026, 8, 6, 6, 0, tzinfo=UTC)
MODEL = FIXED_MODELS[0]
AGENT_ID = AGENT_BY_MODEL[MODEL]


def context(agent_id=AGENT_ID):
    return AgentContext(
        agent_id=agent_id,
        portfolio={"cash": "30000", "holdings": {}},
        history=[],
        private_notes="private",
        research_data=[],
    )


def response(model_id=MODEL, decision_at=NOW):
    return json.dumps(
        {
            "schema_version": "1",
            "model_id": model_id,
            "decision_at": decision_at.isoformat().replace("+00:00", "Z"),
            "action": "hold",
            "symbol": None,
            "quantity": None,
            "pending_order_id": None,
            "reason": "wait",
            "catalyst": None,
            "evidence": [],
            "risks": ["uncertainty"],
            "confidence": 0.5,
            "strategy_update": None,
        }
    )


def database():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    with Session(engine) as session:
        for model_id, agent_id in AGENT_BY_MODEL.items():
            session.add(
                Agent(
                    id=agent_id,
                    model_id=model_id,
                    initial_cash=Decimal("30000"),
                    fee_tier="STARTER",
                    stock_trade_count=0,
                )
            )
        session.commit()
    return engine


def retain_window(session, run_key):
    session.execute(delete(ScheduledAgentRun).where(ScheduledAgentRun.run_key != run_key))
    session.commit()


def test_schedule_is_durable_idempotent_and_uses_canonical_agent_ids():
    engine = database()
    trading_session = session_for(date(2026, 8, 6))
    assert trading_session is not None
    with Session(engine) as session:
        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(executor=lambda *_: ProcessCapture(1, "", "unused")),
            context_provider=lambda _agent_id: context(),
            decision_handler=lambda *_: None,
        )
        assert orchestrator.ensure_session(trading_session) == 24
        assert orchestrator.ensure_session(trading_session) == 0
        rows = session.scalars(select(ScheduledAgentRun)).all()
        assert {row.run_key for row in rows} == {
            window.id for window in build_run_windows(trading_session)
        }
        assert {row.agent_id for row in rows} == set(AGENT_BY_MODEL.values())
    engine.dispose()


def test_claim_invokes_exact_model_and_audits_prompt_response_validation_and_provenance():
    engine = database()
    seen_commands = []
    handled = []

    def execute(argv, _timeout, _limit):
        seen_commands.append(argv)
        return ProcessCapture(0, response(decision_at=NOW), "runner warning")

    with Session(engine) as session:
        window = build_run_windows(session_for(date(2026, 8, 6)))[0]
        assert window.scheduled_at == NOW
        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(executor=execute),
            context_provider=lambda agent_id: context(agent_id),
            decision_handler=lambda agent_id, run_key, decision, supplied_context: handled.append(
                (agent_id, run_key, decision, supplied_context)
            ),
        )
        orchestrator.ensure_session(session_for(date(2026, 8, 6)))
        retain_window(session, window.id)
        assert orchestrator.run_next(NOW) == window.id
        assert orchestrator.run_next(NOW) is None

        scheduled = session.get(ScheduledAgentRun, window.id)
        audit = session.scalar(select(AgentRun).where(AgentRun.run_key == window.id))
        assert scheduled.status == "COMPLETED"
        assert scheduled.attempt_count == 1
        assert audit.status == "OK"
        assert audit.attempt == 1
        assert audit.prompt.startswith("HERMES_ONE_SHOT_CONTRACT_V1")
        assert audit.raw_response == response(decision_at=NOW)
        assert audit.validation_error is None
        assert audit.provenance_json["provider"] == "copilot"
        assert audit.provenance_json["model_id"] == MODEL
        assert audit.decision_json["action"] == "hold"
        assert seen_commands[0][4] == MODEL
        assert handled[0][0:2] == (AGENT_ID, window.id)
    engine.dispose()


def test_invalid_response_retries_only_inside_window_then_records_missed_without_trading():
    engine = database()
    handled = []
    attempts = []

    def execute(*_):
        attempts.append(1)
        return ProcessCapture(0, "not-json", "")

    with Session(engine) as session:
        window = build_run_windows(session_for(date(2026, 8, 6)))[0]
        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(executor=execute),
            context_provider=lambda agent_id: context(agent_id),
            decision_handler=lambda *_: handled.append(True),
            retry_delay=timedelta(minutes=5),
        )
        orchestrator.ensure_session(session_for(date(2026, 8, 6)))
        retain_window(session, window.id)
        assert orchestrator.run_next(NOW) == window.id
        scheduled = session.get(ScheduledAgentRun, window.id)
        assert scheduled.status == "PENDING"
        assert scheduled.next_attempt_at.replace(tzinfo=UTC) == NOW + timedelta(minutes=5)
        assert orchestrator.run_next(NOW + timedelta(minutes=4)) is None
        assert orchestrator.run_next(NOW + timedelta(minutes=5)) == window.id
        assert orchestrator.run_next(window.deadline_at + timedelta(microseconds=1)) == window.id

        session.expire_all()
        scheduled = session.get(ScheduledAgentRun, window.id)
        audits = session.scalars(
            select(AgentRun).where(AgentRun.run_key == window.id).order_by(AgentRun.attempt)
        ).all()
        assert scheduled.status == "MISSED"
        assert [audit.status for audit in audits] == ["ERROR", "ERROR", "MISSED"]
        assert audits[0].validation_error.startswith("invalid_response")
        assert audits[-1].missed_reason == "retry_window_expired"
        assert len(attempts) == 2
        assert handled == []
    engine.dispose()


def test_handler_side_effects_roll_back_when_handler_raises():
    engine = database()

    with Session(engine) as session:

        def failing_handler(agent_id, *_):
            session.add(
                LedgerEvent(
                    agent_id=agent_id,
                    event_type="INITIAL_CASH",
                    cash_delta=Decimal("1"),
                    quantity_delta=0,
                    occurred_at=NOW,
                    reference_id="must-rollback",
                    metadata_json={},
                )
            )
            session.flush()
            raise RuntimeError("audit path failure")

        window = build_run_windows(session_for(date(2026, 8, 6)))[0]
        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(
                executor=lambda *_: ProcessCapture(0, response(decision_at=NOW), "")
            ),
            context_provider=lambda agent_id: context(agent_id),
            decision_handler=failing_handler,
        )
        orchestrator.ensure_session(session_for(date(2026, 8, 6)))
        retain_window(session, window.id)
        assert orchestrator.run_next(NOW) == window.id

        assert (
            session.scalar(select(LedgerEvent).where(LedgerEvent.reference_id == "must-rollback"))
            is None
        )
        audit = session.scalar(select(AgentRun).where(AgentRun.run_key == window.id))
        assert audit.status == "ERROR"
        assert "audit path failure" in audit.validation_error
    engine.dispose()


def test_exact_deadline_is_missed_without_starting_runner():
    engine = database()
    calls = []
    with Session(engine) as session:
        window = build_run_windows(session_for(date(2026, 8, 6)))[0]
        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(executor=lambda *_: calls.append("runner")),
            context_provider=lambda agent_id: context(agent_id),
            decision_handler=lambda *_: calls.append("trading"),
        )
        orchestrator.ensure_session(session_for(date(2026, 8, 6)))
        retain_window(session, window.id)
        assert orchestrator.run_next(window.deadline_at) == window.id
        audit = session.scalar(select(AgentRun).where(AgentRun.run_key == window.id))
        assert audit.status == "MISSED"
        assert calls == []
    engine.dispose()


def test_agent_model_identity_mismatch_fails_closed_without_runner_or_trading():
    engine = database()
    calls = []
    with Session(engine) as session:
        window = build_run_windows(session_for(date(2026, 8, 6)))[0]
        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(executor=lambda *_: calls.append("runner")),
            context_provider=lambda agent_id: context(agent_id),
            decision_handler=lambda *_: calls.append("trading"),
        )
        orchestrator.ensure_session(session_for(date(2026, 8, 6)))
        retain_window(session, window.id)
        session.get(ScheduledAgentRun, window.id).model_id = FIXED_MODELS[1]
        session.commit()
        assert orchestrator.run_next(NOW) == window.id
        audit = session.scalar(select(AgentRun).where(AgentRun.run_key == window.id))
        assert audit.status == "MISSED"
        assert audit.missed_reason == "agent_model_identity_mismatch"
        assert calls == []
    engine.dispose()
