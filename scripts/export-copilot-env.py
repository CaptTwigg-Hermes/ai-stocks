#!/usr/bin/env python3
"""Export only Copilot credential variables to a private new env file."""

from __future__ import annotations

import os
import stat
import sys
from pathlib import Path
from typing import NoReturn

ALLOWED_KEYS = {
    "COPILOT_GITHUB_TOKEN",
    "COPILOT_TOKEN",
    "GH_TOKEN",
    "GITHUB_TOKEN",
}


def fail(message: str) -> NoReturn:
    print(f"export-copilot-env: {message}", file=sys.stderr)
    raise SystemExit(1)


def main() -> None:
    if len(sys.argv) != 3:
        fail("usage: export-copilot-env.py SOURCE_ENV NEW_DESTINATION")

    source = Path(sys.argv[1])
    destination = Path(sys.argv[2])
    try:
        source_mode = source.lstat().st_mode
    except FileNotFoundError:
        fail("source does not exist")
    if not stat.S_ISREG(source_mode) or source.is_symlink():
        fail("source must be a regular non-symlink file")

    selected: list[str] = []
    seen: set[str] = set()
    for raw_line in source.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if key not in ALLOWED_KEYS:
            continue
        if key in seen:
            fail("source contains a duplicate Copilot credential variable")
        if not value or "\x00" in value:
            fail("a Copilot credential variable is empty or invalid")
        seen.add(key)
        selected.append(f"{key}={value}")

    if not selected:
        fail("source has no supported Copilot credential variables")

    destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        descriptor = os.open(destination, flags, 0o600)
    except FileExistsError:
        fail("destination already exists; refusing to overwrite it")

    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            output.write("\n".join(selected) + "\n")
            output.flush()
            os.fsync(output.fileno())
        destination.chmod(0o600)
    except BaseException:
        destination.unlink(missing_ok=True)
        raise

    print(f"exported {len(selected)} Copilot credential variable(s)")


if __name__ == "__main__":
    main()
