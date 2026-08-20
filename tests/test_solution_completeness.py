"""Guard against projects silently missing from the solution.

Every project built and tested by CI is the set listed in
AiStocks.slnx. A project that exists on disk but is absent from
the solution is never compiled and its tests never run, which
hides regressions without any visible failure. This happened to
AiStocks.Exhibition.Worker: 70 tests were dormant in CI while the
suite reported green.
"""

from __future__ import annotations

import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOLUTION = ROOT / "AiStocks.slnx"


def _solution_projects() -> set[str]:
    root = ElementTree.parse(SOLUTION).getroot()
    return {
        Path(element.attrib["Path"]).as_posix()
        for element in root.iter("Project")
        if "Path" in element.attrib
    }


def _disk_projects(folder: str) -> set[str]:
    return {
        path.relative_to(ROOT).as_posix()
        for path in (ROOT / folder).glob("*/*.csproj")
    }


def test_every_source_project_is_in_the_solution():
    missing = _disk_projects("src") - _solution_projects()
    assert not missing, (
        "source projects exist on disk but are absent from AiStocks.slnx, "
        f"so CI never builds them: {sorted(missing)}"
    )


def test_every_test_project_is_in_the_solution():
    missing = _disk_projects("tests") - _solution_projects()
    assert not missing, (
        "test projects exist on disk but are absent from AiStocks.slnx, "
        f"so CI never runs them: {sorted(missing)}"
    )


def test_solution_only_references_projects_that_exist():
    dangling = {
        project
        for project in _solution_projects()
        if not (ROOT / project).is_file()
    }
    assert not dangling, f"AiStocks.slnx references missing projects: {sorted(dangling)}"
