import os
import subprocess
from pathlib import Path

import yaml
import pytest

ROOT = Path(__file__).parents[1]
PINNED_HERMES = "226b095a59df0be88e195a90fbd209f236665b7b"


def test_dockge_compose_separates_and_hardens_services():
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    services = compose["services"]
    assert {
        "api", "ui", "exhibition", "preview-collector", "app", "collector", "warmup-collector", "worker"
    } <= set(services)
    assert services["app"]["ports"] == ["${APP_BIND_ADDRESS:-192.168.50.2}:${APP_PORT:-3232}:8080"]
    assert services["ui"]["ports"] == ["${APP_BIND_ADDRESS:-192.168.50.2}:${APP_PORT:-3232}:8080"]
    assert services["api"]["ports"] == ["${API_BIND_ADDRESS:-192.168.50.2}:${API_PORT:-3233}:8080"]
    assert services["app"]["profiles"] == ["contest"]
    assert services["api"]["profiles"] == ["preview"]
    assert services["ui"]["profiles"] == ["preview"]
    assert services["exhibition"]["profiles"] == ["preview"]
    assert services["preview-collector"]["profiles"] == ["preview"]
    for name in ("collector", "worker", "reporter"):
        assert services[name]["profiles"] == ["contest"]
    assert services["warmup-collector"]["profiles"] == ["warmup"]
    assert services["contest-guard"]["profiles"] == ["contest"]
    assert services["preview-guard"]["profiles"] == ["preview"]
    assert all("profiles" in service for service in services.values())
    for name in ("app", "collector", "worker", "reporter"):
        assert services[name]["depends_on"]["contest-guard"]["condition"] == "service_completed_successfully"
    for name in ("api", "ui", "exhibition"):
        assert services[name]["depends_on"]["preview-guard"]["condition"] == "service_completed_successfully"
    assert services["api"]["environment"]["PREVIEW_MODE"] == "1"
    assert services["api"]["environment"]["ASPNETCORE_ENVIRONMENT"] == "Development"
    assert services["api"]["depends_on"]["preview-collector"]["condition"] == "service_healthy"
    assert services["api"]["environment"]["AI_EXHIBITION_UNIVERSE"] == "${AI_EXHIBITION_UNIVERSE:-stockholm}"
    for name in ("api", "ui", "exhibition", "preview-collector", "app", "collector", "warmup-collector", "worker"):
        service = services[name]
        assert service["read_only"] is True
        assert service["cap_drop"] == ["ALL"]
        assert "no-new-privileges:true" in service["security_opt"]
        assert service["init"] is True
    assert "DATABASE_URL" in services["app"]["environment"]
    assert "DATABASE_URL" not in services["ui"]["environment"]
    assert "HERMES_HOME" not in services["ui"]["environment"]
    repository = "${AISTOCKS_IMAGE_REPOSITORY:-ghcr.io/capttwigg-hermes/ai-stocks}"
    version = "${AISTOCKS_IMAGE_VERSION:-latest}"
    targets = {
        "api": "api",
        "app": "app",
        "ui": "ui",
        "exhibition": "exhibition",
        "collector": "collector",
        "warmup-collector": "collector",
        "preview-collector": "collector",
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
    assert services["preview-collector"]["volumes"] == services["collector"]["volumes"]
    collector_environment = services["collector"]["environment"]
    assert "COLLECT_NORDIC_EXHIBITION" not in collector_environment
    assert "COLLECT_NORDIC_EXHIBITION" not in services["warmup-collector"]["environment"]
    assert services["preview-collector"]["environment"]["COLLECT_NORDIC_EXHIBITION"] == "1"
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
    assert services["api"]["healthcheck"]["test"] == expected_health
    assert services["ui"]["healthcheck"]["test"] == expected_health
    assert services["app"]["healthcheck"]["test"] == expected_health
    assert services["worker"]["healthcheck"]["test"] == expected_health
    assert services["exhibition"]["healthcheck"]["test"] == expected_health
    expected_health[-1] = "http://127.0.0.1:8080/readyz"
    assert services["collector"]["healthcheck"]["test"] == expected_health
    assert services["warmup-collector"]["healthcheck"]["test"] == expected_health
    expected_health[-1] = "http://127.0.0.1:8080/healthz"
    assert services["preview-collector"]["healthcheck"]["test"] == expected_health


def test_compose_mode_preflight_rejects_running_opposite_profile(tmp_path):
    fake = tmp_path / "compose"
    fake.write_text("#!/bin/sh\nif [ \"$1\" = ps ]; then printf 'api\\n'; exit 0; fi\nprintf '%s\\n' \"$*\"\n")
    fake.chmod(0o755)
    env = os.environ | {"COMPOSE": str(fake)}

    result = subprocess.run(
        [ROOT / "scripts/compose-mode.sh", "contest", "up", "-d"],
        cwd=ROOT,
        env=env,
        text=True,
        capture_output=True,
    )

    assert result.returncode != 0
    assert "preview runtime is still active" in result.stderr
    assert "--profile contest up" not in result.stdout


@pytest.mark.parametrize(
    ("mode", "running", "label"),
    [
        ("contest", "preview-collector", "preview"),
        ("preview", "warmup-collector", "warmup"),
        ("warmup", "preview-collector", "preview"),
    ],
)
def test_compose_mode_rejects_concurrent_archive_writers(tmp_path, mode, running, label):
    fake = tmp_path / "docker-compose"
    fake.write_text(
        "#!/bin/sh\n"
        "if [ \"$1\" = \"ps\" ]; then\n"
        f"  printf '%s\\n' {running}\n"
        "  exit 0\n"
        "fi\n"
        "printf '%s\\n' \"$*\"\n"
    )
    fake.chmod(0o755)
    env = os.environ | {
        "COMPOSE": str(fake),
        "CONTEST_DATABASE_URL": "postgresql://contest",
        "COLLECTOR_DATABASE_URL": "postgresql://collector",
    }

    result = subprocess.run(
        [str(ROOT / "scripts" / "compose-mode.sh"), mode, "up", "-d"],
        cwd=ROOT, env=env, text=True, capture_output=True,
    )

    assert result.returncode != 0
    assert f"{label} runtime is still active" in result.stderr


def test_compose_mode_rejects_global_options_before_action(tmp_path):
    fake = tmp_path / "compose"
    log = tmp_path / "invoked"
    fake.write_text("#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$COMPOSE_LOG\"\n")
    fake.chmod(0o755)
    env = os.environ | {"COMPOSE": str(fake), "COMPOSE_LOG": str(log)}

    for arguments in (
        ("--ansi", "never", "up", "-d"),
        ("-f", "other-compose.yaml", "start"),
        ("--project-name", "other", "restart"),
    ):
        result = subprocess.run(
            [ROOT / "scripts/compose-mode.sh", "preview", *arguments],
            cwd=ROOT,
            env=env,
            text=True,
            capture_output=True,
        )

        assert result.returncode != 0, (arguments, result)
        assert "Compose global options are forbidden" in result.stderr
    assert not log.exists()


def test_compose_mode_preflight_fails_closed_when_status_is_unknown(tmp_path):
    fake = tmp_path / "compose"
    log = tmp_path / "started"
    fake.write_text(
        "#!/bin/sh\n"
        "if [ \"$1\" = ps ]; then printf 'status unavailable\\n' >&2; exit 42; fi\n"
        "printf '%s\\n' \"$*\" >> \"$START_LOG\"\n"
    )
    fake.chmod(0o755)
    env = os.environ | {"COMPOSE": str(fake), "START_LOG": str(log)}

    for mode in ("contest", "preview", "warmup"):
        for action in ("up", "start", "restart"):
            result = subprocess.run(
                [ROOT / "scripts/compose-mode.sh", mode, action],
                cwd=ROOT,
                env=env,
                text=True,
                capture_output=True,
            )

            assert result.returncode != 0, (mode, action, result)
            assert "could not determine active runtime services" in result.stderr
    assert not log.exists()


def test_compose_mode_preflight_launches_exactly_one_runtime_profile(tmp_path):
    fake = tmp_path / "compose"
    fake.write_text("#!/bin/sh\nif [ \"$1\" = ps ]; then exit 0; fi\nprintf '%s|%s\\n' \"$AISTOCKS_DEPLOYMENT_PROFILE\" \"$*\"\n")
    fake.chmod(0o755)
    env = os.environ | {"COMPOSE": str(fake)}

    result = subprocess.run(
        [ROOT / "scripts/compose-mode.sh", "preview", "up", "-d", "api", "ui"],
        cwd=ROOT,
        env=env,
        text=True,
        capture_output=True,
        check=True,
    )

    assert result.stdout.strip() == "preview|--profile preview up -d api ui"


def test_ai_exhibition_and_market_warmup_have_explicit_isolated_services():
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    services = compose["services"]

    exhibition = services["exhibition"]
    assert exhibition["profiles"] == ["preview"]
    assert exhibition["depends_on"]["preview-guard"]["condition"] == "service_completed_successfully"
    assert exhibition["depends_on"]["api"]["condition"] == "service_healthy"
    assert exhibition["environment"]["AI_EXHIBITION_API_ORIGIN"] == "http://api:8080"
    assert exhibition["environment"]["AI_EXHIBITION_KEY"].startswith("${AI_EXHIBITION_KEY:?")
    assert exhibition["environment"]["HERMES_CREDENTIAL_FILE"] == "/run/hermes-credentials/copilot.env"
    assert exhibition["volumes"] == [
        "${HERMES_COPILOT_ENV_FILE:?set a mode-0600 Copilot-only Hermes env file}:/run/hermes-credentials/copilot.env:ro"
    ]
    assert services["api"]["environment"]["AI_EXHIBITION_MODE"] == "1"
    assert services["api"]["environment"]["AI_EXHIBITION_KEY"].startswith("${AI_EXHIBITION_KEY:?")
    assert services["api"]["environment"]["AI_EXHIBITION_ARCHIVE_PATH"] == "/data/nasdaq"
    assert "${NASDAQ_ARCHIVE_DIR:?set the Nasdaq archive dataset}:/data/nasdaq:ro" in services["api"]["volumes"]

    assert services["warmup-guard"]["profiles"] == ["warmup"]
    warmup = services["warmup-collector"]
    assert warmup["profiles"] == ["warmup"]
    assert warmup["depends_on"]["warmup-guard"]["condition"] == "service_completed_successfully"
    assert warmup["image"] == services["collector"]["image"]
    assert warmup["environment"] == services["collector"]["environment"]
    assert warmup["volumes"] == services["collector"]["volumes"]


def test_compose_mode_allows_warmup_with_preview_but_not_with_contest_collector(tmp_path):
    fake = tmp_path / "compose"
    fake.write_text(
        "#!/bin/sh\n"
        "if [ \"$1\" = ps ]; then printf '%s\\n' \"$RUNNING_SERVICES\"; exit 0; fi\n"
        "printf '%s|%s\\n' \"$AISTOCKS_DEPLOYMENT_PROFILE\" \"$*\"\n"
    )
    fake.chmod(0o755)

    preview_running = os.environ | {
        "COMPOSE": str(fake),
        "RUNNING_SERVICES": "api\nui",
    }
    result = subprocess.run(
        [ROOT / "scripts/compose-mode.sh", "warmup", "up", "-d", "warmup-collector"],
        cwd=ROOT,
        env=preview_running,
        text=True,
        capture_output=True,
    )
    assert result.returncode == 0, result
    assert result.stdout.strip() == "warmup|--profile warmup up -d warmup-collector"

    contest_collector_running = preview_running | {"RUNNING_SERVICES": "collector"}
    result = subprocess.run(
        [ROOT / "scripts/compose-mode.sh", "warmup", "up", "-d", "warmup-collector"],
        cwd=ROOT,
        env=contest_collector_running,
        text=True,
        capture_output=True,
    )
    assert result.returncode != 0
    assert "contest collector is still active" in result.stderr

    warmup_running = preview_running | {"RUNNING_SERVICES": "warmup-collector"}
    result = subprocess.run(
        [ROOT / "scripts/compose-mode.sh", "contest", "up", "-d", "collector"],
        cwd=ROOT,
        env=warmup_running,
        text=True,
        capture_output=True,
    )
    assert result.returncode != 0
    assert "warmup collector is still active" in result.stderr


def test_example_environment_renders_fail_closed_access_and_separate_preview_routing():
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    example = (ROOT / ".env.example").read_text()
    app_environment = compose["services"]["app"]["environment"]
    assert app_environment["ACCESS_TEAM_DOMAIN"].startswith("${ACCESS_TEAM_DOMAIN:?")
    assert compose["services"]["api"]["environment"]["UI_ORIGINS"].startswith("${PREVIEW_UI_ORIGINS:-")
    assert compose["services"]["ui"]["environment"]["API_PUBLIC_ORIGIN"].startswith("${PREVIEW_API_ORIGIN:-")
    for name in ("TRUSTED_PROXY_IPS", "COLLECTOR_DATABASE_URL", "PREVIEW_API_ORIGIN", "PREVIEW_UI_ORIGINS"):
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


def test_copilot_credential_export_is_minimal_and_private(tmp_path):
    source = tmp_path / "hermes.env"
    destination = tmp_path / "secrets" / "copilot.env"
    source.write_text(
        "COPILOT_GITHUB_TOKEN=primary-secret\n"
        "GH_TOKEN=secondary-secret\n"
        "OPENAI_API_KEY=must-not-copy\n"
        "UNRELATED=value\n"
    )

    result = subprocess.run(
        ["python3", ROOT / "scripts/export-copilot-env.py", source, destination],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=True,
    )

    assert destination.read_text() == "COPILOT_GITHUB_TOKEN=primary-secret\n"
    assert destination.stat().st_mode & 0o777 == 0o600
    assert "secret" not in result.stdout
    assert "secret" not in result.stderr
    replay = subprocess.run(
        ["python3", ROOT / "scripts/export-copilot-env.py", source, destination],
        cwd=ROOT,
        text=True,
        capture_output=True,
    )
    assert replay.returncode != 0
    assert destination.read_text().endswith("primary-secret\n")


def test_image_build_pins_hermes_source_and_frozen_lock():
    dockerfile = (ROOT / "Dockerfile").read_text()
    assert PINNED_HERMES in dockerfile
    assert "checkout --detach" in dockerfile
    assert "uv sync --frozen --no-dev --extra cli --extra web" in dockerfile
    assert "COPY --from=hermes-builder" in dockerfile
    assert "/opt/hermes /opt/hermes" in dockerfile
    assert "apt-get install -y --no-install-recommends ca-certificates curl libicu74" in dockerfile
    assert dockerfile.count("apt-get install -y --no-install-recommends ca-certificates curl libicu72") == 1
    assert "ghcr.io/astral-sh/uv:0.12.3@sha256:2d890623d310b57771ce840f0da5eed5fc6d657da05ffaa45d82797b53fa3abc" in dockerfile
    assert "COPY docs/nasdaq-trading-hours.html docs/nasdaq-holiday-schedule-2026.xlsx ./docs/" in dockerfile
    assert "groupadd --system --gid 10001 app" not in dockerfile
    assert dockerfile.count("groupadd --system --gid 10001 aistocks") == 2
    assert "USER aistocks" in dockerfile


def test_exhibition_image_contains_narrow_public_https_mcp_fetcher():
    dockerfile = (ROOT / "Dockerfile").read_text()
    assert '"mcp==1.28.1"' in dockerfile
    assert "public_https_fetch_mcp.py" in dockerfile
    runner = (ROOT / "src/AiStocks.Research/Execution/HermesResearchRunner.cs").read_text()
    assert '"web,research" : "web"' in runner
    assert '"web,terminal"' not in runner


def test_github_publishes_every_dockge_image_target():
    workflow = yaml.safe_load((ROOT / ".github/workflows/publish-images.yml").read_text())
    assert workflow["permissions"] == {"contents": "read", "packages": "write"}
    targets = workflow["jobs"]["publish"]["strategy"]["matrix"]["target"]
    assert workflow["jobs"]["publish"]["needs"] == "verify"
    verify_steps = "\n".join(step.get("run", "") for step in workflow["jobs"]["verify"]["steps"])
    verify_job = workflow["jobs"]["verify"]
    assert verify_job["services"]["postgres"]["image"].startswith("postgres:17")
    assert "AISTOCKS_TEST_DATABASE_URL" in verify_job["env"]
    assert "TEST_POSTGRES_URL" in verify_job["env"]
    assert "dotnet test AiStocks.slnx -c Release --no-build --no-restore -m:1" in verify_steps
    assert "tests/postgres/bootstrap.sql" in verify_steps
    bootstrap = (ROOT / "tests/postgres/bootstrap.sql").read_text()
    for role in ("ai_stocks_worker", "ai_stocks_operations", "ai_stocks_web"):
        assert role in bootstrap
    assert "uv run pytest -q" in verify_steps
    assert "uv run python scripts/prove_no_broker.py" in verify_steps
    setup_index = next(
        index for index, step in enumerate(verify_job["steps"])
        if step.get("uses", "").startswith("actions/setup-dotnet@")
    )
    negative_capability_index = next(
        index for index, step in enumerate(verify_job["steps"])
        if "uv run python scripts/prove_no_broker.py" in step.get("run", "")
    )
    setup = verify_job["steps"][setup_index]
    assert setup["uses"] == "actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1"
    assert setup["with"]["dotnet-version"] == "10.0.x"
    assert setup_index < negative_capability_index
    assert targets == ["api", "ui", "exhibition", "search", "app", "collector", "worker", "reporter", "operations", "backup-operations"]
    build = workflow["jobs"]["publish"]["steps"][-1]
    assert build["with"]["target"] == "${{ matrix.target }}"
    assert build["with"]["push"] is True
    assert "ghcr.io/capttwigg-hermes/ai-stocks:${{ matrix.target }}-latest" in build["with"]["tags"]


def test_preview_exhibition_has_internal_pinned_search_service():
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    search = compose["services"]["search"]
    exhibition = compose["services"]["exhibition"]
    assert search["profiles"] == ["preview"]
    assert search["image"].endswith(":search-${AISTOCKS_IMAGE_VERSION:-latest}")
    assert "ports" not in search
    assert search["read_only"] is True
    assert search["cap_drop"] == ["ALL"]
    assert search["user"] == "977:977"
    assert all("uid=977" in mount and "gid=977" in mount for mount in search["tmpfs"])
    assert exhibition["depends_on"]["search"]["condition"] == "service_healthy"
    assert exhibition["environment"]["SEARXNG_URL"] == "http://search:8080"
    settings = yaml.safe_load((ROOT / "deploy/searxng/settings.yml").read_text())
    assert "json" in settings["search"]["formats"]
    assert settings["use_default_settings"]["engines"]["keep_only"] == ["bing news"]
    engines = {engine["name"]: engine for engine in settings["engines"]}
    assert engines == {
        "bing news": {
            "name": "bing news",
            "disabled": False,
            "categories": ["general"],
        }
    }
    dockerfile = (ROOT / "Dockerfile").read_text()
    assert "searxng/searxng@sha256:3aed6b4bfb4e6a2b3b94c890dafb3f35cb2588493b83818434aa260e8bdeb4d4 AS search" in dockerfile
    assert "COPY --chown=977:977 deploy/searxng/settings.yml /opt/ai-stocks-searxng/settings.yml" in dockerfile
    assert "ENV SEARXNG_SETTINGS_PATH=/opt/ai-stocks-searxng/settings.yml" in dockerfile
    assert "COPY --chown=977:977 deploy/searxng/settings.yml /etc/searxng/settings.yml" not in dockerfile
    assert "USER 977:977" in dockerfile
    workflow = (ROOT / ".github/workflows/publish-images.yml").read_text()
    assert "docker image inspect ai-stocks-search-verification" in workflow


def test_release_gate_and_restore_fail_closed_with_scheduled_backup():
    verify = (ROOT / "scripts" / "verify-release.sh").read_text()
    readme = (ROOT / "README.md").read_text()
    restore = (ROOT / "scripts" / "restore-test.sh").read_text()
    cycle = (ROOT / "scripts" / "backup-cycle.sh").read_text()
    compose = yaml.safe_load((ROOT / "compose.yaml").read_text())
    assert "AISTOCKS_TEST_DATABASE_URL is required" in verify
    assert '\"$DOTNET\" test AiStocks.slnx -c Release --no-build --no-restore -m:1' in verify
    assert "TEST_POSTGRES_URL is required" in verify
    assert "/workspace/house-consensus" not in verify
    assert "uv run pytest -q" in verify
    assert "uv run python scripts/prove_no_broker.py" in verify
    assert "scripts/compose-mode.sh contest" in readme
    assert "scripts/compose-mode.sh preview" in readme
    assert "contest stop app worker collector reporter" in readme
    assert "scripts/compose-mode.sh contest config -q" in verify
    assert "scripts/compose-mode.sh preview config -q" in verify
    assert "scripts/compose-mode.sh warmup config -q" in verify
    assert "volatile" in readme.lower() and "delayed post-trade" in readme.lower()
    collector_program = (ROOT / "src" / "AiStocks.Collector" / "Program.cs").read_text()
    assert 'AddHttpClient("ecb-fx"' in collector_program
    assert "HttpClientHandler { AllowAutoRedirect = false }" in collector_program
    assert "ConfigurePrimaryHttpMessageHandler(MarketReferenceAcquirer.CreatePrimaryHandler)" in collector_program
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
