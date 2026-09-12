import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "test/browser",
  testMatch: "*.spec.mjs",
  workers: 1,
  timeout: 30_000,
  use: { baseURL: "http://127.0.0.1:8798", browserName: "chromium", headless: true },
  webServer: {
    command: "node test/browser/server.mjs",
    url: "http://127.0.0.1:8798/healthz",
    reuseExistingServer: false,
  },
});
