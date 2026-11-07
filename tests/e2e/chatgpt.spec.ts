import { test, expect, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import type {
    ChatGptState,
    ChatGptVerification,
} from "../../frontend/src/api/chatgpt-contracts.js";

async function fixture(page: Page, configured = true) {
    const state: ChatGptState = {
        configured,
        name: "Goblin",
        description:
            "Read and manage all Work in your private Goblin instance.",
        logoUrl: "/assets/branding/chatgpt.png",
        serverUrl: configured ? "https://goblin.example.test/mcp" : null,
        issuer: configured ? "https://goblin.example.test/" : null,
        clientId: "goblin-chatgpt",
        scope: "openid offline_access goblin.work.read goblin.work.write",
        ownerSubject: configured ? "owner-fixture" : null,
        clientConfigured: configured,
        redirectUri: configured
            ? "https://chatgpt.com/connector_platform_oauth_redirect"
            : null,
        environmentVariables: {
            publicOrigin: "GOBLIN_PUBLIC_ORIGIN",
            enabled: "GOBLIN_CHATGPT_ENABLED",
        },
    };
    const verification: ChatGptVerification = {
        discovery: true,
        database: true,
        issuerResponses: true,
        checkedAt: "2026-11-01T00:00:00Z",
    };
    const writes: { path: string; body: unknown }[] = [];
    const reads: string[] = [];
    let failLoad = false;
    let failSave = false;
    await page.route("**/api/**", async (route) => {
        if (route.request().resourceType() === "script")
            return route.continue();
        const path = new URL(route.request().url()).pathname;
        let json: unknown = [];
        if (route.request().method() === "POST")
            writes.push({ path, body: route.request().postDataJSON() });
        else reads.push(path);
        if (path === "/api/session") json = { authenticated: true };
        if (path === "/api/preferences") json = { timeZone: "Asia/Manila" };
        if (path === "/api/status")
            json = { account: null, login: null, runtimeReady: false };
        if (path === "/api/integrations/chatgpt") {
            if (failLoad)
                return route.fulfill({
                    status: 503,
                    json: {
                        error: { message: "Setup is temporarily unavailable." },
                    },
                });
            json = state;
        }
        if (path === "/api/integrations/chatgpt/verify") json = verification;
        if (path === "/api/integrations/chatgpt/client") {
            if (failSave)
                return route.fulfill({
                    status: 503,
                    json: {
                        error: { message: "Callback could not be saved." },
                    },
                });
            const body = route.request().postDataJSON();
            const generate = !state.clientConfigured || body.rotateSecret;
            state.clientConfigured = true;
            state.redirectUri = body.redirectUri || null;
            json = { clientSecret: generate ? "fixture-oauth-secret" : null };
        }
        if (path === "/api/integrations/chatgpt/client/revoke")
            json = { revoked: true };
        await route.fulfill({ json });
    });
    return {
        state,
        writes,
        reads,
        fail: (value: boolean) => {
            failLoad = value;
        },
        failCallback: (value: boolean) => {
            failSave = value;
        },
    };
}

async function step(page: Page, name: string) {
    await page
        .locator(".chatgpt-stepper")
        .getByRole("button", { name: new RegExp(`^${name}`) })
        .click();
}

test("one private connection offers full Work access without a sharing step", async ({
    page,
}) => {
    const { state, writes, reads } = await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await expect(
        page.getByText("Read and manage all Work", { exact: true }),
    ).toBeVisible();
    await expect(page.locator(".chatgpt-stepper button")).toHaveCount(4);
    await expect(page.getByRole("button", { name: "Share Work" })).toHaveCount(
        0,
    );
    await page
        .getByRole("button", {
            name: "Continue to connection setup",
            exact: true,
        })
        .click();
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await expect(
        page.getByText("Passed: OAuth and Work storage"),
    ).toBeVisible();
    await step(page, "Install");
    await expect(
        page.getByRole("textbox", { name: "OAuth scopes" }),
    ).toHaveValue(state.scope);
    await expect(
        page.getByRole("textbox", { name: "Description" }),
    ).toHaveValue(state.description);
    await expect(
        page.getByRole("img", { name: "Goblin plugin logo" }),
    ).toBeVisible();
    const downloading = page.waitForEvent("download");
    await page.getByRole("link", { name: "Download Goblin logo" }).click();
    const download = await downloading;
    expect(download.suggestedFilename()).toBe("goblin-chatgpt.png");
    const stream = await download.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream) chunks.push(Buffer.from(chunk));
    const png = Buffer.concat(chunks);
    expect(png.length).toBeLessThanOrEqual(10_000);
    expect(png.subarray(0, 8)).toEqual(
        Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    );
    expect(png.readUInt32BE(16)).toBe(512);
    expect(png.readUInt32BE(20)).toBe(512);
    await expect(
        page.getByText(
            "This private connection can read and manage all existing and future Work.",
        ),
    ).toBeVisible();
    await page.getByLabel("I installed the plugin").check();
    await expect(
        page.getByRole("button", { name: "Continue to test" }),
    ).toBeEnabled();
    await step(page, "Test");
    await expect(
        page.getByRole("textbox", { name: "Read prompt" }),
    ).toContainText("created before this connection and one created after it");
    await expect(
        page.getByRole("textbox", { name: "Write prompt" }),
    ).toContainText("Reserve Work and command IDs");
    await expect(page.getByText("Both calls must be denied.")).toBeVisible();
    await page.getByLabel("Existing and new Work are readable").check();
    await page.getByLabel("Work created and updated through ChatGPT").check();
    await expect(
        page.getByText("Your Work connection is ready.", { exact: false }),
    ).toBeVisible();
    expect(writes.some((request) => request.path.includes("/grants"))).toBe(
        false,
    );
    expect(reads).not.toContain("/api/integrations/chatgpt/work");
});

test("callback edits block sign-in until saved and keep the secret out of browser progress", async ({
    page,
}) => {
    const { state, writes, failCallback } = await fixture(page);
    state.redirectUri = null;
    await page.goto("/settings?section=connections&provider=chatgpt");
    await step(page, "Install");
    const callback = page.getByLabel("ChatGPT OAuth callback");
    await callback.fill(
        "https://chatgpt.com/connector_platform_oauth_redirect",
    );
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "entered but not saved",
    );
    failCallback(true);
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "Callback could not be saved",
    );
    await expect(callback).toHaveValue(
        "https://chatgpt.com/connector_platform_oauth_redirect",
    );
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    failCallback(false);
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "Callback saved",
    );
    await expect(page.getByLabel("I installed the plugin")).toBeEnabled();
    expect(writes.at(-1)?.body).toEqual({
        redirectUri: state.redirectUri,
        rotateSecret: false,
    });
    await page.getByLabel("I installed the plugin").check();
    await callback.fill("https://chatgpt.com/connector/oauth/new-callback");
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    await expect(
        page.getByRole("button", { name: "Continue to test" }),
    ).toBeDisabled();
    await callback.fill(state.redirectUri!);
    await expect(page.getByLabel("I installed the plugin")).toBeChecked();
    await page.reload();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveValue(
        state.redirectUri!,
    );
    expect(
        await page.evaluate(() =>
            localStorage.getItem("goblin.chatgpt.full-work-setup"),
        ),
    ).not.toContain("fixture-oauth-secret");
});

test("rotation preserves the callback and revocation remains available", async ({
    page,
}) => {
    const { state, writes } = await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await step(page, "Connection setup");
    await page
        .getByText("Need a replacement client secret?", { exact: true })
        .click();
    await page
        .getByLabel(
            "Generate a new client secret and revoke previous connections",
        )
        .check();
    await page
        .getByRole("button", { name: "Generate new client secret" })
        .click();
    expect(writes.at(-1)).toEqual({
        path: "/api/integrations/chatgpt/client",
        body: { redirectUri: state.redirectUri, rotateSecret: true },
    });
    await step(page, "Install");
    await expect(
        page.getByLabel("OAuth client secret", { exact: true }),
    ).toHaveValue("fixture-oauth-secret");
    await page.reload();
    await expect(
        page.getByLabel("OAuth client secret", { exact: true }),
    ).toHaveCount(0);
    await page.getByText("Need help with this step?").click();
    await page
        .getByRole("button", { name: "Revoke ChatGPT connections" })
        .click();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "connections revoked",
    );
    expect(writes.at(-1)?.path).toBe("/api/integrations/chatgpt/client/revoke");
});

test("progress is scoped to the owner and works on narrow screens without storage or clipboard", async ({
    page,
}) => {
    const { state } = await fixture(page);
    await page.addInitScript(() => {
        Object.defineProperty(navigator, "clipboard", { value: undefined });
        Storage.prototype.setItem = () => {
            throw new Error("Storage unavailable");
        };
    });
    await page.goto("/settings?section=connections&provider=chatgpt");
    await step(page, "Install");
    await page.getByRole("button", { name: "Copy server URL" }).click();
    await expect(page.getByLabel("Server URL", { exact: true })).toBeFocused();
    await expect(
        page.getByText("browser storage is unavailable", { exact: false }),
    ).toBeVisible();
    await mkdir(".artifacts/chatgpt-full-work/browser", { recursive: true });
    await page.screenshot({
        path: ".artifacts/chatgpt-full-work/browser/desktop.png",
        fullPage: true,
    });
    for (const width of [390, 320]) {
        await page.setViewportSize({ width, height: 844 });
        await expect(
            page.getByLabel("Setup step", { exact: true }),
        ).toBeVisible();
        for (const index of ["0", "1", "2", "3"]) {
            await page
                .getByLabel("Setup step", { exact: true })
                .selectOption(index);
            expect(
                await page.evaluate(
                    () =>
                        document.documentElement.scrollWidth <=
                        window.innerWidth,
                ),
            ).toBe(true);
            if (index === "0" || index === "1")
                await page.locator(".chatgpt-step-footer").screenshot({
                    path: `.artifacts/chatgpt-full-work/browser/step-${index}-${width}.png`,
                });
            if (index === "2")
                await page.locator(".chatgpt-branding").screenshot({
                    path: `.artifacts/chatgpt-full-work/browser/logo-${width}.png`,
                });
        }
    }
    await page.screenshot({
        path: ".artifacts/chatgpt-full-work/browser/mobile.png",
        fullPage: true,
    });
    state.issuer = "https://another.example.test/";
    await page.reload();
    await expect(
        page.getByRole("heading", { name: "Prepare your instance" }),
    ).toBeVisible();
});

test("temporary setup failures recover without a Work picker", async ({
    page,
}) => {
    const setup = await fixture(page);
    setup.fail(true);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await expect(
        page.getByText("Setup is temporarily unavailable."),
    ).toBeVisible();
    setup.fail(false);
    await page.getByRole("button", { name: "Reload setup" }).click();
    await step(page, "Test");
    await expect(
        page.getByRole("textbox", { name: "Read prompt" }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Share selected Work" }),
    ).toHaveCount(0);
});
