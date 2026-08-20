"""The /trade route must actually serve the v2 trading page.

Results.File resolves relative paths against the web root, not the
content root, so passing "wwwroot/trade.html" makes ASP.NET look
for wwwroot/wwwroot/trade.html and throw FileNotFoundException at
request time. Unit tests that only inspect trade.html on disk do
not catch this: the file is present and correct, but the route
that serves it is broken. This test asserts the route argument is
web-root relative.
"""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UI_PROGRAM = ROOT / "src" / "AiStocks.Ui" / "Program.cs"
WWWROOT = ROOT / "src" / "AiStocks.Ui" / "wwwroot"

FILE_RESULT = re.compile(r"Results\.File\(\s*\"([^\"]+)\"")


def test_static_file_routes_are_web_root_relative():
    served = FILE_RESULT.findall(UI_PROGRAM.read_text())
    assert served, "expected at least one Results.File route in the UI"

    bad = [path for path in served if path.startswith("wwwroot/")]
    assert not bad, (
        "Results.File resolves against the web root, so these paths "
        f"resolve to wwwroot/wwwroot/... and fail at runtime: {bad}"
    )


def test_every_served_file_exists_in_wwwroot():
    served = FILE_RESULT.findall(UI_PROGRAM.read_text())
    missing = [path for path in served if not (WWWROOT / path).is_file()]
    assert not missing, f"UI routes serve files absent from wwwroot: {missing}"


def test_trade_page_is_served():
    served = FILE_RESULT.findall(UI_PROGRAM.read_text())
    assert "trade.html" in served, (
        "the /trade route must serve trade.html from the web root"
    )
