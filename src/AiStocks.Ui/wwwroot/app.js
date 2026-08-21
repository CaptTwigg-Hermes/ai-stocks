(() => {
  "use strict";

  const apiBase = window.AISTOCKS_API_URL;
  if (!apiBase) throw new Error("AI Stocks runtime configuration is missing.");
  const isLeaderboardPage = /^\/leaderboard\/?$/.test(window.location.pathname);
  const state = {
    selectedInstrument: null,
    side: "buy",
    instruments: [],
    portfolio: null,
    busy: false,
    failures: new Set(),
    loaded: { portfolio: false, leaderboard: false, activity: false }
  };

  const byId = (id) => document.getElementById(id);
  const ui = {
    apiState: byId("api-state"),
    dataBadge: byId("data-badge"),
    tradePage: byId("trade-page"),
    leaderboardPage: byId("leaderboard-page"),
    aiRacePage: byId("ai-race-page"),
    exhibitionFailurePage: byId("exhibition-failure-page"),
    exhibitionFailureMessage: byId("exhibition-failure-message"),
    aiParticipants: byId("ai-participants"),
    aiActivity: byId("ai-activity"),
    aiRefresh: byId("ai-refresh"),
    aiRaceStatus: byId("ai-race-status"),
    performanceChart: byId("performance-chart"),
    performanceTime: byId("performance-time"),
    modelFilters: byId("model-filters"),
    benchmarkFilters: byId("benchmark-filters"),
    performanceLegend: byId("performance-legend"),
    performanceEmpty: byId("performance-empty"),
    search: byId("stock-search"),
    results: byId("search-results"),
    resultCount: byId("result-count"),
    orderTitle: byId("order-title"),
    orderContext: byId("order-context"),
    selectedSymbol: byId("selected-symbol"),
    selectedQuote: byId("selected-quote"),
    quantity: byId("quantity"),
    note: byId("order-note"),
    estimate: byId("estimate"),
    submit: byId("submit-order"),
    form: byId("order-form"),
    formStatus: byId("form-status"),
    heroTotal: byId("hero-total"),
    heroReturn: byId("hero-return"),
    cash: byId("cash-value"),
    holdingsValue: byId("holdings-value"),
    total: byId("total-value"),
    holdings: byId("holdings-list"),
    portfolioStatus: byId("portfolio-status"),
    leaderboard: byId("leaderboard-list"),
    leaderboardStatus: byId("leaderboard-status"),
    leaderboardFull: byId("leaderboard-page-list"),
    leaderboardPageStatus: byId("leaderboard-page-status"),
    leaderboardRefresh: byId("leaderboard-refresh"),
    leaderName: byId("leader-name"),
    leaderValue: byId("leader-value"),
    leaderboardIntro: byId("leaderboard-intro"),
    leaderboardMode: byId("leaderboard-mode"),
    activity: byId("activity-list"),
    activityStatus: byId("activity-status"),
    refresh: byId("refresh-data"),
    shortcut: byId("search-shortcut"),
    toast: byId("toast")
  };

  ui.tradePage.hidden = isLeaderboardPage;
  ui.leaderboardPage.hidden = !isLeaderboardPage;
  document.title = isLeaderboardPage ? "Leaderboard · AI Stocks" : "AI Stocks · Stock Race";
  document.querySelectorAll("[data-route]").forEach((link) => {
    const active = link.dataset.route === (isLeaderboardPage ? "leaderboard" : "trade");
    if (active) link.setAttribute("aria-current", "page");
  });

  const dkk = new Intl.NumberFormat("da-DK", {
    style: "currency", currency: "DKK", maximumFractionDigits: 2
  });
  const number = new Intl.NumberFormat("en", { maximumFractionDigits: 2 });
  const leaderboardNumber = new Intl.NumberFormat("da-DK", { maximumFractionDigits: 2 });
  const decisionTime = new Intl.DateTimeFormat("en", {
    dateStyle: "medium", timeStyle: "short"
  });
  const exhibitionStatuses = new Set(["pending", "queued", "running", "succeeded", "failed"]);
  const clock = new Intl.DateTimeFormat("en", {
    hour: "2-digit", minute: "2-digit", second: "2-digit"
  });

  async function api(path, options = {}) {
    const { headers = {}, ...requestOptions } = options;
    const response = await fetch(`${apiBase}${path}`, {
      credentials: "include",
      ...requestOptions,
      headers: { "Content-Type": "application/json", ...headers }
    });
    if (!response.ok) {
      let problem = null;
      try { problem = await response.json(); } catch { /* response has no JSON */ }
      const error = new Error(problem?.detail || problem?.title || `Request failed (${response.status})`);
      error.status = response.status;
      throw error;
    }
    return response.status === 204 ? null : response.json();
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function serviceResult(scope, succeeded) {
    if (succeeded) state.failures.delete(scope);
    else state.failures.add(scope);
    const healthy = state.failures.size === 0;
    ui.apiState.classList.toggle("online", healthy);
    ui.apiState.classList.toggle("offline", !healthy);
    ui.apiState.lastChild.textContent = healthy ? " Service online" : " Service degraded";
  }

  function renderSearch() {
    ui.results.replaceChildren();
    ui.results.setAttribute("aria-busy", "false");
    ui.resultCount.textContent = `${state.instruments.length} stocks`;
    if (!state.instruments.length) {
      ui.results.append(element("p", "empty", "No preview instruments match that search."));
      return;
    }
    state.instruments.forEach((instrument) => {
      const button = element("button", "stock-result");
      button.type = "button";
      button.classList.toggle("selected", state.selectedInstrument?.id === instrument.id);
      button.setAttribute("aria-pressed", state.selectedInstrument?.id === instrument.id ? "true" : "false");
      button.append(
        element("strong", "", instrument.symbol),
        element("b", "", dkk.format(instrument.priceDkk)),
        element("span", "", instrument.name),
        element("small", "", `${instrument.exchange} · ${instrument.currency}`)
      );
      button.addEventListener("click", () => selectInstrument(instrument));
      ui.results.append(button);
    });
  }

  function selectInstrument(instrument) {
    state.selectedInstrument = instrument;
    ui.orderTitle.textContent = "Paper order";
    ui.orderContext.textContent = instrument.name;
    ui.selectedSymbol.textContent = instrument.symbol;
    ui.selectedQuote.replaceChildren(
      element("strong", "", `${number.format(instrument.price)} ${instrument.currency}`),
      element("span", "", `${dkk.format(instrument.priceDkk)} per share · preview fixture`)
    );
    renderSearch();
    updateEstimate();
    if (window.innerWidth < 760) {
      const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      ui.orderTitle.scrollIntoView({ behavior: reducedMotion ? "auto" : "smooth", block: "center" });
    }
  }

  function setSide(side) {
    state.side = side;
    document.querySelectorAll("[data-action]").forEach((button) => {
      const active = button.dataset.action === side;
      button.classList.toggle("active", active);
      button.setAttribute("aria-pressed", active ? "true" : "false");
    });
    ui.submit.classList.toggle("sell", side === "sell");
    updateEstimate();
  }

  function updateEstimate() {
    const quantity = Number(ui.quantity.value);
    const total = state.selectedInstrument && Number.isInteger(quantity) && quantity > 0
      ? state.selectedInstrument.priceDkk * quantity
      : null;
    ui.estimate.textContent = total === null ? "Select a stock" : dkk.format(total);
    updateSubmitLabel(quantity, total);
  }

  function updateSubmitLabel(quantity = Number(ui.quantity.value), total = null) {
    if (!state.selectedInstrument) {
      ui.submit.textContent = "Select a stock first";
      ui.submit.disabled = true;
      return;
    }
    const validQuantity = Number.isInteger(quantity) && quantity > 0;
    ui.submit.disabled = state.busy || !validQuantity;
    const action = state.side === "buy" ? "Buy" : "Sell";
    ui.submit.textContent = validQuantity && total !== null
      ? `${action} ${quantity} ${state.selectedInstrument.symbol} · ${dkk.format(total)}`
      : "Enter a valid quantity";
  }

  function renderPortfolio(portfolio) {
    state.portfolio = portfolio;
    state.loaded.portfolio = true;
    ui.heroTotal.textContent = dkk.format(portfolio.totalValueDkk);
    ui.heroReturn.textContent = `${portfolio.returnPercent >= 0 ? "+" : ""}${number.format(portfolio.returnPercent)}% return`;
    ui.heroReturn.className = portfolio.returnPercent >= 0 ? "positive" : "negative";
    ui.cash.textContent = dkk.format(portfolio.cashDkk);
    ui.holdingsValue.textContent = dkk.format(portfolio.holdingsValueDkk);
    ui.total.textContent = dkk.format(portfolio.totalValueDkk);
    ui.holdings.replaceChildren();
    if (!portfolio.holdings.length) {
      ui.holdings.append(element("p", "muted-empty", "No holdings yet. Find a stock and place your first paper buy."));
      return;
    }
    portfolio.holdings.forEach((holding) => {
      const row = element("div", "holding-row");
      const name = element("div");
      name.append(element("strong", "", holding.symbol), element("span", "", `${holding.quantity} shares · ${holding.name}`));
      row.append(name, element("b", "", dkk.format(holding.valueDkk)));
      ui.holdings.append(row);
    });
  }

  function renderLeaderboardList(target, data, expanded = false) {
    target.replaceChildren();
    target.setAttribute("aria-busy", "false");
    const returnFormatter = expanded ? leaderboardNumber : number;
    data.items.forEach((entry) => {
      const item = document.createElement("li");
      if (expanded) {
        item.className = "leaderboard-entry";
        item.dataset.rank = String(entry.rank);
      }
      const rank = element("span", "rank", String(entry.rank));
      const identity = element("div");
      identity.append(element("strong", "", entry.displayName), element("small", "", entry.participantType));
      const value = element("b", "", dkk.format(entry.valueDkk));
      const change = element("em", "", `${entry.returnPercent > 0 ? "+" : ""}${returnFormatter.format(entry.returnPercent)}%`);
      change.classList.toggle("negative", entry.returnPercent < 0);
      value.append(change);
      item.append(rank, identity, value);
      target.append(item);
    });
  }

  function renderLeaderboard(data) {
    state.loaded.leaderboard = true;
    renderLeaderboardList(ui.leaderboard, data);
    renderLeaderboardList(ui.leaderboardFull, data, true);
    const leader = data.items[0];
    ui.leaderName.textContent = leader?.displayName || "No participants";
    ui.leaderValue.textContent = leader ? dkk.format(leader.valueDkk) : "—";
  }

  function renderOrders(data) {
    state.loaded.activity = true;
    ui.activity.replaceChildren();
    if (!data.items.length) {
      ui.activity.append(element("p", "muted-empty", "Your filled paper orders will appear here."));
      return;
    }
    data.items.slice(0, 5).forEach((order) => {
      const row = element("div", "activity-row");
      const description = element("div");
      description.append(
        element("strong", order.side, `${order.side.toUpperCase()} ${order.quantity} ${order.symbol}`),
        element("span", "", `${clock.format(new Date(order.filledAt))} · ${dkk.format(order.fillPriceDkk)} each`)
      );
      row.append(description, element("b", "", dkk.format(order.totalDkk)));
      ui.activity.append(row);
    });
  }

  let searchController;
  async function search(query = "") {
    if (searchController) searchController.abort();
    const controller = new AbortController();
    searchController = controller;
    ui.results.setAttribute("aria-busy", "true");
    try {
      const data = await api(`/api/v1/instruments?query=${encodeURIComponent(query)}`, { signal: controller.signal });
      if (searchController !== controller) return;
      state.instruments = data.items;
      serviceResult("search", true);
      renderSearch();
    } catch (error) {
      if (error.name === "AbortError") return;
      if (searchController !== controller) return;
      serviceResult("search", false);
      ui.results.setAttribute("aria-busy", "false");
      ui.results.replaceChildren(element("p", "empty", `Could not load stocks: ${error.message}`));
    } finally {
      if (searchController === controller) searchController = null;
    }
  }

  let refreshGeneration = 0;
  async function refreshAll(showConfirmation = false) {
    const generation = ++refreshGeneration;
    const refreshButtons = [ui.refresh, ui.leaderboardRefresh];
    const leaderboardLists = [ui.leaderboard, ui.leaderboardFull];
    refreshButtons.forEach((button) => {
      button.disabled = true;
      button.textContent = "Refreshing…";
    });
    leaderboardLists.forEach((list) => list.setAttribute("aria-busy", "true"));
    try {
      const results = await Promise.allSettled([
        api("/api/v1/portfolio"), api("/api/v1/leaderboard"), api("/api/v1/orders")
      ]);
      if (generation !== refreshGeneration) return;
      const panels = [
        ["portfolio", ui.portfolioStatus, renderPortfolio],
        ["leaderboard", ui.leaderboardStatus, renderLeaderboard],
        ["activity", ui.activityStatus, renderOrders]
      ];
      const failures = [];
      results.forEach((result, index) => {
        const [scope, status, render] = panels[index];
        const succeeded = result.status === "fulfilled";
        serviceResult(scope, succeeded);
        const statuses = scope === "leaderboard" ? [status, ui.leaderboardPageStatus] : [status];
        statuses.forEach((panelStatus) => panelStatus.classList.toggle("error", !succeeded));
        if (succeeded) {
          statuses.forEach((panelStatus) => { panelStatus.textContent = ""; });
          render(result.value);
        } else {
          failures.push(result.reason);
          const message = state.loaded[scope]
            ? "Refresh failed. Showing the last loaded data."
            : "Could not load this panel. Try Refresh.";
          statuses.forEach((panelStatus) => { panelStatus.textContent = message; });
          if (scope === "leaderboard" && !state.loaded.leaderboard) {
            leaderboardLists.forEach((list) =>
              list.replaceChildren(element("li", "muted-empty", "Standings unavailable.")));
            ui.leaderName.textContent = "Unavailable";
            ui.leaderValue.textContent = "—";
          }
        }
        if (scope === "leaderboard") {
          leaderboardLists.forEach((list) => list.setAttribute("aria-busy", "false"));
        }
      });
      if (failures.length) showToast("Some account data could not be refreshed.");
      else if (showConfirmation) showToast(isLeaderboardPage ? "Leaderboard refreshed." : "Portfolio refreshed.");
    } finally {
      if (generation === refreshGeneration) {
        refreshButtons.forEach((button) => {
          button.disabled = false;
          button.textContent = "Refresh";
        });
      }
    }
  }

  function newIdempotencyKey() {
    if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    return Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
  }

  async function submitOrder(event) {
    event.preventDefault();
    if (!state.selectedInstrument || state.busy) return;
    const quantity = Number(ui.quantity.value);
    if (!Number.isInteger(quantity) || quantity < 1) {
      setFormStatus("Enter a whole quantity of at least 1.", true);
      return;
    }
    state.busy = true;
    ui.submit.disabled = true;
    setFormStatus("Sending paper order…");
    try {
      const order = await api("/api/v1/orders", {
        method: "POST",
        headers: { "Idempotency-Key": newIdempotencyKey() },
        body: JSON.stringify({
          side: state.side,
          instrumentId: state.selectedInstrument.id,
          quantity,
          note: ui.note.value.trim() || null
        })
      });
      ui.note.value = "";
      setFormStatus(`${order.side.toUpperCase()} filled at ${dkk.format(order.fillPriceDkk)}`);
      showToast(`${order.quantity} ${order.symbol} filled · ${dkk.format(order.totalDkk)}`);
      await refreshAll();
    } catch (error) {
      setFormStatus(error.message, true);
    } finally {
      state.busy = false;
      updateEstimate();
    }
  }

  function setFormStatus(message, error = false) {
    ui.formStatus.textContent = message;
    ui.formStatus.classList.toggle("error", error);
  }

  let toastTimer;
  function showToast(message) {
    ui.toast.textContent = message;
    ui.toast.classList.add("show");
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => ui.toast.classList.remove("show"), 3200);
  }

  let searchTimer;
  if (/Mac|iPhone|iPad/.test(navigator.platform)) ui.shortcut.textContent = "⌘ K";
  ui.search.addEventListener("input", () => {
    window.clearTimeout(searchTimer);
    searchTimer = window.setTimeout(() => search(ui.search.value), 180);
  });
  ui.quantity.addEventListener("input", updateEstimate);
  ui.form.addEventListener("submit", submitOrder);
  ui.refresh.addEventListener("click", () => refreshAll(true));
  ui.leaderboardRefresh.addEventListener("click", () =>
    document.body.classList.contains("exhibition-mode") ? refreshAiProgress(true) : refreshAll(true));
  document.querySelectorAll("[data-action]").forEach((button) =>
    button.addEventListener("click", () => setSide(button.dataset.action)));
  document.addEventListener("keydown", (event) => {
    if (!isLeaderboardPage && (event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      ui.search.focus();
    }
  });

  function isExhibitionResponse(data) {
    return window.aiStocksExhibitionContract?.isResponse(data) === true;
  }

  function safeNumber(value) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : 0;
  }

  function detail(label, value) {
    const row = element("div", "ai-detail");
    row.append(element("span", "", label), element("strong", "", value));
    return row;
  }

  function renderAiHoldings(portfolio) {
    const section = element("section", "ai-holdings");
    section.append(element("h3", "", "Holdings"));
    const holdings = Array.isArray(portfolio?.holdings) ? portfolio.holdings : [];
    if (!holdings.length) {
      section.append(element("p", "muted-empty", "No holdings."));
      return section;
    }
    holdings.forEach((holding) => {
      const row = element("div", "holding-row");
      const identity = element("div");
      identity.append(
        element("strong", "", String(holding.symbol || holding.instrumentId || "Unknown instrument")),
        element("span", "", String(holding.name || "Name unavailable")),
        element("small", "holding-isin", String(holding.instrumentId || "ISIN unavailable"))
      );
      const values = element("dl", "holding-detail-grid");
      [
        ["Shares", number.format(safeNumber(holding.quantity))],
        ["Current price", dkk.format(safeNumber(holding.priceDkk))],
        ["Average buy", dkk.format(safeNumber(holding.averageBuyPriceDkk))],
        ["Cost basis", dkk.format(safeNumber(holding.costBasisDkk))],
        ["Market value", dkk.format(safeNumber(holding.valueDkk))],
        ["Gain", `${dkk.format(safeNumber(holding.gainDkk))} · ${safeNumber(holding.gainPercent) >= 0 ? "+" : ""}${number.format(safeNumber(holding.gainPercent))}%`]
      ].forEach(([label, value]) => {
        const item = element("div");
        item.append(element("dt", "", label), element("dd", label === "Gain" && safeNumber(holding.gainDkk) < 0 ? "negative" : "", value));
        values.append(item);
      });
      row.append(identity, values);
      section.append(row);
    });
    return section;
  }

  function renderEvidence(decision) {
    const section = element("section", "ai-evidence");
    section.append(element("h3", "", "Verified sources"));
    const list = element("ul");
    const evidence = Array.isArray(decision?.evidence) ? decision.evidence : [];
    evidence.forEach((source) => {
      let url;
      try { url = new URL(source?.url); } catch { return; }
      if (url.protocol !== "https:") return;
      const item = element("li");
      const link = element("a", "", url.hostname);
      link.href = url.href;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      item.append(link);
      if (source.publishedAt) item.append(element("time", "", `Published ${source.publishedAt}`));
      if (source.exactExcerpt) item.append(element("q", "", String(source.exactExcerpt)));
      if (source.contentSha256) item.append(element("code", "", `SHA-256 ${source.contentSha256}`));
      list.append(item);
    });
    if (!list.childElementCount) list.append(element("li", "muted-empty", "No verified source links supplied."));
    section.append(list);
    return section;
  }

  const svgNamespace = "http://www.w3.org/2000/svg";
  const performanceSelection = new Set();
  const knownPerformanceSeries = new Set();
  let latestPerformance = [];

  function svgElement(tag, attributes = {}, text) {
    const node = document.createElementNS(svgNamespace, tag);
    Object.entries(attributes).forEach(([name, value]) => node.setAttribute(name, String(value)));
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function renderPerformanceFilters(series) {
    ui.modelFilters.replaceChildren();
    ui.benchmarkFilters.replaceChildren();
    series.forEach((item, index) => {
      if (!knownPerformanceSeries.has(item.id)) performanceSelection.add(item.id);
      knownPerformanceSeries.add(item.id);
      const label = element("label", "chart-filter-option");
      const input = document.createElement("input");
      input.type = "checkbox";
      input.checked = performanceSelection.has(item.id);
      input.dataset.seriesId = item.id;
      input.addEventListener("change", () => {
        if (input.checked) performanceSelection.add(item.id);
        else performanceSelection.delete(item.id);
        renderPerformanceChart();
      });
      const swatch = element("i", `series-swatch series-${index % 6}`);
      label.append(input, swatch, document.createTextNode(String(item.label || item.id)));
      if (item.type === "model") ui.modelFilters.append(label);
      if (item.type === "benchmark") ui.benchmarkFilters.append(label);
    });
  }

  function visiblePerformanceSeries() {
    const selected = latestPerformance.filter((series) => performanceSelection.has(series.id));
    const allPoints = selected.flatMap((series) => Array.isArray(series.points) ? series.points : [])
      .map((point) => ({ ...point, timestamp: new Date(point.at).valueOf() }))
      .filter((point) => Number.isFinite(point.timestamp) && Number.isFinite(Number(point.valueDkk)));
    if (!allPoints.length || ui.performanceTime.value === "all") return selected;
    const newest = Math.max(...allPoints.map((point) => point.timestamp));
    const windows = { "24h": 24 * 60 * 60 * 1000, "7d": 7 * 24 * 60 * 60 * 1000, "30d": 30 * 24 * 60 * 60 * 1000 };
    const duration = windows[ui.performanceTime.value];
    if (!duration) return selected;
    return selected.map((series) => ({
      ...series,
      points: series.points.filter((point) => new Date(point.at).valueOf() >= newest - duration)
    }));
  }

  function renderPerformanceChart() {
    const series = visiblePerformanceSeries();
    const plotted = series.map((item) => ({
      ...item,
      points: (Array.isArray(item.points) ? item.points : [])
        .map((point) => ({ at: new Date(point.at), value: Number(point.valueDkk) }))
        .filter((point) => !Number.isNaN(point.at.valueOf()) && Number.isFinite(point.value))
        .sort((left, right) => left.at - right.at)
    })).filter((item) => item.points.length);
    ui.performanceChart.replaceChildren();
    ui.performanceLegend.replaceChildren();
    ui.performanceEmpty.hidden = plotted.length > 0;
    if (!plotted.length) return;

    const width = 720, height = 300, left = 70, right = 18, top = 18, bottom = 42;
    const points = plotted.flatMap((item) => item.points);
    let minTime = Math.min(...points.map((point) => point.at.valueOf()));
    let maxTime = Math.max(...points.map((point) => point.at.valueOf()));
    let minValue = Math.min(...points.map((point) => point.value));
    let maxValue = Math.max(...points.map((point) => point.value));
    if (minTime === maxTime) maxTime += 1;
    const valuePadding = Math.max(10, (maxValue - minValue) * .08);
    minValue -= valuePadding;
    maxValue += valuePadding;
    const x = (at) => left + (at.valueOf() - minTime) / (maxTime - minTime) * (width - left - right);
    const y = (value) => top + (maxValue - value) / (maxValue - minValue) * (height - top - bottom);

    for (let index = 0; index < 5; index += 1) {
      const yPosition = top + index * (height - top - bottom) / 4;
      const value = maxValue - index * (maxValue - minValue) / 4;
      ui.performanceChart.append(
        svgElement("line", { x1: left, y1: yPosition, x2: width - right, y2: yPosition, class: "chart-grid-line" }),
        svgElement("text", { x: left - 10, y: yPosition + 4, class: "chart-axis-label", "text-anchor": "end" },
          leaderboardNumber.format(value))
      );
    }
    const startDate = new Date(minTime), endDate = new Date(maxTime);
    ui.performanceChart.append(
      svgElement("text", { x: left, y: height - 12, class: "chart-axis-label", "text-anchor": "start" }, decisionTime.format(startDate)),
      svgElement("text", { x: width - right, y: height - 12, class: "chart-axis-label", "text-anchor": "end" }, decisionTime.format(endDate))
    );

    plotted.forEach((item) => {
      const sourceIndex = latestPerformance.findIndex((series) => series.id === item.id);
      const className = `chart-series series-${sourceIndex % 6}${item.type === "benchmark" ? " benchmark" : ""}`;
      const path = item.points.map((point, index) => `${index ? "L" : "M"}${x(point.at).toFixed(1)},${y(point.value).toFixed(1)}`).join(" ");
      const line = svgElement("path", { d: path, class: className });
      line.append(svgElement("title", {}, `${item.label}: ${dkk.format(item.points.at(-1).value)}`));
      ui.performanceChart.append(line);
      const last = item.points.at(-1);
      ui.performanceChart.append(svgElement("circle", {
        cx: x(last.at), cy: y(last.value), r: 3.5, class: `chart-point series-${sourceIndex % 6}`
      }));
      const legend = element("span", "chart-legend-item");
      legend.append(element("i", `series-swatch series-${sourceIndex % 6}`),
        document.createTextNode(`${item.label} · ${dkk.format(last.value)}`));
      ui.performanceLegend.append(legend);
    });
  }

  function renderPerformance(data) {
    latestPerformance = Array.isArray(data.performance) ? data.performance.filter((series) =>
      series && Array.isArray(series.points) && (series.type === "model" || series.type === "benchmark")) : [];
    renderPerformanceFilters(latestPerformance);
    renderPerformanceChart();
  }

  function renderAiParticipants(data) {
    ui.aiParticipants.replaceChildren();
    data.participants.forEach((participant) => {
      const portfolio = participant.portfolio || {};
      const decision = participant.latestDecision;
      const status = exhibitionStatuses.has(String(participant.status).toLowerCase())
        ? String(participant.status).toLowerCase() : "degraded";
      const card = element("article", `panel ai-card status-${status}`);
      const heading = element("header", "ai-card-heading");
      const identity = element("div");
      identity.append(
        element("h2", "", String(participant.displayName || participant.modelId)),
        element("p", "model-id", String(participant.modelId))
      );
      heading.append(identity, element("span", `status-badge ${status}`, status));
      const metrics = element("div", "metrics ai-metrics");
      metrics.append(
        detail("Cash", dkk.format(safeNumber(portfolio.cashDkk))),
        detail("Holdings value", dkk.format(safeNumber(portfolio.holdingsValueDkk))),
        detail("Total", dkk.format(safeNumber(portfolio.totalValueDkk)))
      );
      const completedAt = decision?.completedAt ? new Date(decision.completedAt) : null;
      const validTime = completedAt && !Number.isNaN(completedAt.valueOf());
      const narrative = element("div", "ai-narrative");
      narrative.append(
        detail("Status", status),
        detail("Failure", participant.error ? String(participant.error) : "None"),
        detail("Last action", decision?.action ? String(decision.action) : "No completed decision"),
        detail("Decision time", validTime ? decisionTime.format(completedAt) : "Not available"),
        detail("Rationale", decision?.reason ? String(decision.reason) : "No rationale available"),
        detail("Confidence", Number.isFinite(Number(decision?.confidence)) ? `${number.format(Number(decision.confidence) * 100)}%` : "Not available")
      );
      card.append(heading, metrics, narrative, renderAiHoldings(portfolio), renderEvidence(decision));
      ui.aiParticipants.append(card);
    });
    ui.aiParticipants.setAttribute("aria-busy", "false");
  }

  function renderAiActivity(data) {
    ui.aiActivity.replaceChildren();
    const activity = Array.isArray(data.activity) ? data.activity.slice(0, 20) : [];
    if (!activity.length) {
      ui.aiActivity.append(element("p", "muted-empty", "No autonomous runs completed yet."));
      return;
    }
    activity.forEach((entry) => {
      const row = element("div", "activity-row");
      const description = element("div");
      const occurredAt = new Date(entry.occurredAt);
      const validTime = !Number.isNaN(occurredAt.valueOf());
      const action = entry.action ? ` · ${String(entry.action).toUpperCase()}` : "";
      description.append(
        element("strong", "", `${String(entry.modelId || "Unknown model")} · ${String(entry.status || "unknown")}${action}`),
        element("span", "", validTime ? clock.format(occurredAt) : "Time unavailable"),
        element("span", entry.error ? "error" : "", String(entry.error || entry.reason || "No detail supplied"))
      );
      row.append(description);
      ui.aiActivity.append(row);
    });
  }

  function exhibitionLeaderboard(data) {
    const sorted = [...data.participants].sort((left, right) =>
      safeNumber(right.portfolio?.totalValueDkk) - safeNumber(left.portfolio?.totalValueDkk));
    const values = sorted.map((participant) => safeNumber(participant.portfolio?.totalValueDkk));
    return { items: sorted.map((participant, index) => {
      const value = values[index];
      return {
        rank: values.filter((candidate) => candidate > value).length + 1,
        displayName: participant.displayName || participant.modelId,
        participantType: `AI · ${participant.modelId}`,
        valueDkk: value,
        returnPercent: safeNumber(participant.portfolio?.returnPercent)
      };
    }) };
  }

  function activateExhibition(data) {
    if (!isExhibitionResponse(data)) return false;
    document.body.classList.add("exhibition-mode");
    ui.exhibitionFailurePage.hidden = true;
    document.querySelectorAll("[data-exhibition-only]").forEach((element) => {
      element.hidden = false;
    });
    const nordic = data.dataMode === "official-nasdaq-nordic-15m-delayed-ecb-fx";
    ui.dataBadge.textContent = nordic
      ? "ASSUMED FILLS · Nasdaq Nordic delayed post-trade · ECB reference FX · non-live"
      : "ASSUMED FILLS · Official Nasdaq XSTO · delayed · non-live · paper-only";
    const disclosure = nordic
      ? "Inputs are official Nasdaq Nordic observations at least 15-minute delayed and non-live. DKK conversions use checksum-bound ECB informational reference rates—not executable transaction rates. Paper fills use 1% adverse slippage; no broker or real orders exist. Portfolios are volatile. This private exhibition is not licensed global coverage or the strict 2026 contest."
      : "Inputs are official Nasdaq XSTO observations at least 15-minute delayed and non-live. Paper fills use fixed 0.65 DKK/SEK and 1% adverse slippage; no broker or real orders exist. Portfolios are volatile. This is not the strict 2026 contest.";
    document.querySelectorAll("[data-exhibition-disclosure-copy]").forEach((element) => {
      element.textContent = disclosure;
    });
    document.querySelector('[data-route="trade"]').textContent = "AI Race";
    ui.leaderboardIntro.textContent = "Four fixed AI participants ranked by assumed-fill paper portfolio value in DKK.";
    ui.leaderboardMode.textContent = "ASSUMED FILLS · AI-only non-live exhibition";
    ui.tradePage.hidden = true;
    ui.aiRacePage.hidden = false;
    ui.leaderboardPage.hidden = !isLeaderboardPage;
    ui.aiRacePage.hidden = isLeaderboardPage;
    renderPerformance(data);
    renderAiParticipants(data);
    renderAiActivity(data);
    renderLeaderboard(exhibitionLeaderboard(data));
    ui.aiRaceStatus.textContent = "";
    ui.aiRaceStatus.classList.remove("error");
    ui.leaderboardPageStatus.textContent = "";
    ui.leaderboardPageStatus.classList.remove("error");
    serviceResult("ai-progress", true);
    return true;
  }

  function showExhibitionFailure(message) {
    ui.tradePage.hidden = true;
    ui.aiRacePage.hidden = true;
    ui.leaderboardPage.hidden = true;
    ui.exhibitionFailurePage.hidden = false;
    ui.exhibitionFailureMessage.textContent = message;
    serviceResult("ai-progress", false);
  }

  let aiRefreshGeneration = 0;
  let aiRefreshController;
  let aiRefreshInterval;
  async function refreshAiProgress(showConfirmation = false) {
    const generation = ++aiRefreshGeneration;
    if (aiRefreshController) aiRefreshController.abort();
    const controller = new AbortController();
    aiRefreshController = controller;
    ui.aiRefresh.disabled = true;
    ui.aiRefresh.textContent = "Refreshing…";
    try {
      const data = await api("/api/v1/ai-progress", { signal: controller.signal });
      if (generation !== aiRefreshGeneration) return;
      if (!activateExhibition(data)) throw new Error("Invalid AI exhibition response.");
      if (!aiRefreshInterval) aiRefreshInterval = window.setInterval(refreshAiProgress, 60000);
      if (showConfirmation) showToast(isLeaderboardPage ? "AI leaderboard refreshed." : "AI race refreshed.");
    } catch (error) {
      if (error.name === "AbortError" || generation !== aiRefreshGeneration) return;
      if (!document.body.classList.contains("exhibition-mode")) {
        showExhibitionFailure("AI race unavailable. Human trading controls remain hidden.");
      } else {
        const message = "Refresh failed. Showing the last loaded AI delayed-data snapshot.";
        ui.aiRaceStatus.textContent = message;
        ui.aiRaceStatus.classList.add("error");
        ui.leaderboardPageStatus.textContent = message;
        ui.leaderboardPageStatus.classList.add("error");
        serviceResult("ai-progress", false);
      }
    } finally {
      if (generation === aiRefreshGeneration) {
        aiRefreshController = null;
        ui.aiRefresh.disabled = false;
        ui.aiRefresh.textContent = "Refresh AI race";
      }
    }
  }

  function startHumanPreview() {
    return isLeaderboardPage ? refreshAll() : Promise.all([search(), refreshAll()]);
  }

  async function start() {
    try {
      const data = await api("/api/v1/ai-progress");
      if (activateExhibition(data)) {
        aiRefreshInterval = window.setInterval(refreshAiProgress, 60000);
        return;
      }
      showExhibitionFailure("AI race unavailable. Human trading controls remain hidden.");
      return;
    } catch (error) {
      if (error.status !== 404) {
        showExhibitionFailure("AI race unavailable. Human trading controls remain hidden.");
        return;
      }
    }
    await startHumanPreview();
  }

  ui.aiRefresh.addEventListener("click", () => refreshAiProgress(true));
  ui.performanceTime.addEventListener("change", renderPerformanceChart);
  start().catch(() => {});
})();
