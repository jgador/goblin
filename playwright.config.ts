import { defineConfig } from "@playwright/test";

export default defineConfig({
    testDir: "tests/e2e",
    testMatch: "*.spec.ts",
    workers: 1,
    timeout: 30_000,
    use: {
        baseURL: "http://127.0.0.1:8798",
        browserName: "chromium",
        headless: true,
    },
    webServer: {
        command: "node dist/tests/support/browser-server.js",
        url: "http://127.0.0.1:8798/healthz",
        reuseExistingServer: false,
    },
});
