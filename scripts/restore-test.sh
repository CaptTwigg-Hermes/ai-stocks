#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

BACKUP_DIR=${BACKUP_DIR:-./backups}
BACKUP_PASSPHRASE_FILE=${BACKUP_PASSPHRASE_FILE:-./secrets/backup-passphrase}
LOCK_DIR="$BACKUP_DIR/.restore-test.lock"
RESTORE_TOUCHED=0

fail() {
  printf 'restore-test: %s\n' "$*" >&2
  exit 1
}

command -v openssl >/dev/null 2>&1 || fail 'openssl is required'
if [[ ${AISTOCKS_BACKUP_IN_CONTAINER:-0} == 1 ]]; then
  command -v psql >/dev/null 2>&1 || fail 'psql is required'
  command -v pg_restore >/dev/null 2>&1 || fail 'pg_restore is required'
  COMPOSE=()
elif docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  fail 'Docker Compose v2 (or docker-compose) is required'
fi

[[ -n "${RESTORE_DATABASE_URL:-}" ]] || fail 'RESTORE_DATABASE_URL is required and must target ai_stocks_test'

pg_tool() {
  if [[ ${AISTOCKS_BACKUP_IN_CONTAINER:-0} == 1 ]]; then
    "$@"
    return
  fi
  "${COMPOSE[@]}" --profile operations run --rm -T --no-deps \
    -e RESTORE_DATABASE_URL backup-tools "$@"
}

assert_test_database() {
  local database
  database=$(pg_tool psql --no-psqlrc --tuples-only --no-align \
    --set=ON_ERROR_STOP=1 --dbname="$RESTORE_DATABASE_URL" \
    --command='SELECT current_database()')
  [[ "$database" == "ai_stocks_test" ]] || fail \
    "refusing destructive restore into database: $database"
}

clear_test_database() {
  pg_tool psql --no-psqlrc --set=ON_ERROR_STOP=1 \
    --dbname="$RESTORE_DATABASE_URL" \
    --command='DROP SCHEMA public CASCADE; CREATE SCHEMA public' >/dev/null
}

cleanup() {
  local status=$?
  trap - EXIT INT TERM
  if (( RESTORE_TOUCHED )); then
    if ! clear_test_database >/dev/null 2>&1; then
      printf 'restore-test: cleanup failed for ai_stocks_test\n' >&2
      status=1
    fi
  fi
  rmdir -- "$LOCK_DIR" 2>/dev/null || true
  exit "$status"
}
trap cleanup EXIT INT TERM

[[ -s "$BACKUP_PASSPHRASE_FILE" ]] || fail "missing or empty passphrase file: $BACKUP_PASSPHRASE_FILE"
PASS_MODE=$(stat -c '%a' -- "$BACKUP_PASSPHRASE_FILE")
(( (8#$PASS_MODE & 8#077) == 0 )) || fail 'passphrase file must not be accessible by group or others (use chmod 600)'
mkdir -p -- "$BACKUP_DIR"
mkdir -- "$LOCK_DIR" 2>/dev/null || fail "another restore test is running (lock: $LOCK_DIR)"

if (( $# > 1 )); then
  fail 'usage: scripts/restore-test.sh [encrypted-backup]'
elif (( $# == 1 )); then
  BACKUP_PATH=$1
else
  shopt -s nullglob
  CANDIDATES=("$BACKUP_DIR"/ai-stocks-????-??-??.dump.enc)
  (( ${#CANDIDATES[@]} > 0 )) || fail "no encrypted backups found in $BACKUP_DIR"
  BACKUP_PATH=${CANDIDATES[${#CANDIDATES[@]}-1]}
fi
[[ -s "$BACKUP_PATH" ]] || fail "backup is missing or empty: $BACKUP_PATH"

assert_test_database
printf 'Checking encrypted archive: %s\n' "$BACKUP_PATH"
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 \
    -pass "file:$BACKUP_PASSPHRASE_FILE" -in "$BACKUP_PATH" \
  | pg_tool pg_restore --list >/dev/null

clear_test_database
RESTORE_TOUCHED=1
printf 'Restoring into disposable ai_stocks_test database...\n'
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 \
    -pass "file:$BACKUP_PASSPHRASE_FILE" -in "$BACKUP_PATH" \
  | pg_tool pg_restore --exit-on-error --no-owner --no-privileges \
      --dbname="$RESTORE_DATABASE_URL"

MIGRATION_VALUES=''
for migration in src/AiStocks.Persistence/Migrations/*.sql; do
  id=$(basename "$migration" .sql)
  hash=$(sha256sum "$migration" | cut -d' ' -f1)
  MIGRATION_VALUES+="('${id}','${hash}'),"
done
MIGRATION_VALUES=${MIGRATION_VALUES%,}
VERIFY_SQL="WITH expected(id,sha256) AS (VALUES ${MIGRATION_VALUES})
SELECT (SELECT count(*) FROM expected)=(SELECT count(*) FROM schema_migrations)
 AND NOT EXISTS (SELECT FROM expected e LEFT JOIN schema_migrations m USING(id) WHERE m.sha256<>e.sha256 OR m.id IS NULL)
 AND (SELECT count(*)=4 AND count(DISTINCT model_id)=4 AND bool_and(initial_cash=30000) FROM agents)
 AND (SELECT count(*)=4 AND sum(cash)=120000 AND bool_and(cash>=0) FROM account_balances)
 AND NOT EXISTS (SELECT FROM positions WHERE quantity<0 OR average_cost<0)
 AND NOT EXISTS (SELECT FROM ledger_events GROUP BY agent_id HAVING sum(cash_delta)<0);"
VERIFIED=$(pg_tool psql --no-psqlrc --tuples-only --no-align --set=ON_ERROR_STOP=1 \
  --dbname="$RESTORE_DATABASE_URL" --command="$VERIFY_SQL")
[[ "$VERIFIED" == "t" ]] || fail 'restored migration checksums or contest invariants failed verification'

TABLE_COUNT=$(pg_tool psql --no-psqlrc --tuples-only --no-align \
  --set=ON_ERROR_STOP=1 --dbname="$RESTORE_DATABASE_URL" \
  --command="SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'BASE TABLE'")
(( TABLE_COUNT > 0 )) || fail 'restored database contains no user tables'
printf 'Restore test passed: checksums, four agents/funding, invariants, and %s user tables verified; ai_stocks_test will be cleared.\n' "$TABLE_COUNT"
