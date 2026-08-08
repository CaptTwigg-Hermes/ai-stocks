#!/usr/bin/env python3
"""Fail-closed .NET negative-capability inventory for paper trading."""

from __future__ import annotations

import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
from urllib.parse import urlparse

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src"

FORBIDDEN_PACKAGES = {
    "alpaca.markets",
    "alpaca.markets.extensions",
    "ibapi",
    "interactivebrokers.client",
    "robinhood.net",
    "tinkoff.investapi",
    "ccxt.net",
}
FORBIDDEN_HOSTS = {
    "api.alpaca.markets",
    "api.ibkr.com",
    "api.nordnet.se",
    "api.avanza.se",
    "api.robinhood.com",
}
FORBIDDEN_CREDENTIAL = re.compile(
    r"(?:broker|alpaca|ibkr|interactive_?brokers|nordnet|avanza).*(?:api_?key|secret|token|password)",
    re.IGNORECASE,
)
URL = re.compile(r'https?://[^\s"\'<>]+', re.IGNORECASE)
ENVIRONMENT = re.compile(
    r'Environment\.GetEnvironmentVariable\(\s*"([A-Za-z0-9_]+)"',
)
ROUTE = re.compile(
    r'\.Map(?P<method>Get|Post|Put|Patch|Delete)\s*\(\s*"(?P<path>/[^"?]*)"',
)
PROCESS = re.compile(r"\bProcessStartInfo\b|\bProcess\.Start\s*\(")
ALLOWED_PROCESS_PROJECTS = {"AiStocks.Research", "AiStocks.Operations"}
ALLOWED_MUTATIONS = {
    "/owner/contest/start",
    "/owner/contest/pause",
    "/owner/contest/resume",
    "/owner/contest/reset",
}


def relative(path: pathlib.Path) -> str:
    return path.relative_to(ROOT).as_posix()


def inventory() -> dict[str, object]:
    findings: list[str] = []
    packages: list[dict[str, str]] = []
    urls: list[dict[str, object]] = []
    environment: list[dict[str, object]] = []
    processes: list[dict[str, object]] = []
    routes: list[dict[str, object]] = []

    for project in sorted(SOURCE.glob("*/**/*.csproj")):
        try:
            root = ET.parse(project).getroot()
        except ET.ParseError as error:
            findings.append(f"invalid project XML: {relative(project)}: {error}")
            continue
        for package in root.findall(".//PackageReference"):
            name = (package.get("Include") or package.get("Update") or "").strip()
            if not name:
                continue
            entry = {"file": relative(project), "name": name}
            packages.append(entry)
            if name.casefold() in FORBIDDEN_PACKAGES:
                findings.append(f"forbidden broker package: {name} in {relative(project)}")

    for path in sorted(SOURCE.glob("*/**/*.cs")):
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8")
        rel = relative(path)
        project = path.relative_to(SOURCE).parts[0]

        for match in URL.finditer(text):
            value = match.group(0).rstrip(".,);]")
            host = (urlparse(value).hostname or "").casefold()
            line = text.count("\n", 0, match.start()) + 1
            urls.append({"file": rel, "line": line, "value": value})
            if host in FORBIDDEN_HOSTS or any(host.endswith("." + item) for item in FORBIDDEN_HOSTS):
                findings.append(f"forbidden broker host at {rel}:{line}: {host}")

        for match in ENVIRONMENT.finditer(text):
            name = match.group(1)
            line = text.count("\n", 0, match.start()) + 1
            environment.append({"file": rel, "line": line, "name": name})
            if FORBIDDEN_CREDENTIAL.search(name):
                findings.append(f"forbidden broker credential read at {rel}:{line}: {name}")

        for match in PROCESS.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            processes.append({"file": rel, "line": line})
            if project not in ALLOWED_PROCESS_PROJECTS:
                findings.append(f"unexpected process capability at {rel}:{line}")

        for match in ROUTE.finditer(text):
            method = match.group("method").upper()
            route_path = match.group("path")
            line = text.count("\n", 0, match.start()) + 1
            routes.append({"file": rel, "line": line, "method": method, "path": route_path})
            if method != "GET" and route_path not in ALLOWED_MUTATIONS:
                findings.append(f"unapproved HTTP mutation route at {rel}:{line}: {method} {route_path}")

    return {
        "ok": not findings,
        "findings": sorted(findings),
        "packages": sorted(packages, key=lambda item: (item["name"], item["file"])),
        "url_constants": sorted(urls, key=lambda item: (str(item["file"]), str(item["line"]))),
        "environment_reads": sorted(environment, key=lambda item: (str(item["file"]), str(item["line"]))),
        "process_calls": sorted(processes, key=lambda item: (str(item["file"]), str(item["line"]))),
        "routes": sorted(routes, key=lambda item: (str(item["path"]), str(item["method"]))),
    }


def main() -> int:
    result = inventory()
    print(json.dumps(result, indent=2, sort_keys=True))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
