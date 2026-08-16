#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT_DIR"

mode=${1:-}
case "$mode" in
  contest|preview|warmup|operations) ;;
  *) printf 'usage: %s contest|preview|warmup|operations COMPOSE_ARGS...\n' "$0" >&2; exit 64 ;;
esac
shift
(($#)) || { printf 'compose-mode: a Compose command is required\n' >&2; exit 64; }
[[ "$1" != -* ]] || {
  printf 'compose-mode: Compose global options are forbidden; put the Compose command immediately after the mode\n' >&2
  exit 64
}
for argument in "$@"; do
  [[ "$argument" != --profile && "$argument" != --profile=* ]] || {
    printf 'compose-mode: --profile is forbidden; select exactly one mode with the first argument\n' >&2
    exit 64
  }
done

if [[ -n "${COMPOSE:-}" ]]; then
  read -r -a compose_command <<<"$COMPOSE"
elif docker compose version >/dev/null 2>&1; then
  compose_command=(docker compose)
elif [[ -x /opt/data/.tools/docker-compose ]]; then
  compose_command=(/opt/data/.tools/docker-compose)
else
  printf 'compose-mode: Docker Compose is required\n' >&2
  exit 1
fi

if [[ "$mode" == contest || "$mode" == preview || "$mode" == warmup ]]; then
  export AISTOCKS_DEPLOYMENT_PROFILE=$mode
fi

action=$1
if [[ "$action" == up || "$action" == start || "$action" == restart ]]; then
  if ! running_output=$("${compose_command[@]}" ps --services --filter status=running); then
    printf 'compose-mode: could not determine active runtime services; refusing to start %s\n' "$mode" >&2
    exit 1
  fi
  mapfile -t running_services <<<"$running_output"
  for running in "${running_services[@]}"; do
    if [[ "$mode" == contest && "$running" == warmup-collector ]]; then
      printf 'compose-mode: warmup collector is still active; stop it before starting contest\n' >&2
      exit 1
    fi
    if [[ "$mode" == warmup && "$running" == collector ]]; then
      printf 'compose-mode: contest collector is still active; stop it before starting warmup\n' >&2
      exit 1
    fi
  done
  if [[ "$mode" == contest ]]; then
    opposite_label=preview
    opposite_services=(api ui)
  elif [[ "$mode" == preview ]]; then
    opposite_label=contest
    opposite_services=(app collector worker reporter)
  elif [[ "$mode" == warmup ]]; then
    opposite_label=contest
    opposite_services=(collector)
  else
    opposite_label=
    opposite_services=()
  fi

  for running in "${running_services[@]}"; do
    for opposite in "${opposite_services[@]}"; do
      [[ "$running" != "$opposite" ]] || {
        printf 'compose-mode: %s runtime is still active (%s); stop it before starting %s\n' \
          "$opposite_label" "$running" "$mode" >&2
        exit 1
      }
    done
  done
fi

exec "${compose_command[@]}" --profile "$mode" "$@"
