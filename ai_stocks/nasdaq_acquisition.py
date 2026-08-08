"""Acquisition and immutable archival for Nasdaq's delayed post-trade CSV feed."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path

import httpx

from .calendar import TradingSession

_LIST_PATH = "/api/regulatory/trade-reports"
_DOWNLOAD_PATH = "/api/regulatory/trade-report/download"
_PARAMS = {"type": "POST_TRADE", "assetClass": "EQUITY"}
_REPORT_RE = re.compile(r"NordicEquity-posttrade-2026-\d{2}-\d{2}T\d{4}\Z")
_MAX_CSV_BYTES = 50 * 1024 * 1024


class FeedProtocolError(RuntimeError):
    """The official feed returned data outside its pinned contract."""


@dataclass(frozen=True)
class ArchivedReport:
    report: str
    csv_path: Path
    metadata_path: Path
    sha256: str


class NasdaqPostTradeClient:
    def __init__(
        self,
        *,
        archive_dir: Path,
        http: httpx.Client,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self.archive_dir = Path(archive_dir)
        self.http = http
        self.clock = clock or (lambda: datetime.now(UTC))

    @staticmethod
    def _validate_report(report: str) -> None:
        if not _REPORT_RE.fullmatch(report):
            raise FeedProtocolError("invalid Nasdaq post-trade report name")

    def list_reports(self) -> tuple[str, ...]:
        response = self.http.get(_LIST_PATH, params=_PARAMS)
        response.raise_for_status()
        try:
            payload = response.json()
        except ValueError as exc:
            raise FeedProtocolError("Nasdaq report listing is not JSON") from exc
        if not isinstance(payload, dict) or payload.get("message") is not None:
            raise FeedProtocolError("Nasdaq report listing returned an error message")
        reports = payload.get("reports")
        if not isinstance(reports, list) or not reports:
            raise FeedProtocolError("Nasdaq report listing is empty or malformed")
        for report in reports:
            if not isinstance(report, str):
                raise FeedProtocolError("Nasdaq report listing contains a non-string name")
            self._validate_report(report)
        if len(reports) != len(set(reports)):
            raise FeedProtocolError("Nasdaq report listing contains duplicate names")
        return tuple(reports)

    def _existing(self, report: str) -> ArchivedReport | None:
        directory = self.archive_dir / report
        if not directory.exists():
            return None
        csv_path = directory / f"{report}.csv"
        metadata_path = directory / "metadata.json"
        if not csv_path.is_file() or not metadata_path.is_file():
            raise FeedProtocolError("incomplete Nasdaq archive entry")
        try:
            metadata = json.loads(metadata_path.read_text())
        except (OSError, ValueError) as exc:
            raise FeedProtocolError("invalid Nasdaq archive metadata") from exc
        actual = hashlib.sha256(csv_path.read_bytes()).hexdigest()
        if (
            not isinstance(metadata, dict)
            or metadata.get("report") != report
            or metadata.get("sha256") != actual
            or metadata.get("bytes") != csv_path.stat().st_size
        ):
            raise FeedProtocolError("Nasdaq archive checksum or metadata mismatch")
        return ArchivedReport(report, csv_path, metadata_path, actual)

    def existing(self, report: str) -> ArchivedReport | None:
        """Return a checksum-verified immutable archive entry, if present."""
        self._validate_report(report)
        return self._existing(report)

    def download_and_archive(self, report: str) -> ArchivedReport:
        self._validate_report(report)
        existing = self._existing(report)
        if existing is not None:
            return existing

        params = {**_PARAMS, "fileName": report}
        response = self.http.get(_DOWNLOAD_PATH, params=params)
        response.raise_for_status()
        body = response.content
        if not body or len(body) > _MAX_CSV_BYTES:
            raise FeedProtocolError("Nasdaq report body is empty or oversized")
        if not body.startswith(b'"sep=;"') or b"Trading date and time;" not in body[:4096]:
            raise FeedProtocolError("Nasdaq report body is not the expected CSV schema")

        fetched_at = self.clock()
        if fetched_at.tzinfo is None:
            raise FeedProtocolError("archive clock must be timezone-aware")
        digest = hashlib.sha256(body).hexdigest()
        metadata = {
            "bytes": len(body),
            "fetched_at": fetched_at.isoformat(),
            "report": report,
            "sha256": digest,
            "source_url": str(response.request.url),
        }

        self.archive_dir.mkdir(parents=True, exist_ok=True)
        temporary = Path(tempfile.mkdtemp(prefix=f".{report}-", dir=self.archive_dir))
        try:
            csv_path = temporary / f"{report}.csv"
            metadata_path = temporary / "metadata.json"
            csv_path.write_bytes(body)
            metadata_path.write_text(json.dumps(metadata, indent=2, sort_keys=True) + "\n")
            final = self.archive_dir / report
            try:
                os.rename(temporary, final)
            except FileExistsError as exc:
                existing = self._existing(report)
                if existing is None or existing.sha256 != digest:
                    raise FeedProtocolError("conflicting concurrent Nasdaq archive write") from exc
                return existing
            return ArchivedReport(
                report,
                final / f"{report}.csv",
                final / "metadata.json",
                digest,
            )
        finally:
            if temporary.exists():
                for child in temporary.iterdir():
                    child.unlink()
                temporary.rmdir()

    def finalize_session(
        self,
        session: TradingSession,
        reports: list[ArchivedReport],
        *,
        finalized_at: datetime,
    ) -> Path:
        """Persist the immutable checksum set proving one collected session complete.

        The caller is responsible for collecting the full official listing/report set.
        This boundary re-verifies every raw archive before recording that assertion.
        """
        if finalized_at.tzinfo is None or finalized_at.utcoffset() is None:
            raise FeedProtocolError("session finalization timestamp must be timezone-aware")
        delay = finalized_at - session.close_at
        if delay < timedelta(minutes=15) or delay > timedelta(minutes=20):
            raise FeedProtocolError("session finalization is outside 15-20 minute delay")
        if not reports:
            raise FeedProtocolError("cannot finalize an empty Nasdaq session")
        verified: list[ArchivedReport] = []
        seen: set[str] = set()
        expected_day = session.day.strftime("%Y-%m-%d")
        for supplied in reports:
            if supplied.report in seen or expected_day not in supplied.report:
                raise FeedProtocolError("session report set is duplicated or for the wrong day")
            existing = self._existing(supplied.report)
            if existing is None or existing != supplied:
                raise FeedProtocolError("session report identity does not match verified archive")
            seen.add(supplied.report)
            verified.append(existing)
        expected: set[str] = set()
        cursor = session.open_at + timedelta(minutes=15)
        final_publication = session.close_at + timedelta(minutes=15)
        while cursor <= final_publication:
            expected.add(f"NordicEquity-posttrade-{cursor:%Y-%m-%dT%H%M}")
            cursor += timedelta(minutes=1)
        if seen != expected:
            raise FeedProtocolError(
                "session report set is incomplete or contains unexpected reports"
            )
        session_id = f"XSTO-{session.day.isoformat()}"
        payload = {
            "complete": True,
            "finalized_at": finalized_at.isoformat(),
            "reports": [
                {"report": report.report, "sha256": report.sha256}
                for report in sorted(verified, key=lambda item: item.report)
            ],
            "schema_version": 1,
            "session_close": session.close_at.isoformat(),
            "session_id": session_id,
            "session_open": session.open_at.isoformat(),
            "source_listing_url": (
                "https://tradereports.nasdaq.com/api/regulatory/trade-reports"
                "?type=POST_TRADE&assetClass=EQUITY"
            ),
        }
        body = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode()
        directory = self.archive_dir / "sessions"
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / f"{session_id}.json"
        try:
            with path.open("xb") as handle:
                handle.write(body)
        except FileExistsError as exc:
            try:
                existing_payload = json.loads(path.read_text())
                existing_finalized = datetime.fromisoformat(
                    existing_payload["finalized_at"].replace("Z", "+00:00")
                )
                existing_identity = dict(existing_payload)
                candidate_identity = dict(payload)
                existing_identity.pop("finalized_at")
                candidate_identity.pop("finalized_at")
            except (OSError, ValueError, KeyError, TypeError) as parse_exc:
                raise FeedProtocolError(
                    "existing Nasdaq session manifest is malformed"
                ) from parse_exc
            existing_delay = existing_finalized - session.close_at
            if (
                existing_finalized.tzinfo is None
                or existing_finalized.utcoffset() is None
                or existing_delay < timedelta(minutes=15)
                or existing_delay > timedelta(minutes=20)
                or existing_identity != candidate_identity
            ):
                raise FeedProtocolError("conflicting immutable Nasdaq session manifest") from exc
        return path
