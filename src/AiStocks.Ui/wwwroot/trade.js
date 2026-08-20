(() => {
  "use strict";

  const apiBase = window.AISTOCKS_API_URL || "";
  const byId = id => document.getElementById(id);
  const ui = {
    user: byId("current-user"),
    market: byId("market-data-state"),
    races: byId("race-list"),
    raceDetail: byId("race-detail"),
    join: byId("join-race"),
    searchForm: byId("instrument-search"),
    query: byId("instrument-query"),
    results: byId("instrument-results"),
    selected: byId("selected-instrument"),
    orderForm: byId("human-order-form"),
    quantity: byId("order-quantity"),
    note: byId("order-note"),
    submit: byId("submit-order"),
    orderStatus: byId("order-status"),
    portfolio: byId("portfolio-content"),
    orders: byId("orders-content"),
    leaderboard: byId("leaderboard-content")
  };
  const state = { races: [], race: null, instrument: null, side: "buy", busy: false };
  const dkk = new Intl.NumberFormat("da-DK", { style: "currency", currency: "DKK", maximumFractionDigits: 2 });
  const number = new Intl.NumberFormat("en", { maximumFractionDigits: 2 });

  function text(tag, value, className = "") {
    const node = document.createElement(tag);
    if (className) node.className = className;
    node.textContent = value == null ? "Unavailable" : String(value);
    return node;
  }

  async function api(path, options = {}) {
    const { headers = {}, ...rest } = options;
    const response = await fetch(`${apiBase}${path}`, {
      credentials: "include",
      ...rest,
      headers: { "Content-Type": "application/json", ...headers }
    });
    if (!response.ok) {
      let problem;
      try { problem = await response.json(); } catch { /* no JSON body */ }
      throw new Error(problem?.detail || problem?.title || `Request failed (${response.status})`);
    }
    return response.status === 204 ? null : response.json();
  }

  function items(data) {
    return Array.isArray(data?.items) ? data.items : Array.isArray(data) ? data : [];
  }

  function raceId() {
    return state.race?.id;
  }

  function setError(target, message) {
    target.replaceChildren(text("p", message, "error"));
  }

  function marketMessage(data = state.race) {
    const mode = String(data?.dataMode || "non-production").replaceAll("_", " ");
    const status = String(data?.marketDataStatus || data?.valuationStatus || "unavailable").replaceAll("_", " ");
    ui.market.textContent = `${mode} · Market data ${status}. Server responses are authoritative; the browser never invents quotes, FX, fills or valuations.`;
  }

  function renderRaces() {
    ui.races.replaceChildren();
    state.races.forEach(race => {
      const option = text("option", race.name || race.id);
      option.value = race.id;
      ui.races.append(option);
    });
    ui.races.disabled = state.races.length < 2;
    if (!state.races.length) {
      ui.races.append(text("option", "No races available"));
      ui.raceDetail.textContent = "No global human race is currently available.";
      ui.join.disabled = true;
      return;
    }
    selectRace(state.races.find(race => race.id === ui.races.value) || state.races[0]);
  }

  function selectRace(race) {
    state.race = race;
    ui.races.value = race.id;
    const base = race.baseCurrency || "DKK";
    const initialCash = race.initialCashDkk ?? race.startingCashDkk;
    const start = Number.isFinite(Number(initialCash)) ? dkk.format(Number(initialCash)) : "100,000 DKK";
    ui.raceDetail.textContent = `${race.status || "Status unavailable"} · ${base} base · ${start} starting cash`;
    ui.join.disabled = Boolean(race.joined) || state.busy;
    ui.join.textContent = race.joined ? "Joined" : "Join race";
    marketMessage(race);
    state.instrument = null;
    updateOrderButton();
    if (race.joined) refreshRace();
    else clearAccountPanels();
  }

  function clearAccountPanels() {
    ui.portfolio.replaceChildren(text("p", "Join this race to load your own DKK portfolio.", "empty"));
    ui.orders.replaceChildren(text("p", "Join this race to load order statuses.", "empty"));
    ui.leaderboard.replaceChildren(text("li", "Join this race to load standings.", "empty"));
  }

  async function joinRace() {
    if (!raceId() || state.busy) return;
    state.busy = true;
    ui.join.disabled = true;
    ui.join.textContent = "Joining…";
    try {
      const joined = await api(`/api/v1/races/${raceId()}/join`, {
        method: "POST",
        headers: { "Idempotency-Key": crypto.randomUUID() },
        body: "{}"
      });
      state.race = { ...state.race, ...joined, joined: true };
      state.races = state.races.map(race => race.id === raceId() ? state.race : race);
      ui.join.textContent = "Joined";
      await refreshRace();
    } catch (error) {
      ui.join.textContent = "Try joining again";
      ui.join.disabled = false;
      ui.raceDetail.textContent = `Could not join race: ${error.message}`;
    } finally {
      state.busy = false;
    }
  }

  function metric(label, value) {
    const row = document.createElement("div");
    row.className = "metric";
    row.append(text("span", label), text("strong", value));
    return row;
  }

  function renderPortfolio(data) {
    ui.portfolio.replaceChildren();
    const metrics = document.createElement("div");
    metrics.className = "metrics";
    metrics.append(
      metric("Cash", Number.isFinite(Number(data.cashDkk)) ? dkk.format(Number(data.cashDkk)) : "Unavailable"),
      metric("Holdings", Number.isFinite(Number(data.holdingsValueDkk)) ? dkk.format(Number(data.holdingsValueDkk)) : "Unavailable"),
      metric("Total", Number.isFinite(Number(data.totalValueDkk)) ? dkk.format(Number(data.totalValueDkk)) : "Unavailable")
    );
    ui.portfolio.append(metrics);
    const holdings = Array.isArray(data.holdings) ? data.holdings : [];
    if (!holdings.length) ui.portfolio.append(text("p", "No holdings reported by the API.", "empty"));
    holdings.forEach(holding => {
      const row = document.createElement("div");
      row.className = "holding";
      row.append(
        text("strong", `${holding.symbol || holding.instrumentId || "Unknown"} · ${holding.quantity ?? "?"} shares`),
        text("span", holding.name || "Name unavailable"),
        text("span", Number.isFinite(Number(holding.valueDkk)) ? dkk.format(Number(holding.valueDkk)) : "Value unavailable")
      );
      ui.portfolio.append(row);
    });
    marketMessage(data);
  }

  function renderOrders(data) {
    ui.orders.replaceChildren();
    const orders = items(data);
    if (!orders.length) ui.orders.append(text("p", "No orders reported by the API.", "empty"));
    orders.forEach(order => {
      const row = document.createElement("div");
      row.className = "order-row";
      row.append(
        text("strong", `${String(order.side || "order").toUpperCase()} ${order.quantity ?? "?"} ${order.symbol || order.instrumentId || "instrument"}`),
        text("span", `Status: ${order.status || "unavailable"}`)
      );
      if (order.note) row.append(text("span", order.note));
      ui.orders.append(row);
    });
  }

  function renderLeaderboard(data) {
    ui.leaderboard.replaceChildren();
    const leaders = items(data);
    if (!leaders.length) ui.leaderboard.append(text("li", "No standings reported by the API.", "empty"));
    leaders.forEach(entry => {
      const value = Number.isFinite(Number(entry.valueDkk)) ? dkk.format(Number(entry.valueDkk)) : "Value unavailable";
      const change = Number.isFinite(Number(entry.returnPercent)) ? `${number.format(Number(entry.returnPercent))}%` : "Return unavailable";
      ui.leaderboard.append(text("li", `${entry.rank ?? "—"}. ${entry.displayName || "Anonymous"} · ${entry.participantType || "participant"} · ${value} · ${change}`));
    });
    marketMessage(data);
  }

  async function refreshRace() {
    if (!raceId()) return;
    const id = raceId();
    const endpoints = [
      [`/api/v1/races/${id}/accounts/me/portfolio`, ui.portfolio, renderPortfolio],
      [`/api/v1/races/${id}/accounts/me/orders`, ui.orders, renderOrders],
      [`/api/v1/races/${id}/leaderboard`, ui.leaderboard, renderLeaderboard]
    ];
    const results = await Promise.allSettled(endpoints.map(([path]) => api(path)));
    results.forEach((result, index) => {
      const [, target, render] = endpoints[index];
      if (result.status === "fulfilled") render(result.value);
      else setError(target, `Unavailable: ${result.reason.message}`);
    });
  }

  function renderInstruments(data) {
    ui.results.replaceChildren();
    marketMessage(data);
    const instruments = items(data);
    if (!instruments.length) {
      ui.results.append(text("p", "No matching instruments reported by the API.", "empty"));
      return;
    }
    instruments.forEach(instrument => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "instrument-result";
      button.append(
        text("strong", instrument.symbol || instrument.id || instrument.instrumentId || "Unknown"),
        text("small", instrument.currency || "Currency unavailable"),
        text("span", instrument.name || "Name unavailable"),
        text("small", `${instrument.exchange || instrument.mic || "Exchange unavailable"} · ${instrument.tradabilityReason || (instrument.tradable ? "tradable" : "status unavailable")}`)
      );
      button.addEventListener("click", () => selectInstrument(instrument));
      ui.results.append(button);
    });
  }

  function selectInstrument(instrument) {
    state.instrument = instrument;
    ui.selected.textContent = `${instrument.symbol || instrument.id || instrument.instrumentId} · ${instrument.name || "Name unavailable"} · ${instrument.currency || "Currency unavailable"}`;
    marketMessage(instrument);
    updateOrderButton();
  }

  function updateOrderButton() {
    const quantity = Number(ui.quantity.value);
    const valid = Number.isInteger(quantity) && quantity > 0;
    ui.submit.disabled = state.busy || !state.race?.joined || !state.instrument || !valid;
    ui.submit.textContent = state.instrument
      ? valid ? `Submit ${state.side} intent` : "Enter a whole quantity"
      : "Choose an instrument";
  }

  async function search(event) {
    event.preventDefault();
    const query = ui.query.value.trim();
    if (query.length < 2) return;
    const params = new URLSearchParams();
    params.set("q", query);
    params.set("limit", "20");
    ui.results.replaceChildren(text("p", "Searching…", "empty"));
    try {
      const searchPath = "/api/v1/instruments/search";
      renderInstruments(await api(`${searchPath}?${params}`));
    } catch (error) {
      setError(ui.results, `Search unavailable: ${error.message}`);
      marketMessage({ dataMode: "non-production", marketDataStatus: "unavailable" });
    }
  }

  function setSide(side) {
    state.side = side;
    document.querySelectorAll("[data-side]").forEach(button =>
      button.setAttribute("aria-pressed", button.dataset.side === side ? "true" : "false"));
    updateOrderButton();
  }

  async function submitOrder(event) {
    event.preventDefault();
    if (ui.submit.disabled || !raceId() || !state.instrument) return;
    const quantity = Number(ui.quantity.value);
    const payload = { side: state.side, instrumentId: state.instrument.id || state.instrument.instrumentId, quantity };
    const note = ui.note.value.trim();
    if (note) payload.note = note;
    state.busy = true;
    updateOrderButton();
    ui.orderStatus.textContent = "Submitting intent to the API…";
    ui.orderStatus.classList.remove("error");
    try {
      const order = await api(`/api/v1/races/${raceId()}/accounts/me/orders`, {
        method: "POST",
        headers: { "Idempotency-Key": crypto.randomUUID() },
        body: JSON.stringify(payload)
      });
      ui.orderStatus.textContent = `Order ${order.id || "accepted"}: ${order.status || "status unavailable"}. No client-side fill was assumed.`;
      ui.note.value = "";
      await refreshRace();
    } catch (error) {
      ui.orderStatus.textContent = `Order rejected or unavailable: ${error.message}`;
      ui.orderStatus.classList.add("error");
    } finally {
      state.busy = false;
      updateOrderButton();
    }
  }

  async function start() {
    try {
      const [me, races] = await Promise.all([api("/api/v1/me"), api("/api/v1/races")]);
      ui.user.textContent = `${me.displayName || me.email || me.identity || me.id || "Authenticated user"}${me.email && me.displayName ? ` · ${me.email}` : ""}`;
      state.races = items(races);
      renderRaces();
    } catch (error) {
      ui.user.textContent = `Account unavailable: ${error.message}`;
      ui.user.classList.add("error");
      ui.raceDetail.textContent = "Races unavailable. Try reloading after the API recovers.";
      marketMessage({ dataMode: "non-production", marketDataStatus: "unavailable" });
    }
  }

  ui.races.addEventListener("change", () => selectRace(state.races.find(race => race.id === ui.races.value)));
  ui.join.addEventListener("click", joinRace);
  ui.searchForm.addEventListener("submit", search);
  ui.quantity.addEventListener("input", updateOrderButton);
  ui.orderForm.addEventListener("submit", submitOrder);
  document.querySelectorAll("[data-side]").forEach(button => button.addEventListener("click", () => setSide(button.dataset.side)));
  start().catch(() => {});
})();
