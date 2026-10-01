import { environmentVariables as Env } from "../config/environment.mjs";
import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { join, resolve } from "node:path";
import { createServer } from "node:net";
import { once } from "node:events";
import { chromium, expect } from "@playwright/test";
import { startBackend, writePasswordHash } from "../tests/support/backend.js";

// A real VictoriaLogs, reached through isolated Goblin authentication. This
// check reads synthetic log data; it never uses saved Goblin credentials.
const victoriaLogsUrl = process.env[Env.GOBLIN_TEST_VICTORIALOGS_URL.name];
if (!victoriaLogsUrl)
    throw new Error(
        "Set GOBLIN_TEST_VICTORIALOGS_URL to a private VictoriaLogs origin configured with -http.pathPrefix=/logs.",
    );
const marker = process.env[Env.GOBLIN_TEST_LOG_MARKER.name];
const azureHost = "goblin-logs-test.southeastasia.cloudapp.azure.com";
const browser = await chromium.launch({
    args: [
        `--host-resolver-rules=MAP ${azureHost} 127.0.0.1`,
        "--no-proxy-server",
    ],
});
const artifacts = resolve(".artifacts/logs-ui");
await mkdir(artifacts, { recursive: true });
try {
    for (const hostname of ["127.0.0.1", azureHost]) {
        const dataDir = await mkdtemp(join(artifacts, "runtime-"));
        const listener = createServer().listen(0, "127.0.0.1");
        await once(listener, "listening");
        const { port } = listener.address() as { port: number };
        await new Promise<void>((resolve) => listener.close(() => resolve()));
        const publicOrigin = `http://${hostname}:${port}`;
        const passwordHashFile = join(dataDir, "owner-password");
        await writePasswordHash(passwordHashFile, "logging-browser-test");
        const app = await startBackend({
            dataDir,
            passwordHashFile,
            victoriaLogsUrl,
            publicOrigin,
            allowInsecureHttp: true,
            listenUrl: `http://127.0.0.1:${port}`,
        });
        const context = await browser.newContext({
            timezoneId: "Asia/Manila",
        });
        await context.route("https://ipwho.is/**", (route) =>
            route.fulfill({
                json: { success: true, timezone: { id: "Asia/Manila" } },
                headers: { "access-control-allow-origin": "*" },
            }),
        );
        const page = await context.newPage();
        const errors: string[] = [];
        page.on("pageerror", (error) => errors.push(error.message));
        page.on("console", (message) => {
            if (
                message.type() === "error" &&
                /Content Security Policy|Refused to/.test(message.text())
            )
                errors.push(message.text());
        });
        try {
            await page.goto(publicOrigin + "/logs/");
            await expect(page).toHaveURL(publicOrigin + "/?returnTo=logs");
            await page
                .getByLabel("Goblin password", { exact: true })
                .fill("logging-browser-test");
            await page.getByRole("button", { name: "Open workspace" }).click();
            const timezoneSetup = page.getByRole("dialog", {
                name: "Choose your timezone",
            });
            await expect(
                timezoneSetup.getByLabel("Timezone", { exact: true }),
            ).toHaveValue("Asia/Manila");
            await timezoneSetup
                .getByRole("button", { name: "Use timezone" })
                .click();
            await expect(page).toHaveURL(/\/logs\/select\/vmui\//);
            await expect(
                page.getByRole("button", { name: /Execute/ }),
            ).toBeVisible({ timeout: 30_000 });
            // VMUI initializes its time range in the URL after rendering the
            // controls. Wait for that state before submitting a custom query.
            await expect(page).toHaveURL((url) =>
                new URLSearchParams(url.hash.split("?")[1]).has(
                    "g0.range_input",
                ),
            );
            assert.equal(
                await page.evaluate(
                    () =>
                        JSON.parse(localStorage.getItem("VLUI:TIMEZONE")!)
                            .value,
                ),
                "Asia/Manila",
            );
            const query = marker
                ? `state.Marker:="${marker}"`
                : "service:goblin";
            // The actual UI sends read requests with form-encoded POST bodies.
            const input = page.locator("textarea").first();
            await input.fill(query);
            const requested = page.waitForResponse(
                (response) =>
                    response.url().includes("/logs/select/logsql/query") &&
                    new URLSearchParams(
                        response.request().postData() ?? "",
                    ).get("query") === query,
            );
            await page.getByRole("button", { name: /Execute/ }).click();
            const result = await requested;
            assert.equal(result.status(), 200);
            if (marker) assert.match(await result.text(), new RegExp(marker));
            await expect
                .poll(() =>
                    new URLSearchParams(
                        new URL(page.url()).hash.split("?")[1],
                    ).get("query"),
                )
                .toBe(query);
            const deepLink = page.url();
            // A new device with its own browser timezone must use the saved
            // workspace preference even when opening VMUI directly.
            const other = await browser.newContext({
                timezoneId: "Asia/Manila",
            });
            try {
                await other.addCookies(await context.cookies());
                // A stale browser preference must not override this workspace.
                await other.addInitScript(() =>
                    localStorage.setItem(
                        "VLUI:TIMEZONE",
                        JSON.stringify({ value: "UTC" }),
                    ),
                );
                const second = await other.newPage();
                await second.goto(deepLink);
                await expect(
                    second.getByRole("button", { name: /Execute/ }),
                ).toBeVisible();
                assert.equal(
                    await second.evaluate(
                        () =>
                            JSON.parse(localStorage.getItem("VLUI:TIMEZONE")!)
                                .value,
                    ),
                    "Asia/Manila",
                );
                const response = await second.evaluate(() =>
                    fetch("/api/preferences/timezone", {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({
                            timeZone: "UTC",
                            expectedTimeZone: "Asia/Manila",
                        }),
                    }).then((response) => response.status),
                );
                assert.equal(response, 200);
            } finally {
                await other.close();
            }
            await page.reload();
            await expect(input).toHaveValue(query);
            assert.equal(
                await page.evaluate(
                    () =>
                        JSON.parse(localStorage.getItem("VLUI:TIMEZONE")!)
                            .value,
                ),
                "UTC",
            );
            const restored = await page.evaluate(() =>
                fetch("/api/preferences/timezone", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        timeZone: "Asia/Manila",
                        expectedTimeZone: "UTC",
                    }),
                }).then((response) => response.status),
            );
            assert.equal(restored, 200);
            await page.reload();
            await expect(input).toHaveValue(query);
            assert.equal(
                await page.evaluate(
                    () =>
                        JSON.parse(localStorage.getItem("VLUI:TIMEZONE")!)
                            .value,
                ),
                "Asia/Manila",
            );
            if (marker)
                await expect(
                    page
                        .getByText(`Logging smoke ${marker} 9007199254740993`, {
                            exact: true,
                        })
                        .first(),
                ).toBeVisible();
            assert.equal(
                new URL(page.url()).pathname,
                new URL(deepLink).pathname,
            );
            const blocked = await page.evaluate(
                async () =>
                    (
                        await fetch("/logs/insert/jsonline", {
                            method: "POST",
                            body: "{}",
                        })
                    ).status,
            );
            assert.equal(blocked, 403);
            await page.screenshot({
                path: join(
                    artifacts,
                    `logs-${hostname === azureHost ? "azure-host" : "local"}.png`,
                ),
                fullPage: true,
            });
            await page.goto(publicOrigin + "/?settings=logs");
            const link = page.getByRole("link", {
                name: "Open logs",
                exact: true,
            });
            await expect(link).toHaveAttribute("href", "/logs/");
            await expect(link).toHaveAttribute("target", "_blank");
            await expect(link).toHaveAttribute("rel", "noopener");
            assert.deepEqual(errors, []);
            await page.evaluate(() =>
                fetch("/api/session/lock", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: "{}",
                }),
            );
            await page.goto(deepLink);
            await expect(page).toHaveURL(
                (url) =>
                    url.origin === publicOrigin &&
                    url.pathname === "/" &&
                    url.search === "?returnTo=logs",
            );
            await page
                .getByLabel("Goblin password", { exact: true })
                .fill("logging-browser-test");
            await page.getByRole("button", { name: "Open workspace" }).click();
            await expect(page.locator("textarea").first()).toHaveValue(query);
            console.log(
                `PASS: ${hostname} — timezone onboarding, shared preference on direct links and refresh, login, real VMUI search, Settings link, CSP, ingestion denied, logout`,
            );
        } finally {
            await context.close();
            await app.close();
            await rm(dataDir, { recursive: true, force: true });
        }
    }
} finally {
    await browser.close();
}
