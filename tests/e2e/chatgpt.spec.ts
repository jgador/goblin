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
        description: "Read shared Work from your instance.",
        logoUrl: "/assets/branding/icon.svg",
        serverUrl: configured ? "https://goblin.example.test/mcp" : null,
        issuer: configured
            ? "https://identity.example.test/realms/goblin"
            : null,
        clientId: "goblin-chatgpt",
        scope: "goblin.work.read",
        environmentVariables: {
            publicOrigin: "GOBLIN_PUBLIC_ORIGIN",
            issuer: "GOBLIN_CHATGPT_ISSUER",
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
        if (path === "/api/integrations/chatgpt/provider-template")
            json = {
                realm: "goblin",
                clients: [{ clientId: "goblin-chatgpt", redirectUris: [] }],
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
        verification,
        writes,
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
async function goToStep(page: Page, name: string) {
    await page
        .locator(".chatgpt-stepper")
        .getByRole("button", { name: new RegExp(`^${name}`) })
        .click();
}
async function shareWork(page: Page) {
    await page
        .getByLabel("OAuth user ID", { exact: true })
        .fill("reader-<img src=x>");
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
    await page.goto("/settings?section=chatgpt");
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
    await page
        .getByLabel("ChatGPT OAuth callback")
        .fill("https://chatgpt.com/connector_platform_oauth_redirect");
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await expect(
        page.getByText("Needs attention: Provider discovery", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Continue to Install" }),
    ).toBeDisabled();
    await expect(page.getByLabel("ChatGPT OAuth callback")).toHaveValue(
        "https://chatgpt.com/connector_platform_oauth_redirect",
    );
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
        .getByLabel("OAuth user ID", { exact: true })
        .fill("reader-<img src=x>");
    await page
        .getByLabel("Work", { exact: true })
        .selectOption("9007199254740993");
    await goToStep(page, "Sign-in");
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await goToStep(page, "Share Work");
    await expect(page.getByLabel("OAuth user ID", { exact: true })).toHaveValue(
        "reader-<img src=x>",
    );
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
    await page.goto("/settings?section=chatgpt");
    await expect(
        page.getByRole("button", { name: "Continue to Sign-in" }),
    ).toBeDisabled();
    await page
        .getByLabel("Public Goblin origin")
        .fill("http://goblin.example.test");
    await page
        .getByLabel("Keycloak realm issuer")
        .fill("https://identity.example.test/realms/goblin");
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
        'env:\n  - name: GOBLIN_PUBLIC_ORIGIN\n    value: "https://goblin.example.test"\n  - name: GOBLIN_CHATGPT_ISSUER\n    value: "https://identity.example.test/realms/goblin"',
    );
    await page
        .getByRole("button", { name: "Check configuration again" })
        .click();
    await expect(
        page.getByText("The integration is not enabled yet.", { exact: false }),
    ).toBeVisible();
    state.configured = true;
    state.issuer = "https://identity.example.test/realms/goblin";
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

test("realm configuration is previewable and saved only as an import asset", async ({
    page,
}) => {
    const { writes } = await fixture(page);
    await page.goto("/settings?section=chatgpt");
    await goToStep(page, "Sign-in");
    const downloads: string[] = [];
    page.on("download", (download) =>
        downloads.push(download.suggestedFilename()),
    );
    await page
        .getByRole("button", { name: "Generate realm configuration" })
        .click();
    await page
        .getByText("Preview generated realm configuration", { exact: true })
        .click();
    await expect(page.locator(".chatgpt-template")).toContainText(
        '"realm": "goblin"',
    );
    expect(downloads).toEqual([]);
    expect(writes.at(-1)?.body).toEqual({ redirectUri: "" });
    const download = page.waitForEvent("download");
    await page.getByRole("button", { name: "Save realm import file" }).click();
    expect((await download).suggestedFilename()).toBe(
        "goblin-keycloak-realm.json",
    );
});

test("progress resumes without storing drafts or treating old provider checks as current", async ({
    page,
}) => {
    const { state, verification } = await fixture(page);
    verification.discovery = verification.issuerResponses = true;
    await page.goto("/settings?section=chatgpt");
    await goToStep(page, "Sign-in");
    await page
        .getByRole("button", { name: "Check provider and storage" })
        .click();
    await goToStep(page, "Install");
    await page.getByLabel("I installed the plugin").check();
    await goToStep(page, "Share Work");
    await page
        .getByLabel("OAuth user ID", { exact: true })
        .fill("unsaved-user-id");
    await page.reload();
    await expect(
        page.getByRole("heading", { name: "Choose what ChatGPT can read" }),
    ).toBeVisible();
    await expect(page.getByLabel("OAuth user ID", { exact: true })).toBeEmpty();
    expect(
        await page.evaluate(() => localStorage.getItem("goblin.chatgpt.setup")),
    ).not.toContain("unsaved-user-id");
    await goToStep(page, "Install");
    await expect(page.getByLabel("I installed the plugin")).toBeChecked();
    await goToStep(page, "Sign-in");
    await expect(
        page.getByRole("button", { name: "Continue to Install" }),
    ).toBeDisabled();
    state.issuer = "https://identity.example.test/realms/another";
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
    await page.goto("/settings?section=chatgpt");
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

test("grant failures preserve the form and cannot claim success", async ({
    page,
}) => {
    const fixtureState = await fixture(page);
    fixtureState.reject();
    await page.goto("/settings?section=chatgpt");
    await goToStep(page, "Share Work");
    await page.getByLabel("Also share the latest approved result").check();
    await shareWork(page);
    await expect(
        page.getByText("No approved result is available."),
    ).toBeVisible();
    await expect(page.getByLabel("OAuth user ID", { exact: true })).toHaveValue(
        "reader-<img src=x>",
    );
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
    await page.goto("/settings?section=chatgpt");
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
    await page.goto("/settings?section=chatgpt");
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

test("configured setup does not claim an installed connection in the directory", async ({
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
