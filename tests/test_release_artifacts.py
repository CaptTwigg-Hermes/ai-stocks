from pathlib import Path

import yaml

ROOT = Path(__file__).parents[1]
PINNED_HERMES = "226b095a59df0be88e195a90fbd209f236665b7b"


def test_dockge_compose_separates_and_hardens_services():
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    services = compose["services"]
    assert {"app", "collector", "worker"} <= set(services)
    assert services["app"]["ports"] == ["${APP_BIND_ADDRESS:-192.168.50.2}:${APP_PORT:-3232}:8080"]
    for name in ("app", "collector", "worker"):
        service = services[name]
        assert service["read_only"] is True
        assert service["cap_drop"] == ["ALL"]
        assert "no-new-privileges:true" in service["security_opt"]
        assert service["init"] is True
    assert "HERMES_HOME" not in services["app"]["environment"]
    repository = "${AISTOCKS_IMAGE_REPOSITORY:-ghcr.io/capttwigg-hermes/ai-stocks}"
    version = "${AISTOCKS_IMAGE_VERSION:-latest}"
    targets = {
        "app": "app",
        "collector": "collector",
        "worker": "worker",
        "reporter": "reporter",
        "migrate": "operations",
        "backup-scheduler": "backup-operations",
    }
    for name, target in targets.items():
        assert services[name]["image"] == f"{repository}:{target}-{version}"
        assert "build" not in services[name]
    assert services["collector"]["volumes"] == [
        "${NASDAQ_ARCHIVE_DIR:?set a UID-10001 writable Nasdaq archive dataset}:/data/nasdaq",
        "${MARKET_BOOTSTRAP_DIR:?set the reviewed FIRDS plan directory}:/run/market-bootstrap:ro",
        "${CORPORATE_ACTION_INPUT_DIR:?set the reviewed corporate-action input directory}:/run/corporate-actions:ro",
    ]
    collector_environment = services["collector"]["environment"]
    assert collector_environment["CORPORATE_ACTION_INPUT_PATH"] == "/run/corporate-actions"
    assert collector_environment["FIRDS_ACQUISITION_PLAN_PATH"] == "/run/market-bootstrap/firds-plan.json"
    assert not any(name.startswith("STATUS_SEED_") for name in collector_environment)
    assert "STATUS_PINNED_KEY_ID" not in collector_environment
    assert (
        "${NASDAQ_ARCHIVE_DIR:?set the Nasdaq archive dataset}:/data/nasdaq:ro"
        in services["worker"]["volumes"]
    )
    backup = services["backup-tools"]["environment"]
    assert "DATABASE_URL" not in backup
    assert backup["BACKUP_DATABASE_URL"].startswith("${BACKUP_DATABASE_URL:")
    expected_health = [
        "CMD", "curl", "--fail", "--silent", "--max-time", "2",
        "http://127.0.0.1:8080/healthz",
    ]
    assert services["app"]["healthcheck"]["test"] == expected_health
    assert services["worker"]["healthcheck"]["test"] == expected_health
    expected_health[-1] = "http://127.0.0.1:8080/readyz"
    assert services["collector"]["healthcheck"]["test"] == expected_health


def test_example_environment_renders_compose_with_fail_closed_proxy_configuration():
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    example = (ROOT / ".env.example").read_text()
    assert compose["services"]["app"]["environment"]["TRUSTED_PROXY_IPS"].startswith(
        "${TRUSTED_PROXY_IPS:?"
    )
    for name in ("TRUSTED_PROXY_IPS", "COLLECTOR_DATABASE_URL"):
        assert f"{name}=" in example
    assert "STATUS_PINNED_KEY_ID=" not in example


def test_backup_and_restore_handoff_exports_libpq_urls():
    example = (ROOT / ".env.example").read_text()
    readme = (ROOT / "README.md").read_text()
    backup = (ROOT / "scripts" / "backup.sh").read_text()
    assert "BACKUP_DATABASE_URL=postgresql://" in example
    assert 'export BACKUP_DATABASE_URL="$(grep' in readme
    assert 'export RESTORE_DATABASE_URL="$(grep' in readme
    assert "-e BACKUP_DATABASE_URL" in backup
    assert '--dbname="$BACKUP_DATABASE_URL"' in backup


def test_image_build_pins_hermes_source_and_frozen_lock():
    dockerfile = (ROOT / "Dockerfile").read_text()
    assert PINNED_HERMES in dockerfile
    assert "checkout --detach" in dockerfile
    assert "uv sync --frozen --no-dev --extra cli --extra web" in dockerfile
    assert "COPY --from=hermes-builder" in dockerfile
    assert "/opt/hermes /opt/hermes" in dockerfile


def test_github_publishes_every_dockge_image_target():
    workflow = yaml.safe_load((ROOT / ".github/workflows/publish-images.yml").read_text())
    assert workflow["permissions"] == {"contents": "read", "packages": "write"}
    targets = workflow["jobs"]["publish"]["strategy"]["matrix"]["target"]
    assert targets == ["app", "collector", "worker", "reporter", "operations", "backup-operations"]
    build = workflow["jobs"]["publish"]["steps"][-1]
    assert build["with"]["target"] == "${{ matrix.target }}"
    assert build["with"]["push"] is True
    assert "ghcr.io/capttwigg-hermes/ai-stocks:${{ matrix.target }}-latest" in build["with"]["tags"]


def test_release_gate_and_restore_fail_closed_with_scheduled_backup():
    verify = (ROOT / "scripts" / "verify-release.sh").read_text()
    restore = (ROOT / "scripts" / "restore-test.sh").read_text()
    cycle = (ROOT / "scripts" / "backup-cycle.sh").read_text()
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    assert "AISTOCKS_TEST_DATABASE_URL is required" in verify
    assert "restored migration checksums or contest invariants failed verification" in restore
    assert "120000" in restore
    assert "backup or restore verification failed" in cycle
    assert "backup-scheduler" in compose["services"]


def test_clean_start_documents_composed_reference_acquisition_not_preseed_importer_magic():
    readme = (ROOT / "README.md").read_text()
    program = (ROOT / "src" / "AiStocks.Collector" / "Program.cs").read_text()
    worker = (ROOT / "src" / "AiStocks.Collector" / "CollectorWorker.cs").read_text()
    assert "firds-plan.json" in readme
    assert "full" in readme and "delta" in readme
    assert "MarketReferenceAcquirer" in program
    assert "AcquireAsync" in worker
    assert "separately deployed archive-to-PostgreSQL importer" not in readme
