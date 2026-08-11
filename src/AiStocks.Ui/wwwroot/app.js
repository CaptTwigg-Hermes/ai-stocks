(() => {
  "use strict";

  const apiBase = window.AISTOCKS_API_URL;
  if (!apiBase) throw new Error("AI Stocks runtime configuration is missing.");
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
    activity: byId("activity-list"),
    activityStatus: byId("activity-status"),
    refresh: byId("refresh-data"),
    shortcut: byId("search-shortcut"),
    toast: byId("toast")
  };

  const dkk = new Intl.NumberFormat("da-DK", {
    style: "currency", currency: "DKK", maximumFractionDigits: 2
  });
  const number = new Intl.NumberFormat("en", { maximumFractionDigits: 2 });
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
      throw new Error(problem?.detail || problem?.title || `Request failed (${response.status})`);
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

  function renderLeaderboard(data) {
    state.loaded.leaderboard = true;
    ui.leaderboard.replaceChildren();
    data.items.forEach((entry) => {
      const item = document.createElement("li");
      const rank = element("span", "rank", String(entry.rank));
      const identity = element("div");
      identity.append(element("strong", "", entry.displayName), element("small", "", entry.participantType));
      const value = element("b", "", dkk.format(entry.valueDkk));
      const change = element("em", "", `${entry.returnPercent >= 0 ? "+" : ""}${number.format(entry.returnPercent)}%`);
      change.classList.toggle("negative", entry.returnPercent < 0);
      value.append(change);
      item.append(rank, identity, value);
      ui.leaderboard.append(item);
    });
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
    ui.refresh.disabled = true;
    ui.refresh.textContent = "Refreshing…";
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
        status.classList.toggle("error", !succeeded);
        if (succeeded) {
          status.textContent = "";
          render(result.value);
        } else {
          failures.push(result.reason);
          status.textContent = state.loaded[scope]
            ? "Refresh failed. Showing the last loaded data."
            : "Could not load this panel. Try Refresh.";
        }
      });
      if (failures.length) showToast("Some account data could not be refreshed.");
      else if (showConfirmation) showToast("Portfolio refreshed.");
    } finally {
      if (generation === refreshGeneration) {
        ui.refresh.disabled = false;
        ui.refresh.textContent = "Refresh";
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
  document.querySelectorAll("[data-action]").forEach((button) =>
    button.addEventListener("click", () => setSide(button.dataset.action)));
  document.addEventListener("keydown", (event) => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      ui.search.focus();
    }
  });

  Promise.all([search(), refreshAll()]).catch(() => {});
})();
