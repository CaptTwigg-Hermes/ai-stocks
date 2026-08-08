import hmac
import os
from collections.abc import Callable, Mapping
from datetime import UTC, datetime
from decimal import Decimal
from html import escape
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, HTTPException, Path
from fastapi.responses import HTMLResponse
from sqlalchemy import create_engine, select, text
from sqlalchemy.orm import Session, sessionmaker

from .auth import AccessConfig, AccessIdentity, AccessJWTValidator, AuthenticationError
from .db import Base
from .models import Agent, LedgerEvent, SystemState
from .operations import ContestOperationError, ContestOperations
from .preflight import validate_production_environment
from .schedule import AGENT_BY_MODEL, FIXED_MODELS
from .trading import TradingService

MODELS = FIXED_MODELS


def create_app(
    database_url=None,
    *,
    seed=False,
    access_config: AccessConfig | None = None,
    jwks_fetcher: Callable[[str], Mapping[str, Any]] | None = None,
    auth_clock: Callable[[], datetime] | None = None,
):
    production = os.getenv("APP_ENV", "").strip().lower() == "production"
    if production:
        production_config, production_database_url = validate_production_environment()
        if access_config is not None and access_config != production_config:
            raise RuntimeError("injected Access configuration differs from production environment")
        if database_url is not None and database_url != production_database_url:
            raise RuntimeError("injected DATABASE_URL differs from production environment")
        config = production_config
        database_url = production_database_url
    else:
        config = access_config or AccessConfig.from_env()
        database_url = database_url or os.getenv("DATABASE_URL", "sqlite:///./ai_stocks.db")
    validator = AccessJWTValidator(config, fetcher=jwks_fetcher, clock=auth_clock)
    engine = create_engine(database_url)
    if not production:
        Base.metadata.create_all(engine)
    factory = sessionmaker(engine, expire_on_commit=False)
    if seed:
        with factory() as session:
            if not session.get(SystemState, 1):
                session.add(SystemState(id=1, paused=False))
            now = datetime(2026, 1, 1, tzinfo=UTC)
            for model in MODELS:
                agent_id = AGENT_BY_MODEL[model]
                if not session.get(Agent, agent_id):
                    session.add(Agent(id=agent_id, model_id=model, initial_cash=Decimal("30000")))
                    session.add(
                        LedgerEvent(
                            agent_id=agent_id,
                            event_type="INITIAL_CASH",
                            cash_delta=Decimal("30000"),
                            quantity_delta=0,
                            occurred_at=now,
                            reference_id=f"initial:{agent_id}",
                            metadata_json={},
                        )
                    )
            session.commit()

    app = FastAPI(title="AI Stocks Paper Contest")
    app.state.engine = engine

    def db():
        with factory() as session:
            yield session

    def identity(
        assertion: Annotated[str | None, Header(alias="Cf-Access-Jwt-Assertion")] = None,
    ) -> AccessIdentity:
        if assertion is None:
            raise HTTPException(401, "authentication required")
        try:
            return validator.validate(assertion)
        except AuthenticationError as exc:
            raise HTTPException(401, "invalid Access assertion") from exc

    def owner(user: AccessIdentity = Depends(identity)) -> AccessIdentity:
        if user.role != "owner":
            raise HTTPException(403, "owner required")
        return user

    def protected_mutation(
        user: AccessIdentity = Depends(owner),
        origin: Annotated[str | None, Header(alias="Origin")] = None,
        csrf: Annotated[str | None, Header(alias="X-AI-Stocks-CSRF")] = None,
    ) -> AccessIdentity:
        if origin is None or not hmac.compare_digest(origin, config.public_origin):
            raise HTTPException(403, "invalid origin")
        if csrf is None or not hmac.compare_digest(csrf, "1"):
            raise HTTPException(403, "CSRF header required")
        return user

    @app.get("/healthz")
    def health(session: Session = Depends(db)):
        session.execute(text("SELECT 1"))
        return {"ok": True}

    @app.get("/api/portfolio/{agent_id}", dependencies=[Depends(identity)])
    def portfolio(
        agent_id: Annotated[str, Path(min_length=1, max_length=40, pattern=r"^[A-Za-z0-9_-]+$")],
        session: Session = Depends(db),
    ):
        if session.get(Agent, agent_id) is None:
            raise HTTPException(404, "agent not found")
        result = TradingService(session, None).portfolio(agent_id)
        return {"agent_id": agent_id, "cash": str(result.cash), "holdings": result.holdings}

    @app.get("/", response_class=HTMLResponse, dependencies=[Depends(identity)])
    def dashboard(session: Session = Depends(db)):
        rows = "".join(
            "<tr><td>"
            + escape(str(agent.model_id), quote=True)
            + "</td><td>"
            + escape(str(TradingService(session, None).portfolio(agent.id).cash), quote=True)
            + " SEK</td></tr>"
            for agent in session.scalars(select(Agent).order_by(Agent.id))
        )
        return (
            "<!doctype html><meta name=viewport content='width=device-width,initial-scale=1'>"
            "<style>body{font:16px system-ui;max-width:50rem;margin:auto;padding:1rem;"
            "background:#10151c;color:#eef}table{width:100%;border-collapse:collapse}"
            "td,th{padding:.7rem;border-bottom:1px solid #345}</style>"
            "<h1>AI Stocks</h1><table><tr><th>Model</th><th>Cash</th></tr>" + rows + "</table>"
        )

    def contest_response(state: SystemState) -> dict[str, object]:
        return {"contest_status": state.contest_status, "paused": state.paused}

    @app.post("/admin/start")
    def start_contest(
        user: AccessIdentity = Depends(protected_mutation),
        idempotency_key: Annotated[str | None, Header(alias="Idempotency-Key")] = None,
        session: Session = Depends(db),
    ):
        if not idempotency_key:
            raise HTTPException(400, "Idempotency-Key required")
        try:
            state = ContestOperations(session).start(
                user, at=datetime.now(UTC), idempotency_key=idempotency_key
            )
        except ContestOperationError as exc:
            raise HTTPException(409, str(exc)) from exc
        return contest_response(state)

    @app.post("/admin/pause")
    def pause_contest(
        user: AccessIdentity = Depends(protected_mutation),
        idempotency_key: Annotated[str | None, Header(alias="Idempotency-Key")] = None,
        session: Session = Depends(db),
    ):
        if not idempotency_key:
            raise HTTPException(400, "Idempotency-Key required")
        try:
            state = ContestOperations(session).pause(
                user,
                reason="owner technical/security pause",
                at=datetime.now(UTC),
                idempotency_key=idempotency_key,
            )
        except ContestOperationError as exc:
            raise HTTPException(409, str(exc)) from exc
        return contest_response(state)

    @app.post("/admin/resume")
    def resume_contest(
        user: AccessIdentity = Depends(protected_mutation),
        idempotency_key: Annotated[str | None, Header(alias="Idempotency-Key")] = None,
        session: Session = Depends(db),
    ):
        if not idempotency_key:
            raise HTTPException(400, "Idempotency-Key required")
        try:
            state = ContestOperations(session).resume(
                user, at=datetime.now(UTC), idempotency_key=idempotency_key
            )
        except ContestOperationError as exc:
            raise HTTPException(409, str(exc)) from exc
        return contest_response(state)

    @app.post("/admin/reset")
    def reset_contest(
        user: AccessIdentity = Depends(protected_mutation),
        idempotency_key: Annotated[str | None, Header(alias="Idempotency-Key")] = None,
        session: Session = Depends(db),
    ):
        if not idempotency_key:
            raise HTTPException(400, "Idempotency-Key required")
        try:
            state = ContestOperations(session).prestart_reset(
                user,
                reason="owner pre-start reset",
                at=datetime.now(UTC),
                idempotency_key=idempotency_key,
            )
        except ContestOperationError as exc:
            raise HTTPException(409, str(exc)) from exc
        return contest_response(state)

    return app
