import { test, expect } from "@playwright/test";
import { writeFile, unlink } from "node:fs/promises";
import { resolve } from "node:path";

test("owner can connect, cancel, complete ChatGPT sign-in, and switch to an API key", async ({ page }) => {
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Open your workspace" })).toBeVisible();
  await page.getByLabel("Workspace access code", { exact: true }).fill("browser-test-access-code-never-use-in-production");
  await page.getByRole("button", { name: "Open workspace" }).click();
  await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();
  await page.screenshot({ path: "test-results/authentication-connect.png", fullPage: true });

  await page.getByRole("button", { name: "Continue with ChatGPT" }).click();
  await expect(page.getByText("ABCD-1234", { exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Open OpenAI sign-in" })).toHaveAttribute("href", "https://auth.openai.com/codex/device");
  await page.reload();
  await expect(page.getByText("ABCD-1234", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Cancel sign-in" }).click();
  await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Continue with ChatGPT" }).click();
  await expect(page.getByText("ABCD-1234", { exact: true })).toBeVisible();
  await page.screenshot({ path: "test-results/authentication-device-mobile.png", fullPage: true });
  const completion = resolve(".goblin-browser-test/codex/complete-login");
  await writeFile(completion, "complete");
  await expect(page.getByText("owner@example.test · plus", { exact: true })).toBeVisible({ timeout: 8000 });
  await unlink(completion);
  await page.getByRole("button", { name: "Lock preview" }).click();
  await expect(page.getByRole("heading", { name: "Open your workspace" })).toBeVisible();
  await page.getByLabel("Workspace access code", { exact: true }).fill("browser-test-access-code-never-use-in-production");
  await page.getByRole("button", { name: "Open workspace" }).click();
  await expect(page.getByText("owner@example.test · plus", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Disconnect Codex" }).click();
  await expect(page.getByRole("heading", { name: "Choose how to sign in" })).toBeVisible();

  await page.getByText("Use an OpenAI API key instead", { exact: false }).click();
  await page.getByLabel("OpenAI API key", { exact: true }).fill("sk-invalid-fake-browser-test-key");
  await page.getByRole("button", { name: "Connect API key" }).click();
  await expect(page.getByRole("alert")).toContainText("OpenAI rejected this API key");
  await expect(page.getByLabel("OpenAI API key", { exact: true })).toHaveValue("");
  await page.getByLabel("OpenAI API key", { exact: true }).fill("sk-valid-fake-browser-test-key");
  await page.getByRole("button", { name: "Connect API key" }).click();
  await expect(page.getByText("Billed to your OpenAI Platform project", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length }))).toEqual({ local: 0, session: 0 });
  await expect(page.locator("body")).not.toContainText("sk-valid-fake-browser-test-key");
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  expect(errors).toEqual([]);
});
