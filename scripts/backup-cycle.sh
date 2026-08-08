#!/usr/bin/env bash
set -Eeuo pipefail

case "${BACKUP_INTERVAL_SECONDS:-86400}" in
  ''|*[!0-9]*) printf 'backup-cycle: BACKUP_INTERVAL_SECONDS must be a positive integer\n' >&2; exit 1 ;;
esac
(( BACKUP_INTERVAL_SECONDS > 0 )) || { printf 'backup-cycle: interval must be positive\n' >&2; exit 1; }
[[ -n "${BACKUP_ALERT_WEBHOOK_URL:-}" ]] || { printf 'backup-cycle: BACKUP_ALERT_WEBHOOK_URL is required\n' >&2; exit 1; }

alert() {
  curl --fail --silent --show-error --max-time 15 \
    -H 'Content-Type: application/json' \
    --data '{"content":"AI Stocks backup or restore verification failed; inspect backup-scheduler logs and pause the contest."}' \
    "$BACKUP_ALERT_WEBHOOK_URL" || printf 'backup-cycle: ALERT DELIVERY FAILED\n' >&2
}

while true; do
  if ! scripts/backup.sh || ! scripts/restore-test.sh; then
    alert
  fi
  sleep "$BACKUP_INTERVAL_SECONDS"
done
