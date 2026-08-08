import json
from datetime import UTC, datetime, timedelta

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from fastapi.testclient import TestClient
from sqlalchemy import event
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from ai_stocks.app import create_app
from ai_stocks.auth import AccessConfig
from ai_stocks.dayrun import run_deterministic_day
from ai_stocks.models import Agent, SystemState
from ai_stocks.preflight import validate_production_environment

NOW = datetime(2026, 8, 6, 8, tzinfo=UTC)
ISSUER = "https://contest.cloudflareaccess.com"
ORIGIN = "https://stocks.example.com"


def _client(tmp_path):
    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    jwk = json.loads(jwt.algorithms.RSAAlgorithm.to_jwk(private.public_key()))
    jwk.update(kid="app-key", alg="RS256", use="sig")
    config = AccessConfig(
        ISSUER,
        "exact-aud",
        ORIGIN,
        frozenset({"owner@example.com"}),
        frozenset({"viewer@example.com"}),
    )
    app = create_app(
        f"sqlite:///{tmp_path / 'app.db'}",
        seed=True,
        access_config=config,
        jwks_fetcher=lambda _: {"keys": [jwk]},
        auth_clock=lambda: NOW,
    )
    return TestClient(app), private


def _headers(private, email="viewer@example.com", mutation=False):
    assertion = jwt.encode(
        {
            "iss": ISSUER,
            "aud": "exact-aud",
            "exp": int((NOW + timedelta(hours=1)).timestamp()),
            "nbf": int((NOW - timedelta(minutes=1)).timestamp()),
            "email": email,
        },
        private,
        algorithm="RS256",
        headers={"kid": "app-key"},
    )
    result = {"Cf-Access-Jwt-Assertion": assertion}
    if mutation:
        result.update({"Origin": ORIGIN, "X-AI-Stocks-CSRF": "1"})
    return result


def _close(client):
    client.close()
    client.app.state.engine.dispose()


def test_healthz_queries_the_database(tmp_path):
    client, _ = _client(tmp_path)
    statements = []
    event.listen(
        client.app.state.engine,
        "before_cursor_execute",
        lambda _conn, _cursor, statement, _parameters, _context, _many: statements.append(
            statement
        ),
    )
    try:
        assert client.get("/healthz").json() == {"ok": True}
        assert any("SELECT 1" in statement.upper() for statement in statements)
    finally:
        _close(client)


def test_startup_fails_closed_without_access_configuration(tmp_path, monkeypatch):
    for name in (
        "ACCESS_TEAM_DOMAIN",
        "ACCESS_AUD",
        "PUBLIC_ORIGIN",
        "ACCESS_OWNER_EMAILS",
        "ACCESS_VIEWER_EMAILS",
    ):
        monkeypatch.delenv(name, raising=False)
    with pytest.raises(RuntimeError):
        create_app(f"sqlite:///{tmp_path / 'missing.db'}")


def test_production_preflight_requires_postgresql_and_access_before_database_use(
    tmp_path, monkeypatch
):
    valid = {
        "APP_ENV": "production",
        "ACCESS_TEAM_DOMAIN": ISSUER,
        "ACCESS_AUD": "exact-aud",
        "PUBLIC_ORIGIN": ORIGIN,
        "ACCESS_OWNER_EMAILS": "owner@example.com",
        "ACCESS_VIEWER_EMAILS": "viewer@example.com",
    }
    with pytest.raises(RuntimeError, match="DATABASE_URL"):
        validate_production_environment(valid)
    with pytest.raises(RuntimeError, match="PostgreSQL"):
        validate_production_environment({**valid, "DATABASE_URL": "sqlite:///unsafe.db"})

    monkeypatch.setenv("APP_ENV", "production")
    monkeypatch.delenv("DATABASE_URL", raising=False)
    monkeypatch.chdir(tmp_path)
    with pytest.raises(RuntimeError, match="DATABASE_URL"):
        create_app(
            access_config=AccessConfig(
                ISSUER,
                "exact-aud",
                ORIGIN,
                frozenset({"owner@example.com"}),
                frozenset({"viewer@example.com"}),
            )
        )
    assert not (tmp_path / "ai_stocks.db").exists()


def test_viewer_reads_all_and_spoofed_identity_header_fails(tmp_path):
    client, private = _client(tmp_path)
    spoof = {"Cf-Access-Authenticated-User-Email": "owner@example.com"}
    assert client.get("/api/portfolio/a0", headers=spoof).status_code == 401
    assert client.get("/api/portfolio/a0", headers=_headers(private)).status_code == 200
    assert client.get("/api/portfolio/a1", headers=_headers(private)).status_code == 200
    _close(client)


def test_owner_can_only_pause_reset_with_exact_origin_and_csrf(tmp_path):
    client, private = _client(tmp_path)
    owner = _headers(private, "owner@example.com")
    protected = _headers(private, "owner@example.com", mutation=True)
    assert client.post("/admin/pause", headers=owner).status_code == 403
    assert (
        client.post(
            "/admin/pause", headers={**protected, "Origin": "https://evil.example"}
        ).status_code
        == 403
    )

    def post(path, key):
        return client.post(path, headers={**protected, "Idempotency-Key": key})

    assert post("/admin/reset", "reset-before-start").json() == {
        "contest_status": "DRAFT",
        "paused": False,
    }
    assert post("/admin/start", "start").json() == {
        "contest_status": "RUNNING",
        "paused": False,
    }
    assert post("/admin/pause", "pause").json() == {
        "contest_status": "PAUSED",
        "paused": True,
    }
    with Session(client.app.state.engine) as session:
        assert session.get(SystemState, 1).paused is True
    assert post("/admin/resume", "resume").json() == {
        "contest_status": "RUNNING",
        "paused": False,
    }
    assert post("/admin/reset", "reset-after-start").status_code == 409
    assert client.post("/internal/orders", headers=protected, json={}).status_code == 404
    assert client.post("/internal/runs", headers=protected, json={}).status_code == 404
    _close(client)


def test_dashboard_rejects_unapproved_model_identity(tmp_path):
    client, private = _client(tmp_path)
    hostile = '<img src=x onerror="alert(1)">'
    with Session(client.app.state.engine) as session:
        session.get(Agent, "a0").model_id = hostile
        with pytest.raises(IntegrityError):
            session.commit()
        session.rollback()
    body = client.get("/", headers=_headers(private)).text
    assert hostile not in body
    assert "gpt-5.6-sol" in body
    _close(client)


def test_full_day_all_four_models_is_deterministic():
    one = run_deterministic_day()
    two = run_deterministic_day()
    assert one == two
    assert len(one["agents"]) == 4 and one["ledger_balanced"] is True and one["runs"] == 24
