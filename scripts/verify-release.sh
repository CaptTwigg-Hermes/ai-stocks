#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

if [[ -z "${DOTNET:-}" ]]; then
  if command -v dotnet >/dev/null 2>&1; then DOTNET=$(command -v dotnet)
  elif [[ -x /opt/data/dotnet/dotnet ]]; then DOTNET=/opt/data/dotnet/dotnet
  else printf 'verify-release: dotnet is required\n' >&2; exit 1
  fi
fi
DOTNET_ROOT=${DOTNET_ROOT:-$(dirname -- "$DOTNET")}
if [[ -n "${COMPOSE:-}" ]]; then read -r -a COMPOSE_CMD <<<"$COMPOSE"
elif docker compose version >/dev/null 2>&1; then COMPOSE_CMD=(docker compose)
elif [[ -x /opt/data/.tools/docker-compose ]]; then COMPOSE_CMD=(/opt/data/.tools/docker-compose)
else printf 'verify-release: Docker Compose is required\n' >&2; exit 1
fi

[[ -n "${AISTOCKS_TEST_DATABASE_URL:-}" ]] || {
  printf 'verify-release: AISTOCKS_TEST_DATABASE_URL is required and must name a disposable test database\n' >&2
  exit 1
}
case "$AISTOCKS_TEST_DATABASE_URL" in
  *[Dd]atabase=*test*|*/*test*|*/*_test*) ;;
  *) printf 'verify-release: refusing non-test AISTOCKS_TEST_DATABASE_URL\n' >&2; exit 1 ;;
esac
[[ -n "${TEST_POSTGRES_URL:-}" ]] || {
  printf 'verify-release: TEST_POSTGRES_URL is required and must name a disposable test database\n' >&2
  exit 1
}
case "$TEST_POSTGRES_URL" in
  *://*/*test*) ;;
  *) printf 'verify-release: refusing non-test TEST_POSTGRES_URL\n' >&2; exit 1 ;;
esac

export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export LD_LIBRARY_PATH="$DOTNET_ROOT${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-1}
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

"$DOTNET" restore AiStocks.slnx --locked-mode
"$DOTNET" format AiStocks.slnx --verify-no-changes --no-restore
"$DOTNET" build AiStocks.slnx -c Release --no-restore
"$DOTNET" test AiStocks.slnx -c Release --no-build --no-restore \
  --logger 'console;verbosity=minimal'
command -v uv >/dev/null 2>&1 || { printf 'verify-release: uv is required\n' >&2; exit 1; }
uv run pytest -q
uv run python scripts/prove_no_broker.py

export AISTOCKS_DEPLOYMENT_PROFILE=contest
export WEB_DATABASE_URL='Host=db;Database=ai_stocks;Username=web;Password=x'
export COLLECTOR_DATABASE_URL='Host=db;Database=ai_stocks;Username=collector;Password=x'
export WORKER_DATABASE_URL='Host=db;Database=ai_stocks;Username=worker;Password=x'
export OPERATIONS_DATABASE_URL='Host=db;Database=ai_stocks;Username=operations;Password=x'
export MIGRATOR_DATABASE_URL='Host=db;Database=ai_stocks;Username=migrator;Password=x'
export BACKUP_DATABASE_URL='postgresql://backup:***@db/ai_stocks'
export RESTORE_DATABASE_URL='postgresql://restore:***@db/ai_stocks_test'
export HERMES_AUTH_DIR=/tmp/hermes
export NASDAQ_ARCHIVE_DIR=/tmp/archive
export NASDAQ_STATUS_DIR=/tmp/status
export MARKET_BOOTSTRAP_DIR=/tmp/market-bootstrap
export CORPORATE_ACTION_INPUT_DIR=/tmp/corporate-actions
export ACCESS_TEAM_DOMAIN=https://example.cloudflareaccess.com
export ACCESS_AUD=test-aud
export PUBLIC_ORIGIN=https://ai-stocks.example
export PREVIEW_API_ORIGIN=http://192.168.50.2:3233
export PREVIEW_UI_ORIGINS=http://192.168.50.2:3232
export ACCESS_OWNER_EMAILS=owner@example.com
export ACCESS_VIEWER_EMAILS=viewer@example.com
export TRUSTED_PROXY_IPS=127.0.0.1
export DISCORD_REPORT_TARGET='discord:1534963881317896212'
export COMPOSE="${COMPOSE_CMD[*]}"
scripts/compose-mode.sh contest config -q
scripts/compose-mode.sh preview config -q
scripts/compose-mode.sh warmup config -q

git diff --check
printf 'release verification passed\n'
