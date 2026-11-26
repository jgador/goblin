import { test, expect, type Page } from "@playwright/test";
import { mkdir } from "node:fs/promises";

// UI fixtures only. Connection setup itself remains covered by the existing
// GitHub/Slack journeys; these tests never authenticate with external services.
async function workspace(page: Page) {
    const now = new Date("2026-10-10T04:00:00Z");
    await page.clock.install({ time: now });
    await page.clock.pauseAt(now);
    const state = {
        authenticated: true,
        runtimeAvailability: "Available",
        github: {
            configured: true,
            status: "Connected",
            login: "example-owner",
        },
        slack: {
            available: true,
            connection: {
                status: "Disconnected",
                workspace: "Example team",
                appId: "",
            },
            setup: { status: "Idle" },
            identities: [],
        },
        githubFailure: false,
        workFailure: false,
        slackWait: null as Promise<void> | null,
        commands: [] as string[],
        requests: [] as string[],
    };
    await page.route("**/api/**", async (route) => {
        if (route.request().resourceType() === "script")
            return route.continue();
        const path = new URL(route.request().url()).pathname;
        state.requests.push(path);
        if (route.request().method() === "POST") state.commands.push(path);
        if (path === "/api/session/lock") state.authenticated = false;
        if (path === "/api/integrations/slack") await state.slackWait;
        if (
            (path === "/api/github" && state.githubFailure) ||
            (path === "/api/work" && state.workFailure)
        )
            return route.fulfill({
                status: 503,
                json: { error: { message: "Private upstream failure" } },
            });
        if (path === "/api/github/disconnect")
            state.github = {
                configured: true,
                status: "Disconnected",
                login: "",
            };
        const data: Record<string, unknown> = {
            "/api/session": { authenticated: state.authenticated },
            "/api/preferences": { timeZone: "Asia/Manila" },
            "/api/github": state.github,
            "/api/github/disconnect": state.github,
            "/api/github/repositories": [
                {
                    id: "1",
                    name: "example-owner/project",
                    defaultBranch: "main",
                    enabled: true,
                },
            ],
            "/api/integrations/slack": state.slack.available
                ? state.slack
                : {
                      available: false,
                      connection: null,
                      setup: null,
                      identities: [],
                  },
            "/api/integrations/chatgpt": {
                configured: false,
                name: "Goblin",
                description:
                    "Read and manage all Work in your private Goblin instance.",
                logoUrl: "/assets/branding/chatgpt.png",
                serverUrl: null,
                issuer: null,
                clientId: "goblin-chatgpt",
                scope: "openid offline_access goblin.work.read goblin.work.write",
                ownerSubject: null,
                clientConfigured: false,
                redirectUri: null,
                environmentVariables: {
                    publicOrigin: "GOBLIN_PUBLIC_ORIGIN",
                    enabled: "GOBLIN_CHATGPT_ENABLED",
                },
            },
            "/api/system": {
                status: "unsupported",
                machine: null,
                history: [],
            },
            "/api/connections": [
                {
                    id: "1",
                    runtime: "codex",
                    name: "Codex",
                    availability: state.runtimeAvailability,
                },
            ],
            "/api/status": { account: null, login: null, runtimeReady: false },
        };
        return route.fulfill({ json: data[path] ?? [] });
    });
    return state;
}

test("Connections groups every tool under one Settings section without changing access", async ({
    page,
}) => {
    const state = await workspace(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/settings?section=connections");
    await expect(page).toHaveTitle("Settings · Goblin");
    const runtime = page.getByRole("list", {
        name: "Agent runtime",
        exact: true,
    });
    const integrations = page.getByRole("list", {
        name: "Integrations",
        exact: true,
    });
    await expect(runtime).toHaveAttribute("aria-busy", "false");
    await expect(integrations).toHaveAttribute("aria-busy", "false");
    await expect(runtime.getByRole("listitem")).toHaveCount(1);
    await expect(runtime.getByRole("heading", { name: "Codex" })).toBeVisible();
    await expect(integrations.getByRole("listitem")).toHaveCount(4);
    await expect(
        page.locator('.settings-nav [data-provider="connections"]'),
    ).toHaveAttribute("aria-current", "page");
    await expect(page.locator('[data-connection="codex"]')).toContainText(
        "Ready to run Work.",
    );
    await expect(page.locator('[data-connection="github"]')).toContainText(
        "Signed in as @example-owner",
    );
    await expect(
        page.locator('[data-connection="chatgpt"] .connection-status'),
    ).toHaveText("Not configured");
    await expect(page.locator('[data-connection="teams"]')).toContainText(
        "Planned",
    );
    await expect(
        page.locator(
            '[data-connection="teams"] a, [data-connection="teams"] button',
        ),
    ).toHaveCount(0);
    await expect(page.locator('.sidebar [href="/integrations"]')).toHaveCount(
        0,
    );
    for (const provider of ["codex", "chatgpt", "github", "slack"]) {
        const link = page.locator("#connection-" + provider);
        await expect(link).toHaveAttribute(
            "href",
            "/settings?section=connections&provider=" + provider,
        );
        await link.click();
        await expect(page).toHaveURL(
            new RegExp(
                "/settings\\?section=connections&provider=" + provider + "$",
            ),
        );
        await expect(
            page.locator('.settings-nav [data-provider="connections"]'),
        ).toHaveAttribute("aria-current", "page");
        await page
            .getByRole("link", { name: "Back to connections", exact: true })
            .click();
        await expect(page).toHaveURL(/\/settings\?section=connections$/);
        await expect(link).toBeFocused();
    }
    expect(state.commands).toEqual([]);
    expect(errors).toEqual([]);
});

test("GitHub permissions and disconnect remain explicit and return to Connections", async ({
    page,
}) => {
    const state = await workspace(page);
    await page.goto("/settings?section=connections");
    await page
        .getByRole("link", { name: "Manage GitHub", exact: true })
        .click();
    await expect(
        page.getByRole("heading", { name: "Enabled repositories" }),
    ).toBeVisible();
    await expect(
        page.getByText("example-owner/project", { exact: true }),
    ).toBeVisible();
    await page
        .getByRole("button", { name: "Disconnect GitHub", exact: true })
        .click();
    await expect(
        page.getByRole("button", { name: "Connect GitHub", exact: true }),
    ).toBeVisible();
    await page
        .getByRole("link", { name: "Back to connections", exact: true })
        .click();
    await expect(
        page.locator('[data-connection="github"] .connection-status'),
    ).toHaveText("Not connected");
    await expect(
        page.getByRole("link", { name: "Connect GitHub", exact: true }),
    ).toBeFocused();
    expect(state.commands).toEqual(["/api/github/disconnect"]);
});

test("unavailable Slack remains a setup page without claiming a connection", async ({
    page,
}) => {
    const state = await workspace(page);
    state.slack.available = false;
    await page.goto("/settings?section=connections");
    await expect(page.locator('[data-connection="slack"]')).toContainText(
        "Unavailable",
    );
    await page
        .getByRole("link", { name: "View setup Slack", exact: true })
        .click();
    await expect(
        page.getByText("Slack requires durable Work storage.", {
            exact: false,
        }),
    ).toBeVisible();
    await page
        .getByRole("link", { name: "Back to connections", exact: true })
        .click();
    await expect(
        page.getByRole("link", { name: "View setup Slack", exact: true }),
    ).toBeFocused();
    expect(state.commands).toEqual([]);
});

test("failed reads clear connected state, isolate provider errors, and can recover", async ({
    page,
}) => {
    const state = await workspace(page);
    state.slack.connection.status = "Connected";
    await page.goto("/settings?section=connections");
    await expect(
        page.locator('[data-connection="github"] .connection-status'),
    ).toHaveText("Connected");
    state.githubFailure = true;
    await page.clock.runFor(3000);
    await expect(
        page.locator('[data-connection="github"] .connection-status'),
    ).toHaveText("Status unavailable");
    await expect(
        page.locator('[data-connection="slack"] .connection-status'),
    ).toHaveText("Connected");
    await expect(page.getByText("Private upstream failure")).toHaveCount(0);
    state.githubFailure = false;
    await page.getByRole("button", { name: "Try again", exact: true }).click();
    await expect(
        page.locator('[data-connection="github"] .connection-status'),
    ).toHaveText("Connected");
});

test("pending setup is distinct from connected and initial reads show loading", async ({
    page,
}) => {
    const state = await workspace(page);
    state.github.status = "Connecting";
    state.runtimeAvailability = "Verifying";
    state.slack.setup.status = "AwaitingAuthorization";
    let release = () => {};
    state.slackWait = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.goto("/settings?section=connections");
    await expect(page.locator('[data-connection="github"]')).toContainText(
        "Checking…",
    );
    release();
    await expect(
        page.locator('[data-connection="codex"] .connection-status'),
    ).toHaveText("Verifying sign-in");
    await expect(
        page.locator('[data-connection="github"] .connection-status'),
    ).toHaveText("Awaiting sign-in");
    await expect(
        page.locator('[data-connection="slack"] .connection-status'),
    ).toHaveText("Awaiting authorization");
    expect(state.commands).toEqual([]);
});

test("connection management stays accessible when Work storage is unavailable", async ({
    page,
}) => {
    const state = await workspace(page);
    state.workFailure = true;
    state.slack.available = false;
    await page.setViewportSize({ width: 320, height: 800 });
    await page.goto("/work");
    await expect(page.getByText(/Work could not be loaded/)).toBeVisible();
    await page.getByLabel("Workspace menu", { exact: true }).click();
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    await expect(
        page.getByRole("heading", { name: "Connections", exact: true }),
    ).toBeVisible();
    await page
        .getByRole("link", { name: "Manage GitHub", exact: true })
        .click();
    await expect(
        page.getByRole("button", { name: "Check connection", exact: true }),
    ).toBeVisible();
    await page
        .getByRole("link", { name: "Back to connections", exact: true })
        .click();
    await page
        .getByRole("button", { name: "Show sidebar", exact: true })
        .click();
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await expect(
        page.getByRole("heading", {
            name: "Knowledge indexing is unavailable in this deployment",
        }),
    ).toBeVisible();
    expect(state.commands).toEqual([]);
});

test("locking clears late connection reads and both pages retain the access gate", async ({
    page,
}) => {
    const state = await workspace(page);
    let release = () => {};
    state.slackWait = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.goto("/settings/?section=connections");
    await page.getByLabel("Workspace menu").click();
    await page
        .locator("#workspace-menu")
        .getByRole("button", { name: "Lock workspace", exact: true })
        .click();
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    release();
    await expect(page.locator(".connection-row")).toHaveCount(0);
    await page.goto("/knowledge/");
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    await expect(
        page.getByRole("heading", {
            name: "Knowledge indexing is unavailable in this deployment",
        }),
    ).toHaveCount(0);
});

test("Connections and Knowledge fit desktop and mobile with keyboard navigation", async ({
    page,
}) => {
    await workspace(page);
    await mkdir(".artifacts/settings-coherence", { recursive: true });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto("/settings?section=connections");
    await expect(page.locator('[data-connection="github"]')).toContainText(
        "Connected",
    );
    await page.screenshot({
        path: ".artifacts/settings-coherence/connections-desktop.png",
    });
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await page.screenshot({
        path: ".artifacts/settings-coherence/knowledge-desktop.png",
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByLabel("Workspace menu", { exact: true }).click();
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    await expect(
        page.getByRole("combobox", { name: "Settings section", exact: true }),
    ).toHaveValue("connections");
    await expect(
        page.getByRole("heading", { name: "Connections", exact: true }),
    ).toBeFocused();
    await page.screenshot({
        path: ".artifacts/settings-coherence/connections-mobile.png",
        fullPage: true,
    });
    await page.locator("#connection-github").focus();
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(
        /\/settings\?section=connections&provider=github$/,
    );
    await page
        .getByRole("link", { name: "Back to connections", exact: true })
        .focus();
    await page.keyboard.press("Enter");
    await expect(page.locator("#connection-github")).toBeFocused();
    for (const width of [320, 390, 768, 1024, 1440]) {
        await page.setViewportSize({ width, height: 844 });
        expect(
            await page
                .locator(".settings-page")
                .evaluate((el) => el.scrollWidth <= el.clientWidth),
        ).toBe(true);
        expect(
            await page.evaluate(
                () => document.documentElement.scrollWidth <= innerWidth,
            ),
        ).toBe(true);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page
        .getByRole("button", { name: "Back to Knowledge", exact: true })
        .click();
    await page.screenshot({
        path: ".artifacts/settings-coherence/knowledge-mobile.png",
    });
});
test("Knowledge stays honest, preserves Work drafts, and supports browser history", async ({
    page,
}) => {
    const state = await workspace(page);
    await page.goto("/?new=work");
    await page
        .getByRole("textbox", { name: "Describe the work" })
        .fill("Keep my draft while browsing tools");
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await expect(page).toHaveURL(/\/knowledge$/);
    await expect(
        page.getByRole("searchbox", { name: "Search indexed knowledge" }),
    ).toHaveCount(0);
    await expect(
        page.getByRole("heading", {
            name: "Knowledge indexing is unavailable in this deployment",
        }),
    ).toBeVisible();
    await expect(
        page.getByRole("link", { name: "Manage connections", exact: true }),
    ).toHaveCount(0);
    await page.getByLabel("Workspace menu", { exact: true }).click();
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    await expect(page).toHaveURL(/\/settings\?section=connections$/);
    await page.goBack();
    await expect(
        page.getByRole("heading", { name: "Knowledge", exact: true }),
    ).toBeVisible();
    await page.goBack();
    await expect(
        page.getByRole("textbox", { name: "Describe the work" }),
    ).toHaveValue("Keep my draft while browsing tools");
    await page.goForward();
    await expect(page).toHaveURL(/\/knowledge$/);
    await page.reload();
    await expect(
        page.getByRole("heading", { name: "Knowledge", exact: true }),
    ).toBeVisible();
    expect(
        state.requests.filter((path) => /knowledge|setup-memory/.test(path)),
    ).toEqual([]);
    expect(state.commands).toEqual([]);
});

for (const width of [1440, 390]) {
    test(`global search opens and cancels on Knowledge at ${width}px`, async ({
        page,
    }) => {
        await workspace(page);
        await page.setViewportSize({ width, height: 844 });
        await page.route("**/api/work", (route) =>
            route.fulfill({
                json: [
                    {
                        version: "1",
                        createdAt: "2026-09-21T00:00:00Z",
                        updatedAt: "2026-09-21T00:00:00Z",
                        work: {
                            id: "10",
                            title: "Release checklist",
                            objective: "Release checklist",
                            status: "Ready",
                            attempts: [],
                            history: [],
                            decisions: [],
                            results: [],
                            artifacts: [],
                        },
                    },
                ],
            }),
        );
        await page.route("**/api/conversations", (route) =>
            route.fulfill({
                json: [{ id: "11", title: "Release ideas", messages: [] }],
            }),
        );
        await page.goto("/knowledge");
        const opener = page.getByRole("button", {
            name: "Search",
            exact: true,
        });
        await opener.click();
        const dialog = page.getByRole("dialog", { name: "Global search" });
        const query = dialog.getByRole("combobox", {
            name: "Search work and conversations",
        });
        await expect(query).toBeFocused();
        await query.fill("release");
        await expect(
            dialog.getByRole("group", { name: "Work", exact: true }),
        ).toContainText("Release checklist");
        await expect(
            dialog.getByRole("group", { name: "Conversations", exact: true }),
        ).toContainText("Release ideas");
        await expect(page).toHaveURL(/\/knowledge$/);
        expect(
            await dialog.evaluate(
                (panel) => panel.getBoundingClientRect().right <= innerWidth,
            ),
        ).toBe(true);
        await query.press("Escape");
        await expect(dialog).toBeHidden();
        await expect(opener).toBeFocused();
        await expect(page).toHaveURL(/\/knowledge$/);

        await page
            .getByRole("heading", { name: "Knowledge", exact: true })
            .focus();
        await page.keyboard.press("Control+k");
        await query.fill("release");
        await query.press("ArrowDown");
        await query.press("ArrowDown");
        await expect(dialog.getByRole("option", { selected: true })).toHaveText(
            /Release ideas/,
        );
        await query.press("Enter");
        await expect(
            page.getByRole("heading", { name: "Release ideas" }),
        ).toBeFocused();
    });
}
