"""Container liveness heartbeats for non-HTTP services."""

import os
import sys
import tempfile
import time
from pathlib import Path


class HealthError(RuntimeError):
    pass


def write_heartbeat(path: Path, *, now: float | None = None) -> None:
    timestamp = time.time() if now is None else now
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(f"{timestamp}\n")
    os.utime(temporary, (timestamp, timestamp))
    temporary.replace(path)


def check_heartbeat(path: Path, *, max_age: float, now: float | None = None) -> None:
    if max_age <= 0:
        raise HealthError("heartbeat max age must be positive")
    timestamp = time.time() if now is None else now
    try:
        age = timestamp - path.stat().st_mtime
    except OSError as exc:
        raise HealthError("heartbeat is missing") from exc
    if age < 0 or age > max_age:
        raise HealthError("heartbeat is stale")


def heartbeat_path(role: str) -> Path:
    if role not in {"worker", "collector"}:
        raise HealthError("unknown heartbeat role")
    return Path(tempfile.gettempdir()) / f"ai-stocks-{role}.heartbeat"


def main(arguments: list[str] | None = None) -> int:
    values = arguments if arguments is not None else sys.argv[1:]
    if len(values) != 1:
        raise HealthError("exactly one heartbeat role is required")
    max_age = float(os.environ.get("HEARTBEAT_MAX_AGE_SECONDS", "120"))
    check_heartbeat(heartbeat_path(values[0]), max_age=max_age)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
