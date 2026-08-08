"""Continuous official Nasdaq delayed-report collector."""

from __future__ import annotations

import os
import re
import time
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

import httpx

from .calendar import TradingSession, session_for
from .health import heartbeat_path, write_heartbeat
from .nasdaq_acquisition import ArchivedReport, NasdaqPostTradeClient

STOCKHOLM = ZoneInfo("Europe/Stockholm")
_REPORT_TIME = re.compile(r"^NordicEquity-posttrade-(\d{4}-\d{2}-\d{2}T\d{4})$")
PUBLICATION_DELAY = timedelta(minutes=15)


@dataclass(frozen=True)
class CollectionResult:
    downloaded: tuple[str, ...]
    finalized: Path | None
    missing: tuple[str, ...]


def report_timestamp(report: str) -> datetime:
    match = _REPORT_TIME.fullmatch(report)
    if match is None:
        raise ValueError("invalid report identity")
    return datetime.strptime(match.group(1), "%Y-%m-%dT%H%M").replace(tzinfo=STOCKHOLM)


def expected_reports(session: TradingSession) -> tuple[str, ...]:
    cursor = session.open_at + PUBLICATION_DELAY
    final = session.close_at + PUBLICATION_DELAY
    reports: list[str] = []
    while cursor <= final:
        reports.append(f"NordicEquity-posttrade-{cursor.astimezone(STOCKHOLM):%Y-%m-%dT%H%M}")
        cursor += timedelta(minutes=1)
    return tuple(reports)


def reports_due(
    reports: tuple[str, ...], session: TradingSession, now: datetime
) -> tuple[str, ...]:
    if now.tzinfo is None or now.utcoffset() is None:
        raise ValueError("collector clock must be timezone-aware")
    start = session.open_at + PUBLICATION_DELAY
    end = session.close_at + PUBLICATION_DELAY
    due = []
    for report in reports:
        published = report_timestamp(report)
        if start <= published <= end and published <= now:
            due.append(report)
    return tuple(sorted(due))


def collect_once(client: NasdaqPostTradeClient, *, now: datetime) -> CollectionResult:
    if now.tzinfo is None or now.utcoffset() is None:
        raise ValueError("collector clock must be timezone-aware")
    listing = client.list_reports()
    days = {now.astimezone(STOCKHOLM).date()}
    for name in listing:
        try:
            days.add(report_timestamp(name).date())
        except ValueError:
            continue

    downloaded: list[str] = []
    missing: list[str] = []
    finalized: Path | None = None
    listed = set(listing)
    for day in sorted(days):
        try:
            session = session_for(day)
        except ValueError:
            continue
        if session is None:
            continue
        for name in reports_due(listing, session, now):
            downloaded.append(client.download_and_archive(name).report)
        if now < session.close_at + PUBLICATION_DELAY:
            continue
        expected = expected_reports(session)
        archived: list[ArchivedReport] = []
        session_missing: list[str] = []
        for name in expected:
            if name not in listed:
                session_missing.append(name)
                continue
            existing = client.existing(name)
            if existing is None:
                session_missing.append(name)
            else:
                archived.append(existing)
        if session_missing:
            missing.extend(session_missing)
            continue
        finalized = client.finalize_session(session, archived, finalized_at=now)
    return CollectionResult(tuple(downloaded), finalized, tuple(sorted(set(missing))))


def main() -> None:
    archive = Path(os.environ.get("ARCHIVE_PATH", "/data/nasdaq"))
    poll = float(os.environ.get("COLLECTOR_POLL_SECONDS", "15"))
    if poll < 5 or poll > 60:
        raise RuntimeError("COLLECTOR_POLL_SECONDS must be between 5 and 60")
    with httpx.Client(
        base_url="https://tradereports.nasdaq.com",
        timeout=httpx.Timeout(30),
        headers={"User-Agent": "ai-stocks/0.1 official delayed-feed collector"},
    ) as http:
        client = NasdaqPostTradeClient(archive_dir=archive, http=http)
        while True:
            result = collect_once(client, now=datetime.now(UTC))
            write_heartbeat(heartbeat_path("collector"))
            if result.missing:
                print(f"session incomplete: {len(result.missing)} reports missing", flush=True)
            time.sleep(poll)


if __name__ == "__main__":
    main()
