"""One-time, idempotent initialization of the immutable contest state."""

from __future__ import annotations

import os
from datetime import UTC, datetime
from decimal import Decimal

from sqlalchemy import create_engine, select, text
from sqlalchemy.orm import Session

from .models import Agent, LedgerEvent, SystemState
from .schedule import AGENT_BY_MODEL


class BootstrapError(RuntimeError):
    pass


def bootstrap(session: Session, *, at: datetime) -> bool:
    if at.tzinfo is None or at.utcoffset() is None:
        raise BootstrapError("bootstrap timestamp must be timezone-aware")
    if session.bind and session.bind.dialect.name == "postgresql":
        session.execute(text("SELECT pg_advisory_xact_lock(420043)"))

    agents = session.scalars(select(Agent).order_by(Agent.id)).all()
    events = session.scalars(
        select(LedgerEvent)
        .where(LedgerEvent.event_type == "INITIAL_CASH")
        .order_by(LedgerEvent.agent_id)
    ).all()
    state = session.get(SystemState, 1)
    if agents or events or state is not None:
        if _is_complete(agents, events, state):
            session.commit()
            return False
        session.rollback()
        raise BootstrapError("database contains partial or conflicting bootstrap state")

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
        session.add(
            LedgerEvent(
                agent_id=agent_id,
                event_type="INITIAL_CASH",
                cash_delta=Decimal("30000"),
                quantity_delta=0,
                occurred_at=at,
                reference_id=f"bootstrap:{agent_id}",
                metadata_json={"bootstrap_version": 1},
            )
        )
    session.add(SystemState(id=1, contest_status="DRAFT", paused=False))
    session.commit()
    return True


def _is_complete(agents, events, state) -> bool:
    expected = {agent_id: model_id for model_id, agent_id in AGENT_BY_MODEL.items()}
    return bool(
        len(agents) == 4
        and {row.id: row.model_id for row in agents} == expected
        and all(row.initial_cash == Decimal("30000") for row in agents)
        and len(events) == 4
        and {row.agent_id for row in events} == set(expected)
        and all(
            row.cash_delta == Decimal("30000")
            and row.quantity_delta == 0
            and row.reference_id == f"bootstrap:{row.agent_id}"
            for row in events
        )
        and state is not None
        and state.contest_status == "DRAFT"
        and state.paused is False
        and state.started_at is None
        and state.finished_at is None
    )


def main() -> int:
    database_url = os.environ.get("DATABASE_URL", "").strip()
    if not database_url:
        raise BootstrapError("DATABASE_URL is required")
    engine = create_engine(database_url)
    try:
        with Session(engine) as session:
            bootstrap(session, at=datetime.now(UTC))
    finally:
        engine.dispose()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
