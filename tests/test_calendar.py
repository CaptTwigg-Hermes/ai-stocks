from datetime import date, datetime
from zoneinfo import ZoneInfo

from ai_stocks.calendar import (
    CLOSED_DATES_2026,
    HALF_DATES_2026,
    SessionKind,
    contest_final_session,
    next_full_session,
    session_for,
    six_run_times,
    verify_source_artifacts,
)

STOCKHOLM = ZoneInfo("Europe/Stockholm")


def test_pinned_artifacts_and_2026_equity_exceptions_match_official_sources():
    verify_source_artifacts()
    assert date(2026, 6, 19) in CLOSED_DATES_2026
    assert date(2026, 12, 31) in CLOSED_DATES_2026
    assert HALF_DATES_2026 == {
        date(2026, 1, 5),
        date(2026, 4, 2),
        date(2026, 4, 30),
        date(2026, 5, 13),
        date(2026, 10, 30),
    }


def test_full_and_half_sessions_generate_six_session_relative_runs():
    full = session_for(date(2026, 8, 6))
    assert full is not None
    assert full.kind is SessionKind.FULL
    assert full.open_at == datetime(2026, 8, 6, 9, 0, tzinfo=STOCKHOLM)
    assert full.close_at == datetime(2026, 8, 6, 17, 30, tzinfo=STOCKHOLM)
    assert six_run_times(full) == (
        datetime(2026, 8, 6, 8, 0, tzinfo=STOCKHOLM),
        datetime(2026, 8, 6, 10, 42, tzinfo=STOCKHOLM),
        datetime(2026, 8, 6, 12, 24, tzinfo=STOCKHOLM),
        datetime(2026, 8, 6, 14, 6, tzinfo=STOCKHOLM),
        datetime(2026, 8, 6, 15, 48, tzinfo=STOCKHOLM),
        datetime(2026, 8, 6, 18, 0, tzinfo=STOCKHOLM),
    )

    half = session_for(date(2026, 4, 30))
    assert half is not None
    assert half.kind is SessionKind.HALF
    assert half.close_at == datetime(2026, 4, 30, 13, 0, tzinfo=STOCKHOLM)
    assert six_run_times(half)[1:-1] == (
        datetime(2026, 4, 30, 9, 48, tzinfo=STOCKHOLM),
        datetime(2026, 4, 30, 10, 36, tzinfo=STOCKHOLM),
        datetime(2026, 4, 30, 11, 24, tzinfo=STOCKHOLM),
        datetime(2026, 4, 30, 12, 12, tzinfo=STOCKHOLM),
    )


def test_weekends_holidays_start_rule_and_final_session_fail_closed():
    assert session_for(date(2026, 8, 8)) is None
    assert session_for(date(2026, 6, 19)) is None
    assert next_full_session(date(2026, 4, 29)).day == date(2026, 5, 4)
    assert contest_final_session().day == date(2026, 12, 30)
    assert session_for(date(2027, 1, 4)) is None
