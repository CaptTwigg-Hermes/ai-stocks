#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

BACKUP_DIR=${BACKUP_DIR:-./backups}
BACKUP_PASSPHRASE_FILE=${BACKUP_PASSPHRASE_FILE:-./secrets/backup-passphrase}
BACKUP_RETENTION_DAYS=${BACKUP_RETENTION_DAYS:-550}
TODAY=$(date -u +%F)
FINAL_PATH="$BACKUP_DIR/ai-stocks-$TODAY.dump.enc"
TEMP_PATH="$FINAL_PATH.partial.$$"
LOCK_DIR="$BACKUP_DIR/.backup.lock"

cleanup() {
  rm -f -- "$TEMP_PATH"
  rmdir -- "$LOCK_DIR" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

fail() {
  printf 'backup: %s\n' "$*" >&2
  exit 1
}

case "$BACKUP_RETENTION_DAYS" in
  ''|*[!0-9]*) fail 'BACKUP_RETENTION_DAYS must be a non-negative integer' ;;
esac
command -v docker >/dev/null 2>&1 || fail 'docker is required'
command -v openssl >/dev/null 2>&1 || fail 'openssl is required'
if docker compose version >/dev/null 2>&1; then
  COMPOSE=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE=(docker-compose)
else
  fail 'Docker Compose v2 (or docker-compose) is required'
fi

[[ -s "$BACKUP_PASSPHRASE_FILE" ]] || fail "missing or empty passphrase file: $BACKUP_PASSPHRASE_FILE"
[[ ${BACKUP_DATABASE_URL:-} == postgresql://* || ${BACKUP_DATABASE_URL:-} == postgres://* ]] || \
  fail 'BACKUP_DATABASE_URL must be an exported libpq postgresql:// or postgres:// URL'
PASS_MODE=$(stat -c '%a' -- "$BACKUP_PASSPHRASE_FILE")
(( (8#$PASS_MODE & 8#077) == 0 )) || fail 'passphrase file must not be accessible by group or others (use chmod 600)'
mkdir -p -- "$BACKUP_DIR"
mkdir -- "$LOCK_DIR" 2>/dev/null || fail "another backup is running (lock: $LOCK_DIR)"
[[ ! -e "$FINAL_PATH" ]] || fail "daily backup already exists: $FINAL_PATH"

printf 'Creating encrypted PostgreSQL backup for %s...\n' "$TODAY"
"${COMPOSE[@]}" --profile operations run --rm -T --no-deps \
  -e BACKUP_DATABASE_URL backup-tools sh -ceu \
  'exec pg_dump --format=custom --compress=9 --no-owner --no-privileges --dbname="$BACKUP_DATABASE_URL"' \
  | openssl enc -aes-256-cbc -salt -pbkdf2 -iter 200000 -md sha256 \
      -pass "file:$BACKUP_PASSPHRASE_FILE" -out "$TEMP_PATH"

[[ -s "$TEMP_PATH" ]] || fail 'encrypted output is empty'
chmod 600 -- "$TEMP_PATH"
mv -- "$TEMP_PATH" "$FINAL_PATH"

# Retention is based on encrypted backup mtime. Set 0 to disable deletion.
if (( BACKUP_RETENTION_DAYS > 0 )); then
  find "$BACKUP_DIR" -maxdepth 1 -type f -name 'ai-stocks-????-??-??.dump.enc' \
    -mtime "+$BACKUP_RETENTION_DAYS" -delete
fi

printf 'Backup complete: %s\n' "$FINAL_PATH"
