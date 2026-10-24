import { test, expect, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import type { ChatGptState } from "../../frontend/src/api/chatgpt-contracts.js";

async function fixture(page: Page, configured = true) {
    const state: ChatGptState = {
        configured,
        name: "Goblin",
        description: "Read shared Work from your instance.",
        logoUrl: "/assets/branding/icon.svg",
        serverUrl: "https://goblin.example.test/mcp",
        issuer: "https://identity.example.test/realms/goblin",
        clientId: "goblin-chatgpt",
        scope: "goblin.work.read",
        grants: [],
    };
    const writes: { path: string; body: unknown }[] = [];
    let reject = false;
    await page.route("**/api/**", async (route) => {
        if (route.request().resourceType() === "script")
            return route.continue();
        const path = new URL(route.request().url()).pathname;
        let json: unknown = [];
        if (route.request().method() === "POST")
            writes.push({ path, body: route.request().postDataJSON() });
        if (path === "/api/session") json = { authenticated: true };
        if (path === "/api/preferences") json = { timeZone: "Asia/Manila" };
        if (path === "/api/status")
            json = { account: null, login: null, runtimeReady: false };
        if (path === "/api/integrations/chatgpt") json = state;
        if (path === "/api/integrations/chatgpt/work")
            json = [
                {
                    id: "9007199254740993",
                    objective: "Review <script>example</script> output",
                },
            ];
        if (path === "/api/integrations/chatgpt/verify")
            json = {
                discovery: false,
                database: true,
                issuerResponses: false,
                checkedAt: "2026-10-24T00:00:00Z",
            };
        if (path === "/api/integrations/chatgpt/grants") {
            if (reject)
                return route.fulfill({
                    status: 409,
                    json: {
                        error: {
                            code: "chatgpt_result_unavailable",
                            message: "No approved result is available.",
                        },
                    },
                });
            const body = route.request().postDataJSON();
            state.grants = [
                {
                    id: "42",
                    subject: body.subject,
                    workId: body.workId,
                    resultAttemptId: null,
                    expiresAt: "2099-01-01T00:00:00Z",
                    expired: false,
                },
            ];
            json = { saved: true };
        }
        if (path === "/api/integrations/chatgpt/grants/revoke") {
            state.grants = [];
            json = { revoked: true };
        }
        await route.fulfill({ json });
    });
    return {
        state,
        writes,
        reject: () => {
            reject = true;
        },
    };
}

test("ChatGPT setup provides details, preserves drafts during checks, and shares then revokes Work", async ({
    page,
}) => {
    const { writes } = await fixture(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/settings?section=chatgpt");
    await expect(
        page.getByRole("heading", { name: "ChatGPT plugin" }),
    ).toBeVisible();
    await expect(page.getByLabel("Server URL", { exact: true })).toHaveValue(
        "https://goblin.example.test/mcp",
    );
    await expect(
        page.getByRole("link", { name: "Download Goblin logo" }),
    ).toHaveAttribute("download", "goblin.svg");
    const subject = page.getByLabel("OAuth user ID", { exact: true });
    await subject.fill("reader-<img src=x>");
    await page
        .getByLabel("Work", { exact: true })
        .selectOption("9007199254740993");
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await expect(page.getByText(/Provider check failed/)).toBeVisible();
    await expect(subject).toHaveValue("reader-<img src=x>");
    await expect(page.getByLabel("Work", { exact: true })).toHaveValue(
        "9007199254740993",
    );
    await page.getByRole("button", { name: "Share selected Work" }).click();
    await expect(
        page.getByText("Work shared.", { exact: false }),
    ).toBeVisible();
    expect(writes.find((w) => w.path.endsWith("/grants"))?.body).toEqual({
        subject: "reader-<img src=x>",
        workId: "9007199254740993",
        hours: 24,
        shareApprovedResult: false,
    });
    await expect(page.locator(".chatgpt-grants")).toContainText(
        "reader-<img src=x>",
    );
    await expect(page.locator(".chatgpt-grants img")).toHaveCount(0);
    await page.getByRole("button", { name: /Revoke Work/ }).click();
    await expect(page.getByText("No Work is shared yet.")).toBeVisible();
    expect(writes.at(-1)?.body).toEqual({ id: "42" });
    expect(errors).toEqual([]);
    await mkdir(".artifacts/chatgpt-plugin/browser", { recursive: true });
    await page
        .getByRole("heading", { name: "ChatGPT plugin" })
        .scrollIntoViewIfNeeded();
    await page.screenshot({
        path: ".artifacts/chatgpt-plugin/browser/onboarding-desktop.png",
        fullPage: true,
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByLabel("Server URL", { exact: true })).toBeVisible();
    await page
        .getByRole("heading", { name: "ChatGPT plugin" })
        .scrollIntoViewIfNeeded();
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
    ).toBe(true);
    await page.screenshot({
        path: ".artifacts/chatgpt-plugin/browser/onboarding-mobile.png",
        fullPage: true,
    });
});

test("ChatGPT grant failures keep the form and cannot claim success", async ({
    page,
}) => {
    const state = await fixture(page);
    state.reject();
    await page.goto("/settings?section=chatgpt");
    await page.getByLabel("OAuth user ID", { exact: true }).fill("reader");
    await page
        .getByLabel("Work", { exact: true })
        .selectOption("9007199254740993");
    await page.getByLabel("Also share the latest approved result").check();
    await page.getByRole("button", { name: "Share selected Work" }).click();
    await expect(
        page.getByText("No approved result is available."),
    ).toBeVisible();
    await expect(page.getByLabel("OAuth user ID", { exact: true })).toHaveValue(
        "reader",
    );
    await expect(
        page.getByLabel("Also share the latest approved result"),
    ).toBeChecked();
    await expect(page.getByText("Work shared.", { exact: false })).toHaveCount(
        0,
    );
});

test("disabled ChatGPT setup explains prerequisites without grant controls", async ({
    page,
}) => {
    await fixture(page, false);
    await page.goto("/settings?section=chatgpt");
    await expect(
        page.getByRole("heading", { name: "Prepare your instance" }),
    ).toBeVisible();
    await expect(
        page.getByRole("link", { name: "Download the installation guide" }),
    ).toHaveAttribute("href", "/api/integrations/chatgpt/guide");
    await expect(
        page.getByRole("button", { name: "Share selected Work" }),
    ).toHaveCount(0);
});

test("configured ChatGPT setup does not claim an installed connection", async ({
    page,
}) => {
    await fixture(page);
    await page.goto("/integrations");
    const row = page.locator(".integration-row").filter({ hasText: "ChatGPT" });
    await expect(row).toContainText("Configured");
    await expect(row).toContainText("verify sign-in in ChatGPT");
    await page.getByRole("tab", { name: /^Connected/ }).click();
    await expect(row).toHaveCount(0);
});
