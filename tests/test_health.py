import pytest

from ai_stocks.health import HealthError, check_heartbeat, write_heartbeat


def test_recent_heartbeat_is_healthy(tmp_path):
    path = tmp_path / "worker.heartbeat"
    write_heartbeat(path, now=100.0)
    check_heartbeat(path, max_age=30, now=130.0)


def test_missing_or_stale_heartbeat_fails(tmp_path):
    path = tmp_path / "worker.heartbeat"
    with pytest.raises(HealthError, match="missing"):
        check_heartbeat(path, max_age=30, now=130.0)
    write_heartbeat(path, now=99.0)
    with pytest.raises(HealthError, match="stale"):
        check_heartbeat(path, max_age=30, now=130.0)
