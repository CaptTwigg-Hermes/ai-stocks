#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

DOTNET_ROOT=${DOTNET_ROOT:-/workspace/house-consensus/.tools/dotnet}
DOTNET=${DOTNET:-$DOTNET_ROOT/dotnet}
ICU_DIR=${ICU_DIR:-/workspace/house-consensus/.tools/icu/usr/lib/x86_64-linux-gnu}
COMPOSE=${COMPOSE:-/opt/data/.tools/docker-compose}

[[ -n "${AISTOCKS_TEST_DATABASE_URL:-}" ]] || {
  printf 'verify-release: AISTOCKS_TEST_DATABASE_URL is required and must name a disposable test database\n' >&2
  exit 1
}
case "$AISTOCKS_TEST_DATABASE_URL" in
  *[Dd]atabase=*test*|*/*test*|*/*_test*) ;;
  *) printf 'verify-release: refusing non-test AISTOCKS_TEST_DATABASE_URL\n' >&2; exit 1 ;;
esac

export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export LD_LIBRARY_PATH="$ICU_DIR:$DOTNET_ROOT${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

"$DOTNET" restore AiStocks.slnx --locked-mode
"$DOTNET" format AiStocks.slnx --verify-no-changes --no-restore
"$DOTNET" build AiStocks.slnx -c Release --no-restore
"$DOTNET" test AiStocks.slnx -c Release --no-build --no-restore \
  --logger 'console;verbosity=minimal'
python3 scripts/prove_no_broker.py

env \
  WEB_DATABASE_URL='Host=db;Database=ai_stocks;Username=web;Password=x' \
  WORKER_DATABASE_URL='Host=db;Database=ai_stocks;Username=worker;Password=x' \
  OPERATIONS_DATABASE_URL='Host=db;Database=ai_stocks;Username=operations;Password=x' \
  MIGRATOR_DATABASE_URL='Host=db;Database=ai_stocks;Username=migrator;Password=x' \
  BACKUP_DATABASE_URL='postgresql://backup:***@db/ai_stocks' \
  RESTORE_DATABASE_URL='postgresql://restore:***@db/ai_stocks_test' \
  HERMES_AUTH_DIR=/tmp/hermes \
  NASDAQ_ARCHIVE_DIR=/tmp/archive \
  NASDAQ_STATUS_DIR=/tmp/status \
  ACCESS_TEAM_DOMAIN=https://example.cloudflareaccess.com \
  ACCESS_AUD=test-aud \
  PUBLIC_ORIGIN=https://ai-stocks.example \
  ACCESS_OWNER_EMAILS=owner@example.com \
  ACCESS_VIEWER_EMAILS=viewer@example.com \
  DISCORD_TARGET='discord:#ai-stocks' \
  BACKUP_ALERT_WEBHOOK_URL='https://discord.com/api/webhooks/test/test' \
  "$COMPOSE" config -q

git diff --check
printf 'release verification passed\n'
