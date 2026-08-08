from datetime import UTC, datetime, timedelta
from decimal import Decimal

from sqlalchemy import create_engine
from sqlalchemy.orm import Session

from ai_stocks.calendar import session_for
from ai_stocks.db import Base
from ai_stocks.market import FakeMarket, Quote
from ai_stocks.models import Agent, LedgerEvent, Order, SystemState
from ai_stocks.runner import Decision, EvidenceSource
from ai_stocks.worker import WorkerRuntime

NOW = datetime(2026, 8, 7, 8, tzinfo=UTC)


def _runtime():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    session = Session(engine)
    session.add_all(
        [
            Agent(id="a0", model_id="gpt-5.6-sol", initial_cash=Decimal("30000")),
            Agent(id="a1", model_id="claude-opus-4.8", initial_cash=Decimal("30000")),
            LedgerEvent(
                agent_id="a0",
                event_type="INITIAL_CASH",
                cash_delta=Decimal("30000"),
                quantity_delta=0,
                occurred_at=NOW,
                reference_id="initial:a0",
                metadata_json={},
            ),
            LedgerEvent(
                agent_id="a1",
                event_type="INITIAL_CASH",
                cash_delta=Decimal("30000"),
                quantity_delta=0,
                occurred_at=NOW,
                reference_id="initial:a1",
                metadata_json={},
            ),
        ]
    )
    session.commit()
    runtime = WorkerRuntime(
        session,
        runner=object(),
        market=FakeMarket([]),
        clock=lambda: NOW,
    )
    return runtime, session


def test_context_contains_only_requested_agent_history_and_state():
    runtime, _ = _runtime()
    context = runtime.context_for("a0")
    assert context.agent_id == "a0"
    assert context.portfolio == {"cash": "30000.00", "holdings": {}}
    assert context.history == ()
    assert context.private_notes == ""


def test_paused_worker_does_not_create_schedule_or_call_runner():
    runtime, session = _runtime()
    session.add(SystemState(id=1, paused=True, contest_status="PAUSED", reason="test"))
    session.commit()
    assert runtime.tick(NOW) is None


def test_verified_research_provenance_survives_order_persistence():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    session = Session(engine)
    session_window = session_for(NOW.date())
    assert session_window is not None
    observed_at = NOW + timedelta(minutes=15)
    quote = Quote(
        symbol="ERIC-B",
        instrument_id="SE0000108656:ERIC-B-XSTO",
        price=Decimal("100"),
        source_at=NOW,
        retrieved_at=observed_at,
        venue="XSTO",
        currency="SEK",
        volume=1000,
        adv20=Decimal("1000000"),
        history_days=20,
        warning=False,
        suspended=False,
        session_id=f"XSTO-{NOW.date().isoformat()}",
        session_open=session_window.open_at,
        session_close=session_window.close_at,
        raw_checksum="b" * 64,
        verified=True,
    )
    verification = {
        "verified": True,
        "verification_mode": "independent_fetch",
        "url": "https://example.com/news",
        "final_url": "https://example.com/final",
        "published_at": NOW.isoformat(),
        "observed_at": observed_at.isoformat(),
        "claim": "CEO bought shares",
        "matched_excerpt": "CEO bought shares",
        "pinned_ip": "8.8.8.8",
        "content_sha256": "c" * 64,
    }

    class Verifier:
        def verify(self, sources, *, observed_at):
            return (verification,)

    session.add_all(
        [
            Agent(id="a0", model_id="gpt-5.6-sol", initial_cash=Decimal("30000")),
            LedgerEvent(
                agent_id="a0",
                event_type="INITIAL_CASH",
                cash_delta=Decimal("30000"),
                quantity_delta=0,
                occurred_at=NOW,
                reference_id="initial:a0",
                metadata_json={},
            ),
            SystemState(id=1, paused=False, contest_status="RUNNING", started_at=NOW),
        ]
    )
    session.commit()
    runtime = WorkerRuntime(
        session,
        runner=object(),
        market=FakeMarket([quote]),
        clock=lambda: observed_at,
        research_verifier=Verifier(),
    )
    source = EvidenceSource(url=verification["url"], published_at=NOW, claim=verification["claim"])
    decision = Decision(
        schema_version="1.0",
        model_id="gpt-5.6-sol",
        decision_at=NOW,
        action="buy",
        symbol="ERIC-B",
        quantity=1,
        pending_order_id=None,
        reason="Verified insider purchase",
        catalyst="CEO bought shares",
        evidence=(source,),
        risks=("Market risk",),
        confidence=0.7,
        strategy_update=None,
    )

    runtime.handle_decision("a0", "run:a0:1", decision, runtime.context_for("a0"))
    session.commit()
    session.expire_all()

    order = session.query(Order).filter_by(decision_id="run:a0:1").one()
    assert order.evidence_json["research_verification"] == [verification]
