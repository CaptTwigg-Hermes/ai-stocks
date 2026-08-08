#!/usr/bin/env python3
"""Executable negative-capability inventory for the frozen paper-trading candidate."""

from __future__ import annotations

import ast
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = ROOT / "ai_stocks"
FORBIDDEN_IMPORTS = {
    "alpaca_trade_api",
    "alpaca",
    "ib_insync",
    "ibapi",
    "ccxt",
    "robin_stocks",
    "tinkoff",
}
FORBIDDEN_TERMS = re.compile(
    r"(?:alpaca|interactive.?brokers|ibkr|nordnet|avanza|broker(?:age)?[_-]?(?:api|key|secret)|place.?order)",
    re.IGNORECASE,
)
ALLOWED_SUBPROCESS_FILES = {"runner.py", "delivery.py"}


def dotted_name(node: ast.AST) -> str:
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        prefix = dotted_name(node.value)
        return f"{prefix}.{node.attr}" if prefix else node.attr
    return ""


def inventory() -> dict:
    imports: set[str] = set()
    urls: list[dict] = []
    environment: list[dict] = []
    subprocesses: list[dict] = []
    routes: list[dict] = []
    findings: list[str] = []

    for path in sorted(SOURCE.glob("*.py")):
        relative = str(path.relative_to(ROOT))
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=relative)
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                imports.update(alias.name for alias in node.names)
            elif isinstance(node, ast.ImportFrom) and node.module:
                imports.add(node.module)
            elif isinstance(node, ast.Constant) and isinstance(node.value, str):
                if re.search(r"https?://", node.value):
                    urls.append({"file": relative, "line": node.lineno, "value": node.value[:500]})
                if FORBIDDEN_TERMS.search(node.value):
                    findings.append(f"forbidden executable string at {relative}:{node.lineno}")
            elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
                for decorator in node.decorator_list:
                    if (
                        isinstance(decorator, ast.Call)
                        and isinstance(decorator.func, ast.Attribute)
                        and isinstance(decorator.func.value, ast.Name)
                        and decorator.func.value.id == "app"
                        and decorator.func.attr in {"get", "post", "put", "patch", "delete"}
                        and decorator.args
                        and isinstance(decorator.args[0], ast.Constant)
                    ):
                        routes.append(
                            {
                                "method": decorator.func.attr.upper(),
                                "path": decorator.args[0].value,
                            }
                        )
            elif isinstance(node, ast.Call):
                name = dotted_name(node.func)
                if name in {"os.getenv", "os.environ.get"} and node.args:
                    value = node.args[0]
                    if isinstance(value, ast.Constant) and isinstance(value.value, str):
                        environment.append(
                            {"file": relative, "line": node.lineno, "name": value.value}
                        )
                        if FORBIDDEN_TERMS.search(value.value):
                            findings.append(
                                f"forbidden environment read at {relative}:{node.lineno}"
                            )
                if name.startswith("subprocess.") or name == "os.system":
                    subprocesses.append({"file": relative, "line": node.lineno, "call": name})
                    if path.name not in ALLOWED_SUBPROCESS_FILES or name == "os.system":
                        findings.append(
                            f"unexpected process capability at {relative}:{node.lineno}"
                        )

    forbidden_imports = sorted(
        item for item in imports if item.split(".", 1)[0] in FORBIDDEN_IMPORTS
    )
    findings.extend(f"forbidden import: {item}" for item in forbidden_imports)

    lock = (ROOT / "uv.lock").read_text(encoding="utf-8")
    package_names = set(re.findall(r'^name = "([^"]+)"$', lock, flags=re.MULTILINE))
    forbidden_packages = sorted(package_names & FORBIDDEN_IMPORTS)
    findings.extend(f"forbidden locked package: {item}" for item in forbidden_packages)

    mutation_routes = sorted(
        route["path"]
        for route in routes
        if route["method"] != "GET" and not str(route["path"]).startswith("/admin/")
    )
    findings.extend(f"unapproved HTTP mutation route: {path}" for path in mutation_routes)

    return {
        "ok": not findings,
        "findings": findings,
        "imports": sorted(imports),
        "locked_packages": sorted(package_names),
        "url_constants": urls,
        "environment_reads": environment,
        "subprocess_calls": subprocesses,
        "routes": sorted(routes, key=lambda route: (str(route["path"]), route["method"])),
    }


def main() -> int:
    result = inventory()
    print(json.dumps(result, indent=2, sort_keys=True))
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
