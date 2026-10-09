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
            "/api/system": {
                status: "unsupported",
                machine: null,
                history: [],
            },
            "/api/status": { account: null, login: null, runtimeReady: false },
        };
        return route.fulfill({ json: data[path] ?? [] });
    });
    return state;
}

test("directory uses verified connections, search, tabs, and real provider assets", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1440, height: 960 });
    const state = await workspace(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/integrations");
    await expect(
        page.getByRole("heading", { name: "Integrations", exact: true }),
    ).toBeVisible();
    await expect(page).toHaveTitle("Integrations · Goblin");
    await expect(
        page.getByRole("link", { name: "Integrations", exact: true }),
    ).toHaveAttribute("aria-current", "page");
    await expect(page.locator('[data-integration="github"]')).toContainText(
        "Signed in as @example-owner",
    );
    await expect(page.locator(".integration-row")).toHaveCount(3);
    await expect(page.locator('[data-integration="teams"]')).toContainText(
        "Planned",
    );
    await expect(page.locator('[data-integration="teams"] button')).toHaveCount(
        0,
    );
    for (const logo of await page.locator(".integration-row img").all())
        expect(
            await logo.evaluate(
                (img: HTMLImageElement) => img.complete && img.naturalWidth > 0,
            ),
        ).toBe(true);
    const github = await page
        .locator('[data-integration="github"]')
        .boundingBox();
    const slack = await page
        .locator('[data-integration="slack"]')
        .boundingBox();
    expect(github!.y).toBe(slack!.y);
    expect(slack!.x).toBeGreaterThan(github!.x);
    await page.getByRole("tab", { name: /Connected/ }).click();
    await expect(page.locator(".integration-row")).toHaveCount(1);
    await page.getByRole("tab", { name: /Connected/ }).press("ArrowLeft");
    await expect(
        page.getByRole("tab", { name: "All", exact: true }),
    ).toBeFocused();
    const search = page.getByRole("searchbox", { name: "Search integrations" });
    await search.fill("sLaCk");
    await expect(page.locator(".integration-row")).toHaveCount(1);
    await expect(page.locator(".integration-row")).toContainText("Slack");
    await page.clock.fastForward(3100);
    await expect(search).toHaveValue("sLaCk");
    await expect(search).toBeFocused();
    await search.fill("nonexistent-provider");
    await expect(page.getByText("No matching integrations")).toBeVisible();
    await page
        .getByRole("button", { name: "Clear search", exact: true })
        .click();
    await expect(search).toHaveValue("");
    await expect(page.locator(".integration-row")).toHaveCount(3);
    expect(state.commands).toEqual([]);
    expect(errors).toEqual([]);
});

test("manage preserves GitHub permissions, disconnects, and restores focus", async ({
    page,
}) => {
    const state = await workspace(page);
    await page.goto("/integrations");
    await page
        .getByRole("button", { name: "Manage GitHub", exact: true })
        .click();
    const settings = page.getByRole("dialog", {
        name: "Settings",
        exact: true,
    });
    await expect(
        settings.getByRole("heading", { name: "Enabled repositories" }),
    ).toBeVisible();
    await expect(
        settings.getByText("example-owner/project", { exact: true }),
    ).toBeVisible();
    await settings
        .getByRole("button", { name: "Disconnect GitHub", exact: true })
        .click();
    await expect(
        settings.getByRole("button", { name: "Connect GitHub", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Close settings" }).click();
    await expect(page.locator('[data-integration="github"]')).toContainText(
        "Not connected",
    );
    await expect(
        page.getByRole("button", { name: "Connect GitHub", exact: true }),
    ).toBeFocused();
    await page.getByRole("tab", { name: /Connected/ }).click();
    await expect(page.getByText("No connected integrations yet")).toBeVisible();
    expect(state.commands).toEqual(["/api/github/disconnect"]);
});

test("Add integration opens existing setup and unavailable Slack never claims a connection", async ({
    page,
}) => {
    const state = await workspace(page);
    state.slack.available = false;
    await page.goto("/integrations");
    await expect(page.locator('[data-integration="slack"]')).toContainText(
        "Unavailable",
    );
    await page
        .getByRole("button", { name: "Add integration", exact: true })
        .click();
    const picker = page.getByRole("dialog", {
        name: "Add integration",
        exact: true,
    });
    await expect(picker.getByRole("button", { name: /Slack/ })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(
        page.getByRole("button", { name: "Add integration", exact: true }),
    ).toBeFocused();
    await page
        .getByRole("button", { name: "View setup Slack", exact: true })
        .click();
    await expect(
        page.getByText("Slack requires durable Work storage.", {
            exact: false,
        }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Close settings" }).click();
    await expect(
        page.getByRole("button", { name: "View setup Slack", exact: true }),
    ).toBeFocused();
    await page
        .getByRole("button", { name: "Add integration", exact: true })
        .click();
    await picker.getByRole("button", { name: /Slack/ }).click();
    await expect(
        page.getByText("Slack requires durable Work storage.", {
            exact: false,
        }),
    ).toBeVisible();
    expect(state.commands).toEqual([]);
});

test("failed reads clear connected state, isolate provider errors, and can recover", async ({
    page,
}) => {
    const state = await workspace(page);
    state.slack.connection.status = "Connected";
    await page.goto("/integrations");
    await expect(page.getByRole("tab", { name: "Connected 2" })).toBeVisible();
    state.githubFailure = true;
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(page.locator('[data-integration="github"]')).toContainText(
        "Status unavailable",
    );
    await expect(
        page.locator('[data-integration="slack"] .integration-status'),
    ).toHaveText("Connected");
    await expect(page.getByText("Private upstream failure")).toHaveCount(0);
    await page.getByRole("tab", { name: "Connected", exact: true }).click();
    await expect(page.locator(".integration-row")).toHaveCount(1);
    state.githubFailure = false;
    await page.getByRole("button", { name: "Try again", exact: true }).click();
    await expect(page.getByRole("tab", { name: "Connected 2" })).toBeVisible();
    await expect(page.locator(".integration-row")).toHaveCount(2);
});

test("pending setup is distinct from connected and initial reads show loading", async ({
    page,
}) => {
    const state = await workspace(page);
    state.github.status = "Connecting";
    state.slack.setup.status = "AwaitingAuthorization";
    let release = () => {};
    state.slackWait = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.goto("/integrations");
    await expect(page.locator('[data-integration="github"]')).toContainText(
        "Checking…",
    );
    await page.getByRole("tab", { name: "Connected", exact: true }).click();
    await expect(page.getByText("Checking your connections…")).toBeVisible();
    release();
    await expect(page.getByText("No connected integrations yet")).toBeVisible();
    await page.getByRole("tab", { name: "All", exact: true }).click();
    await expect(page.locator('[data-integration="github"]')).toContainText(
        "Awaiting sign-in",
    );
    await expect(page.locator('[data-integration="slack"]')).toContainText(
        "Awaiting authorization",
    );
    expect(state.commands).toEqual([]);
});

test("Knowledge stays honest, preserves Work drafts, and supports browser history", async ({
    page,
}) => {
    const state = await workspace(page);
    await page.goto("/?new=work");
    await page
        .getByRole("textbox", { name: "Describe the intended outcome…" })
        .fill("Keep my draft while browsing tools");
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await expect(page).toHaveURL(/\/knowledge$/);
    await expect(
        page.getByRole("searchbox", { name: "Search indexed knowledge" }),
    ).toBeDisabled();
    await expect(
        page.getByRole("heading", { name: "No indexed sources" }),
    ).toBeVisible();
    await expect(
        page.getByText("Indexing unavailable", { exact: true }),
    ).toBeVisible();
    await page
        .getByRole("link", { name: "View integrations", exact: true })
        .click();
    await expect(page).toHaveURL(/\/integrations$/);
    await page.goBack();
    await expect(
        page.getByRole("heading", { name: "Knowledge", exact: true }),
    ).toBeVisible();
    await page.goBack();
    await expect(
        page.getByRole("textbox", { name: "Describe the intended outcome…" }),
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

test("connection management stays accessible when Work storage is unavailable", async ({
    page,
}) => {
    const state = await workspace(page);
    state.workFailure = true;
    state.slack.available = false;
    await page.goto("/integrations");
    await expect(
        page.getByRole("heading", { name: "Integrations", exact: true }),
    ).toBeVisible();
    await page
        .getByRole("button", { name: "Manage GitHub", exact: true })
        .click();
    await expect(
        page.getByRole("button", { name: "Check connection", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Close settings" }).click();
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await expect(
        page.getByRole("heading", { name: "No indexed sources" }),
    ).toBeVisible();
    expect(state.commands).toEqual([]);
});

test("locking clears late status reads and both page routes retain the access gate", async ({
    page,
}) => {
    const state = await workspace(page);
    let release = () => {};
    state.slackWait = new Promise<void>((resolve) => {
        release = resolve;
    });
    await page.goto("/integrations/");
    await page.getByLabel("Workspace menu").click();
    await page
        .getByRole("button", { name: "Lock workspace", exact: true })
        .click();
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    release();
    await expect(page.locator(".integration-row")).toHaveCount(0);
    await page.goto("/knowledge/");
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    await expect(
        page.getByRole("heading", { name: "No indexed sources" }),
    ).toHaveCount(0);
});

test("pages fit desktop and mobile with keyboard-accessible navigation", async ({
    page,
}) => {
    await workspace(page);
    await mkdir(".artifacts/integrations-knowledge", { recursive: true });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto("/integrations");
    await expect(page.locator('[data-integration="github"]')).toContainText(
        "Connected",
    );
    await page.screenshot({
        path: ".artifacts/integrations-knowledge/integrations-desktop.png",
    });
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await page.screenshot({
        path: ".artifacts/integrations-knowledge/knowledge-desktop.png",
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await page
        .getByRole("button", { name: "Show sidebar", exact: true })
        .click();
    await page.getByRole("link", { name: "Integrations", exact: true }).click();
    await expect(
        page.getByRole("heading", { name: "Integrations", exact: true }),
    ).toBeFocused();
    const first = await page
        .locator('[data-integration="github"]')
        .boundingBox();
    const second = await page
        .locator('[data-integration="slack"]')
        .boundingBox();
    expect(second!.y).toBeGreaterThan(first!.y);
    await page.screenshot({
        path: ".artifacts/integrations-knowledge/integrations-mobile.png",
    });
    for (const width of [320, 390, 768, 1024, 1440]) {
        await page.setViewportSize({ width, height: 844 });
        expect(
            await page
                .locator(".directory-page")
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
        .getByRole("button", { name: "Show sidebar", exact: true })
        .click();
    await page.getByRole("link", { name: "Knowledge", exact: true }).click();
    await expect(
        page.getByRole("heading", { name: "Knowledge", exact: true }),
    ).toBeFocused();
    expect(
        await page
            .locator(".directory-page")
            .evaluate((el) => el.scrollWidth <= el.clientWidth),
    ).toBe(true);
    await page.screenshot({
        path: ".artifacts/integrations-knowledge/knowledge-mobile.png",
    });
});
