"""Pinned Nasdaq Stockholm equity calendar for the 2026 contest."""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from datetime import date, datetime, time, timedelta
from enum import StrEnum
from pathlib import Path
from zoneinfo import ZoneInfo

STOCKHOLM = ZoneInfo("Europe/Stockholm")

HOLIDAY_WORKBOOK_SHA256 = "867f80011a2d8cf91f29dce6de8b6c77d4c4fda0954efa8f757f40b25c585395"
TRADING_HOURS_SHA256 = "f16f58c7520eaaae3210ddab666e7bde2609d1935c69e9f5d706bbd0d14fe395"

CLOSED_DATES_2026 = frozenset(
    {
        date(2026, 1, 1),
        date(2026, 1, 6),
        date(2026, 4, 3),
        date(2026, 4, 6),
        date(2026, 5, 1),
        date(2026, 5, 14),
        date(2026, 6, 19),
        date(2026, 12, 24),
        date(2026, 12, 25),
        date(2026, 12, 31),
    }
)
HALF_DATES_2026 = frozenset(
    {
        date(2026, 1, 5),
        date(2026, 4, 2),
        date(2026, 4, 30),
        date(2026, 5, 13),
        date(2026, 10, 30),
    }
)


class SessionKind(StrEnum):
    FULL = "FULL"
    HALF = "HALF"


@dataclass(frozen=True)
class TradingSession:
    day: date
    open_at: datetime
    close_at: datetime
    kind: SessionKind


def verify_source_artifacts(root: Path | None = None) -> None:
    docs = (root or Path(__file__).resolve().parents[1]) / "docs"
    expected = {
        "nasdaq-holiday-schedule-2026.xlsx": HOLIDAY_WORKBOOK_SHA256,
        "nasdaq-trading-hours.html": TRADING_HOURS_SHA256,
    }
    for name, digest in expected.items():
        path = docs / name
        if not path.is_file():
            raise RuntimeError(f"missing pinned Nasdaq source artifact: {name}")
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual != digest:
            raise RuntimeError(f"Nasdaq source artifact checksum mismatch: {name}")


def session_for(day: date) -> TradingSession | None:
    if day.year != 2026:
        return None
    if day.weekday() >= 5 or day in CLOSED_DATES_2026:
        return None
    kind = SessionKind.HALF if day in HALF_DATES_2026 else SessionKind.FULL
    close = time(13, 0) if kind is SessionKind.HALF else time(17, 30)
    return TradingSession(
        day=day,
        open_at=datetime.combine(day, time(9, 0), STOCKHOLM),
        close_at=datetime.combine(day, close, STOCKHOLM),
        kind=kind,
    )


def six_run_times(session: TradingSession) -> tuple[datetime, ...]:
    duration = session.close_at - session.open_at
    intraday = tuple(session.open_at + duration * index / 5 for index in range(1, 5))
    return (
        session.open_at - timedelta(minutes=60),
        *intraday,
        session.close_at + timedelta(minutes=30),
    )


def next_full_session(after: date) -> TradingSession:
    candidate = after + timedelta(days=1)
    while candidate.year == 2026:
        session = session_for(candidate)
        if session is not None and session.kind is SessionKind.FULL:
            return session
        candidate += timedelta(days=1)
    raise LookupError("no later full 2026 Nasdaq Stockholm session")


def contest_final_session() -> TradingSession:
    candidate = date(2026, 12, 31)
    while True:
        session = session_for(candidate)
        if session is not None:
            return session
        candidate -= timedelta(days=1)
