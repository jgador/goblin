import { test, expect, type Page } from "@playwright/test";
import type { Account, AuthenticationState } from "../../shared/api.js";

const chatgpt: Account = { type: "chatgpt", email: "saved@example.test", planType: "plus" };
const reply = { reply: "Hidden test reply", model: "test-model", durationMs: 10, authType: "chatgpt" };

async function savedAccount(page: Page, account: Account = chatgpt) {
  const state: AuthenticationState = { account, login: null, notice: null, verification: null, runtimeReady: true };
  await page.route("**/api/session", (route) => route.fulfill({ json: { authenticated: true } }));
  await page.route("**/api/status", (route) => route.fulfill({ json: state }));
  return state;
}

async function pollAccount(page: Page) {
  const response = page.waitForResponse("**/api/status");
  await page.clock.fastForward(7000);
  await response;
  // Let fetch continuations render the status before checking request counts.
  await page.waitForFunction(() => document.getElementById("card")?.getAttribute("aria-busy") === "false");
}

test("saved accounts check automatically and require a manual retry after failures", async ({ page }) => {
  await page.clock.install();
  await savedAccount(page);
  const failures = [
    { code: "prompt_limit_reached", title: "Usage limit reached", detail: "ChatGPT plan", status: 429 },
    { code: "prompt_timeout", title: "Connection check timed out", detail: "didn’t finish", status: 504 },
    { code: "prompt_cancelled", title: "Connection check cancelled", detail: "interrupted", status: 408 },
    { code: "prompt_access_denied", title: "Model access unavailable", detail: "access and permissions", status: 502 },
    { code: "prompt_failed", title: "Connection check failed", detail: "couldn’t verify access", status: 502 },
    { code: "prompt_in_progress", title: "Another check is running", detail: "current check", status: 409 },
    { code: "not_connected", title: "Not connected", detail: "Sign in again", status: 409 },
  ];
  let checks = 0;
  await page.route("**/api/prompt", (route) => {
    const failure = failures[checks++];
    return route.fulfill(failure
      ? { status: failure.status, json: { error: { code: failure.code, message: "Internal prompt details" } } }
      : { json: reply });
  });
  await page.goto("/");
  for (const [index, failure] of failures.entries()) {
    await expect(page.locator("#connection-status")).toHaveText(failure.title);
    await expect(page.locator("#connection-description")).toContainText(failure.detail);
    await expect(page.getByText("Connected", { exact: true })).not.toBeVisible();
    await pollAccount(page);
    await expect(page.locator("#connection-status")).toHaveText(failure.title);
    expect(checks).toBe(index + 1);
    await page.getByRole("button", { name: "Retry check" }).click();
  }
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  await pollAccount(page);
  await pollAccount(page);
  expect(checks).toBe(failures.length + 1);
  await expect(page.locator("body")).not.toContainText("Hidden test reply");
  await expect(page.locator("body")).not.toContainText("Internal prompt details");

  await page.reload();
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  expect(checks).toBe(failures.length + 2);
});

test("API key failures explain how to restore access", async ({ page }) => {
  await savedAccount(page, { type: "apiKey" });
  let checks = 0;
  await page.route("**/api/prompt", (route) => route.fulfill({ status: 502, json: {
    error: { code: checks++ === 0 ? "prompt_unauthorized" : "prompt_limit_reached", message: "Request failed" },
  } }));
  await page.goto("/");
  await expect(page.locator("#connection-status")).toHaveText("API key rejected");
  await expect(page.locator("#connection-description")).toContainText("connect a valid API key");
  await page.getByRole("button", { name: "Retry check" }).click();
  await expect(page.locator("#connection-status")).toHaveText("Usage limit reached");
  await expect(page.locator("#connection-description")).toContainText("API billing and limits");
});

test("network errors fail the check and a retry can recover", async ({ page }) => {
  await savedAccount(page);
  let checks = 0;
  await page.route("**/api/prompt", (route) => checks++ === 0 ? route.abort("failed") : route.fulfill({ json: reply }));
  await page.goto("/");
  await expect(page.locator("#connection-status")).toHaveText("Connection check failed");
  await page.getByRole("button", { name: "Retry check" }).click();
  await expect(page.locator("#connection-status")).toHaveText("Connected");
});

test("an expired workspace session clears the check and verifies again after unlocking", async ({ page }) => {
  await savedAccount(page);
  const checkGate = Promise.withResolvers<void>();
  let checks = 0;
  await page.route("**/api/prompt", async (route) => {
    if (checks++ === 0) {
      await checkGate.promise;
      await route.fulfill({ status: 401, json: { error: { code: "workspace_locked", message: "Unlock the workspace to continue." } } });
    } else await route.fulfill({ json: reply });
  });
  await page.goto("/");
  await expect(page.locator("#connection-status")).toHaveText("Verifying connection…");
  checkGate.resolve();
  await expect(page.getByRole("heading", { name: "Open your workspace" })).toBeVisible();
  await expect(page.locator("#connection-check")).toBeHidden();
  await expect(page.locator("#account-detail")).toHaveText("");
  await page.getByLabel("Goblin password", { exact: true }).fill("a");
  await page.getByRole("button", { name: "Open workspace" }).click();
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  expect(checks).toBe(2);
});

test("account changes clear a previous success and trigger a fresh check", async ({ page }) => {
  await page.clock.install();
  const state = await savedAccount(page);
  const checkGate = Promise.withResolvers<void>();
  let checks = 0;
  await page.route("**/api/prompt", async (route) => {
    if (++checks === 2) await checkGate.promise;
    await route.fulfill({ json: reply });
  });
  await page.goto("/");
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  state.account = { type: "chatgpt", email: "another@example.test", planType: "team" };
  await page.clock.fastForward(7000);
  await expect(page.locator("#account-detail")).toHaveText("another@example.test · team");
  await expect(page.locator("#connection-status")).toHaveText("Verifying connection…");
  await expect(page.getByText("Connected", { exact: true })).not.toBeVisible();
  checkGate.resolve();
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  expect(checks).toBe(2);
  state.account = null;
  await page.clock.fastForward(7000);
  await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();
  await expect(page.locator("#connection-check")).toBeHidden();
  await expect(page.locator("#account-detail")).toHaveText("");
  await expect(page.getByText("Connected", { exact: true })).toHaveCount(0);
  expect(checks).toBe(2);
});

test("losing account status removes a previous success without automatically sending another check", async ({ page }) => {
  await page.clock.install();
  await savedAccount(page);
  let checks = 0;
  await page.route("**/api/prompt", (route) => { checks++; return route.fulfill({ json: reply }); });
  await page.goto("/");
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  await page.route("**/api/status", (route) => route.fulfill({ status: 503, json: {
    error: { code: "runtime_unavailable", message: "Codex is unavailable" },
  } }), { times: 1 });
  await page.clock.fastForward(7000);
  await expect(page.locator("#connection-status")).toHaveText("Connection unavailable");
  await pollAccount(page);
  await expect(page.locator("#connection-status")).toHaveText("Connection unavailable");
  expect(checks).toBe(1);
  await page.getByRole("button", { name: "Retry check" }).click();
  await expect(page.locator("#connection-status")).toHaveText("Connected");
  expect(checks).toBe(2);
});
