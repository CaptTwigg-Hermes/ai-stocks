import json
import subprocess
import sys
from pathlib import Path


def test_executable_negative_capability_inventory_passes():
    root = Path(__file__).parents[1]
    result = subprocess.run(  # noqa: S603  # nosec B603 - fixed repository command
        [sys.executable, str(root / "scripts" / "prove_no_broker.py")],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
        timeout=20,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    proof = json.loads(result.stdout)
    assert proof["ok"] is True
    assert proof["findings"] == []
    assert proof["routes"] == [
        {"method": "GET", "path": "/"},
        {"method": "POST", "path": "/admin/pause"},
        {"method": "POST", "path": "/admin/pre-start-reset"},
        {"method": "POST", "path": "/admin/resume"},
        {"method": "POST", "path": "/admin/start"},
        {"method": "GET", "path": "/api/audit"},
        {"method": "GET", "path": "/api/dashboard"},
        {"method": "GET", "path": "/api/dividends"},
        {"method": "GET", "path": "/api/evidence"},
        {"method": "GET", "path": "/api/failures"},
        {"method": "GET", "path": "/api/fees"},
        {"method": "GET", "path": "/api/leaderboard"},
        {"method": "GET", "path": "/api/portfolios"},
        {"method": "GET", "path": "/api/queued-orders"},
        {"method": "GET", "path": "/healthz"},
        {"method": "GET", "path": "/readyz"},
    ]
