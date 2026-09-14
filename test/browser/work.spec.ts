import { test, expect } from "@playwright/test";

test("work can move from a decision through revision and approval without calling an agent", async ({ page }) => {
  const errors: string[] = [];
  const agentRequests: string[] = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => {
    if (message.type() === "error") errors.push(message.text());
  });
  page.on("request", request => {
    if (new URL(request.url()).pathname.startsWith("/api/")) agentRequests.push(request.url());
  });
  const response = await page.goto("/work");
  expect(response?.status()).toBe(200);
  expect(response?.headers()["content-security-policy"]).toContain("style-src 'self'");
  await expect(page.getByRole("heading", { name: "Make Goblin easier to set up", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Keep it in the terminal" }).click();
  await page.getByRole("button", { name: "Continue with this" }).click();
  await expect(page.getByText("Goblin is working on it", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Preview a finished result" }).click();
  await page.getByRole("tab", { name: "Outputs" }).click();
  await expect(page.getByRole("heading", { name: "A simpler terminal setup" })).toBeVisible();
  await page.getByRole("button", { name: "Ask for changes" }).click();
  await page.getByRole("textbox").fill("Include a way to retry a failed provider connection.");
  await page.getByRole("button", { name: "Send message" }).click();
  await expect(page.getByText("Goblin is working on it", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Preview a finished result" }).click();
  await page.getByRole("button", { name: "Approve & complete" }).click();
  await expect(page.getByText("Work completed.", { exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Activity" }).click();
  await expect(page.getByRole("heading", { name: "You approved the result" })).toBeVisible();
  await expect(page.locator("[style]")).toHaveCount(0);
  expect(agentRequests).toEqual([]);
  expect(errors).toEqual([]);
  await page.reload();
  await expect(page.getByRole("button", { name: "Continue with this" })).toBeVisible();
});

test("a conversation is tracked only on request and renders user content as text", async ({ page }) => {
  await page.goto("/work/");
  await page.getByRole("button", { name: "New conversation", exact: true }).click();
  const request = 'Plan the next release <img src=x onerror="alert(1)">';
  await page.getByRole("textbox").fill(request);
  await page.getByRole("button", { name: "Send message" }).click();
  await expect(page.getByRole("button", { name: "Track this work" })).toBeVisible();
  await expect(page.locator('img[src="x"]')).toHaveCount(0);
  await page.getByRole("button", { name: "Track this work" }).click();
  await expect(page.locator(".detail-meta-left")).toContainText("GB-025");
  await expect(page.locator(".detail .message-text").first()).toHaveText(request);
  await expect(page.locator(".detail-properties")).toContainText("Assigned to Goblin");
  await page.getByRole("tab", { name: "Activity" }).click();
  await expect(page.getByText("You chose to track this conversation.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Reset experience preview" }).click();
  await expect(page.locator(".work-list")).not.toContainText("GB-025");
});

test("mobile work navigation fits the viewport and keeps connection settings reachable", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto("/");
  await page.getByRole("link", { name: "Explore work preview" }).click();
  await page.getByRole("button", { name: /Make Goblin easier to set up/ }).click();
  await expect(page.getByRole("heading", { name: "Make Goblin easier to set up", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Work", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Work", exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.getByRole("link", { name: "Connection settings" }).click();
  await expect(page.getByRole("heading", { name: "Open your workspace" })).toBeVisible();
});
