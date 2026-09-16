import { test, expect } from "@playwright/test";
import { startSetup } from "../support/setup.js";

test("setup shows live progress, failure and a successful retry across refreshes", async ({ page }) => {
  const setup = await startSetup();
  try {
    await page.goto(setup.url);
    await expect(page.getByRole("heading", { name: "Getting things ready." })).toBeVisible();
    await expect(page.getByRole("listitem")).toHaveCount(8);
    setup.transition("begin");
    setup.transition("start", "k3s");
    await expect(page.locator('li[data-status="running"]')).toContainText("Install Kubernetes");
    setup.transition("failed");
    await expect(page.getByRole("heading")).toHaveText("Setup needs attention.");
    await expect(page.locator("#recovery")).toBeVisible();
    await page.reload();
    await expect(page.locator('li[data-status="failed"]')).toContainText("Install Kubernetes");
    setup.transition("begin");
    setup.transition("start", "k3s");
    setup.transition("complete");
    setup.transition("start", "cert-manager");
    await expect(page.locator('li[data-status="complete"]')).toContainText("Install Kubernetes");
    await expect(page.locator('li[data-status="running"]')).toContainText("Install certificate manager");
    await expect(page.locator("#recovery")).toBeHidden();
    expect(await page.locator("body").evaluate(element => element.scrollWidth <= innerWidth)).toBe(true);
  } finally { await setup.close(); }
});

test("setup tolerates the handoff gap and opens the Kubernetes application", async ({ page }) => {
  const setup = await startSetup();
  try {
    await page.goto(setup.url);
    setup.transition("begin");
    setup.transition("start", "activate");
    setup.transition("handoff");
    await expect(page.locator("#message")).toContainText("Connecting to your workspace");
    await page.route("**/setup/status", route => route.fulfill({ status: 404 }));
    await page.route("**/readyz", route => route.fulfill({ status: 503, json: { ready: false } }));
    await expect(page.locator("#connection")).toHaveText("Connecting to Goblin…");
    await page.unroute("**/readyz");
    await page.route("**/readyz", route => route.fulfill({ json: { ready: true } }));
    await page.route(setup.url + "/", route => route.fulfill({ contentType: "text/html", body: "<h1>Goblin workspace</h1>" }));
    await expect(page.getByRole("heading")).toHaveText("Goblin workspace", { timeout: 15000 });
  } finally { await setup.close(); }
});

test("a localhost visit moves to the configured origin and opens Goblin even if it misses the handoff update", async ({ page }) => {
  const setup = await startSetup();
  try {
    const alias = setup.url.replace("127.0.0.1", "localhost");
    await page.goto(alias);
    await expect(page.getByRole("listitem")).toHaveCount(8);
    setup.transition("begin");
    setup.transition("public-url", setup.url);
    await expect(page).toHaveURL(setup.url + "/");
    setup.transition("start", "verify");
    await expect(page.locator("#message")).toHaveText("Check application readiness");
    // The last observed step can still be readiness when setup releases its port.
    await page.route("**/setup/status", route => route.fulfill({ status: 404 }));
    await page.route("**/readyz", route => route.fulfill({ status: 503, json: { ready: false } }));
    await expect(page.locator("#connection")).toContainText("Reconnecting");
    await expect(page).toHaveURL(setup.url + "/");
    await page.unroute("**/readyz");
    await page.route("**/readyz", route => route.fulfill({ json: { ready: true } }));
    await page.route(setup.url + "/", route => route.fulfill({ contentType: "text/html", body: "<h1>Goblin workspace</h1>" }));
    await expect(page.getByRole("heading")).toHaveText("Goblin workspace", { timeout: 15000 });
  } finally { await setup.close(); }
});
