"""Verified Nasdaq Stockholm delayed post-trade market provider.

The provider consumes only immutable archives produced by
:mod:`ai_stocks.nasdaq_acquisition`.  A history session counts only when a
collector-written completion manifest binds the exact official reports and
checksums used for that session.
"""

from __future__ import annotations

import csv
import hashlib
import json
import re
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from decimal import Decimal, InvalidOperation
from io import StringIO
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from .calendar import STOCKHOLM, session_for
from .market import Quote, SessionWindow

DEFAULT_UNIVERSE_SHA256 = "b82692385d2accf8cd52f111814d1219faae7dfb181a3c6592177aa22cd222c1"
_REPORT_RE = re.compile(r"NordicEquity-posttrade-2026-\d{2}-\d{2}T\d{4}\Z")
_DOWNLOAD_PATH = "/api/regulatory/trade-report/download"
_LIST_PATH = "/api/regulatory/trade-reports"
_PARAMS = {"type": ["POST_TRADE"], "assetClass": ["EQUITY"]}
_MIN_DELAY = timedelta(minutes=15)
_MAX_DELAY = timedelta(minutes=20)


class MarketDataError(RuntimeError):
    """Archived official evidence failed closed validation."""


@dataclass(frozen=True)
class InstrumentStatus:
    """Separately verified unresolved-warning and suspension state."""

    verified: bool
    warning: bool = False
    suspended: bool = False


@dataclass(frozen=True)
class _Instrument:
    symbol: str
    isin: str
    order_book_id: str


@dataclass(frozen=True)
class _Observation:
    instrument: _Instrument
    price: Decimal
    quantity: int
    executed_at: datetime
    published_at: datetime
    fetched_at: datetime
    transaction_id: str
    report: str
    checksum: str
    execution_eligible: bool


@dataclass(frozen=True)
class _Archive:
    observations: tuple[_Observation, ...]
    report_checksums: dict[str, str]


def _aware(value: object, label: str) -> datetime:
    if not isinstance(value, str):
        raise MarketDataError(f"{label} is missing")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise MarketDataError(f"{label} is invalid") from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise MarketDataError(f"{label} must be timezone-aware")
    return parsed


def _session_window(day: date) -> SessionWindow | None:
    try:
        session = session_for(day)
    except ValueError:
        return None
    if session is None:
        return None
    return SessionWindow(
        open_at=session.open_at,
        close_at=session.close_at,
        session_id=f"XSTO-{day.isoformat()}",
    )


def _official_url(value: object, path: str, *, report: str | None = None) -> bool:
    if not isinstance(value, str):
        return False
    parsed = urlparse(value)
    if parsed.scheme != "https" or parsed.netloc != "tradereports.nasdaq.com":
        return False
    if parsed.path != path:
        return False
    expected = dict(_PARAMS)
    if report is not None:
        expected["fileName"] = [report]
    return parse_qs(parsed.query) == expected


class NasdaqMarketProvider:
    """Concrete ``MarketProvider`` backed by checksummed official raw CSV."""

    def __init__(
        self,
        *,
        archive_dir: Path,
        universe_path: Path,
        universe_sha256: str,
        statuses: dict[str, InstrumentStatus],
    ) -> None:
        self.archive_dir = Path(archive_dir)
        self.instruments = self._load_universe(Path(universe_path), universe_sha256)
        self.statuses = dict(statuses)

    @staticmethod
    def _load_universe(path: Path, expected: str) -> dict[str, _Instrument]:
        try:
            body = path.read_bytes()
        except OSError as exc:
            raise MarketDataError("eligible universe artifact is missing") from exc
        if hashlib.sha256(body).hexdigest() != expected:
            raise MarketDataError("eligible universe checksum mismatch")
        try:
            payload = json.loads(body)
        except ValueError as exc:
            raise MarketDataError("eligible universe artifact is malformed") from exc
        if not isinstance(payload, dict) or payload.get("artifact_version") != 1:
            raise MarketDataError("eligible universe artifact version is invalid")
        provenance = payload.get("provenance")
        rows = payload.get("instruments")
        if not isinstance(provenance, dict) or not provenance.get("source_url"):
            raise MarketDataError("eligible universe provenance is missing")
        if not isinstance(rows, list) or not rows:
            raise MarketDataError("eligible universe is empty")
        instruments: dict[str, _Instrument] = {}
        isins: set[str] = set()
        order_books: set[str] = set()
        for row in rows:
            if (
                not isinstance(row, dict)
                or row.get("venue") != "XSTO"
                or row.get("currency") != "SEK"
                or row.get("instrument_type") != "COMMON_SHARE"
                or row.get("segment") not in {"LARGE_CAP", "MID_CAP", "SMALL_CAP"}
            ):
                raise MarketDataError("eligible universe contains an ineligible instrument")
            try:
                instrument = _Instrument(
                    symbol=row["symbol"], isin=row["isin"], order_book_id=row["order_book_id"]
                )
            except KeyError as exc:
                raise MarketDataError("eligible universe identity is incomplete") from exc
            if (
                not all(isinstance(item, str) and item for item in instrument.__dict__.values())
                or instrument.symbol in instruments
                or instrument.isin in isins
                or instrument.order_book_id in order_books
            ):
                raise MarketDataError("eligible universe identity is invalid or duplicated")
            instruments[instrument.symbol] = instrument
            isins.add(instrument.isin)
            order_books.add(instrument.order_book_id)
        return instruments

    def session_containing(self, at: datetime) -> SessionWindow | None:
        if at.tzinfo is None or at.utcoffset() is None:
            raise MarketDataError("market timestamp must be timezone-aware")
        session = _session_window(at.astimezone(STOCKHOLM).date())
        return session if session is not None and session.contains(at) else None

    def next_session(self, at: datetime) -> SessionWindow | None:
        if at.tzinfo is None or at.utcoffset() is None:
            raise MarketDataError("market timestamp must be timezone-aware")
        candidate = at.astimezone(STOCKHOLM).date()
        for _ in range(370):
            session = _session_window(candidate)
            if session is not None and session.open_at > at:
                return session
            candidate += timedelta(days=1)
        return None

    def first_quote_at_or_after(
        self, symbol: str, at: datetime, *, as_of: datetime | None = None
    ) -> Quote | None:
        self._validate_query_times(at, as_of)
        instrument = self.instruments.get(symbol)
        if instrument is None:
            return None
        status = self._status(symbol)
        session = self.session_containing(at)
        if session is None:
            return None
        archive = self._read_archive()
        candidates = [
            observation
            for observation in archive.observations
            if observation.instrument == instrument
            and observation.execution_eligible
            and observation.executed_at >= at
            and session.contains(observation.executed_at)
            and (as_of is None or observation.fetched_at <= as_of)
        ]
        if not candidates:
            return None
        observation = min(
            candidates,
            key=lambda item: (item.executed_at, item.transaction_id, item.report),
        )
        return self._quote(observation, session, archive, status, as_of)

    def latest_quote_at_or_before(
        self, symbol: str, at: datetime, *, as_of: datetime | None = None
    ) -> Quote | None:
        self._validate_query_times(at, as_of)
        instrument = self.instruments.get(symbol)
        if instrument is None:
            return None
        status = self._status(symbol)
        archive = self._read_archive()
        candidates = [
            observation
            for observation in archive.observations
            if observation.instrument == instrument
            and observation.execution_eligible
            and observation.executed_at <= at
            and (as_of is None or observation.fetched_at <= as_of)
            and self.session_containing(observation.executed_at) is not None
        ]
        if not candidates:
            return None
        observation = max(
            candidates,
            key=lambda item: (item.executed_at, item.transaction_id, item.report),
        )
        session = self.session_containing(observation.executed_at)
        if session is None:  # guarded above, retained for type narrowing
            return None
        return self._quote(observation, session, archive, status, as_of)

    @staticmethod
    def _validate_query_times(at: datetime, as_of: datetime | None) -> None:
        if at.tzinfo is None or at.utcoffset() is None:
            raise MarketDataError("market timestamp must be timezone-aware")
        if as_of is not None:
            if as_of.tzinfo is None or as_of.utcoffset() is None:
                raise MarketDataError("market observation timestamp must be timezone-aware")
            if as_of < at:
                raise MarketDataError("market observation timestamp precedes query")

    def _status(self, symbol: str) -> InstrumentStatus:
        status = self.statuses.get(symbol)
        if status is None:
            raise MarketDataError("instrument warning/suspension status is unresolved")
        return status

    def _read_archive(self) -> _Archive:
        observations: list[_Observation] = []
        checksums: dict[str, str] = {}
        if not self.archive_dir.is_dir():
            return _Archive((), {})
        for directory in sorted(self.archive_dir.iterdir()):
            if directory.name == "sessions":
                continue
            if not directory.is_dir() or not _REPORT_RE.fullmatch(directory.name):
                raise MarketDataError("Nasdaq archive contains an invalid report entry")
            report = directory.name
            csv_path = directory / f"{report}.csv"
            metadata_path = directory / "metadata.json"
            try:
                body = csv_path.read_bytes()
                metadata = json.loads(metadata_path.read_text())
            except (OSError, ValueError) as exc:
                raise MarketDataError("Nasdaq archive entry is incomplete or malformed") from exc
            digest = hashlib.sha256(body).hexdigest()
            if (
                not isinstance(metadata, dict)
                or metadata.get("report") != report
                or metadata.get("sha256") != digest
                or metadata.get("bytes") != len(body)
            ):
                raise MarketDataError("Nasdaq archive checksum or metadata mismatch")
            if not _official_url(metadata.get("source_url"), _DOWNLOAD_PATH, report=report):
                raise MarketDataError("Nasdaq archive source URL is invalid")
            fetched_at = _aware(metadata.get("fetched_at"), "Nasdaq retrieval timestamp")
            checksums[report] = digest
            observations.extend(self._parse_report(body, report, digest, fetched_at))
        return _Archive(tuple(observations), checksums)

    def _parse_report(
        self, body: bytes, report: str, checksum: str, fetched_at: datetime
    ) -> list[_Observation]:
        try:
            text = body.decode("utf-8-sig")
        except UnicodeDecodeError as exc:
            raise MarketDataError("Nasdaq report is not UTF-8 CSV") from exc
        lines = text.splitlines()
        if not lines or lines[0].strip().lower() != '"sep=;"':
            raise MarketDataError("Nasdaq report separator declaration is invalid")
        reader = csv.DictReader(StringIO("\n".join(lines[1:])), delimiter=";")
        required = {
            "Trading date and time",
            "Instrument identification code",
            "Price",
            "Price currency",
            "Price notation",
            "Quantity",
            "Venue of execution",
            "Publication date and time",
            "Transaction identification code",
        }
        if reader.fieldnames is None or not required.issubset(reader.fieldnames):
            raise MarketDataError("Nasdaq report schema is invalid")
        by_isin = {item.isin: item for item in self.instruments.values()}
        observations: list[_Observation] = []
        for row in reader:
            instrument = by_isin.get(row.get("Instrument identification code", ""))
            if instrument is None:
                continue
            if (
                row.get("Venue of execution") != "XSTO"
                or row.get("Price currency") != "SEK"
                or row.get("Price notation") != "MONE"
            ):
                continue
            try:
                executed_at = _aware(row.get("Trading date and time"), "trade timestamp")
                published_at = _aware(row.get("Publication date and time"), "publication timestamp")
                price = Decimal(row["Price"])
                quantity = int(row["Quantity"])
                transaction_id = row["Transaction identification code"].strip()
            except (KeyError, TypeError, ValueError, InvalidOperation) as exc:
                raise MarketDataError("eligible Nasdaq trade row is malformed") from exc
            publication_delay = published_at - executed_at
            retrieval_delay = fetched_at - executed_at
            if not (_MIN_DELAY <= publication_delay <= _MAX_DELAY):
                raise MarketDataError("Nasdaq publication is outside 15-20 minute delay")
            if fetched_at < published_at or retrieval_delay < _MIN_DELAY:
                raise MarketDataError("Nasdaq retrieval timestamp precedes publication eligibility")
            execution_eligible = retrieval_delay <= _MAX_DELAY
            session = self.session_containing(executed_at)
            if price <= 0 or quantity <= 0 or not transaction_id or session is None:
                raise MarketDataError("eligible Nasdaq trade row has invalid values or session")
            observations.append(
                _Observation(
                    instrument=instrument,
                    price=price,
                    quantity=quantity,
                    executed_at=executed_at,
                    published_at=published_at,
                    fetched_at=fetched_at,
                    transaction_id=transaction_id,
                    report=report,
                    checksum=checksum,
                    execution_eligible=execution_eligible,
                )
            )
        return observations

    def _quote(
        self,
        observation: _Observation,
        session: SessionWindow,
        archive: _Archive,
        status: InstrumentStatus,
        as_of: datetime | None,
    ) -> Quote:
        daily_values = self._history_values(
            observation.instrument, session, archive, before=observation.executed_at
        )
        latest_twenty = daily_values[-20:]
        adv20 = (
            sum(latest_twenty, Decimal(0)) / Decimal(20) if len(latest_twenty) == 20 else Decimal(0)
        )
        visible = [
            item
            for item in archive.observations
            if item.instrument == observation.instrument
            and session.contains(item.executed_at)
            and (as_of is None or item.fetched_at <= as_of)
        ]
        volume = sum(item.quantity for item in self._deduplicate(visible))
        return Quote(
            symbol=observation.instrument.symbol,
            instrument_id=f"{observation.instrument.isin}:{observation.instrument.order_book_id}",
            price=observation.price,
            source_at=observation.executed_at,
            retrieved_at=observation.fetched_at,
            venue="XSTO",
            currency="SEK",
            volume=volume,
            adv20=adv20,
            history_days=len(daily_values),
            warning=status.warning,
            suspended=status.suspended,
            session_id=session.session_id,
            session_open=session.open_at,
            session_close=session.close_at,
            raw_checksum=observation.checksum,
            verified=status.verified,
            raw_evidence_id=(
                f"{observation.report}:{observation.checksum}:{observation.transaction_id}"
            ),
        )

    def _history_values(
        self,
        instrument: _Instrument,
        quote_session: SessionWindow,
        archive: _Archive,
        *,
        before: datetime,
    ) -> list[Decimal]:
        manifests = self.archive_dir / "sessions"
        if not manifests.is_dir():
            return []
        values: list[tuple[datetime, Decimal]] = []
        for path in sorted(manifests.glob("XSTO-*.json")):
            manifest_session, report_names = self._verify_manifest(path, archive)
            if manifest_session.close_at >= quote_session.open_at:
                continue
            rows = [
                item
                for item in archive.observations
                if item.instrument == instrument
                and item.report in report_names
                and manifest_session.contains(item.executed_at)
                and item.executed_at < before
            ]
            rows = self._deduplicate(rows)
            values.append(
                (
                    manifest_session.open_at,
                    sum((item.price * item.quantity for item in rows), Decimal(0)),
                )
            )
        return [value for _, value in sorted(values)]

    @staticmethod
    def _deduplicate(rows: list[_Observation]) -> list[_Observation]:
        unique: dict[tuple[str, str], _Observation] = {}
        for row in rows:
            key = (row.instrument.isin, row.transaction_id)
            existing = unique.get(key)
            if existing is not None and existing != row:
                raise MarketDataError("Nasdaq transaction identity conflicts across reports")
            unique[key] = row
        return list(unique.values())

    def _verify_manifest(
        self, path: Path, archive: _Archive
    ) -> tuple[SessionWindow, frozenset[str]]:
        try:
            payload = json.loads(path.read_text())
        except (OSError, ValueError) as exc:
            raise MarketDataError("Nasdaq complete-session manifest is malformed") from exc
        if (
            not isinstance(payload, dict)
            or payload.get("schema_version") != 1
            or payload.get("complete") is not True
            or not _official_url(payload.get("source_listing_url"), _LIST_PATH)
        ):
            raise MarketDataError("Nasdaq complete-session manifest provenance is invalid")
        session_id = payload.get("session_id")
        if not isinstance(session_id, str) or not session_id.startswith("XSTO-"):
            raise MarketDataError("Nasdaq complete-session identity is invalid")
        try:
            day = date.fromisoformat(session_id.removeprefix("XSTO-"))
        except ValueError as exc:
            raise MarketDataError("Nasdaq complete-session identity is invalid") from exc
        session = _session_window(day)
        if (
            session is None
            or path.name != f"{session_id}.json"
            or payload.get("session_open") != session.open_at.isoformat()
            or payload.get("session_close") != session.close_at.isoformat()
        ):
            raise MarketDataError("Nasdaq complete-session calendar identity mismatch")
        finalized = _aware(payload.get("finalized_at"), "session finalization timestamp")
        if not _MIN_DELAY <= finalized - session.close_at <= _MAX_DELAY:
            raise MarketDataError("Nasdaq session finalization is outside 15-20 minute delay")
        report_rows = payload.get("reports")
        if not isinstance(report_rows, list) or not report_rows:
            raise MarketDataError("Nasdaq complete-session report set is empty")
        report_names: set[str] = set()
        for row in report_rows:
            if not isinstance(row, dict):
                raise MarketDataError("Nasdaq complete-session report identity is malformed")
            report = row.get("report")
            checksum = row.get("sha256")
            if (
                not isinstance(report, str)
                or report in report_names
                or archive.report_checksums.get(report) != checksum
            ):
                raise MarketDataError("Nasdaq complete-session report checksum mismatch")
            report_names.add(report)
        expected_reports: set[str] = set()
        cursor = session.open_at + timedelta(minutes=15)
        final_publication = session.close_at + timedelta(minutes=15)
        while cursor <= final_publication:
            expected_reports.add(f"NordicEquity-posttrade-{cursor:%Y-%m-%dT%H%M}")
            cursor += timedelta(minutes=1)
        if report_names != expected_reports:
            raise MarketDataError("Nasdaq complete-session report set is incomplete")
        return session, frozenset(report_names)
