import hashlib
import json
from datetime import date, timedelta
from decimal import Decimal

from ai_stocks.calendar import session_for
from ai_stocks.market import SessionWindow
from ai_stocks.nasdaq_market import (
    InstrumentStatus,
    NasdaqMarketProvider,
    _Archive,
    _Observation,
)


def _provider(tmp_path):
    universe = {
        "artifact_version": 1,
        "provenance": {
            "source_url": "https://www.nasdaq.com/solutions/european-market-data/reference-data"
        },
        "instruments": [
            {
                "symbol": "ERIC-B",
                "isin": "SE0000108656",
                "order_book_id": "ERIC-B-XSTO",
                "venue": "XSTO",
                "currency": "SEK",
                "instrument_type": "COMMON_SHARE",
                "segment": "LARGE_CAP",
            }
        ],
    }
    body = json.dumps(universe, sort_keys=True).encode()
    universe_path = tmp_path / "universe.json"
    universe_path.write_bytes(body)
    provider = NasdaqMarketProvider(
        archive_dir=tmp_path / "archive",
        universe_path=universe_path,
        universe_sha256=hashlib.sha256(body).hexdigest(),
        statuses={"ERIC-B": InstrumentStatus(verified=True, warning=False, suspended=False)},
    )
    return provider


def _observation(provider, executed_at, *, delay_minutes=15, transaction_id="tx-1"):
    instrument = provider.instruments["ERIC-B"]
    observed_at = executed_at + timedelta(minutes=delay_minutes)
    return _Observation(
        instrument=instrument,
        price=Decimal("100"),
        quantity=10,
        executed_at=executed_at,
        published_at=observed_at,
        fetched_at=observed_at,
        transaction_id=transaction_id,
        report="NordicEquity-posttrade-2026-08-07T0915",
        checksum="a" * 64,
        execution_eligible=True,
    )


def _window(day):
    session = session_for(day)
    assert session is not None
    return SessionWindow(session.open_at, session.close_at, f"XSTO-{day.isoformat()}")


def test_first_quote_is_visible_exactly_when_retrieved_at_fifteen_minutes(tmp_path, monkeypatch):
    provider = _provider(tmp_path)
    session = _window(date(2026, 8, 7))
    observation = _observation(provider, session.open_at)
    monkeypatch.setattr(provider, "_read_archive", lambda: _Archive((observation,), {}))

    quote = provider.first_quote_at_or_after(
        "ERIC-B", session.open_at, as_of=observation.fetched_at
    )

    assert quote is not None
    assert quote.retrieved_at == observation.fetched_at
    assert quote.session_id == session.session_id


def test_complete_zero_trade_session_counts_as_zero_adv_history_day(tmp_path, monkeypatch):
    provider = _provider(tmp_path)
    quote_session = _window(date(2026, 8, 7))
    history_session = _window(date(2026, 8, 6))
    manifests = provider.archive_dir / "sessions"
    manifests.mkdir(parents=True)
    manifest = manifests / f"{history_session.session_id}.json"
    manifest.write_text("{}")
    monkeypatch.setattr(
        provider,
        "_verify_manifest",
        lambda path, archive: (history_session, frozenset()),
    )

    values = provider._history_values(
        provider.instruments["ERIC-B"],
        quote_session,
        _Archive((), {}),
        before=quote_session.open_at,
    )

    assert values == [Decimal("0")]


def test_full_and_half_day_session_boundaries_are_exact():
    full = session_for(date(2026, 8, 7))
    half = session_for(date(2026, 10, 30))
    assert full is not None and half is not None

    def expected_names(session):
        cursor = session.open_at + timedelta(minutes=15)
        end = session.close_at + timedelta(minutes=15)
        names = []
        while cursor <= end:
            names.append(f"NordicEquity-posttrade-{cursor:%Y-%m-%dT%H%M}")
            cursor += timedelta(minutes=1)
        return names

    full_names = expected_names(full)
    half_names = expected_names(half)
    assert (len(full_names), full_names[0], full_names[-1]) == (
        511,
        "NordicEquity-posttrade-2026-08-07T0915",
        "NordicEquity-posttrade-2026-08-07T1745",
    )
    assert (len(half_names), half_names[0], half_names[-1]) == (
        241,
        "NordicEquity-posttrade-2026-10-30T0915",
        "NordicEquity-posttrade-2026-10-30T1315",
    )
