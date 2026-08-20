const { test, expect } = require("@playwright/test");
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "../../src/AiStocks.Ui/wwwroot");
const requests = [];
let joined = false;
const hostile = `<img src=x onerror=alert(1)>${"X".repeat(180)}`;

const fixtures = {
  me: { id: "user-1", displayName: hostile, email: "human@example.test" },
  race: {
    id: "race-global", name: `Global humans ${hostile}`, status: "running",
    kind: "human_sandbox", baseCurrency: "DKK", startingCashDkk: 100000,
    joined: false, dataMode: "non-production", marketDataStatus: "unavailable"
  },
  instrument: {
    instrumentId: "US0378331005:XNAS", symbol: hostile, name: hostile,
    exchange: "NASDAQ", mic: "XNAS", country: "US", currency: "USD",
    tradable: false, tradabilityReason: "market_data_unavailable",
    marketDataStatus: "unavailable", quoteDelayMinutes: null
  }
};

function json(response, status, body) {
  response.writeHead(status, { "Content-Type": "application/json" });
  response.end(JSON.stringify(body));
}

function asset(response, file, type) {
  response.writeHead(200, { "Content-Type": type });
  response.end(fs.readFileSync(path.join(root, file)));
}

let server;
test.beforeAll(async () => {
  server = http.createServer((request, response) => {
    const url = new URL(request.url, "http://127.0.0.1:4177");
    let body = "";
    request.on("data", chunk => { body += chunk; });
    request.on("end", () => {
      requests.push({
        method: request.method,
        path: url.pathname,
        query: url.search,
        body,
        idempotencyKey: request.headers["idempotency-key"]
      });
      if (url.pathname === "/") return asset(response, "index.html", "text/html");
      if (url.pathname === "/trade") return asset(response, "trade.html", "text/html");
      if (url.pathname === "/trade.js") return asset(response, "trade.js", "text/javascript");
      if (url.pathname === "/trade.css") return asset(response, "trade.css", "text/css");
      if (url.pathname === "/runtime-config.js") {
        response.writeHead(200, { "Content-Type": "text/javascript" });
        return response.end("window.AISTOCKS_API_URL='';");
      }
      if (url.pathname === "/api/v1/me") return json(response, 200, fixtures.me);
      if (url.pathname === "/api/v1/races") return json(response, 200, { items: [{ ...fixtures.race, joined }] });
      if (url.pathname === "/api/v1/races/race-global/join" && request.method === "POST") {
        joined = true;
        return json(response, 200, { ...fixtures.race, joined: true });
      }
      if (url.pathname === "/api/v1/instruments/search") return json(response, 200, {
        items: [fixtures.instrument], dataMode: "non-production", marketDataStatus: "unavailable"
      });
      if (url.pathname.endsWith("/portfolio")) return json(response, 200, {
        startingCashDkk: 100000, cashDkk: 100000, holdingsValueDkk: 0,
        totalValueDkk: 100000, returnPercent: 0, holdings: [], currency: "DKK",
        valuationStatus: "unavailable"
      });
      if (url.pathname.endsWith("/leaderboard")) return json(response, 200, {
        items: [{ rank: 1, displayName: hostile, participantType: "human", valueDkk: 100000, returnPercent: 0 }],
        valuationStatus: "unavailable"
      });
      if (url.pathname.endsWith("/orders") && request.method === "GET") return json(response, 200, {
        items: [{ id: "order-old", side: "buy", symbol: hostile, quantity: 1, status: "queued", note: hostile }]
      });
      if (url.pathname.endsWith("/orders") && request.method === "POST") return json(response, 202, {
        id: "order-new", side: "sell", instrumentId: fixtures.instrument.instrumentId,
        symbol: hostile, quantity: 2, status: "queued", note: hostile
      });
      response.writeHead(404); response.end();
    });
  });
  await new Promise(resolve => server.listen(4177, "127.0.0.1", resolve));
});

test.afterAll(async () => new Promise(resolve => server.close(resolve)));
test.beforeEach(() => { requests.length = 0; joined = false; });

for (const viewport of [{ name: "desktop", width: 1280, height: 900 }, { name: "mobile-320", width: 320, height: 900 }]) {
  test(`${viewport.name}: dedicated route loads safely without horizontal overflow`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.goto("/trade");
    await expect(page.locator("#v2-trade-page")).toBeVisible();
    await expect(page.locator('a[aria-current="page"]')).toHaveAttribute("href", "/trade");
    await expect(page.locator("#market-data-state")).toContainText(/non-production/i);
    await expect(page.locator("#market-data-state")).toContainText(/unavailable/i);
    await expect(page.locator("#current-user")).toContainText("<img");
    await expect(page.locator("#current-user img")).toHaveCount(0);
    const geometry = await page.evaluate(() => ({
      viewport: innerWidth,
      document: document.documentElement.scrollWidth,
      controls: [...document.querySelectorAll("button, input, textarea, select, a")]
        .filter(element => element.getClientRects().length)
        .map(element => ({ tag: element.tagName, width: element.getBoundingClientRect().width, height: element.getBoundingClientRect().height }))
    }));
    expect(geometry.document).toBeLessThanOrEqual(geometry.viewport);
    expect(geometry.controls.every(control => control.height >= 44)).toBeTruthy();
  });
}

test("navigation keeps legacy dashboard and exposes dedicated trade route", async ({ page }) => {
  await page.goto("/");
  await expect(page.locator("#trade-page")).toBeAttached();
  await page.getByRole("link", { name: /human trade/i }).click();
  await expect(page).toHaveURL(/\/trade$/);
  await page.getByRole("link", { name: "Legacy dashboard", exact: true }).click();
  await expect(page).toHaveURL(/\/$/);
});

test("join search and human order use exact v2 endpoints and intent-only payload", async ({ page }) => {
  await page.goto("/trade");
  await page.getByRole("button", { name: /join race/i }).click();
  await page.getByLabel("Search global stocks").fill("Apple & sons");
  await page.getByRole("button", { name: /search/i }).click();
  await page.locator("#instrument-results button").click();
  await page.getByRole("button", { name: "Sell" }).click();
  await page.getByLabel("Quantity").fill("2");
  await page.getByLabel(/optional note/i).fill(hostile);
  await page.getByRole("button", { name: /submit sell intent/i }).click();
  await expect(page.locator("#order-status")).toContainText(/queued/i);

  expect(requests.some(item => item.method === "POST" && item.path === "/api/v1/races/race-global/join")).toBeTruthy();
  expect(requests.some(item => item.path === "/api/v1/instruments/search" && item.query === "?q=Apple+%26+sons&limit=20")).toBeTruthy();
  const order = requests.find(item => item.method === "POST" && item.path === "/api/v1/races/race-global/accounts/me/orders");
  expect(order.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
  expect(JSON.parse(order.body)).toEqual({
    side: "sell", instrumentId: fixtures.instrument.instrumentId, quantity: 2, note: hostile
  });
  expect(order.body).not.toContain("fill");
  expect(order.body).not.toContain("price");
  expect(order.body).not.toContain("clientPreview");
});