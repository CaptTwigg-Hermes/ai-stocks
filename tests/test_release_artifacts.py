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
    assert services["app"]["build"]["target"] == "app"
    assert services["collector"]["build"]["target"] == "collector"
    assert services["worker"]["build"]["target"] == "worker"
    assert services["collector"]["volumes"] == [
        "${NASDAQ_ARCHIVE_DIR:?set a UID-10001 writable Nasdaq archive dataset}:/data/nasdaq"
    ]
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
