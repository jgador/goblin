import { test, expect } from "@playwright/test";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startBackend } from "../backend.js";

test("localhost prefills the password and waits for Open workspace, including after locking", async ({ page }) => {
  const dataDir = await mkdtemp(join(tmpdir(), "goblin-local-browser-"));
  const origin = "http://127.0.0.1:8799";
  const app = await startBackend({ dataDir, publicOrigin: origin, listenUrl: origin });
  try {
    const loginRequests: unknown[] = [];
    page.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/session" && request.method() === "POST")
        loginRequests.push(request.postDataJSON());
    });
    await page.goto(origin);
    const password = page.getByLabel("Goblin password", { exact: true });
    const open = page.getByRole("button", { name: "Open workspace" });
    await expect(password).toHaveValue("goblin");
    await expect(password).toHaveAttribute("type", "password");
    await expect(page.getByText("The local password is filled in.", { exact: false })).toBeVisible();
    expect(loginRequests).toEqual([]);
    expect((await page.request.get(`${origin}/api/status`)).status()).toBe(401);
    expect((await page.context().cookies()).some((cookie) => cookie.name === "goblin_auth_session")).toBe(false);
    await page.screenshot({ path: "test-results/local-login.png", fullPage: true });

    await open.click();
    await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();
    expect(loginRequests).toEqual([{ password: "goblin" }]);
    await page.reload();
    await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();
    expect(loginRequests).toHaveLength(1);

    await page.getByRole("button", { name: "Lock workspace" }).click();
    await expect(password).toHaveValue("goblin");
    expect((await page.request.get(`${origin}/api/status`)).status()).toBe(401);
    expect(loginRequests).toHaveLength(1);
    await open.click();
    await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();
    expect(loginRequests).toHaveLength(2);
  } finally {
    await app.close();
    await rm(dataDir, { recursive: true, force: true });
  }
});
