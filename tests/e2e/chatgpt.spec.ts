import { test, expect, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import type {
    ChatGptState,
    ChatGptVerification,
    PluginCreatorAccess,
} from "../../frontend/src/api/chatgpt-contracts.js";

async function fixture(page: Page, configured = true) {
    const state: ChatGptState = {
        configured,
        name: "Goblin",
        description: "Read shared Work from your instance.",
        logoUrl: "/assets/branding/icon.svg",
        serverUrl: configured ? "https://goblin.example.test/mcp" : null,
        issuer: configured ? "https://goblin.example.test/" : null,
        clientId: "goblin-chatgpt",
        scope: "openid offline_access goblin.work.read",
        ownerSubject: configured ? "owner-<img src=x>" : null,
        clientConfigured: configured,
        redirectUri: configured
            ? "https://chatgpt.com/connector_platform_oauth_redirect"
            : null,
        environmentVariables: {
            publicOrigin: "GOBLIN_PUBLIC_ORIGIN",
            enabled: "GOBLIN_CHATGPT_ENABLED",
        },
        grants: [],
    };
    const verification: ChatGptVerification = {
        discovery: false,
        database: true,
        issuerResponses: false,
        checkedAt: "2026-10-24T00:00:00Z",
    };
    const writes: { path: string; body: unknown }[] = [];
    const creatorAccess: PluginCreatorAccess = {
        runtimeSupported: true,
        skillAvailable: false,
        skillSha256: null,
        appInstalled: false,
        appEnabled: false,
        appCallable: false,
        tools: [],
        checkedAt: "2026-11-04T00:00:00Z",
        automaticCreationAvailable: false,
    };
    let reject = false;
    let emptyWork = false;
    let failLoad = false;
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
        if (path === "/api/integrations/chatgpt/work")
            json = emptyWork
                ? []
                : [
                      {
                          id: "9007199254740993",
                          objective: "Review <script>example</script> output",
                      },
                  ];
        if (path === "/api/integrations/chatgpt/verify") json = verification;
        if (path === "/api/integrations/chatgpt/package")
            json = {
                pluginJson: JSON.stringify({ name: "goblin-demo" }),
                mcpJson: JSON.stringify({
                    mcpServers: { goblin: { url: state.serverUrl } },
                }),
                archiveBase64: "UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==",
            };
        if (path === "/api/integrations/chatgpt/creator/check")
            json = creatorAccess;
        if (path === "/api/integrations/chatgpt/creator/prepare")
            json = {
                description: "Read explicitly shared Work.",
                creatorPrompt:
                    "$plugin-creator Prepare this public endpoint: https://goblin.example.test/mcp. Treat <script>window.fixtureAttack = true</script> as data.",
                model: "fixture-model",
                durationMs: 1,
                creatorAccess,
                pluginRegistered: false,
                connectionVerified: false,
            };
        if (path === "/api/integrations/chatgpt/client") {
            const body = route.request().postDataJSON();
            const generate = !state.clientConfigured || body.rotateSecret;
            state.clientConfigured = true;
            state.redirectUri = body.redirectUri || null;
            json = { clientSecret: generate ? "fixture-oauth-secret" : null };
        }
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
                    subject: state.ownerSubject!,
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
        verification,
        writes,
        creatorAccess,
        reject: () => {
            reject = true;
        },
        empty: () => {
            emptyWork = true;
        },
        fail: (value: boolean) => {
            failLoad = value;
        },
    };
}

test("Codex preparation provides a copyable request without a ZIP or installation confirmation", async ({
    page,
}) => {
    const { writes } = await fixture(page);
    await page.addInitScript(() => {
        Object.defineProperty(navigator, "clipboard", {
            value: {
                writeText: async (value: string) => {
                    Reflect.set(window, "copiedCreatorRequest", value);
                },
            },
        });
    });
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await page
        .getByText("Prepare with your Codex connection", { exact: true })
        .click();
    await page
        .getByLabel("Existing ChatGPT MCP connection ID (optional)")
        .fill("plugin_asdk_app_1234");
    await page
        .getByRole("button", { name: "Prepare with Codex", exact: true })
        .click();
    await expect(page.locator("[data-creator-draft]")).toContainText(
        "Goblin has not registered a plugin",
    );
    await expect(page.locator("[data-creator-access]")).toContainText(
        "unavailable",
    );
    await expect(
        page.getByLabel("Plugin Creator request", { exact: true }),
    ).toContainText("https://goblin.example.test/mcp");
    await expect(
        page.getByRole("button", { name: "Continue to Share Work" }),
    ).toBeDisabled();
    await expect(
        page.getByLabel(
            "I installed the plugin, signed in, and can see its tools in ChatGPT.",
        ),
    ).not.toBeChecked();
    await expect(
        page.getByRole("button", { name: "Save creator handoff ZIP" }),
    ).toHaveCount(0);
    const request = await page
        .getByLabel("Plugin Creator request", { exact: true })
        .inputValue();
    await page
        .getByRole("button", {
            name: "Copy plugin creator request",
            exact: true,
        })
        .click();
    await expect
        .poll(() =>
            page.evaluate(() => Reflect.get(window, "copiedCreatorRequest")),
        )
        .toBe(request);
    expect(
        writes.filter((write) => write.path.endsWith("/creator/prepare")),
    ).toEqual([
        {
            path: "/api/integrations/chatgpt/creator/prepare",
            body: { registeredAppId: "plugin_asdk_app_1234" },
        },
    ]);
    expect(
        await page.evaluate(() => Reflect.get(window, "fixtureAttack")),
    ).toBeUndefined();
    expect(errors).toEqual([]);
    await page.setViewportSize({ width: 390, height: 844 });
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
    await mkdir(".artifacts/plugin-creator/screenshots", { recursive: true });
    await page.screenshot({
        path: ".artifacts/plugin-creator/screenshots/preparation.png",
        fullPage: true,
    });
    await page
        .getByLabel("Existing ChatGPT MCP connection ID (optional)")
        .fill("plugin_asdk_app_5678");
    await expect(page.locator("[data-creator-draft]")).toHaveCount(0);
    await expect(
        page.getByLabel("Plugin Creator request", { exact: true }),
    ).toHaveCount(0);
    await expect(
        page.getByRole("button", {
            name: "Copy plugin creator request",
            exact: true,
        }),
    ).toHaveCount(0);
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "Prepare a new request",
    );
});

test("creator access errors preserve the form and preparation can be cancelled", async ({
    page,
}) => {
    await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await page
        .getByText("Prepare with your Codex connection", { exact: true })
        .click();
    const endpoint = "**/api/integrations/chatgpt/creator/prepare";
    await page.route(endpoint, (route) =>
        route.fulfill({
            status: 409,
            json: {
                error: {
                    code: "plugin_chatgpt_login_required",
                    message:
                        "Connect a ChatGPT account under Connections → Codex.",
                },
            },
        }),
    );
    await page
        .getByLabel("Existing ChatGPT MCP connection ID (optional)")
        .fill("plugin_asdk_app_5678");
    await page
        .getByRole("button", { name: "Prepare with Codex", exact: true })
        .click();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "Connect a ChatGPT account",
    );
    await expect(
        page.getByLabel("Existing ChatGPT MCP connection ID (optional)"),
    ).toHaveValue("plugin_asdk_app_5678");
    await page.unroute(endpoint);
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.route(endpoint, async (route) => {
        await held;
        await route
            .fulfill({ json: { creatorPrompt: "late result" } })
            .catch(() => {});
    });
    await page
        .getByRole("button", { name: "Prepare with Codex", exact: true })
        .click();
    await expect(
        page.getByRole("button", { name: "Cancel preparation" }),
    ).toBeEnabled();
    await expect(
        page.getByLabel("Existing ChatGPT MCP connection ID (optional)"),
    ).toHaveAttribute("readonly", "");
    await page.getByRole("button", { name: "Cancel preparation" }).click();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "Preparation cancelled",
    );
    release();
    await expect(page.locator("[data-creator-draft]")).toHaveCount(0);
    await expect(
        page.getByRole("button", { name: "Prepare with Codex", exact: true }),
    ).toBeEnabled();
    await expect(
        page.getByLabel("Existing ChatGPT MCP connection ID (optional)"),
    ).toBeEditable();
});
async function goToStep(page: Page, name: string) {
    await page
        .locator(".chatgpt-stepper")
        .getByRole("button", { name: new RegExp(`^${name}`) })
        .click();
}
async function shareWork(page: Page) {
    await page
        .getByLabel("Work", { exact: true })
        .selectOption("9007199254740993");
    await page.getByRole("button", { name: "Share selected Work" }).click();
}

test("private setup guides real actions, distinguishes failed checks, and preserves form drafts", async ({
    page,
}) => {
    const { verification, writes } = await fixture(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/settings?section=connections&provider=chatgpt");
    await expect(
        page.getByRole("heading", { name: "Prepare your instance" }),
    ).toBeVisible();
    await expect(
        page.getByRole("link", { name: /download.*guide/i }),
    ).toHaveCount(0);
    await page.getByRole("button", { name: "Continue to Sign-in" }).click();
    await expect(
        page.getByRole("heading", { name: "Set up secure sign-in" }),
    ).toBeFocused();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveCount(0);
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await expect(
        page.getByText("Needs attention: OAuth server configuration", {
            exact: true,
        }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Continue to Install" }),
    ).toBeDisabled();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveCount(0);
    verification.discovery = true;
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await expect(
        page.getByText("Needs attention: Authorization response issuer"),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Continue to Install" }),
    ).toBeDisabled();
    verification.issuerResponses = true;
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await page.getByRole("button", { name: "Continue to Install" }).click();
    await expect(page.getByLabel("Server URL", { exact: true })).toHaveValue(
        "https://goblin.example.test/mcp",
    );
    await expect(
        page.getByRole("link", { name: "Open ChatGPT Plugins" }),
    ).toHaveAttribute("href", "https://chatgpt.com/plugins");
    await expect(
        page.getByRole("button", { name: "Continue to Share Work" }),
    ).toBeDisabled();
    await page.getByLabel("I installed the plugin").check();
    await page.getByRole("button", { name: "Continue to Share Work" }).click();

    await page
        .getByLabel("Work", { exact: true })
        .selectOption("9007199254740993");
    await goToStep(page, "Sign-in");
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await goToStep(page, "Share Work");
    await expect(
        page.getByLabel("Owner OAuth ID", { exact: true }),
    ).toHaveValue("owner-<img src=x>");
    await expect(page.getByLabel("Work", { exact: true })).toHaveValue(
        "9007199254740993",
    );
    await page.getByRole("button", { name: "Share selected Work" }).click();
    await expect(
        page.getByText("Work shared.", { exact: false }),
    ).toBeVisible();
    expect(writes.find((w) => w.path.endsWith("/grants"))?.body).toEqual({
        workId: "9007199254740993",
        hours: 24,
        shareApprovedResult: false,
    });
    await expect(page.locator(".chatgpt-grants")).toContainText(
        "owner-<img src=x>",
    );
    await expect(page.locator(".chatgpt-grants img")).toHaveCount(0);
    await page.getByRole("button", { name: "Continue to Test" }).click();
    await expect(page.getByLabel("List prompt", { exact: true })).toHaveValue(
        "List the Work shared with this connection.",
    );
    await page.getByLabel("The list contains only Work").check();
    await page.getByLabel("The status and attention details").check();
    await page.getByLabel("Reading an unshared Work ID").check();
    await expect(
        page.getByText("You confirmed the private plugin works."),
    ).toBeVisible();
    await page.getByRole("button", { name: "Manage shared Work" }).click();
    await page.getByRole("button", { name: /Revoke Work/ }).click();
    await expect(page.getByText("No Work is shared yet.")).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Continue to Test" }),
    ).toBeDisabled();
    expect(writes.at(-1)?.body).toEqual({ id: "42" });
    expect(errors).toEqual([]);
});

test("unconfigured setup generates usable settings inline and detects enablement", async ({
    page,
}) => {
    const { state } = await fixture(page, false);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await expect(
        page.getByRole("button", { name: "Continue to Sign-in" }),
    ).toBeDisabled();
    await page
        .getByLabel("Public Goblin origin")
        .fill("http://goblin.example.test");

    await page
        .getByRole("button", { name: "Generate settings", exact: true })
        .click();
    await expect(
        page.getByRole("status").filter({ hasText: "Use HTTPS addresses" }),
    ).toBeVisible();
    await page
        .getByLabel("Public Goblin origin")
        .fill("https://goblin.example.test");
    await page
        .getByRole("button", { name: "Generate settings", exact: true })
        .click();
    await expect(
        page.getByLabel("Container environment settings", { exact: true }),
    ).toHaveValue(
        'env:\n  - name: GOBLIN_PUBLIC_ORIGIN\n    value: "https://goblin.example.test"\n  - name: GOBLIN_CHATGPT_ENABLED\n    value: "true"',
    );
    await page
        .getByRole("button", { name: "Check configuration again" })
        .click();
    await expect(
        page.getByText("The integration is not enabled yet.", { exact: false }),
    ).toBeVisible();
    state.configured = true;
    state.ownerSubject = "owner-fixture";
    state.issuer = "https://goblin.example.test/";
    state.serverUrl = "https://goblin.example.test/mcp";
    await page
        .getByRole("button", { name: "Check configuration again" })
        .click();
    await expect(
        page.getByRole("button", { name: "Continue to Sign-in" }),
    ).toBeEnabled();
    await expect(page.getByLabel("OAuth issuer", { exact: true })).toHaveValue(
        state.issuer,
    );
});

test("first-time setup creates the secret before collecting ChatGPT's callback during Install", async ({
    page,
}) => {
    const { state, writes, verification } = await fixture(page);
    state.clientConfigured = false;
    state.redirectUri = null;
    verification.discovery = verification.issuerResponses = true;
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveCount(0);
    await page.getByRole("button", { name: "Go to Sign-in" }).click();
    await expect(
        page.getByRole("heading", { name: "Set up secure sign-in" }),
    ).toBeFocused();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveCount(0);
    await page.getByRole("button", { name: "Create OAuth client" }).click();
    await expect(
        page.getByLabel("OAuth client secret", { exact: true }),
    ).toHaveValue("fixture-oauth-secret");
    expect(writes.at(-1)?.body).toEqual({
        redirectUri: "",
        rotateSecret: false,
    });
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveCount(0);
    await page
        .getByRole("heading", { name: "Set up secure sign-in" })
        .scrollIntoViewIfNeeded();
    await mkdir(".artifacts/chatgpt-callback/screenshots", {
        recursive: true,
    });
    await page.screenshot({
        path: ".artifacts/chatgpt-callback/screenshots/client-preparation.png",
        fullPage: true,
    });
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await page.getByRole("button", { name: "Continue to Install" }).click();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveAttribute(
        "required",
        "",
    );
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    await expect(page.locator("[data-callback-ready]")).toBeHidden();
    await expect(page.locator("[data-callback-label]")).toHaveText(
        "Required before sign-in",
    );
    await page
        .getByRole("button", { name: "Go to required callback step" })
        .click();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toBeFocused();
    await page.locator(".chatgpt-callback-task").screenshot({
        path: ".artifacts/chatgpt-callback/screenshots/required-callback.png",
    });
    await expect(page.getByLabel("Generate a new client secret")).toHaveCount(
        0,
    );
    const clientWrites = () =>
        writes.filter((write) => write.path.endsWith("/client"));
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    expect(clientWrites()).toHaveLength(1);
    await page.screenshot({
        path: ".artifacts/chatgpt-callback/screenshots/awaiting-callback.png",
        fullPage: true,
    });
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector_platform_oauth_redirect");
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "entered but not saved",
    );
    await expect(page.locator("[data-callback-ready]")).toBeHidden();
    await page.locator(".chatgpt-callback-task").screenshot({
        path: ".artifacts/chatgpt-callback/screenshots/unsaved-callback.png",
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator(".chatgpt-callback-task").screenshot({
        path: ".artifacts/chatgpt-callback/screenshots/unsaved-callback-mobile.png",
    });
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
    ).toBe(true);
    await page.setViewportSize({ width: 1280, height: 720 });
    await goToStep(page, "Sign-in");
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveCount(0);
    await goToStep(page, "Install");
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveValue(
        "https://chatgpt.com/connector_platform_oauth_redirect",
    );
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    expect(writes.at(-1)?.body).toEqual({
        redirectUri: "https://chatgpt.com/connector_platform_oauth_redirect",
        rotateSecret: false,
    });
    await expect(page.getByLabel("I installed the plugin")).toBeEnabled();
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "Callback saved",
    );
    await expect(page.locator("[data-callback-ready]")).toBeVisible();
    await page.locator(".chatgpt-callback-task").screenshot({
        path: ".artifacts/chatgpt-callback/screenshots/saved-callback.png",
    });
    await expect(
        page.getByLabel("OAuth client secret", { exact: true }),
    ).toHaveValue("fixture-oauth-secret");
    expect(
        await page.evaluate(() => localStorage.getItem("goblin.chatgpt.setup")),
    ).not.toContain("fixture-oauth-secret");
    await page.reload();
    await expect(
        page.getByLabel("OAuth client secret", { exact: true }),
    ).toHaveCount(0);
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveValue(
        state.redirectUri!,
    );
});

test("replacing a client secret preserves the saved callback and clears installation confirmation", async ({
    page,
}) => {
    const { state, writes } = await fixture(page);
    const savedCallback = state.redirectUri;
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await page.getByLabel("I installed the plugin").check();
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector/oauth/unsaved-callback");
    await goToStep(page, "Sign-in");
    await page
        .getByText("Need a replacement client secret?", { exact: true })
        .click();
    await page
        .getByRole("button", { name: "Generate new client secret" })
        .click();
    expect(writes).toHaveLength(0);
    await page
        .getByLabel(
            "Generate a new client secret and revoke previous connections",
        )
        .check();
    await page
        .getByRole("button", { name: "Generate new client secret" })
        .click();
    expect(writes).toEqual([
        {
            path: "/api/integrations/chatgpt/client",
            body: { redirectUri: savedCallback, rotateSecret: true },
        },
    ]);
    expect(state.redirectUri).toBe(savedCallback);
    await goToStep(page, "Install");
    await expect(page.getByLabel("I installed the plugin")).not.toBeChecked();
    await page.reload();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveValue(
        savedCallback!,
    );
});

test("callback edits block sign-in until saved and preserve confirmation when reverted", async ({
    page,
}) => {
    const { state, writes } = await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await page.getByLabel("I installed the plugin").check();
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector/oauth/new-callback");
    await expect(page.getByLabel("ChatGPT OAuth callback")).toBeFocused();
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    await expect(page.locator("[data-callback-ready]")).toBeHidden();
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "Unsaved changes",
    );
    await expect(
        page.getByRole("button", { name: "Continue to Share Work" }),
    ).toBeDisabled();
    expect(writes).toHaveLength(0);
    await page.getByLabel("ChatGPT OAuth callback").fill(state.redirectUri!);
    await expect(page.getByLabel("I installed the plugin")).toBeChecked();
    await expect(page.locator("[data-callback-ready]")).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Continue to Share Work" }),
    ).toBeEnabled();
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector/oauth/new-callback");
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    expect(writes.at(-1)?.body).toEqual({
        redirectUri: "https://chatgpt.com/connector/oauth/new-callback",
        rotateSecret: false,
    });
    await expect(page.getByLabel("I installed the plugin")).not.toBeChecked();
    await expect(
        page.getByRole("button", { name: "Continue to Share Work" }),
    ).toBeDisabled();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "restart the connection",
    );
});

test("failed callback saves preserve the draft and keep sign-in blocked until a successful save", async ({
    page,
}) => {
    const { state } = await fixture(page);
    state.redirectUri = null;
    let failSave = true;
    let releaseSave!: () => void;
    const pendingSave = new Promise<void>((resolve) => {
        releaseSave = resolve;
    });
    await page.route("**/api/integrations/chatgpt/client", async (route) => {
        if (!failSave) return route.fallback();
        failSave = false;
        await pendingSave;
        await route.fulfill({
            status: 503,
            json: {
                error: { message: "Callback could not be saved. Try again." },
            },
        });
    });
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    const callback = page.getByLabel("ChatGPT OAuth callback");
    await callback.fill(
        "https://chatgpt.com/connector_platform_oauth_redirect",
    );
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    await expect(callback).not.toBeEditable();
    await expect(page.locator("[data-callback-ready]")).toBeHidden();
    releaseSave();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "Callback could not be saved",
    );
    await expect(callback).toBeEditable();
    await expect(callback).toHaveValue(
        "https://chatgpt.com/connector_platform_oauth_redirect",
    );
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "entered but not saved",
    );
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    expect(state.redirectUri).toBeNull();
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    await expect(page.locator("[data-callback-ready]")).toBeVisible();
    await expect(page.getByLabel("I installed the plugin")).toBeEnabled();
    await expect(page.locator("[data-callback-reminder]")).toBeHidden();
});

test("a failed configuration refresh keeps a saved callback draft blocked until confirmed", async ({
    page,
}) => {
    const { state, writes, fail } = await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    const callback = page.getByLabel("ChatGPT OAuth callback");
    await callback.fill("https://chatgpt.com/connector/oauth/new-callback");
    fail(true);
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "temporarily unavailable",
    );
    expect(writes).toHaveLength(1);
    expect(state.redirectUri).toBe(
        "https://chatgpt.com/connector/oauth/new-callback",
    );
    await expect(callback).toHaveValue(state.redirectUri!);
    await expect(page.locator("[data-callback-ready]")).toBeHidden();
    await expect(page.getByLabel("I installed the plugin")).toBeDisabled();
    fail(false);
    await page
        .getByRole("button", { name: "Save callback", exact: true })
        .click();
    await expect(page.locator("[data-callback-ready]")).toBeVisible();
    await expect(page.locator("#chatgpt-callback-status")).toContainText(
        "Callback saved",
    );
});

test("progress resumes without storing drafts or treating old provider checks as current", async ({
    page,
}) => {
    const { state, verification } = await fixture(page);
    verification.discovery = verification.issuerResponses = true;
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Sign-in");
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await goToStep(page, "Install");
    await page.getByLabel("I installed the plugin").check();
    await goToStep(page, "Share Work");
    await goToStep(page, "Install");
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector/oauth/unsaved-callback");
    await goToStep(page, "Share Work");
    await page.reload();
    await expect(
        page.getByRole("heading", { name: "Choose what ChatGPT can read" }),
    ).toBeVisible();
    await expect(
        page.getByLabel("Owner OAuth ID", { exact: true }),
    ).toHaveValue(state.ownerSubject!);
    expect(
        await page.evaluate(() => localStorage.getItem("goblin.chatgpt.setup")),
    ).not.toContain("unsaved-callback");
    await goToStep(page, "Install");
    await expect(page.getByLabel("I installed the plugin")).toBeChecked();
    await goToStep(page, "Sign-in");
    await expect(
        page.getByRole("button", { name: "Continue to Install" }),
    ).toBeDisabled();
    state.issuer = "https://other.example.test/";
    await page.reload();
    await expect(
        page.getByRole("heading", { name: "Prepare your instance" }),
    ).toBeVisible();
    await goToStep(page, "Install");
    await expect(page.getByLabel("I installed the plugin")).not.toBeChecked();
});

test("public submission is a separate workflow with explicit external confirmations", async ({
    page,
}) => {
    const { writes } = await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await page.getByRole("button", { name: /^Publish publicly/ }).click();
    await expect(
        page.getByRole("heading", { name: "Prepare your public listing" }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Continue to Review tests" }),
    ).toBeDisabled();
    await expect(
        page.getByRole("link", { name: "View Goblin logo" }),
    ).not.toHaveAttribute("download");
    await page.getByLabel("Plugin ID", { exact: true }).fill("goblin-demo");
    await page.getByLabel("Verified publisher name").fill("Example Studio");
    await page
        .getByLabel("Website URL", { exact: true })
        .fill("https://example.test");
    await page
        .getByLabel("Support URL", { exact: true })
        .fill("https://example.test/support");
    await page
        .getByLabel("Privacy policy URL", { exact: true })
        .fill("https://example.test/privacy");
    await page
        .getByLabel("Terms of service URL", { exact: true })
        .fill("https://example.test/terms");
    await page
        .getByLabel("Listing description", { exact: true })
        .fill("Read shared Work from this instance.");
    await page.getByLabel("I have a public review endpoint").check();
    await page
        .getByRole("button", { name: "Continue to Review tests" })
        .click();
    await expect(page.locator(".chatgpt-review-tests li")).toHaveCount(8);
    await page
        .getByLabel("Status-only sample Work ID")
        .fill("9007199254740993");
    await page
        .getByLabel("Shared-result sample Work ID")
        .fill("9007199254740994");
    await page.getByLabel("Unshared sample Work ID").fill("9007199254740995");
    await page.getByLabel("Demo video URL").fill("https://example.test/demo");
    await page.getByLabel("I ran all eight cases").check();
    await page
        .getByRole("button", { name: "Continue to Submit", exact: true })
        .click();
    await page.getByRole("button", { name: "Generate plugin package" }).click();
    await page.getByText("Review package contents", { exact: true }).click();
    await expect(page.locator(".chatgpt-template").first()).toContainText(
        "goblin-demo",
    );
    expect(writes.at(-1)?.body).toMatchObject({
        packageId: "goblin-demo",
        publisher: "Example Studio",
        description: "Read shared Work from this instance.",
        statusWorkId: "9007199254740993",
        resultWorkId: "9007199254740994",
        privateWorkId: "9007199254740995",
        videoUrl: "https://example.test/demo",
    });
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Save plugin ZIP" }).click();
    expect((await download).suggestedFilename()).toBe("goblin-plugin.zip");
    await expect(
        page.getByRole("link", { name: "Open plugin submission portal" }),
    ).toHaveAttribute("href", "https://platform.openai.com/plugins");
    await expect(
        page.getByRole("button", { name: "Continue to Publish", exact: true }),
    ).toBeDisabled();
    await page.getByLabel("The portal confirms I submitted").check();
    await page
        .getByRole("button", { name: "Continue to Publish", exact: true })
        .click();
    await expect(
        page.getByText("Approval and publication are separate steps.", {
            exact: false,
        }),
    ).toBeVisible();
    await expect(
        page.getByLabel("I published the approved plugin"),
    ).not.toBeChecked();
    await page.getByLabel("I published the approved plugin").check();
    await expect(page.getByText("Publication recorded by you.")).toBeVisible();
    expect(writes.map((write) => write.path)).toEqual([
        "/api/integrations/chatgpt/package",
    ]);
    await page.reload();
    await expect(
        page.getByRole("heading", {
            name: "Publish and use the approved plugin",
        }),
    ).toBeVisible();
    await expect(
        page.getByLabel("I published the approved plugin"),
    ).toBeChecked();
});

test("starting setup again clears browser confirmations and drafts without revoking server settings", async ({
    page,
}) => {
    const { writes, state } = await fixture(page);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await page.getByLabel("I installed the plugin").check();
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector/oauth/draft");
    const before = writes.length;
    await page.getByRole("button", { name: "Start setup again" }).click();
    await expect(page.locator("[data-chatgpt-notice]")).toContainText(
        "Setup progress cleared. Saved OAuth settings and shared Work",
    );
    await expect(
        page.getByRole("heading", { name: "Prepare your instance" }),
    ).toBeVisible();
    await goToStep(page, "Install");
    await expect(page.getByLabel("I installed the plugin")).not.toBeChecked();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveValue(
        state.redirectUri ?? "",
    );
    expect(writes).toHaveLength(before);
});

test("grant failures preserve the form and cannot claim success", async ({
    page,
}) => {
    const fixtureState = await fixture(page);
    fixtureState.reject();
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Share Work");
    await page.getByLabel("Also share the latest approved result").check();
    await shareWork(page);
    await expect(
        page.getByText("No approved result is available."),
    ).toBeVisible();
    await expect(
        page.getByLabel("Owner OAuth ID", { exact: true }),
    ).toHaveValue("owner-<img src=x>");
    await expect(
        page.getByLabel("Also share the latest approved result"),
    ).toBeChecked();
    await expect(page.getByText("Work shared.", { exact: false })).toHaveCount(
        0,
    );
    await expect(
        page.getByRole("button", { name: "Continue to Test" }),
    ).toBeDisabled();
});

test("empty Work and temporary load failures have recovery actions", async ({
    page,
}) => {
    const fixtureState = await fixture(page);
    fixtureState.fail(true);
    await page.goto("/settings?section=connections&provider=chatgpt");
    await expect(
        page.getByText("Setup is temporarily unavailable."),
    ).toBeVisible();
    fixtureState.fail(false);
    fixtureState.empty();
    await page.getByRole("button", { name: "Reload setup" }).click();
    await goToStep(page, "Share Work");
    await expect(
        page.getByRole("link", { name: "Create Work in Goblin" }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Refresh Work and access" }).click();
    await expect(
        page.getByRole("button", { name: "Share selected Work" }),
    ).toBeDisabled();
});

test("workflow remains usable without clipboard or browser storage and fits narrow screens", async ({
    page,
}) => {
    await fixture(page);
    await page.addInitScript(() => {
        Object.defineProperty(navigator, "clipboard", { value: undefined });
        Storage.prototype.setItem = () => {
            throw new Error("Storage unavailable");
        };
    });
    await page.goto("/settings?section=connections&provider=chatgpt");
    await goToStep(page, "Install");
    await page.getByRole("button", { name: "Copy server URL" }).click();
    await expect(page.getByLabel("Server URL", { exact: true })).toBeFocused();
    expect(
        await page
            .getByLabel("Server URL", { exact: true })
            .evaluate(
                (input: HTMLInputElement) =>
                    input.selectionEnd! - input.selectionStart!,
            ),
    ).toBe("https://goblin.example.test/mcp".length);
    await expect(
        page.getByText("browser storage is unavailable", { exact: false }),
    ).toBeVisible();
    await mkdir(".artifacts/chatgpt-workflow/browser", { recursive: true });
    await page
        .getByRole("heading", { name: "ChatGPT plugin", exact: true })
        .scrollIntoViewIfNeeded();
    await page.screenshot({
        path: ".artifacts/chatgpt-workflow/browser/desktop.png",
        fullPage: true,
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(page.getByLabel("Setup step", { exact: true })).toBeVisible();
    for (const step of ["0", "1", "2", "3", "4"]) {
        await page.getByLabel("Setup step", { exact: true }).selectOption(step);
        expect(
            await page.evaluate(
                () => document.documentElement.scrollWidth <= window.innerWidth,
            ),
        ).toBe(true);
    }
    await page.getByLabel("Setup step", { exact: true }).selectOption("2");
    await page
        .getByRole("heading", { name: "ChatGPT plugin", exact: true })
        .scrollIntoViewIfNeeded();
    await page.screenshot({
        path: ".artifacts/chatgpt-workflow/browser/mobile.png",
        fullPage: true,
    });
});

test("configured ChatGPT setup does not claim an installed connection", async ({
    page,
}) => {
    await fixture(page);
    await page.goto("/settings?section=connections");
    const row = page.locator('[data-connection="chatgpt"]');
    await expect(row).toContainText("Configured");
    await expect(row).toContainText("verify sign-in in ChatGPT");
    await expect(row.locator(".connection-status")).toHaveText("Configured");
    await expect(row.locator(".connection-status")).not.toHaveClass(
        /connected/,
    );
});
