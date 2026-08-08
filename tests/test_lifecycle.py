import hashlib
from datetime import UTC, datetime, timedelta
from decimal import Decimal

import pytest
from sqlalchemy import create_engine, select
from sqlalchemy.orm import Session

from ai_stocks.db import Base
from ai_stocks.market import FakeMarket, Quote, SessionWindow
from ai_stocks.models import (
    Agent,
    CorporateAction,
    FinalRanking,
    LedgerEvent,
    OrderLifecycleEvent,
)
from ai_stocks.trading import Evidence, OrderRequest, TradingError, TradingService

NOW = datetime(2026, 12, 30, 10, tzinfo=UTC)
SESSION = SessionWindow(
    datetime(2026, 12, 30, 8, tzinfo=UTC),
    datetime(2026, 12, 30, 16, 30, tzinfo=UTC),
    "XSTO-2026-12-30",
)
CHECKSUM = hashlib.sha256(b"official-close").hexdigest()


def quote(symbol="VOLV-B", price="100", *, at=NOW, bid=None, ask=None):
    return Quote(
        symbol=symbol,
        instrument_id=f"SE-{symbol}",
        price=Decimal(price),
        source_at=at,
        retrieved_at=at + timedelta(minutes=15),
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
        bid=Decimal(bid) if bid is not None else None,
        ask=Decimal(ask) if ask is not None else None,
    )


def evidence(decision_at=NOW):
    return Evidence(
        reason="value",
        catalyst="earnings",
        sources=[
            {
                "url": "https://example.com/research",
                "published_at": (decision_at - timedelta(hours=1)).isoformat(),
            }
        ],
        risks=["market"],
        confidence=Decimal("0.7"),
        model_id="gpt-5.6-sol",
        decision_at=decision_at,
        observed_price=Decimal("100"),
        observed_portfolio={"cash": "30000", "holdings": {}},
    )


@pytest.fixture
def session():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    with Session(engine) as session:
        for agent_id, model_id in (("a", "gpt-5.6-sol"), ("b", "claude-opus-4.8")):
            session.add(Agent(id=agent_id, model_id=model_id, initial_cash=Decimal("30000")))
            session.add(
                LedgerEvent(
                    agent_id=agent_id,
                    event_type="INITIAL_CASH",
                    cash_delta=Decimal("30000"),
                    quantity_delta=0,
                    occurred_at=NOW - timedelta(days=1),
                    metadata_json={},
                )
            )
        session.commit()
        yield session
    engine.dispose()


def request(decision_id, quantity=1, decision_at=NOW):
    return OrderRequest(
        decision_id=decision_id,
        agent_id="a",
        symbol="VOLV-B",
        side="BUY",
        quantity=quantity,
        evidence=evidence(decision_at),
    )


def test_cancel_is_immutable_idempotent_and_prevents_later_execution(session):
    provider = FakeMarket([], sessions=[SESSION])
    service = TradingService(session, provider)
    queued = service.submit(request("cancel-me"))

    first = service.cancel_order("a", queued.id, "thesis changed", NOW, "cancel-request-1")
    second = service.cancel_order("a", queued.id, "thesis changed", NOW, "cancel-request-1")
    assert first == second
    assert first.status == "CANCELLED"
    assert session.scalars(select(OrderLifecycleEvent)).all()[0].reason == "thesis changed"

    provider.add(quote(at=NOW + timedelta(minutes=1)))
    assert service.execute_queued(NOW + timedelta(minutes=16)) == []
    with pytest.raises(TradingError, match="conflict"):
        service.cancel_order("a", queued.id, "different reason", NOW, "cancel-request-1")
    event = session.scalars(select(OrderLifecycleEvent)).one()
    event.reason = "mutated"
    with pytest.raises(Exception, match="append-only"):
        session.commit()


def test_replace_cancels_original_and_is_idempotent_before_quote(session):
    service = TradingService(session, FakeMarket([], sessions=[SESSION]))
    original = service.submit(request("original", 1))
    replacement_request = request("replacement", 2)

    first = service.replace_order(
        "a", original.id, replacement_request, "increase conviction", NOW, "replace-request-1"
    )
    second = service.replace_order(
        "a", original.id, replacement_request, "increase conviction", NOW, "replace-request-1"
    )
    assert first.id == second.id
    assert first.status == "QUEUED"
    assert service.order_result(original.id).status == "CANCELLED"
    action = session.scalars(select(OrderLifecycleEvent)).one()
    assert action.replacement_order_id == first.id

    provider = FakeMarket([quote()], sessions=[SESSION])
    filled = TradingService(session, provider).submit(
        request("already-filled"), observed_at=NOW + timedelta(minutes=15)
    )
    with pytest.raises(TradingError, match="no longer queued"):
        TradingService(session, provider).replace_order(
            "a", filled.id, request("too-late"), "late", NOW, "replace-request-2"
        )


def test_all_corporate_actions_replay_deterministically_and_conflict_per_agent(session):
    session.add_all(
        [
            LedgerEvent(
                agent_id=agent,
                event_type="FILL",
                symbol="OLD",
                cash_delta=Decimal("-1000"),
                quantity_delta=10,
                occurred_at=NOW,
                metadata_json={},
            )
            for agent in ("a", "b")
        ]
    )
    session.commit()
    service = TradingService(session, None)

    for agent in ("a", "b"):
        service.apply_dividend(agent, "OLD", Decimal("2.50"), NOW, "div-1")
        service.apply_split(agent, "OLD", 2, NOW, "split-1")
        service.apply_stock_merger(agent, "OLD", "NEW", 1, 4, NOW, "stock-merger-1")
        service.apply_cash_merger(agent, "NEW", Decimal("50"), NOW, "cash-merger-1")
        service.apply_cash_merger(agent, "NEW", Decimal("50"), NOW, "cash-merger-1")
        service.apply_delisting(agent, "FROZEN", None, NOW, "delist-1")

    assert service.portfolio("a").cash == Decimal("29275.00")
    assert service.portfolio("a").holdings == {}
    assert session.query(CorporateAction).count() == 10
    with pytest.raises(TradingError, match="reference conflict"):
        service.apply_dividend("a", "OLD", Decimal("3"), NOW, "div-1")


def test_final_liquidation_uses_adverse_slippage_fees_and_freezes_ranking(session):
    session.add_all(
        [
            LedgerEvent(
                agent_id="a",
                event_type="FILL",
                symbol="VOLV-B",
                cash_delta=Decimal("-1000"),
                quantity_delta=10,
                occurred_at=NOW,
                metadata_json={},
            ),
            LedgerEvent(
                agent_id="b",
                event_type="FILL",
                symbol="VOLV-B",
                cash_delta=Decimal("-500"),
                quantity_delta=5,
                occurred_at=NOW,
                metadata_json={},
            ),
        ]
    )
    session.commit()
    close = quote(price="110", at=SESSION.close_at, bid="109", ask="111")
    service = TradingService(session, FakeMarket([close], sessions=[SESSION]))

    first = service.final_liquidation(
        {"VOLV-B": close}, SESSION.close_at + timedelta(minutes=15), "final-2026"
    )
    second = service.final_liquidation(
        {"VOLV-B": close}, SESSION.close_at + timedelta(minutes=15), "final-2026"
    )
    assert [(row.agent_id, row.rank, row.net_liquidation_value) for row in first] == [
        (row.agent_id, row.rank, row.net_liquidation_value) for row in second
    ]
    assert first[0].agent_id == "a"
    assert first[0].net_liquidation_value == Decimal("30089.98")
    assert first[1].net_liquidation_value == Decimal("30044.99")
    assert all(service.portfolio(agent).holdings == {} for agent in ("a", "b"))
    ranking = session.scalars(select(FinalRanking).where(FinalRanking.rank == 1)).one()
    ranking.rank = 2
    with pytest.raises(Exception, match="append-only"):
        session.commit()
