#!/usr/bin/env bash
set -Eeuo pipefail

case "${BACKUP_INTERVAL_SECONDS:-86400}" in
  ''|*[!0-9]*) printf 'backup-cycle: BACKUP_INTERVAL_SECONDS must be a positive integer\n' >&2; exit 1 ;;
esac
(( BACKUP_INTERVAL_SECONDS > 0 )) || { printf 'backup-cycle: interval must be positive\n' >&2; exit 1; }
[[ -n "${BACKUP_DATABASE_URL:-}" ]] || { printf 'backup-cycle: BACKUP_DATABASE_URL is required\n' >&2; exit 1; }

alert() {
  local key="backup:$(date -u +%F)"
  psql "$BACKUP_DATABASE_URL" -X --set=ON_ERROR_STOP=1 --quiet \
    --command "SELECT enqueue_immediate_alert('DatabaseOrBackupFailure','backup or restore verification failed; inspect backup-scheduler logs and pause the contest','$key',clock_timestamp())" \
    >/dev/null || printf 'backup-cycle: AUDITED ALERT ENQUEUE FAILED\n' >&2
}

while true; do
  if ! scripts/backup.sh || ! scripts/restore-test.sh; then
    alert
  fi
  sleep "$BACKUP_INTERVAL_SECONDS"
done
