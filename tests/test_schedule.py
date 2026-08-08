from datetime import date, timedelta

from ai_stocks.calendar import session_for, six_run_times
from ai_stocks.schedule import FIXED_MODELS, RunWindowState, build_run_windows


def test_builds_24_fixed_isolated_windows_from_official_session():
    session = session_for(date(2026, 8, 6))
    assert session is not None
    windows = build_run_windows(session)

    assert len(windows) == 24
    assert {window.model_id for window in windows} == set(FIXED_MODELS)
    for model_id in FIXED_MODELS:
        own = [window for window in windows if window.model_id == model_id]
        assert len(own) == 6
        assert tuple(window.scheduled_at for window in own) == six_run_times(session)
        assert len({window.agent_id for window in own}) == 1
    assert len({window.id for window in windows}) == 24


def test_retry_window_expires_after_15_minutes_and_never_replays():
    session = session_for(date(2026, 8, 6))
    assert session is not None
    window = build_run_windows(session)[0]

    assert (
        window.state_at(window.scheduled_at - timedelta(microseconds=1)) is RunWindowState.PENDING
    )
    assert window.state_at(window.scheduled_at) is RunWindowState.RUNNABLE
    assert window.state_at(window.deadline_at) is RunWindowState.RUNNABLE
    assert window.state_at(window.deadline_at + timedelta(microseconds=1)) is RunWindowState.EXPIRED
    assert window.deadline_at == window.scheduled_at + timedelta(minutes=15)


def test_window_identity_is_deterministic_but_session_specific():
    one = session_for(date(2026, 8, 6))
    two = session_for(date(2026, 8, 7))
    assert one is not None and two is not None
    first = build_run_windows(one)
    replay = build_run_windows(one)
    next_day = build_run_windows(two)

    assert [window.id for window in first] == [window.id for window in replay]
    assert set(window.id for window in first).isdisjoint(window.id for window in next_day)
