from datetime import UTC, datetime
from decimal import Decimal

from sqlalchemy import create_engine
from sqlalchemy.orm import Session

from ai_stocks.db import Base
from ai_stocks.models import Agent, LedgerEvent
from ai_stocks.trading import TradingService

NOW = datetime(2026, 8, 6, tzinfo=UTC)


def test_dividend_and_split_are_per_agent_idempotent_reconciling_events():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    with Session(engine) as session:
        for agent_id, model_id in (
            ("a", "gpt-5.6-sol"),
            ("b", "claude-opus-4.8"),
        ):
            session.add(
                Agent(
                    id=agent_id,
                    model_id=model_id,
                    initial_cash=Decimal("30000"),
                )
            )
            session.add_all(
                [
                    LedgerEvent(
                        agent_id=agent_id,
                        event_type="INITIAL_CASH",
                        cash_delta=Decimal("30000"),
                        quantity_delta=0,
                        occurred_at=NOW,
                        metadata_json={},
                    ),
                    LedgerEvent(
                        agent_id=agent_id,
                        event_type="FILL",
                        symbol="VOLV-B",
                        cash_delta=Decimal("-1000"),
                        quantity_delta=10,
                        occurred_at=NOW,
                        metadata_json={},
                    ),
                ]
            )
        session.commit()

        service = TradingService(session, None)
        for agent_id in ("a", "b"):
            service.apply_dividend(agent_id, "VOLV-B", Decimal("5"), NOW, "corp-1")
            service.apply_split(agent_id, "VOLV-B", 2, NOW, "corp-2")
            service.apply_dividend(agent_id, "VOLV-B", Decimal("5"), NOW, "corp-1")
            service.apply_split(agent_id, "VOLV-B", 2, NOW, "corp-2")

        for agent_id in ("a", "b"):
            portfolio = service.portfolio(agent_id)
            assert portfolio.cash == Decimal("29050.00")
            assert portfolio.holdings["VOLV-B"] == 20
    engine.dispose()
