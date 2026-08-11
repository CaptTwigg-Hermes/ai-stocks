from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).parents[1]
UI = ROOT / "src" / "AiStocks.Ui" / "wwwroot"


class UiParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.buttons: list[dict[str, str | None]] = []
        self.meta: list[dict[str, str | None]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        if tag == "button":
            self.buttons.append(values)
        elif tag == "meta":
            self.meta.append(values)


def test_ui_keeps_accessible_minimal_interaction_contract():
    html = (UI / "index.html").read_text()
    css = (UI / "styles.css").read_text()
    script = (UI / "app.js").read_text()
    parser = UiParser()
    parser.feed(html)

    assert any(item.get("name") == "viewport" for item in parser.meta)
    assert parser.buttons and all(button.get("type") for button in parser.buttons)
    assert 'id="submit-order" type="submit" disabled>Select a stock first</button>' in html
    assert 'id="api-state" role="status" aria-live="polite"' in html
    for panel in ("portfolio", "leaderboard", "activity"):
        assert f'id="{panel}-status"' in html
    assert "min-height: 44px" in css
    assert ".lower-grid" in css and "align-items: stretch" in css
    assert "innerHTML" not in script
    assert 'ui.submit.textContent = "Select a stock first"' in script
    assert 'ui.apiState.lastChild.textContent = healthy ? " Service online" : " Service degraded"' in script
    assert "new AbortController" in script and "searchController.abort()" in script
    assert "Promise.allSettled" in script
    assert "refreshGeneration" in script
    assert "generation !== refreshGeneration" in script
    assert "prefers-reduced-motion: reduce" in script
    assert 'href="/leaderboard"' in html
    assert 'id="trade-page"' in html
    assert 'id="leaderboard-page"' in html
    assert 'id="leaderboard-page-list"' in html
    assert 'id="leaderboard-refresh"' in html
    assert "Loading standings…" in html
    assert "Live exhibition order" not in html
    assert "window.location.pathname" in script
    assert "isLeaderboardPage" in script
    assert 'isLeaderboardPage ? "Leaderboard refreshed." : "Portfolio refreshed."' in script
    assert "const leaderboardLists = [ui.leaderboard, ui.leaderboardFull]" in script
    assert 'list.setAttribute("aria-busy", "true")' in script
    assert 'list.setAttribute("aria-busy", "false")' in script
    assert 'list.replaceChildren(element("li", "muted-empty", "Standings unavailable."))' in script
    assert 'ui.leaderName.textContent = "Unavailable"' in script
    assert 'const number = new Intl.NumberFormat("en"' in script
    assert 'const leaderboardNumber = new Intl.NumberFormat("da-DK"' in script
    assert "const returnFormatter = expanded ? leaderboardNumber : number" in script
    assert "leaderboard-page" in css and "leaderboard-full" in css
    assert ".leaderboard-full .leaderboard-entry > b" in css and "grid-column: 2" in css
    assert "overflow-wrap: anywhere" in css
    assert ".leaderboard-full .leaderboard-entry > div strong" in css
    assert ".leader-summary strong" in css
