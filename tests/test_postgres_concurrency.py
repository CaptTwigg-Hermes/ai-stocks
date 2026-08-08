import hashlib
import os
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from threading import Barrier, Event
from time import sleep

import pytest
from sqlalchemy import create_engine, func, select, text
from sqlalchemy.orm import Session

from ai_stocks.auth import AccessIdentity
from ai_stocks.market import FakeMarket, Quote, SessionWindow
from ai_stocks.models import (
    Agent,
    AgentRun,
    Fill,
    LedgerEvent,
    Order,
    ScheduledAgentRun,
    SystemState,
)
from ai_stocks.operations import ContestOperations
from ai_stocks.orchestration import AgentOrchestrator
from ai_stocks.runner import HermesRunner, ProcessCapture
from ai_stocks.schedule import AGENT_BY_MODEL
from ai_stocks.trading import Evidence, OrderRequest, TradingError, TradingService
from alembic import command
from tests.test_orchestration import context as agent_context
from tests.test_orchestration import response as agent_response
from tests.test_trading_migration import alembic_config

pytestmark = pytest.mark.skipif(
    not os.getenv("TEST_POSTGRES_URL"), reason="TEST_POSTGRES_URL required"
)

NOW = datetime(2026, 8, 6, 10, tzinfo=UTC)
WINDOW = SessionWindow(
    open_at=datetime(2026, 8, 6, 7, tzinfo=UTC),
    close_at=datetime(2026, 8, 6, 15, 30, tzinfo=UTC),
    session_id="XSTO-2026-08-06",
)
CHECKSUM = hashlib.sha256(b"official").hexdigest()
MODELS = tuple(AGENT_BY_MODEL)
AGENTS = tuple(AGENT_BY_MODEL.values())


def _quote(symbol):
    return Quote(
        symbol=symbol,
        instrument_id=f"SE-{symbol}",
        price=Decimal("100"),
        source_at=NOW,
        retrieved_at=NOW + timedelta(minutes=15),
        venue="XSTO",
        currency="SEK",
        volume=1_000_000,
        adv20=Decimal("1000000"),
        history_days=20,
        warning=False,
        suspended=False,
        session_id=WINDOW.session_id,
        session_open=WINDOW.open_at,
        session_close=WINDOW.close_at,
        raw_checksum=CHECKSUM,
        verified=True,
        raw_evidence_id=f"official:{symbol}",
    )


def _request(decision, agent, model, symbol, side="BUY", quantity=1):
    return OrderRequest(
        decision_id=decision,
        agent_id=agent,
        symbol=symbol,
        side=side,
        quantity=quantity,
        evidence=Evidence(
            reason="race regression",
            catalyst="verified event",
            sources=[
                {
                    "url": "https://example.com/source",
                    "published_at": (NOW - timedelta(hours=1)).isoformat(),
                }
            ],
            risks=["race"],
            confidence=Decimal("0.7"),
            model_id=model,
            decision_at=NOW,
            observed_price=Decimal("100"),
            observed_portfolio={"cash": "30000", "holdings": {}},
        ),
    )


def _race(engine, requests, market):
    barrier = Barrier(len(requests))

    def submit(request):
        with Session(engine) as session:
            barrier.wait(timeout=10)
            try:
                result = TradingService(session, market).submit(
                    request, observed_at=NOW + timedelta(minutes=15)
                )
                return "ok", result.id, result.status
            except TradingError as exc:
                return "error", str(exc), None

    with ThreadPoolExecutor(max_workers=len(requests)) as pool:
        return list(pool.map(submit, requests))


@pytest.fixture
def engine():
    url = os.environ["TEST_POSTGRES_URL"]
    engine = create_engine(url, pool_size=12, max_overflow=4)
    assert engine.connect().scalar(select(func.current_database())) == "ai_stocks_test"
    config = alembic_config(url)
    command.downgrade(config, "base")
    command.upgrade(config, "head")
    with Session(engine) as session:
        session.add(SystemState(id=1, paused=False, contest_status="RUNNING", started_at=NOW))
        for model, agent in AGENT_BY_MODEL.items():
            session.add(Agent(id=agent, model_id=model, initial_cash=Decimal("30000")))
        session.flush()
        for agent, cash in zip(AGENTS, (1000, 0, 30000, 30000), strict=True):
            session.add(
                LedgerEvent(
                    agent_id=agent,
                    event_type="INITIAL_CASH",
                    cash_delta=Decimal(cash),
                    quantity_delta=0,
                    occurred_at=NOW - timedelta(days=1),
                    reference_id=f"initial:{agent}",
                    metadata_json={},
                )
            )
        session.add(
            LedgerEvent(
                agent_id=AGENTS[1],
                event_type="FILL",
                symbol="SELLME",
                cash_delta=Decimal(0),
                quantity_delta=1,
                occurred_at=NOW - timedelta(hours=1),
                reference_id="holding:a1",
                metadata_json={},
            )
        )
        session.commit()
    yield engine
    engine.dispose()


def test_concurrent_orders_cannot_overspend_or_oversell(engine):
    symbols = [f"CASH-{index}" for index in range(6)]
    cash_results = _race(
        engine,
        [
            _request(f"cash-{index}", AGENTS[0], MODELS[0], symbol, quantity=2)
            for index, symbol in enumerate(symbols)
        ],
        FakeMarket([_quote(symbol) for symbol in symbols], sessions=[WINDOW]),
    )
    assert sum(result[0] == "ok" for result in cash_results) == 4
    with Session(engine) as session:
        portfolio = TradingService(session, None).portfolio(AGENTS[0])
        assert portfolio.cash >= 0
        assert len(portfolio.holdings) == 4

    sell_results = _race(
        engine,
        [_request(f"sell-{index}", AGENTS[1], MODELS[1], "SELLME", "SELL") for index in range(2)],
        FakeMarket([_quote("SELLME")], sessions=[WINDOW]),
    )
    assert sum(result[0] == "ok" for result in sell_results) == 1
    with Session(engine) as session:
        assert TradingService(session, None).portfolio(AGENTS[1]).holdings == {}


def test_concurrent_idempotent_submission_creates_one_order(engine):
    request = _request("same-decision", AGENTS[2], MODELS[2], "IDEMP")
    results = _race(
        engine,
        [request.model_copy(deep=True), request.model_copy(deep=True)],
        FakeMarket([_quote("IDEMP")], sessions=[WINDOW]),
    )
    assert all(result[0] == "ok" for result in results)
    assert len({result[1] for result in results}) == 1
    with Session(engine) as session:
        assert (
            session.scalar(
                select(func.count()).select_from(Order).where(Order.decision_id == "same-decision")
            )
            == 1
        )


def test_postgres_rolls_back_trade_side_effect_when_run_audit_records_failure(engine):
    run_key = "postgres-atomic-run"
    with Session(engine) as session:
        session.add(
            ScheduledAgentRun(
                run_key=run_key,
                agent_id=AGENTS[0],
                model_id=MODELS[0],
                scheduled_at=NOW,
                deadline_at=NOW + timedelta(minutes=10),
                status="PENDING",
                attempt_count=0,
                next_attempt_at=NOW,
            )
        )
        session.commit()

        def failing_handler(agent_id, *_):
            session.add(
                LedgerEvent(
                    agent_id=agent_id,
                    event_type="INITIAL_CASH",
                    cash_delta=Decimal("1"),
                    quantity_delta=0,
                    occurred_at=NOW,
                    reference_id="postgres-must-rollback",
                    metadata_json={},
                )
            )
            session.flush()
            raise RuntimeError("forced post-trade failure")

        orchestrator = AgentOrchestrator(
            session,
            runner=HermesRunner(
                executor=lambda *_: ProcessCapture(0, agent_response(MODELS[0], NOW), "")
            ),
            context_provider=lambda agent_id: agent_context(agent_id),
            decision_handler=failing_handler,
        )
        assert orchestrator.run_next(NOW) == run_key
        assert (
            session.scalar(
                select(LedgerEvent).where(LedgerEvent.reference_id == "postgres-must-rollback")
            )
            is None
        )
        audit = session.scalar(select(AgentRun).where(AgentRun.run_key == run_key))
        assert audit.status == "ERROR"
        assert "forced post-trade failure" in audit.validation_error


def test_pause_serializes_before_concurrent_submission(engine):
    pause_has_lock = Event()
    market = FakeMarket([_quote("PAUSE-RACE")], sessions=[WINDOW])

    def pause():
        with Session(engine) as session:
            session.execute(text("SELECT pg_advisory_xact_lock(420042)"))
            pause_has_lock.set()
            sleep(0.2)
            ContestOperations(session).pause(
                AccessIdentity("owner@example.com", "owner"),
                reason="race pause",
                at=NOW + timedelta(minutes=15),
                idempotency_key="pause-race",
            )

    def submit():
        assert pause_has_lock.wait(timeout=5)
        with Session(engine) as session:
            with pytest.raises(TradingError, match="system paused"):
                TradingService(session, market).submit(
                    _request("pause-race-order", AGENTS[2], MODELS[2], "PAUSE-RACE"),
                    observed_at=NOW + timedelta(minutes=15),
                )

    with ThreadPoolExecutor(max_workers=2) as pool:
        futures = (pool.submit(pause), pool.submit(submit))
        for future in futures:
            future.result(timeout=10)

    with Session(engine) as session:
        state = session.get(SystemState, 1)
        assert state is not None and state.paused is True
        assert (
            session.scalar(
                select(func.count())
                .select_from(Order)
                .where(Order.decision_id == "pause-race-order")
            )
            == 0
        )


def test_concurrent_queue_workers_create_each_fill_once(engine):
    with Session(engine) as session:
        service = TradingService(session, FakeMarket([], sessions=[WINDOW]))
        queued = [
            service.submit(_request(f"queued-{index}", AGENTS[3], MODELS[3], f"QUEUE-{index}"))
            for index in range(2)
        ]
        assert all(order.status == "QUEUED" for order in queued)

    market = FakeMarket([_quote("QUEUE-0"), _quote("QUEUE-1")], sessions=[WINDOW])
    barrier = Barrier(2)

    def execute(_):
        with Session(engine) as session:
            barrier.wait(timeout=10)
            return len(TradingService(session, market).execute_queued(NOW + timedelta(minutes=15)))

    with ThreadPoolExecutor(max_workers=2) as pool:
        counts = list(pool.map(execute, range(2)))
    assert sum(counts) == 2
    with Session(engine) as session:
        assert (
            session.scalar(
                select(func.count())
                .select_from(Fill)
                .join(Order, Fill.order_id == Order.id)
                .where(Order.decision_id.in_(("queued-0", "queued-1")))
            )
            == 2
        )
