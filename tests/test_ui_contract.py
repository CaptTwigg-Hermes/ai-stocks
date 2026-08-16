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


def test_exhibition_mode_replaces_human_workspace_without_breaking_preview_fallback():
    html = (UI / "index.html").read_text()
    script = (UI / "app.js").read_text()

    assert 'id="ai-race-page" hidden' in html
    assert 'id="ai-participants"' in html
    assert 'id="ai-refresh" type="button"' in html
    assert 'api("/api/v1/ai-progress")' in script
    assert 'data.strictContest === false' in script
    assert 'data.isNonLive === true' in script
    assert 'ui.tradePage.hidden = true' in script
    assert 'ui.aiRacePage.hidden = false' in script
    assert 'document.body.classList.add("exhibition-mode")' in script
    assert 'if (error.status !== 404)' in script
    assert 'startHumanPreview()' in script


def test_exhibition_cards_are_four_defensive_safe_complete_ai_participants():
    html = (UI / "index.html").read_text()
    script = (UI / "app.js").read_text()

    assert "const exhibitionModelIds = new Set([" in script
    for model_id in ("gpt-5.6-sol", "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview"):
        assert f'"{model_id}"' in script
    assert "data.participants.length !== 4" in script
    assert "new Set(data.participants.map((participant) => participant.modelId))" in script
    for label in ("Status", "Last action", "Decision time", "Rationale", "Confidence", "Verified sources", "Cash", "Holdings value", "Total"):
        assert f'"{label}"' in script
    for status in ("pending", "running", "degraded", "failure", "success"):
        assert f'"{status}"' in script
    assert "renderAiHoldings" in script
    assert "renderEvidence" in script
    assert 'url.protocol !== "https:"' in script
    assert "link.rel = \"noopener noreferrer\"" in script
    assert "innerHTML" not in script
    assert html.count("fixture-backed and non-live") >= 2
    assert html.count("Portfolios are volatile") >= 2
    assert html.count("not the strict 2026 contest") >= 2


def test_exhibition_refresh_is_bounded_race_safe_and_responsive():
    script = (UI / "app.js").read_text()
    css = (UI / "styles.css").read_text()

    assert "let aiRefreshGeneration = 0" in script
    assert "let aiRefreshController" in script
    assert "aiRefreshController.abort()" in script
    assert "generation !== aiRefreshGeneration" in script
    assert "window.setInterval(refreshAiProgress, 60000)" in script
    assert 'ui.aiRefresh.addEventListener("click", () => refreshAiProgress(true))' in script
    assert 'isLeaderboardPage ? "AI leaderboard refreshed." : "AI race refreshed."' in script
    assert 'showExhibitionFailure("AI race unavailable. Human trading controls remain hidden.")' in script
    assert ".ai-grid" in css and "repeat(2, minmax(0, 1fr))" in css
    assert ".ai-card" in css and "min-width: 0" in css
    assert ".ai-detail strong" in css and "overflow-wrap: anywhere" in css
    assert "@media (max-width: 700px)" in css
    assert "@media (max-width: 390px)" in css
    assert "@media (max-width: 340px)" in css


def test_exhibition_leaderboard_is_ai_only_and_uses_ai_refresh():
    html = (UI / "index.html").read_text()
    script = (UI / "app.js").read_text()

    assert 'id="leaderboard-intro"' in html
    assert 'id="leaderboard-mode"' in html
    assert 'ui.leaderboardIntro.textContent = "Four fixed AI participants ranked by total fixture portfolio value in DKK."' in script
    assert 'ui.leaderboardMode.textContent = "AI-only fixture exhibition"' in script
    assert 'document.body.classList.contains("exhibition-mode") ? refreshAiProgress(true) : refreshAll(true)' in script
