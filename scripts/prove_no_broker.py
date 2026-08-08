#!/usr/bin/env python3
"""Fail-closed negative-capability inventory for every shipped .NET executable."""
from __future__ import annotations

import json
import os
import pathlib
import re
import shutil
import subprocess  # nosec B404 - fixed local dotnet executable and arguments only
import sys
import xml.etree.ElementTree as ET  # nosec B405 - parses repository-owned csproj files only
from typing import Any, cast
from urllib.parse import urlparse

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = ROOT / "src"
FORBIDDEN_PACKAGES = {"alpaca.markets", "alpaca.markets.extensions", "ibapi", "interactivebrokers.client", "robinhood.net", "tinkoff.investapi", "ccxt.net"}
FORBIDDEN_HOSTS = {"api.alpaca.markets", "api.ibkr.com", "api.nordnet.se", "api.avanza.se", "api.robinhood.com"}
FORBIDDEN_CREDENTIAL = re.compile(r"(?:broker|alpaca|ibkr|interactive_?brokers|nordnet|avanza).*(?:api_?key|secret|token|password)", re.I)
URL = re.compile(r'https?://[^\s"\'<>]+', re.I)
ENVIRONMENT = re.compile(r'Environment\.GetEnvironmentVariable\(\s*"([A-Za-z0-9_]+)"')
CONFIGURATION = re.compile(r'(?:Configuration\s*\[\s*"(?P<index>[A-Za-z0-9_:.-]+)"\s*\]|Configuration\.(?:GetValue|GetSection)\s*\(\s*"(?P<method>[A-Za-z0-9_:.-]+)"|PostgresConfiguration\.Require\s*\([^,]+,\s*"(?P<require>[A-Za-z0-9_:.-]+)")')
DYNAMIC_CONFIGURATION = re.compile(r'Configuration\s*\[\s*(?!")(?P<index>[^\]]+)\]|PostgresConfiguration\.Require\s*\([^,]+,\s*(?!")(?P<require>[^\)]+)\)')
BULK_ENVIRONMENT = re.compile(r'(?:PostgresConfiguration\.Environment|(?:System\.)?Environment\.GetEnvironmentVariables)\s*\(\s*\)')
GROUP = re.compile(r'\bvar\s+(?P<name>[A-Za-z_]\w*)\s*=\s*\w+\.MapGroup\s*\(\s*"(?P<prefix>/[^"?]*)"')
ROUTE = re.compile(r'\b(?P<receiver>[A-Za-z_]\w*)\.Map(?P<method>Get|Post|Put|Patch|Delete)\s*\(\s*"(?P<path>/[^"?]*)"')
ROUTE_HELPER = re.compile(r'\bMapControl\s*\(\s*(?P<receiver>[A-Za-z_]\w*)\s*,\s*"(?P<path>/[^"?]*)"')
PROCESS = re.compile(r"\bProcessStartInfo\b|\bProcess\.Start\s*\(")
NETWORK_API = re.compile(r"\b(?:Socket|HttpClient|HttpRequestMessage|WebRequest|TcpClient)\b")
ALLOWED_PROCESS_PROJECTS = {"AiStocks.Research", "AiStocks.Operations"}
ALLOWED_MUTATIONS = {"/admin/start", "/admin/pause", "/admin/resume", "/admin/pre-start-reset"}
SHIPPED_EXECUTABLES = {"AiStocks.Collector", "AiStocks.Web", "AiStocks.Worker"}


def relative(path: pathlib.Path) -> str:
    return path.relative_to(ROOT).as_posix()


def configuration_reads(text: str, file: str) -> dict[str, list[dict[str, object]]]:
    environment = [{"file": file, "line": text.count("\n", 0, match.start()) + 1, "name": match.group(1)}
                   for match in ENVIRONMENT.finditer(text)]
    environment.extend({"file": file, "line": text.count("\n", 0, match.start()) + 1, "name": "*"}
                       for match in BULK_ENVIRONMENT.finditer(text))
    configuration = [{"file": file, "line": text.count("\n", 0, match.start()) + 1,
                      "name": next(value for value in match.groupdict().values() if value is not None)}
                     for match in CONFIGURATION.finditer(text)]
    dynamic = [{"file": file, "line": text.count("\n", 0, match.start()) + 1,
                "expression": next(value.strip() for value in match.groupdict().values() if value is not None)}
               for match in DYNAMIC_CONFIGURATION.finditer(text)
               if not next(value.strip() for value in match.groupdict().values() if value is not None).startswith('"')]
    return {"environment": environment, "configuration": configuration, "dynamic": dynamic}


def order_path_denial_probe(findings: list[str]) -> dict[str, object]:
    build = os.environ.get("AISTOCKS_BUILD_CONFIGURATION", "Release")
    dll = SOURCE / "AiStocks.Worker" / "bin" / build / "net10.0" / "AiStocks.Worker.dll"
    dotnet = shutil.which("dotnet") or ("/opt/data/dotnet/dotnet" if pathlib.Path("/opt/data/dotnet/dotnet").is_file() else None)
    unavailable = {"ok": False, "executed": False, "paper_order_count": 0, "network_events": []}
    if dotnet is None or not dll.is_file():
        findings.append("executable order-path denial probe unavailable; build AiStocks.Worker first")
        return unavailable
    environment = os.environ.copy()
    environment.update({"http_proxy": "http://127.0.0.1:1", "https_proxy": "http://127.0.0.1:1", "NO_PROXY": ""})
    try:
        result = subprocess.run(  # noqa: S603  # nosec B603
            [dotnet, str(dll), "--probe-order-path-denial"], cwd=ROOT, env=environment,
            text=True, capture_output=True, check=False, timeout=10)
    except (OSError, subprocess.TimeoutExpired) as error:
        findings.append(f"executable order-path denial probe failed: {error}")
        return unavailable
    marker = next((line.removeprefix("AISTOCKS_ORDER_PATH_PROBE=") for line in result.stdout.splitlines()
                   if line.startswith("AISTOCKS_ORDER_PATH_PROBE=")), None)
    if result.returncode != 0 or marker is None:
        findings.append(f"executable order-path denial probe failed: exit {result.returncode}")
        return unavailable
    try:
        probe = cast(dict[str, object], json.loads(marker))
    except json.JSONDecodeError as error:
        findings.append(f"executable order-path denial probe malformed: {error}")
        return unavailable
    if probe.get("ok") is not True or probe.get("executed") is not True or probe.get("paper_order_count") != 1:
        findings.append("executable order-path denial probe did not execute one isolated paper order")
    if probe.get("network_events") != []:
        findings.append("order path attempted DNS/socket/HTTP network capability")
    return probe


def runtime_endpoint_table(executable: str, findings: list[str]) -> dict[str, object]:
    configuration = os.environ.get("AISTOCKS_BUILD_CONFIGURATION", "Release")
    dll = SOURCE / executable / "bin" / configuration / "net10.0" / f"{executable}.dll"
    dotnet = shutil.which("dotnet") or ("/opt/data/dotnet/dotnet" if pathlib.Path("/opt/data/dotnet/dotnet").is_file() else None)
    if dotnet is None or not dll.is_file():
        findings.append(f"runtime endpoint inventory unavailable for {executable}; build the shipped executables first")
        return {"executable": executable, "routes": []}
    environment = os.environ.copy()
    denied_database = "Host=127.0.0.1;Database=aistocks_endpoint_inventory;Username=denied;Password=denied"
    environment.update({
        "DOTNET_ENVIRONMENT": "Testing",
        "DATABASE_URL": denied_database,
        "COLLECTOR_DATABASE_URL": denied_database,
        "FIRDS_ACQUISITION_PLAN_PATH": str(ROOT / "tests" / "AiStocks.MarketData.Tests" / "Fixtures" / "firds-plan-unused.json"),
        "ARTIFACT_ROOT": str(ROOT),
    })
    try:
        result = subprocess.run(  # noqa: S603  # nosec B603
            [dotnet, str(dll), "--print-endpoints"], cwd=ROOT, env=environment,
            text=True, capture_output=True, check=False, timeout=10)
    except (OSError, subprocess.TimeoutExpired) as error:
        findings.append(f"runtime endpoint inventory failed for {executable}: {error}")
        return {"executable": executable, "routes": []}
    marker = next((line.removeprefix("AISTOCKS_ENDPOINTS=") for line in result.stdout.splitlines()
                   if line.startswith("AISTOCKS_ENDPOINTS=")), None)
    if result.returncode != 0 or marker is None:
        findings.append(f"runtime endpoint inventory failed for {executable}: exit {result.returncode}")
        return {"executable": executable, "routes": []}
    try:
        values = json.loads(marker)
        routes = sorted(({"method": str(item["method"]).upper(), "path": str(item["path"])} for item in values),
                        key=lambda item: (item["path"], item["method"]))
        if not routes:
            findings.append(f"runtime endpoint inventory was empty for {executable}")
    except (json.JSONDecodeError, KeyError, TypeError) as error:
        findings.append(f"runtime endpoint inventory malformed for {executable}: {error}")
        routes = []
    return {"executable": executable, "routes": routes}


def inventory() -> dict[str, object]:
    findings: list[str] = []
    packages: list[dict[str, str]] = []
    resolved: list[dict[str, str]] = []
    urls: list[dict[str, object]] = []
    environment: list[dict[str, object]] = []
    configuration: list[dict[str, object]] = []
    dynamic_configuration: list[dict[str, object]] = []
    processes: list[dict[str, object]] = []
    routes: list[dict[str, object]] = []

    for lock in sorted(ROOT.glob("**/packages.lock.json")):
        if any(part in {"bin", "obj"} for part in lock.parts):
            continue
        try:
            graph = json.loads(lock.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            findings.append(f"invalid lock graph: {relative(lock)}: {error}")
            continue
        for framework, nodes in graph.get("dependencies", {}).items():
            for name, node in nodes.items():
                resolved.append({"file": relative(lock), "framework": framework, "name": name, "version": str(node.get("resolved", ""))})
                if name.casefold() in FORBIDDEN_PACKAGES:
                    findings.append(f"forbidden resolved broker package: {name} in {relative(lock)}")

    for project in sorted(SOURCE.glob("*/**/*.csproj")):
        try:
            root = ET.parse(project).getroot()  # noqa: S314  # nosec B314 - repository-owned XML
        except ET.ParseError as error:
            findings.append(f"invalid project XML: {relative(project)}: {error}")
            continue
        for package in root.findall(".//PackageReference"):
            name = (package.get("Include") or package.get("Update") or "").strip()
            if name:
                packages.append({"file": relative(project), "name": name})
                if name.casefold() in FORBIDDEN_PACKAGES:
                    findings.append(f"forbidden broker package: {name} in {relative(project)}")

    for path in sorted(SOURCE.glob("*/**/*.cs")):
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        text = path.read_text(encoding="utf-8")
        rel = relative(path)
        project = path.relative_to(SOURCE).parts[0]
        groups = {m.group("name"): m.group("prefix").rstrip("/") for m in GROUP.finditer(text)}
        for match in URL.finditer(text):
            value = match.group(0).rstrip(".,);]")
            host = (urlparse(value).hostname or "").casefold()
            line = text.count("\n", 0, match.start()) + 1
            urls.append({"file": rel, "line": line, "value": value})
            if host in FORBIDDEN_HOSTS or any(host.endswith("." + item) for item in FORBIDDEN_HOSTS):
                findings.append(f"forbidden broker host at {rel}:{line}: {host}")
        reads = configuration_reads(text, rel)
        environment.extend(reads["environment"])
        configuration.extend(reads["configuration"])
        unresolved = reads["dynamic"]
        if rel == "src/AiStocks.Web/Program.cs":
            names = sorted(set(re.findall(r'\bRequired\(\s*"([A-Za-z0-9_:.-]+)"\s*\)', text)))
            configuration.extend({"file": rel, "line": item["line"], "name": name, "via": "Required(name)"}
                                 for item in unresolved if item["expression"] == "name" for name in names)
            unresolved = [item for item in unresolved if item["expression"] != "name" or not names]
        if rel == "src/AiStocks.Operations/Program.cs":
            names = sorted(set(re.findall(r'"((?:MIGRATOR_)?DATABASE_URL)"', text)))
            configuration.extend({"file": rel, "line": item["line"], "name": name, "via": "migratorKey"}
                                 for item in unresolved if item["expression"] == "migratorKey" for name in names)
            unresolved = [item for item in unresolved if item["expression"] != "migratorKey" or not names]
        dynamic_configuration.extend(unresolved)
        for item in reads["environment"] + reads["configuration"]:
            if FORBIDDEN_CREDENTIAL.search(str(item["name"])):
                findings.append(f"forbidden broker configuration read at {rel}:{item['line']}: {item['name']}")
        for match in PROCESS.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            processes.append({"file": rel, "line": line})
            if project not in ALLOWED_PROCESS_PROJECTS:
                findings.append(f"unexpected process capability at {rel}:{line}")
        for match in ROUTE.finditer(text):
            method = match.group("method").upper()
            route_path = groups.get(match.group("receiver"), "") + match.group("path")
            line = text.count("\n", 0, match.start()) + 1
            routes.append({"executable": project, "method": method, "path": route_path})
            if method != "GET" and route_path not in ALLOWED_MUTATIONS:
                findings.append(f"unapproved HTTP mutation route at {rel}:{line}: {method} {route_path}")
        for match in ROUTE_HELPER.finditer(text):
            route_path = groups.get(match.group("receiver"), "") + match.group("path")
            line = text.count("\n", 0, match.start()) + 1
            routes.append({"executable": project, "method": "POST", "path": route_path})
            if route_path not in ALLOWED_MUTATIONS:
                findings.append(f"unapproved HTTP mutation route at {rel}:{line}: POST {route_path}")

    endpoint_tables = [runtime_endpoint_table(executable, findings) for executable in sorted(SHIPPED_EXECUTABLES)]
    worker_packages = {item["name"].casefold() for item in resolved if item["file"] == "src/AiStocks.Worker/packages.lock.json"}
    if worker_packages & FORBIDDEN_PACKAGES:
        findings.append("order-path provider denial failed: " + ", ".join(sorted(worker_packages & FORBIDDEN_PACKAGES)))
    if dynamic_configuration:
        findings.extend(f"unresolved dynamic configuration read at {item['file']}:{item['line']}: {item['expression']}"
                        for item in dynamic_configuration)
    order_probe = order_path_denial_probe(findings)

    flat_routes = [{"method": method, "path": path} for path, method in sorted({
        (str(route["path"]), str(route["method"])) for table in endpoint_tables
        for route in cast(list[dict[str, Any]], table["routes"])})]
    return {"ok": not findings, "findings": sorted(findings),
            "packages": sorted(packages, key=lambda i: (i["name"], i["file"])),
            "resolved_lock_packages": sorted(resolved, key=lambda i: (i["name"], i["file"], i["framework"])),
            "url_constants": sorted(urls, key=lambda i: (str(i["file"]), str(i["line"]))),
            "environment_reads": sorted(environment, key=lambda i: (str(i["file"]), str(i["line"]))),
            "configuration_reads": sorted(configuration, key=lambda i: (str(i["file"]), str(i["line"]))),
            "dynamic_configuration_reads": sorted(dynamic_configuration, key=lambda i: (str(i["file"]), str(i["line"]))),
            "process_calls": sorted(processes, key=lambda i: (str(i["file"]), str(i["line"]))),
            "endpoint_tables": endpoint_tables,
            "order_path_denial_probe": order_probe,
            "routes": flat_routes}


def main() -> int:
    result = inventory()
    print(json.dumps(result, indent=2, sort_keys=True))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
