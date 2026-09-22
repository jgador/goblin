import { test, expect, type Page } from "@playwright/test";

// Layout fixtures only. The durable Work journeys exercise the real API and database.
async function workspace(page: Page, connected = true) {
    const items = [
        {
            id: "1",
            objective: "Polish the welcome page",
            status: "NeedsAttention",
            attention: { reason: "ResultReview" },
        },
        {
            id: "2",
            objective: "Investigate slow repository clones",
            status: "InProgress",
        },
        { id: "3", objective: "Write the release notes", status: "Completed" },
    ].map((work) => ({
        version: "1",
        createdAt: "2026-09-21T00:00:00Z",
        updatedAt: "2026-09-21T00:00:00Z",
        work: {
            ...work,
            agentId: "1",
            attempts: [
                {
                    id: "1",
                    status: "Succeeded",
                    target: {
                        runtime: "codex",
                        repository: {
                            repository: "owner/project",
                            grant: { branch: "goblin/1/1", login: "owner" },
                        },
                    },
                    session: { model: "fixture-model" },
                },
            ],
            history: [
                {
                    sequence: "1",
                    kind: "ResultProposed",
                    text: "The welcome page is ready for review. The introduction is shorter, the primary action is clear, and the mobile layout keeps the important content in view.",
                    occurredAt: "2026-09-21T00:02:00Z",
                },
            ],
            decisions: [],
            results: [{ attemptId: "1", text: "Ready for review." }],
            artifacts: [],
        },
    }));
    const commands: string[] = [];
    await page.route("**/api/**", (route) => {
        const path = new URL(route.request().url()).pathname;
        if (route.request().method() === "POST") commands.push(path);
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: true };
        if (path === "/api/work") json = items;
        if (path === "/api/agents") json = [{ id: "1", name: "Goblin" }];
        if (path === "/api/connections")
            json = [
                {
                    id: "1",
                    runtime: "codex",
                    name: "Codex",
                    availability: connected ? "Available" : "Disconnected",
                },
            ];
        if (path === "/api/status")
            json = {
                account: null,
                login: null,
                notice: null,
                runtimeReady: true,
            };
        if (path === "/api/github")
            json = { configured: true, status: "Disconnected" };
        if (path === "/api/cluster") json = { available: true };
        if (path === "/api/conversations")
            json = [
                {
                    id: "8",
                    title: "Ideas for the next release",
                    messages: [
                        {
                            id: "1",
                            text: "Keep the release small.",
                            createdAt: "2026-09-21T00:00:00Z",
                        },
                    ],
                },
            ];
        return route.fulfill({ json });
    });
    return commands;
}

test("home, Settings, and collapsing navigation preserve a draft without executing work", async ({
    page,
}) => {
    const commands = await workspace(page, false);
    await page.goto("/");
    await expect(
        page.getByRole("heading", { name: "What should we work on?" }),
    ).toBeVisible();
    await expect(
        page.getByRole("heading", { name: "Choose how to sign in" }),
    ).toHaveCount(0);
    const draft = page.getByPlaceholder("Describe the intended outcome…");
    await draft.fill("Keep my idea while I connect an AI account");
    await page
        .getByRole("button", { name: "Connect an AI provider", exact: true })
        .click();
    const settings = page.getByRole("dialog", { name: "Settings" });
    await expect(
        settings.getByRole("heading", { name: "AI connections" }),
    ).toBeVisible();
    await settings
        .getByRole("button", { name: "Manage connection", exact: true })
        .click();
    await expect(
        settings.getByRole("button", { name: "Continue with ChatGPT" }),
    ).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(draft).toHaveValue(
        "Keep my idea while I connect an AI account",
    );
    await page
        .getByRole("button", { name: "Hide sidebar", exact: true })
        .click();
    await expect(
        page.getByRole("button", { name: "Show sidebar", exact: true }),
    ).toBeFocused();
    await expect(draft).toHaveValue(
        "Keep my idea while I connect an AI account",
    );
    await page.reload();
    await expect(
        page.getByRole("button", { name: "Show sidebar", exact: true }),
    ).toBeVisible();
    expect(commands).toEqual([]);
});

test("search, attention filters, and persistent details share one main work area", async ({
    page,
}) => {
    const commands = await workspace(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/");
    await expect(
        page.getByRole("heading", { name: "What should we work on?" }),
    ).toBeVisible();
    await page.screenshot({ path: "test-results/workspace-home.png" });
    const search = page.getByRole("searchbox");
    await search.fill("repository");
    await expect(
        page
            .getByRole("navigation", { name: "Recent work" })
            .getByRole("button"),
    ).toHaveCount(1);
    await search.fill("");
    await page.getByRole("button", { name: "Needs you", exact: true }).click();
    await expect(
        page
            .getByRole("navigation", { name: "Recent work" })
            .getByRole("button"),
    ).toHaveCount(1);
    await page.getByRole("button", { name: /Polish the welcome page/ }).click();
    await expect(page.locator(".detail h2")).toHaveText(
        "Polish the welcome page",
    );
    await expect(
        page.getByRole("button", { name: "Approve & complete" }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Details", exact: true }),
    ).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "Activity" })).toBeVisible();
    await expect(page.locator(".detail-properties span")).toHaveText([
        "Goblin",
        "Work 1",
        "codex · fixture-model",
    ]);
    await expect(
        page.getByText("owner/project · goblin/1/1", { exact: true }),
    ).toBeVisible();
    await page.reload();
    await expect(page.getByRole("tab", { name: "Conversation" })).toBeVisible();
    await expect(
        page.getByRole("tabpanel", { name: "Conversation" }),
    ).toBeVisible();
    await page.screenshot({ path: "test-results/workspace-work.png" });
    await page
        .getByPlaceholder("Add context to this work…")
        .fill("Keep the review draft");
    await page.getByRole("tab", { name: "Activity" }).click();
    await expect(
        page.getByRole("heading", { name: "Executions", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(page.getByRole("tab", { name: "Activity" })).toHaveAttribute(
        "aria-selected",
        "true",
    );
    await expect(
        page.getByRole("tabpanel", { name: "Activity" }),
    ).toBeVisible();
    await page.getByRole("tab", { name: "Activity" }).press("ArrowRight");
    await expect(page.getByRole("tab", { name: "Outputs" })).toBeFocused();
    await expect(page.getByRole("tabpanel", { name: "Outputs" })).toBeVisible();
    await page.getByRole("tab", { name: "Conversation" }).click();
    await expect(
        page.getByPlaceholder("Add context to this work…"),
    ).toHaveValue("Keep the review draft");
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await page
        .getByPlaceholder("Describe the intended outcome…")
        .fill("A second idea");
    await page.getByRole("button", { name: /Polish the welcome page/ }).click();
    await expect(page.getByRole("tab", { name: "Activity" })).toBeVisible();
    await expect(
        page.getByPlaceholder("Add context to this work…"),
    ).toHaveValue("Keep the review draft");
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await expect(
        page.getByPlaceholder("Describe the intended outcome…"),
    ).toHaveValue("A second idea");
    expect(commands).toEqual([]);
    expect(errors).toEqual([]);
});

test("mobile navigation restores focus and leaves the selected work at full width", async ({
    page,
}) => {
    await workspace(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/");
    await expect(
        page.getByRole("heading", { name: "What should we work on?" }),
    ).toBeVisible();
    await page.screenshot({ path: "test-results/workspace-home-mobile.png" });
    const show = page.getByRole("button", {
        name: "Show sidebar",
        exact: true,
    });
    await show.click();
    const nav = page.getByRole("dialog", { name: "Workspace", exact: true });
    await expect(nav).toBeVisible();
    await page
        .getByRole("button", { name: "Hide sidebar", exact: true })
        .focus();
    await page.keyboard.press("Escape");
    await expect(show).toBeFocused();
    await show.click();
    await page.getByRole("button", { name: /Polish the welcome page/ }).click();
    await expect(nav).not.toBeVisible();
    await expect(page.locator(".detail h2")).toBeFocused();
    await expect(
        page.getByRole("button", { name: "Details", exact: true }),
    ).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "Activity" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Outputs" })).toBeVisible();
    await expect(
        page.getByText("owner/project · goblin/1/1", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Approve & complete" }),
    ).toBeVisible();
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
    await page.screenshot({ path: "test-results/workspace-work-mobile.png" });
});
