from datetime import UTC, datetime
from decimal import Decimal

import pytest
from sqlalchemy import create_engine, func, select
from sqlalchemy.orm import Session

from ai_stocks.bootstrap import BootstrapError, bootstrap
from ai_stocks.db import Base
from ai_stocks.models import Agent, LedgerEvent, SystemState


def test_bootstrap_is_complete_idempotent_and_refuses_partial_state():
    engine = create_engine("sqlite+pysqlite:///:memory:")
    Base.metadata.create_all(engine)
    at = datetime(2026, 8, 7, 8, tzinfo=UTC)
    with Session(engine) as session:
        assert bootstrap(session, at=at) is True
        assert bootstrap(session, at=at) is False
        assert session.scalar(select(func.count()).select_from(Agent)) == 4
        assert session.scalar(select(func.count()).select_from(LedgerEvent)) == 4
        state = session.get(SystemState, 1)
        assert state.contest_status == "DRAFT"
        assert state.paused is False
        assert all(
            event.cash_delta == Decimal("30000")
            for event in session.scalars(select(LedgerEvent)).all()
        )
        session.delete(session.get(Agent, "a3"))
        session.commit()
        with pytest.raises(BootstrapError, match="partial or conflicting"):
            bootstrap(session, at=at)
    engine.dispose()
