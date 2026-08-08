"""Fail-closed production worker configuration and process loop."""

from __future__ import annotations

import hashlib
import json
import os
import time
from collections.abc import Mapping
from datetime import UTC, datetime, timedelta
from pathlib import Path

from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker

from .health import heartbeat_path, write_heartbeat
from .nasdaq_market import DEFAULT_UNIVERSE_SHA256, InstrumentStatus, NasdaqMarketProvider
from .runner import HermesRunner
from .worker import WorkerRuntime

_MAX_STATUS_AGE = timedelta(minutes=20)


class StatusArtifactError(RuntimeError):
    pass


def _aware(value: object, label: str) -> datetime:
    if not isinstance(value, str):
        raise StatusArtifactError(f"{label} is missing")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise StatusArtifactError(f"{label} is invalid") from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise StatusArtifactError(f"{label} must be timezone-aware")
    return parsed


def load_statuses(
    status_path: Path,
    universe_path: Path,
    *,
    now: datetime | None = None,
) -> dict[str, InstrumentStatus]:
    checked_at = now or datetime.now(UTC)
    if checked_at.tzinfo is None or checked_at.utcoffset() is None:
        raise StatusArtifactError("status validation time must be timezone-aware")
    try:
        universe_body = universe_path.read_bytes()
        universe = json.loads(universe_body)
        payload = json.loads(status_path.read_text())
    except (OSError, ValueError) as exc:
        raise StatusArtifactError("status or universe artifact is unreadable") from exc
    if not isinstance(universe, dict) or not isinstance(payload, dict):
        raise StatusArtifactError("status or universe artifact is malformed")
    if payload.get("schema_version") != 1:
        raise StatusArtifactError("status artifact schema is unsupported")
    expected_digest = hashlib.sha256(universe_body).hexdigest()
    if payload.get("universe_sha256") != expected_digest:
        raise StatusArtifactError("status artifact universe checksum does not match")
    verified_at = _aware(payload.get("verified_at"), "status verified_at")
    age = checked_at - verified_at
    if age < timedelta(0) or age > _MAX_STATUS_AGE:
        raise StatusArtifactError("status artifact is stale")

    urls = payload.get("source_urls")
    digests = payload.get("source_sha256")
    if (
        not isinstance(urls, list)
        or not urls
        or not all(
            isinstance(url, str)
            and url.startswith("https://")
            and (
                url.split("/", 3)[2] == "nasdaq.com" or url.split("/", 3)[2].endswith(".nasdaq.com")
            )
            for url in urls
        )
    ):
        raise StatusArtifactError("status evidence is not from an official Nasdaq source")
    if (
        not isinstance(digests, list)
        or len(digests) != len(urls)
        or not all(
            isinstance(digest, str)
            and len(digest) == 64
            and all(character in "0123456789abcdef" for character in digest)
            for digest in digests
        )
    ):
        raise StatusArtifactError("status evidence checksum is invalid")

    instruments = universe.get("instruments")
    statuses = payload.get("statuses")
    if not isinstance(instruments, list) or not isinstance(statuses, dict):
        raise StatusArtifactError("status universe coverage is malformed")
    symbols = {
        row.get("symbol")
        for row in instruments
        if isinstance(row, dict) and isinstance(row.get("symbol"), str)
    }
    if not symbols or set(statuses) != symbols:
        raise StatusArtifactError("status artifact does not completely cover the universe")

    result: dict[str, InstrumentStatus] = {}
    for symbol, raw in statuses.items():
        if not isinstance(raw, dict) or set(raw) != {"verified", "warning", "suspended"}:
            raise StatusArtifactError("instrument status record is malformed")
        if not all(isinstance(raw[key], bool) for key in raw):
            raise StatusArtifactError("instrument status fields must be boolean")
        if not raw["verified"]:
            raise StatusArtifactError("instrument status is unresolved")
        result[symbol] = InstrumentStatus(**raw)
    return result


def run_iteration(
    factory: sessionmaker,
    *,
    universe_path: Path,
    universe_sha256: str,
    status_path: Path,
    archive_path: Path,
    now: datetime | None = None,
) -> str | None:
    tick_at = now or datetime.now(UTC)
    statuses = load_statuses(status_path, universe_path, now=tick_at)
    market = NasdaqMarketProvider(
        archive_dir=archive_path,
        universe_path=universe_path,
        universe_sha256=universe_sha256,
        statuses=statuses,
    )
    with factory() as session:
        runtime = WorkerRuntime(
            session=session,
            runner=HermesRunner(),
            market=market,
            clock=lambda: datetime.now(UTC),
        )
        claimed = runtime.tick(tick_at)
        session.commit()
        return claimed


def main(environment: Mapping[str, str] | None = None) -> int:
    values = environment if environment is not None else os.environ
    database_url = values.get("DATABASE_URL", "").strip()
    if not database_url:
        raise RuntimeError("DATABASE_URL is required")
    universe_path = Path(
        values.get("NASDAQ_UNIVERSE_PATH", "/app/config/nasdaq-stockholm-main-market-universe.json")
    )
    status_path = Path(values.get("NASDAQ_STATUS_PATH", "/data/status/status.json"))
    archive_path = Path(values.get("NASDAQ_ARCHIVE_PATH", "/data/nasdaq"))
    poll_seconds = float(values.get("WORKER_POLL_SECONDS", "5"))
    if poll_seconds < 1 or poll_seconds > 60:
        raise RuntimeError("WORKER_POLL_SECONDS must be between 1 and 60")
    factory = sessionmaker(bind=create_engine(database_url), expire_on_commit=False)
    while True:
        run_iteration(
            factory,
            universe_path=universe_path,
            universe_sha256=DEFAULT_UNIVERSE_SHA256,
            status_path=status_path,
            archive_path=archive_path,
        )
        write_heartbeat(heartbeat_path("worker"))
        time.sleep(poll_seconds)


if __name__ == "__main__":
    raise SystemExit(main())
