import hashlib
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from decimal import Decimal

import pytest
from sqlalchemy import create_engine
from sqlalchemy.orm import Session

from ai_stocks.db import Base
from ai_stocks.market import FakeMarket, Quote, SessionWindow
from ai_stocks.models import Agent, LedgerEvent, Order
from ai_stocks.trading import Evidence, OrderRequest, TradingError, TradingService

NOW = datetime(2026, 8, 6, 10, tzinfo=UTC)
SESSION = SessionWindow(
    datetime(2026, 8, 6, 7, tzinfo=UTC),
    datetime(2026, 8, 6, 15, 30, tzinfo=UTC),
    "XSTO-2026-08-06",
)
CHECKSUM = hashlib.sha256(b"official").hexdigest()


def evidence(model="gpt-5.6-sol"):
    return Evidence(
        reason="value",
        catalyst="earnings",
        sources=[
            {"url": "https://example.com/a", "published_at": (NOW - timedelta(hours=1)).isoformat()}
        ],
        risks=["market"],
        confidence=Decimal("0.7"),
        model_id=model,
        decision_at=NOW,
        observed_price=Decimal("100"),
        observed_portfolio={"cash": "30000", "holdings": {}},
    )


@pytest.fixture
def session():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    with Session(engine) as s:
        for i, model in enumerate(
            ("gpt-5.6-sol", "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview")
        ):
            s.add(Agent(id=f"a{i}", model_id=model, initial_cash=Decimal("30000")))
            s.add(
                LedgerEvent(
                    agent_id=f"a{i}",
                    event_type="INITIAL_CASH",
                    cash_delta=Decimal("30000"),
                    quantity_delta=0,
                    occurred_at=NOW - timedelta(days=1),
                    metadata_json={},
                )
            )
        s.commit()
        yield s
    engine.dispose()


def market(price="100", quote_at=NOW, retrieval_delay=timedelta(minutes=15)):
    return FakeMarket(
        [
            Quote(
                symbol="VOLV-B",
                instrument_id="SE-VOLV-B",
                price=Decimal(price),
                source_at=quote_at,
                retrieved_at=quote_at + retrieval_delay,
                venue="XSTO",
                currency="SEK",
                volume=1_000_000,
                adv20=Decimal("20000000"),
                history_days=20,
                warning=False,
                suspended=False,
                session_id=SESSION.session_id,
                session_open=SESSION.open_at,
                session_close=SESSION.close_at,
                raw_checksum=CHECKSUM,
                verified=True,
            )
        ],
        sessions=[SESSION],
    )


def test_evidence_gated_idempotent_buy_creates_immutable_ledger(session):
    svc = TradingService(session, market())
    req = OrderRequest(
        decision_id="d1",
        agent_id="a0",
        symbol="VOLV-B",
        side="BUY",
        quantity=10,
        evidence=evidence(),
    )
    first = svc.submit(req, observed_at=NOW + timedelta(minutes=15))
    second = svc.submit(req)
    assert first.id == second.id and first.status == "FILLED"
    assert {
        "bid",
        "ask",
        "adv20",
        "history_days",
        "warning",
        "suspended",
        "slippage_rate",
        "fee_tier",
    } <= first.quote_json.keys()
    assert svc.portfolio("a0").holdings == {"VOLV-B": 10}
    assert svc.portfolio("a0").cash == Decimal("28998.98")
    event = session.query(LedgerEvent).filter_by(event_type="FILL").one()
    event.cash_delta = Decimal("0")
    with pytest.raises(Exception):
        session.commit()


def test_rejects_incomplete_evidence_without_order(session):
    bad = evidence().model_copy(update={"sources": []})
    with pytest.raises(Exception):
        TradingService(session, market()).submit(
            OrderRequest(
                decision_id="bad",
                agent_id="a0",
                symbol="VOLV-B",
                side="BUY",
                quantity=1,
                evidence=bad,
            )
        )
    assert session.query(Order).filter_by(decision_id="bad").count() == 0


def test_quote_before_decision_never_fills(session):
    result = TradingService(session, market(quote_at=NOW - timedelta(seconds=1))).submit(
        OrderRequest(
            decision_id="d2",
            agent_id="a0",
            symbol="VOLV-B",
            side="BUY",
            quantity=1,
            evidence=evidence(),
        )
    )
    assert result.status == "QUEUED"


def test_fees_slippage_cash_holdings_concentration_and_liquidity(session):
    svc = TradingService(session, market())
    with pytest.raises(TradingError, match="concentration"):
        svc.submit(
            OrderRequest(
                decision_id="cap",
                agent_id="a0",
                symbol="VOLV-B",
                side="BUY",
                quantity=80,
                evidence=evidence(),
            ),
            observed_at=NOW + timedelta(minutes=15),
        )
    with pytest.raises(TradingError, match="liquidity"):
        svc.submit(
            OrderRequest(
                decision_id="liq",
                agent_id="a0",
                symbol="VOLV-B",
                side="BUY",
                quantity=2001,
                evidence=evidence(),
            ),
            observed_at=NOW + timedelta(minutes=15),
        )
    with pytest.raises(TradingError, match="holdings"):
        svc.submit(
            OrderRequest(
                decision_id="sell",
                agent_id="a0",
                symbol="VOLV-B",
                side="SELL",
                quantity=1,
                evidence=evidence(),
            ),
            observed_at=NOW + timedelta(minutes=15),
        )


def test_out_of_hours_order_queues_then_fills_at_next_open_quote(session):
    closed = FakeMarket([], is_open=False)
    svc = TradingService(session, closed)
    req = OrderRequest(
        decision_id="queued",
        agent_id="a0",
        symbol="VOLV-B",
        side="BUY",
        quantity=1,
        evidence=evidence(),
    )
    assert svc.submit(req).status == "QUEUED"
    closed.add(
        Quote(
            symbol="VOLV-B",
            instrument_id="SE-VOLV-B",
            price=Decimal("100"),
            source_at=NOW + timedelta(hours=20),
            retrieved_at=NOW + timedelta(hours=20, minutes=15),
            venue="XSTO",
            currency="SEK",
            volume=1000,
            adv20=Decimal("20000000"),
            history_days=20,
            warning=False,
            suspended=False,
            session_id="XSTO-2026-08-07",
            session_open=NOW + timedelta(hours=20),
            session_close=NOW + timedelta(hours=28, minutes=30),
            raw_checksum=CHECKSUM,
            verified=True,
        )
    )
    closed.is_open = True
    assert svc.execute_queued(NOW + timedelta(hours=20, minutes=15))[0].status == "FILLED"


def test_submit_cannot_consume_quote_before_it_is_observable(session):
    result = TradingService(session, market()).submit(
        OrderRequest(
            decision_id="not-yet-observed",
            agent_id="a0",
            symbol="VOLV-B",
            side="BUY",
            quantity=1,
            evidence=evidence(),
        )
    )
    assert result.status == "QUEUED"


def test_post_cost_concentration_and_terminal_queue_replay(session):
    svc = TradingService(session, market())
    with pytest.raises(TradingError, match="concentration"):
        svc.submit(
            OrderRequest(
                decision_id="post-cost-cap",
                agent_id="a0",
                symbol="VOLV-B",
                side="BUY",
                quantity=75,
                evidence=evidence(),
            ),
            observed_at=NOW + timedelta(minutes=15),
        )

    warning_market = market()
    warning_market.quotes[0] = replace(warning_market.quotes[0], warning=True)
    warning_service = TradingService(session, warning_market)
    request = OrderRequest(
        decision_id="queued-warning",
        agent_id="a0",
        symbol="VOLV-B",
        side="BUY",
        quantity=1,
        evidence=evidence(),
    )
    assert warning_service.submit(request).status == "QUEUED"
    assert warning_service.execute_queued(NOW + timedelta(minutes=15)) == []
    with pytest.raises(TradingError, match="instrument warning"):
        warning_service.submit(request)


@pytest.mark.parametrize(
    "delay",
    [timedelta(minutes=14, seconds=59), timedelta(minutes=20, microseconds=1)],
)
def test_official_quote_delay_outside_window_rejects(session, delay):
    with pytest.raises(TradingError, match="retrieval delay"):
        TradingService(session, market(retrieval_delay=delay)).submit(
            OrderRequest(
                decision_id=f"delay-{delay.total_seconds()}",
                agent_id="a0",
                symbol="VOLV-B",
                side="BUY",
                quantity=1,
                evidence=evidence(),
            ),
            observed_at=NOW + delay,
        )


def test_queue_no_work_return_releases_transaction(session):
    provider = FakeMarket([], sessions=[SESSION])
    service = TradingService(session, provider)
    request = OrderRequest(
        decision_id="no-work",
        agent_id="a0",
        symbol="VOLV-B",
        side="BUY",
        quantity=1,
        evidence=evidence(),
    )
    assert service.submit(request).status == "QUEUED"
    assert service.execute_queued(NOW + timedelta(minutes=15)) == []
    assert not session.in_transaction()
