from datetime import UTC, date, datetime, timedelta
from zoneinfo import ZoneInfo

import pytest

from ai_stocks.calendar import TradingSession, session_for
from ai_stocks.collector import expected_reports, report_timestamp, reports_due

STOCKHOLM = ZoneInfo("Europe/Stockholm")
SESSION = session_for(date(2026, 8, 7))
assert isinstance(SESSION, TradingSession)


def test_report_timestamp_is_timezone_aware_and_strict():
    stamp = report_timestamp("NordicEquity-posttrade-2026-08-07T1015")
    assert stamp == datetime(2026, 8, 7, 10, 15, tzinfo=STOCKHOLM)
    with pytest.raises(ValueError):
        report_timestamp("bad.csv")


def test_only_reports_retrieved_inside_fifteen_to_twenty_minute_window_are_due():
    candidate = "NordicEquity-posttrade-2026-08-07T0915"
    late = "NordicEquity-posttrade-2026-08-07T0909"
    before_open = "NordicEquity-posttrade-2026-08-07T0714"
    now = datetime(2026, 8, 7, 7, 19, tzinfo=UTC)
    assert reports_due((candidate, late, before_open), SESSION, now) == (candidate,)


def test_expected_reports_cover_every_publication_minute():
    names = expected_reports(SESSION)
    expected_count = int((SESSION.close_at - SESSION.open_at) / timedelta(minutes=1)) + 1
    assert len(names) == expected_count
    assert report_timestamp(names[0]) == SESSION.open_at + timedelta(minutes=15)
    assert report_timestamp(names[-1]) == SESSION.close_at + timedelta(minutes=15)
