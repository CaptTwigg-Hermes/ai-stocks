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
        {"method": "POST", "path": "/admin/reset"},
        {"method": "POST", "path": "/admin/resume"},
        {"method": "POST", "path": "/admin/start"},
        {"method": "GET", "path": "/api/portfolio/{agent_id}"},
        {"method": "GET", "path": "/healthz"},
    ]
