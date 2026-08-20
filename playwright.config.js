const { defineConfig } = require("@playwright/test");

module.exports = defineConfig({
  workers: 1,
  retries: 0,
  timeout: 20_000,
  use: { baseURL: "http://127.0.0.1:4177", trace: "off" }
});