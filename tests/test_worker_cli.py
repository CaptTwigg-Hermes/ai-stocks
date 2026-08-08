import hashlib
import json
from datetime import UTC, datetime, timedelta

import pytest

from ai_stocks.worker_cli import StatusArtifactError, load_statuses

NOW = datetime(2026, 8, 7, 8, tzinfo=UTC)


def _artifacts(tmp_path):
    universe = {
        "artifact_version": 1,
        "instruments": [{"symbol": "INVE B"}],
    }
    universe_path = tmp_path / "universe.json"
    universe_path.write_text(json.dumps(universe))
    status_path = tmp_path / "status.json"
    payload = {
        "schema_version": 1,
        "universe_sha256": hashlib.sha256(universe_path.read_bytes()).hexdigest(),
        "verified_at": NOW.isoformat(),
        "source_urls": ["https://www.nasdaq.com/european-market-activity/shares"],
        "source_sha256": ["a" * 64],
        "statuses": {"INVE B": {"verified": True, "warning": False, "suspended": False}},
    }
    status_path.write_text(json.dumps(payload))
    return universe_path, status_path, payload


def test_valid_fresh_complete_status_artifact_is_loaded(tmp_path):
    universe, status, _ = _artifacts(tmp_path)
    result = load_statuses(status, universe, now=NOW)
    assert result["INVE B"].verified is True
    assert result["INVE B"].warning is False


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda p: p.update(verified_at=(NOW - timedelta(minutes=21)).isoformat()), "stale"),
        (lambda p: p.update(universe_sha256="0" * 64), "checksum"),
        (lambda p: p.update(source_urls=["https://example.com/status"]), "official Nasdaq"),
        (lambda p: p.update(statuses={}), "completely cover"),
        (
            lambda p: p["statuses"]["INVE B"].update(warning="false"),
            "must be boolean",
        ),
        (
            lambda p: p["statuses"]["INVE B"].update(verified=False),
            "unresolved",
        ),
    ],
)
def test_status_artifact_failures_are_closed(tmp_path, mutation, message):
    universe, status, payload = _artifacts(tmp_path)
    mutation(payload)
    status.write_text(json.dumps(payload))
    with pytest.raises(StatusArtifactError, match=message):
        load_statuses(status, universe, now=NOW)
