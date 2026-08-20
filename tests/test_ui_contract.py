import subprocess
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


def test_exhibition_leaderboard_uses_shared_rank_semantics():
    script = (UI / "app.js").read_text()
    assert "values.filter((candidate) => candidate > value).length + 1" in script


def test_exhibition_mode_replaces_human_workspace_without_breaking_preview_fallback():
    html = (UI / "index.html").read_text()
    script = (UI / "app.js").read_text()
    contract = (UI / "exhibition-contract.js").read_text()

    assert 'id="ai-race-page" hidden' in html
    assert 'id="ai-participants"' in html
    assert 'id="ai-activity"' in html
    assert 'id="ai-refresh" type="button"' in html
    assert 'api("/api/v1/ai-progress")' in script
    assert "data.strictContest !== false" in contract
    assert "data.isNonLive !== true" in contract
    assert "data.holdOnly !== false" in contract
    assert "data.assumedFills !== true" in contract
    assert "data.assumedSekToDkk !== 0.65" in contract
    assert "data.assumedSlippagePercent !== 1" in contract
    assert 'ui.tradePage.hidden = true' in script
    assert 'ui.aiRacePage.hidden = false' in script
    assert 'document.body.classList.add("exhibition-mode")' in script
    assert 'if (error.status !== 404)' in script
    assert 'startHumanPreview()' in script


def test_exhibition_cards_are_four_defensive_safe_complete_ai_participants():
    html = (UI / "index.html").read_text()
    script = (UI / "app.js").read_text()
    contract = (UI / "exhibition-contract.js").read_text()

    assert "const modelIds = new Set([" in contract
    for model_id in ("gpt-5.6-sol", "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview"):
        assert f'"{model_id}"' in contract
    assert "data.participants.length !== 4" in contract
    assert "new Set(data.participants.map((participant) => participant.modelId))" in contract
    for label in ("Status", "Failure", "Last action", "Decision time", "Rationale", "Confidence", "Verified sources", "Cash", "Holdings value", "Total"):
        assert f'"{label}"' in script
    for status in ("pending", "queued", "running", "succeeded", "failed"):
        assert f'"{status}"' in script
    assert "renderAiHoldings" in script
    assert "renderEvidence" in script
    assert "renderAiActivity" in script
    assert 'url.protocol !== "https:"' in script
    assert "link.rel = \"noopener noreferrer\"" in script
    assert "innerHTML" not in script
    assert 'id="data-badge"' in html
    assert 'ui.dataBadge.textContent = "ASSUMED FILLS · Official Nasdaq XSTO · delayed · non-live · paper-only"' in script
    assert html.count("official Nasdaq XSTO") >= 2
    assert html.count("15-minute delayed") >= 2
    assert html.count("non-live") >= 2
    assert html.count("paper-only") >= 2
    assert html.count("ASSUMED FILLS") >= 2
    assert html.count("0.65 DKK/SEK") >= 2
    assert html.count("1% adverse slippage") >= 2
    assert "HOLD-only" not in html
    assert "fixture-backed" not in html
    assert html.count("Portfolios are volatile") >= 2
    assert html.count("not the strict 2026 contest") >= 2


def test_exhibition_has_filterable_performance_chart_and_detailed_holdings():
    html = (UI / "index.html").read_text()
    script = (UI / "app.js").read_text()
    css = (UI / "styles.css").read_text()

    for element_id in ("performance-chart", "performance-time", "model-filters", "benchmark-filters"):
        assert f'id="{element_id}"' in html
    for label in ("Current price", "Average buy", "Cost basis", "Gain"):
        assert f'"{label}"' in script
    assert "renderPerformanceChart" in script
    assert "createElementNS" in script
    assert 'series.type === "model"' in script
    assert 'series.type === "benchmark"' in script
    assert ".performance-panel" in css
    assert ".chart-filters" in css
    assert "min-height: 44px" in css


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
    contract = (UI / "exhibition-contract.js").read_text()

    assert 'id="leaderboard-intro"' in html
    assert 'id="leaderboard-mode"' in html
    assert 'ui.leaderboardIntro.textContent = "Four fixed AI participants ranked by assumed-fill paper portfolio value in DKK."' in script
    assert 'ui.leaderboardMode.textContent = "ASSUMED FILLS · AI-only non-live exhibition"' in script
    assert html.count('data-exhibition-only hidden') == 2
    assert 'id="exhibition-failure-page" hidden' in html
    assert 'id="exhibition-failure-message"' in html
    assert 'ui.exhibitionFailurePage.hidden = false;' in script
    assert 'ui.exhibitionFailureMessage.textContent = message;' in script
    assert 'document.querySelectorAll("[data-exhibition-only]").forEach((element) => {' in script
    assert 'element.hidden = false;' in script
    assert 'src="/exhibition-contract.js" defer' in html
    assert 'data.dataMode !== dataMode' in contract
    assert 'data.executionMode !== executionMode' in contract
    assert 'data.holdOnly !== false' in contract
    assert 'participant.portfolio?.dataMode === dataMode' in contract
    assert 'participant.portfolio?.executionMode === executionMode' in contract
    assert "window.aiStocksExhibitionContract?.isResponse(data) === true" in script
    assert 'document.body.classList.contains("exhibition-mode") ? refreshAiProgress(true) : refreshAll(true)' in script


def test_exhibition_contract_rejects_missing_wrong_and_mixed_execution_provenance():
    contract_path = UI / "exhibition-contract.js"
    probe = r"""
global.window = {};
require(process.argv[1]);
const contract = window.aiStocksExhibitionContract;
const models = ["gpt-5.6-sol", "claude-opus-4.8", "claude-sonnet-5", "gemini-3.1-pro-preview"];
function payload(dataModes, executionModes) {
  return {
    dataMode: contract.dataMode,
    executionMode: contract.executionMode,
    strictContest: false,
    isNonLive: true,
    holdOnly: false,
    assumedFills: true,
    assumedSekToDkk: 0.65,
    assumedSlippagePercent: 1,
    participants: models.map((modelId, index) => ({
      modelId,
      portfolio: dataModes[index] === undefined ? {} : {
        dataMode: dataModes[index],
        executionMode: executionModes[index]
      }
    }))
  };
}
const exact = [contract.dataMode, contract.dataMode, contract.dataMode, contract.dataMode];
const executions = [contract.executionMode, contract.executionMode, contract.executionMode, contract.executionMode];
if (!contract.isResponse(payload(exact, executions))) process.exit(10);
if (contract.isResponse(payload([undefined, ...exact.slice(1)], executions))) process.exit(11);
if (contract.isResponse(payload(["preview-fixtures", ...exact.slice(1)], executions))) process.exit(12);
if (contract.isResponse(payload(exact, [undefined, ...executions.slice(1)]))) process.exit(13);
if (contract.isResponse(payload(exact, ["strict-contest", ...executions.slice(1)]))) process.exit(14);
if (contract.isResponse(payload(exact, [contract.executionMode, "strict-contest", ...executions.slice(2)]))) process.exit(15);
"""
    subprocess.run(["node", "-e", probe, str(contract_path)], check=True)
