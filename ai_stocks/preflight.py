"""Fail-closed production configuration validation with no database side effects."""

from __future__ import annotations

import os
from collections.abc import Mapping

from sqlalchemy.engine import make_url

from .auth import AccessConfig


def validate_production_environment(
    environment: Mapping[str, str] | None = None,
) -> tuple[AccessConfig, str]:
    values = environment if environment is not None else os.environ
    if values.get("APP_ENV", "").strip().lower() != "production":
        raise RuntimeError("APP_ENV must be production")

    database_url = values.get("DATABASE_URL", "").strip()
    if not database_url:
        raise RuntimeError("DATABASE_URL is required in production")
    try:
        parsed = make_url(database_url)
    except Exception as exc:
        raise RuntimeError("DATABASE_URL is invalid") from exc
    if not parsed.drivername.startswith("postgresql"):
        raise RuntimeError("production DATABASE_URL must use PostgreSQL")
    if not parsed.host or not parsed.database or not parsed.username or parsed.password is None:
        raise RuntimeError("production PostgreSQL URL must include host, database, and credentials")

    def required(name: str) -> str:
        value = values.get(name, "").strip()
        if not value:
            raise RuntimeError(f"missing required Access configuration: {name}")
        return value

    allow_any_value = values.get("ACCESS_ALLOW_ANY_AUTHENTICATED_VIEWER", "false").strip().lower()
    if allow_any_value not in {"true", "false"}:
        raise RuntimeError("ACCESS_ALLOW_ANY_AUTHENTICATED_VIEWER must be true or false")
    allow_any = allow_any_value == "true"
    viewer_value = values.get("ACCESS_VIEWER_EMAILS", "").strip()
    if not allow_any and not viewer_value:
        raise RuntimeError("missing required Access configuration: ACCESS_VIEWER_EMAILS")

    config = AccessConfig(
        team_domain=required("ACCESS_TEAM_DOMAIN"),
        audience=required("ACCESS_AUD"),
        public_origin=required("PUBLIC_ORIGIN"),
        owner_emails=frozenset(
            item.strip() for item in required("ACCESS_OWNER_EMAILS").split(",") if item.strip()
        ),
        viewer_emails=frozenset(item.strip() for item in viewer_value.split(",") if item.strip()),
        allow_any_authenticated_viewer=allow_any,
    )
    return config, database_url


def main() -> int:
    validate_production_environment()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
