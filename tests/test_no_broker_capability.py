import importlib.util
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
    assert proof["resolved_lock_packages"]
    assert proof["configuration_reads"]
    assert proof["environment_reads"]
    assert proof["dynamic_configuration_reads"] == []
    assert proof["order_path_denial_probe"]["ok"] is True
    assert proof["order_path_denial_probe"]["executed"] is True
    assert proof["order_path_denial_probe"]["paper_order_count"] == 1
    assert proof["order_path_denial_probe"]["network_events"] == []
    assert {table["executable"] for table in proof["endpoint_tables"]} == {
        "AiStocks.Api", "AiStocks.Collector", "AiStocks.Exhibition.Worker", "AiStocks.Ui", "AiStocks.Web", "AiStocks.Worker"
    }
    production_api = next(
        table for table in proof["endpoint_tables"]
        if table["executable"] == "AiStocks.Api" and table["environment"] == "Production"
    )
    preview_api = next(
        table for table in proof["endpoint_tables"]
        if table["executable"] == "AiStocks.Api" and table["environment"] == "Testing"
    )
    assert not any(route["method"] != "GET" for route in production_api["routes"])
    assert {"method": "POST", "path": "/api/v1/orders"} in preview_api["routes"]
    assert proof["simulated_mutation_routes"] == [
        {"environment": "Testing", "executable": "AiStocks.Api", "method": "POST", "path": "/api/v1/orders"}
    ]
    assert proof["routes"] == [
        {"method": "GET", "path": "/"},
        {"method": "GET", "path": "/__endpoint-inventory"},
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
        {"method": "GET", "path": "/api/v1/instruments"},
        {"method": "GET", "path": "/api/v1/leaderboard"},
        {"method": "GET", "path": "/api/v1/me"},
        {"method": "GET", "path": "/api/v1/orders"},
        {"method": "POST", "path": "/api/v1/orders"},
        {"method": "GET", "path": "/api/v1/portfolio"},
        {"method": "GET", "path": "/healthz"},
        {"method": "GET", "path": "/readyz"},
        {"method": "GET", "path": "/runtime-config.js"},
        {"method": "GET", "path": "/trade"},
        {"method": "GET", "path": "/{*path:nonfile}"},
        {"method": "HEAD", "path": "/{*path:nonfile}"},
    ]


def test_inventory_fails_closed_on_unseen_dynamic_configuration_name():
    root = Path(__file__).parents[1]
    spec = importlib.util.spec_from_file_location("prove_no_broker", root / "scripts" / "prove_no_broker.py")
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    reads = module.configuration_reads(
        'var environment = PostgresConfiguration.Environment();\n'
        'var known = builder.Configuration["DATABASE_URL"];\n'
        'var unknown = builder.Configuration[name];\n',
        "sentinel.cs",
    )

    assert [item["name"] for item in reads["environment"]] == ["*"]
    assert [item["name"] for item in reads["configuration"]] == ["DATABASE_URL"]
    assert reads["dynamic"] == [{"file": "sentinel.cs", "line": 3, "expression": "name"}]
