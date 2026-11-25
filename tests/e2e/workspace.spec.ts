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
                            grant: {
                                branch: `goblin/work-${work.id}`,
                                login: "owner",
                            },
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
        if (route.request().resourceType() === "script")
            return route.continue();
        if (route.request().method() === "POST") commands.push(path);
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: true };
        else if (path === "/api/preferences")
            json = { timeZone: "Asia/Manila" };
        if (path === "/api/work") json = items;
        if (path === "/api/agents")
            json = [
                { id: "1", name: "Goblin", connectionId: "1", isDefault: true },
            ];
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

test("Changes previews and downloads the saved native patch through read-only routes", async ({
    page,
}) => {
    const commands = await workspace(page);
    const base = "b".repeat(40);
    const head = "a".repeat(40);
    const patch = `From ${head} Mon Sep 17 00:00:00 2001\nFrom: Goblin <agent@example.test>\nDate: Sun, 20 Sep 2026 00:00:00 +0000\nSubject: [PATCH] Update welcome\n\ndiff --git a/welcome.txt b/welcome.txt\n@@ -1 +1 @@\n-Hello\n+<welcome>\n`;
    await page.route("**/api/work/1/changes", (route) =>
        route.fulfill({
            json: {
                checkpoint: {
                    id: "91",
                    workId: "1",
                    branch: "goblin/work-1",
                    commitSha: head,
                },
                baseCommitSha: base,
                patch,
            },
        }),
    );
    await page.route("**/api/work/1/changes/91.patch", (route) =>
        route.fulfill({
            body: patch,
            headers: {
                "content-disposition": 'attachment; filename="work-1-91.patch"',
            },
        }),
    );
    await page.goto("/work?item=1");
    await page.getByRole("button", { name: "Changes", exact: true }).click();
    const review = page.getByRole("dialog", { name: "Changes" });
    await expect(review.getByText("+<welcome>", { exact: true })).toBeVisible();
    const downloaded = page.waitForEvent("download");
    await review.getByRole("link", { name: "Download patch" }).click();
    expect((await downloaded).suggestedFilename()).toBe("work-1-91.patch");
    await page.keyboard.press("Escape");
    await expect(review).toBeHidden();
    expect(commands).toEqual([]);
});

test("Create work saves a goal and leaves execution for an explicit Start", async ({
    page,
}) => {
    await workspace(page);
    const commands: Record<string, unknown>[] = [];
    let saved: Record<string, unknown>[] = [];
    await page.route("**/api/identities", (route) =>
        route.fulfill({ json: { ids: ["10", "11"] } }),
    );
    await page.route("**/api/work", (route) => route.fulfill({ json: saved }));
    await page.route("**/api/work/commands", (route) => {
        const command = route.request().postDataJSON();
        commands.push(command);
        const at = "2026-11-04T00:00:00Z";
        saved = [
            {
                version: "1",
                createdAt: at,
                updatedAt: at,
                work: {
                    id: command.workId,
                    objective: command.text,
                    status: "Ready",
                    agentId: "1",
                    attempts: [],
                    history: [],
                    decisions: [],
                    results: [],
                    artifacts: [],
                },
            },
        ];
        return route.fulfill({ json: {} });
    });
    await page.goto("/?new=work");
    await page
        .getByPlaceholder("Describe the intended outcome…")
        .fill("Make the release guide easier to follow");
    await page
        .getByRole("button", { name: "Create work", exact: true })
        .click();
    await expect(page.locator(".detail h2")).toHaveText(
        "Make the release guide easier to follow",
    );
    await expect(page).toHaveURL(/\/work\?item=11$/);
    await expect(page.locator(".detail .status-pill")).toHaveText("Ready");
    await expect(
        page.getByRole("button", { name: "Start work", exact: true }),
    ).toBeVisible();
    expect(commands).toEqual([
        {
            commandId: "10",
            workId: "11",
            action: "Create",
            text: "Make the release guide easier to follow",
        },
    ]);
});

test("home, Settings, and collapsing navigation preserve a draft without executing work", async ({
    page,
}) => {
    const commands = await workspace(page, false);
    await page.goto("/?new=work");
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
    const settings = page.getByRole("region", { name: "Settings" });
    await expect(
        settings.getByRole("heading", { name: "Connections" }),
    ).toBeVisible();
    await settings
        .getByRole("link", { name: "Connect Codex", exact: true })
        .click();
    await expect(
        settings.getByRole("button", { name: "Continue with ChatGPT" }),
    ).toBeVisible();
    await settings.getByRole("button", { name: "Back to workspace" }).click();
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

test("the result leads the Work page, Activity opens on demand, and inspection preserves drafts", async ({
    page,
}) => {
    const commands = await workspace(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto("/work?item=1");
    await expect(page.locator(".detail h2")).toHaveText(
        "Polish the welcome page",
    );
    await expect(page.getByRole("tab")).toHaveCount(0);
    await expect(page.locator(".work-section h3")).toHaveText([
        "Outputs",
        "Progress",
        "Executions",
        "Details",
    ]);
    await expect(page.locator("#result-1")).toHaveAttribute("open", "");
    await expect(page.locator("#result-1 .inspection")).toBeInViewport();
    await expect(page.locator("#work-outputs")).not.toContainText(
        "fixture-model",
    );
    await expect(
        page.getByRole("region", { name: "Current status" }),
    ).toContainText("Ready for your review");
    await expect(page.locator(".activity-panel")).toBeHidden();
    await expect(
        page.getByRole("button", { name: "Approve & complete" }),
    ).toBeVisible();
    const center = (await page.locator(".main-shell").boundingBox())!;
    const navigation = (await page.locator(".sidebar").boundingBox())!;
    expect(center.x).toBe(navigation.width);
    expect(center.x + center.width).toBe(1440);
    await page.getByRole("button", { name: "Activity", exact: true }).click();
    await expect(page.getByRole("dialog", { name: "Activity" })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.locator("#work-conversation")).toBeHidden();
    const draft = page.getByPlaceholder("Add context to this work…");
    await draft.fill("Keep the review draft");
    await page.getByRole("button", { name: "Read proposed result" }).click();
    await expect(page.locator("#result-1")).toHaveAttribute("open", "");
    await page
        .getByRole("button", { name: "Conversation", exact: true })
        .click();
    await expect(
        page.getByRole("region", { name: "Conversation about this Work" }),
    ).toContainText("The welcome page is ready for review");
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(page.locator("#result-1")).toHaveAttribute("open", "");
    await expect(draft).toHaveValue("Keep the review draft");
    await expect(
        page.getByRole("button", { name: "Hide conversation" }),
    ).toHaveAttribute("aria-expanded", "true");
    await page.getByRole("button", { name: "Hide conversation" }).click();
    await page.locator("#result-1 > summary").click();
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(page.locator("#result-1")).not.toHaveAttribute("open", "");
    await page.getByRole("button", { name: "Read proposed result" }).click();
    await expect(page.locator("#result-1")).toHaveAttribute("open", "");
    await page.locator(".detail-body").evaluate((el) => {
        el.scrollTop = 0;
    });
    await page.screenshot({
        path: test.info().outputPath("workspace-work.png"),
    });
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await page
        .getByPlaceholder("Describe the intended outcome…")
        .fill("A second idea");
    await page.getByRole("button", { name: /Polish the welcome page/ }).click();
    await expect(draft).toHaveValue("Keep the review draft");
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await expect(
        page.getByPlaceholder("Describe the intended outcome…"),
    ).toHaveValue("A second idea");
    expect(commands).toEqual([]);
    expect(errors).toEqual([]);
});

test("search finds output content and filters keep selection independent from browsing", async ({
    page,
}) => {
    await workspace(page);
    await page.goto("/work?item=1");
    await expect(page.locator(".detail h2")).toHaveText(
        "Polish the welcome page",
    );
    const search = page.getByRole("searchbox");
    await search.fill("repository clones");
    const list = page.getByRole("navigation", { name: "Recent work" });
    await expect(list.getByRole("button")).toHaveCount(1);
    await search.fill("shorter"); // Result text, not a Work title.
    await expect(list.getByRole("button")).toHaveCount(3);
    await search.fill("");
    const workView = page.getByRole("combobox", { name: "Work view" });
    await workView.selectOption("attention");
    await expect(list.getByRole("button")).toHaveCount(1);
    await expect(page.locator(".detail h2")).toHaveText(
        "Polish the welcome page",
    );
    await workView.selectOption("done");
    await expect(list.getByRole("button")).toHaveCount(1);
    await page.getByRole("button", { name: /Write the release notes/ }).click();
    await expect(page.locator(".detail .status-pill")).toHaveText("Completed");
    await workView.selectOption("assigned");
    await expect(list.getByRole("button")).toHaveCount(2);
    await search.fill("release notes");
    await expect(list.getByRole("button")).toHaveCount(1);
    await expect(list.getByRole("button")).toContainText(
        "Write the release notes",
    );
    await search.fill("");
    await expect(list.getByRole("button")).toHaveCount(2);
    await expect(workView).toHaveValue("assigned");
    await page
        .getByRole("button", { name: "Back to work", exact: true })
        .click();
    await expect(
        page.getByRole("heading", { name: "Work inbox", exact: true }),
    ).toBeVisible();
    await expect(
        page.getByRole("list", { name: "Work list" }).getByRole("button"),
    ).toHaveCount(3);
});

test("Work opens as a list and the optional board groups matching items", async ({
    page,
}) => {
    const commands = await workspace(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/");
    const board = page.getByRole("region", { name: "Work inbox", exact: true });
    const list = board.getByRole("list", { name: "Work list" });
    await expect(
        board.getByText("3 work items", { exact: true }),
    ).toBeVisible();
    await expect(list.getByRole("listitem")).toHaveCount(3);
    await expect(
        page.getByRole("navigation", { name: "Recent work" }),
    ).toHaveCount(0);
    await expect(board.locator(".board-lane")).toHaveCount(0);

    const boardLayout = board.getByRole("button", {
        name: "Board",
        exact: true,
    });
    await boardLayout.click();
    await expect(board.locator(".board-lane")).toHaveCount(3);
    await expect(
        board.getByRole("region", { name: "Ready", exact: true }),
    ).toHaveCount(0);
    await expect(
        board.getByRole("region", { name: "In progress" }).getByRole("button", {
            name: "Investigate slow repository clones",
        }),
    ).toBeVisible();
    await expect(board.getByRole("region", { name: "Closed" })).toContainText(
        "Write the release notes",
    );
    const filters = board.getByRole("group", { name: "Filter work" });
    const summary = board.getByRole("group", { name: "Work status summary" });
    await summary.getByRole("button", { name: "In progress 1" }).click();
    await expect(board.locator(".board-lane")).toHaveCount(1);
    await expect(
        filters.getByRole("button", { name: "In progress 1" }),
    ).toHaveAttribute("aria-pressed", "true");
    await filters.getByRole("button", { name: "All work 3" }).click();
    const next = board.getByRole("button", { name: "Polish the welcome page" });
    await next.click();
    await expect(
        page.getByRole("heading", { name: "Polish the welcome page" }),
    ).toBeFocused();
    await expect(board).toBeVisible();
    await expect(next).toHaveAttribute("aria-current", "true");
    const details = page.getByRole("complementary", { name: "Selected work" });
    await expect(
        details.getByRole("heading", { name: "Recent activity" }),
    ).toBeVisible();
    await page.goBack();
    await expect(boardLayout).toHaveAttribute("aria-pressed", "true");

    const filter = filters.getByRole("button", { name: "Needs attention 1" });
    await filter.click();
    await expect(board.locator(".board-lane")).toHaveCount(1);
    const layout = board.getByRole("button", { name: "List", exact: true });
    await layout.click();
    await expect(list.getByRole("listitem")).toHaveCount(1);
    const item = list.getByRole("button", { name: /Polish the welcome page/ });
    await expect(item).toHaveAccessibleDescription(/Ready for review.*Updated/);
    await item.press("Enter");
    await expect(page).toHaveURL(/\/work\?item=1$/);
    await expect(list).toBeVisible();
    await expect(item).toHaveAttribute("aria-current", "true");
    await page.screenshot({
        path: test.info().outputPath("work-inbox-detail.png"),
    });
    await details.getByRole("button", { name: "Open full work" }).click();
    await expect(board).toHaveCount(0);
    await page.getByRole("button", { name: "Back to work" }).click();
    await expect(item).toBeFocused();
    await expect(layout).toHaveAttribute("aria-pressed", "true");
    await expect(filter).toHaveAttribute("aria-pressed", "true");

    const search = page.getByRole("searchbox");
    await search.fill("shorter");
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await search.fill("release notes");
    await expect(board.getByText("No work matches your search.")).toBeVisible();
    await boardLayout.click();
    await expect(board.locator(".board-lane")).toHaveCount(0);
    await search.press("Escape");
    await expect(search).toHaveValue("");
    await layout.click();
    await expect(item).toBeVisible();
    await page
        .getByRole("navigation", { name: "Work navigation" })
        .getByRole("button", { name: "Ask Goblin" })
        .click();
    await expect(
        page.getByRole("textbox", { name: "Add to the conversation…" }),
    ).toBeFocused();
    expect(commands).toEqual([]);
});

test("the Recent work filter fits its labels and keeps the open item selected", async ({
    page,
}) => {
    const commands = await workspace(page);
    const filter = page.getByRole("combobox", { name: "Work view" });
    const list = page.getByRole("navigation", { name: "Recent work" });
    const draft = page.locator("#reply");

    for (const width of [1105, 800, 320]) {
        await page.setViewportSize({ width, height: 893 });
        await page.goto("/work?item=1");
        await draft.fill("A note about the welcome page");
        if (width === 320)
            await page.getByRole("button", { name: "Show sidebar" }).click();
        await page.evaluate(() => document.fonts.ready.then(() => undefined));

        await filter.click();
        await expect(filter.locator("option").last()).toBeVisible();
        const labelsFit = await filter.evaluate((select: HTMLSelectElement) => {
            const style = getComputedStyle(select);
            const contentWidth =
                select.clientWidth -
                parseFloat(style.paddingLeft) -
                parseFloat(style.paddingRight);
            return Array.from(select.options, (option) => {
                const label = document.createRange();
                label.selectNodeContents(option);
                return (
                    label.getClientRects().length === 1 &&
                    label.getBoundingClientRect().width <= contentWidth
                );
            });
        });
        expect(labelsFit).toEqual([true, true, true, true, true]);

        await filter
            .getByRole("option", { name: "Needs attention (1)" })
            .click();
        await expect(filter).toHaveValue("attention");
        await expect(list.getByRole("button")).toHaveCount(1);
        await expect(
            list.getByRole("button", { name: /Polish the welcome page/ }),
        ).toHaveAttribute("aria-current", "page");
        await expect(page).toHaveURL(/\/work\?item=1$/);
        await expect(draft).toHaveValue("A note about the welcome page");
    }
    expect(commands).toEqual([]);
});

test("Work inbox filters fit narrow screens and keep keyboard focus", async ({
    page,
}) => {
    await workspace(page);
    await page.goto("/work");
    const filters = page.getByRole("group", { name: "Filter work" });
    const attention = filters.getByRole("button", {
        name: "Needs attention 1",
    });
    await expect(filters).toBeVisible();

    for (const width of [1105, 320]) {
        await page.setViewportSize({ width, height: 893 });
        await filters.getByRole("button", { name: "All work 3" }).click();
        await attention.press("Enter");
        await expect(attention).toBeFocused();
        await expect(attention).toHaveAttribute("aria-pressed", "true");
        await expect(
            page.getByRole("list", { name: "Work list" }).getByRole("listitem"),
        ).toHaveCount(1);
        expect(
            await filters.evaluate(
                (element) => element.scrollWidth <= element.clientWidth,
            ),
        ).toBe(true);
    }
});

test("both Work layouts fit mobile and keep primary actions available with the sidebar closed", async ({
    page,
}) => {
    await workspace(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/work");
    const board = page.getByRole("region", { name: "Work inbox", exact: true });
    await expect(board.getByRole("button", { name: "New work" })).toBeVisible();
    await expect(
        board.getByRole("button", { name: "Ask Goblin" }),
    ).toBeVisible();
    await expect(board.getByRole("list", { name: "Work list" })).toBeVisible();
    await board.getByRole("button", { name: "Board", exact: true }).click();
    await expect(board.locator(".board-lane")).toHaveCount(3);
    const lastLane = board.getByRole("region", { name: "Closed" });
    await lastLane.scrollIntoViewIfNeeded();
    await expect(lastLane).toBeInViewport();
    expect(
        await board.evaluate(
            (element) => element.scrollWidth <= element.clientWidth,
        ),
    ).toBe(true);
    await board.getByRole("button", { name: "List", exact: true }).click();
    await expect(board.getByRole("list", { name: "Work list" })).toBeVisible();
    expect(
        await board.evaluate(
            (element) => element.scrollWidth <= element.clientWidth,
        ),
    ).toBe(true);
    await board.getByRole("button", { name: "New work" }).click();
    await expect(
        page.getByRole("textbox", { name: "Describe the intended outcome…" }),
    ).toBeFocused();
    await page.goBack();
    const search = page.getByRole("searchbox");
    await search.fill("welcome page");
    await board
        .getByRole("list", { name: "Work list" })
        .getByRole("button", { name: "Polish the welcome page" })
        .click();
    await expect(board).toBeHidden();
    await expect(
        page.getByRole("complementary", { name: "Selected work" }),
    ).toBeVisible();
    await expect(
        page.getByRole("region", { name: "Search results" }),
    ).toHaveCount(0);
    await page.getByRole("button", { name: "Back to work" }).click();
    await expect(board).toBeVisible();
    await expect(search).toHaveValue("welcome page");
});

for (const width of [1024, 390, 320]) {
    test(`Activity collapses before navigation at ${width}px with keyboard focus and full width Work`, async ({
        page,
    }) => {
        await workspace(page);
        await page.setViewportSize({ width, height: 844 });
        await page.goto("/work?item=1");
        const toggle = page.getByRole("button", {
            name: "Activity",
            exact: true,
        });
        await expect(toggle).toBeVisible();
        await expect(page.locator(".activity-panel")).not.toBeVisible();
        await toggle.click();
        const panel = page.getByRole("dialog", {
            name: "Activity",
            exact: true,
        });
        await expect(panel).toBeVisible();
        const close = page.getByRole("button", {
            name: "Close activity",
            exact: true,
        });
        await expect(close).toBeFocused();
        await close.press("Shift+Tab");
        await expect(panel.locator("summary").last()).toBeFocused();
        await page.keyboard.press("Tab");
        await expect(close).toBeFocused();
        await page.keyboard.press("Escape");
        await expect(toggle).toBeFocused();
        if (width <= 760) {
            const show = page.getByRole("button", {
                name: "Show sidebar",
                exact: true,
            });
            await show.click();
            await expect(
                page.getByRole("dialog", { name: "Workspace", exact: true }),
            ).toBeVisible();
            await page.keyboard.press("Escape");
            await expect(show).toBeFocused();
            await show.click();
            await page
                .getByRole("button", {
                    name: /Investigate slow repository clones/,
                })
                .click();
            await expect(page.locator(".detail h2")).toBeFocused();
            await expect(
                page.getByRole("dialog", { name: "Workspace" }),
            ).toHaveCount(0);
            // Search remains usable while the navigation drawer is closed.
            await page.getByRole("searchbox").fill("Polish");
            await page
                .getByRole("region", { name: "Search results" })
                .getByRole("button", { name: "Polish the welcome page" })
                .click();
            await expect(page.locator(".detail h2")).toHaveText(
                "Polish the welcome page",
            );
            await expect(page.getByRole("searchbox")).toHaveValue("");
        } else
            await expect(
                page.getByRole("complementary", { name: "Workspace" }),
            ).toBeVisible();
        expect(
            await page.evaluate(
                () => document.documentElement.scrollWidth <= innerWidth,
            ),
        ).toBe(true);
        await expect(
            page.getByPlaceholder("Add context to this work…"),
        ).toBeInViewport();
        expect(
            await page.locator(".workspace-header").evaluate((header) => {
                const bounds = header.getBoundingClientRect();
                return Array.from(
                    header.querySelectorAll(".header-actions, .global-search"),
                ).every(
                    (child) =>
                        child.getBoundingClientRect().bottom <= bounds.bottom,
                );
            }),
        ).toBe(true);
        await page.screenshot({
            path: test.info().outputPath(`workspace-work-${width}.png`),
        });
    });
}

test("completed work keeps outputs, sharing, and agent inspection available without execution controls", async ({
    page,
    context,
}) => {
    const commands = await workspace(page);
    const at = "2026-09-21T00:00:00Z";
    const objective = "Migrate authentication preview to TypeScript";
    await page.route("**/api/work", (route) =>
        route.fulfill({
            json: [
                {
                    version: "3",
                    createdAt: at,
                    updatedAt: at,
                    work: {
                        id: "3",
                        objective,
                        status: "Completed",
                        agentId: "1",
                        attempts: [
                            {
                                id: "30",
                                agentId: "1",
                                status: "Succeeded",
                                target: { runtime: "codex" },
                                startedAt: at,
                                finishedAt: at,
                            },
                        ],
                        results: [
                            {
                                attemptId: "30",
                                text: "Migrated the authentication preview and updated the tests.",
                                proposedAt: at,
                                approvedAt: at,
                            },
                        ],
                        artifacts: [
                            {
                                name: "PR #123",
                                reference:
                                    "https://github.com/owner/project/pull/123",
                                attemptId: "30",
                                createdAt: at,
                            },
                            {
                                name: "Commit b4a7e35",
                                reference:
                                    "https://github.com/owner/project/commit/b4a7e35",
                                attemptId: "30",
                                createdAt: at,
                            },
                            {
                                name: "Updated guide",
                                reference:
                                    "https://github.com/owner/project/blob/main/README.md",
                                attemptId: "30",
                                createdAt: at,
                            },
                        ],
                        decisions: [],
                        history: [],
                    },
                },
            ],
        }),
    );
    await page.setViewportSize({ width: 1743, height: 902 });
    await page.goto("/work?item=3");
    await expect(page.locator(".detail h2")).toHaveText(objective);
    await expect(
        page.locator(".progress-row .milestone-icon.complete"),
    ).toHaveCount(3);
    await expect(
        page.getByRole("region", { name: "Decisions", exact: true }),
    ).toHaveCount(0);
    await expect(
        page.getByRole("button", { name: "Start work", exact: true }),
    ).toHaveCount(0);
    await page.keyboard.press("Control+k");
    await expect(page.getByRole("searchbox")).toBeFocused();
    await page.getByLabel("Workspace menu", { exact: true }).click();
    await expect(
        page.getByRole("button", { name: "Lock workspace", exact: true }),
    ).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(
        page.getByLabel("Workspace menu", { exact: true }),
    ).toBeFocused();
    await expect(page.locator("#workspace-menu")).not.toHaveAttribute(
        "open",
        "",
    );
    await page.screenshot({
        path: test.info().outputPath("redesign-completed-desktop.png"),
    });
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    await page.getByRole("button", { name: "Share", exact: true }).click();
    await expect(page.locator("#toast")).toContainText("Work link copied");
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(
        page.url(),
    );
    await page.getByRole("button", { name: "View all", exact: true }).click();
    await expect(page.locator("#all-outputs")).toHaveAttribute("open", "");
    await expect(
        page.locator("#work-outputs .output-row > summary:visible"),
    ).toHaveCount(4);
    await expect(
        page
            .getByRole("region", { name: "Executions", exact: true })
            .locator(".output-row"),
    ).toHaveCount(1);
    await page.getByRole("button", { name: "Agent", exact: true }).click();
    const settings = page.getByRole("region", { name: "Settings" });
    await expect(
        settings.getByRole("heading", { name: "Agents", exact: true }),
    ).toBeVisible();
    await expect(
        settings.getByRole("heading", { name: "Goblin", exact: true }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Back to workspace" }).click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator(".detail-body").evaluate((element) => {
        element.scrollTop = 0;
    });
    await page.locator(".work-composer textarea").blur();
    await expect(page.locator("#toast")).toHaveText("");
    await expect(
        page.getByRole("button", { name: "Share", exact: true }),
    ).toBeVisible();
    await expect(page.locator(".work-composer")).toBeInViewport();
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
    await page.locator(".detail-body").evaluate((element) => {
        element.scrollTop = 0;
    });
    await expect(page.locator(".detail h2")).toBeInViewport();
    await page.screenshot({
        path: test.info().outputPath("redesign-completed-mobile.png"),
    });
    expect(commands).toEqual([]);
});

test("a result stays prominent among newer artifacts and older executions remain reachable from Activity", async ({
    page,
}) => {
    const commands = await workspace(page);
    const at = "2026-11-07T03:33:00Z";
    await page.route("**/api/work", (route) =>
        route.fulfill({
            json: [
                {
                    version: "8",
                    createdAt: at,
                    updatedAt: at,
                    work: {
                        id: "1",
                        objective: "Draft a weekend garden checklist",
                        status: "Completed",
                        agentId: "1",
                        attempts: ["1", "2", "3", "4"].map((id) => ({
                            id,
                            agentId: "1",
                            status: "Succeeded",
                            target: { runtime: "codex" },
                            startedAt: at,
                            finishedAt: at,
                        })),
                        results: [
                            {
                                attemptId: "4",
                                text: "Choose a sunny spot. Prepare the soil. Plant and water.",
                                proposedAt: at,
                                approvedAt: at,
                            },
                        ],
                        artifacts: [
                            "Soil guide",
                            "Plant list",
                            "Watering plan",
                            "Garden notes",
                        ].map((name) => ({
                            name,
                            reference: `notes/${name}`,
                            attemptId: "4",
                            createdAt: "2026-11-07T03:34:00Z",
                        })),
                        decisions: [],
                        history: [
                            {
                                sequence: "1",
                                kind: "ExecutionStarted",
                                attemptId: "1",
                                occurredAt: at,
                            },
                        ],
                    },
                },
            ],
        }),
    );
    await page.goto("/work?item=1");
    const outputs = page.getByRole("region", { name: "Outputs", exact: true });
    await expect(outputs.locator(".output-row").first()).toHaveAttribute(
        "id",
        "result-4",
    );
    await expect(outputs.locator("#result-4 .inspection")).toBeInViewport();
    await expect(outputs).not.toContainText("Codex · Succeeded");
    await expect(
        page.getByRole("region", { name: "Current status" }),
    ).toHaveCount(0);
    const draft = page.getByPlaceholder("Add context to this work…");
    await draft.fill("Keep the planting note");
    await page.getByRole("button", { name: "Activity", exact: true }).click();
    await page.locator("#event-1 > summary").click();
    await page.getByRole("button", { name: "Attempt 1", exact: true }).click();
    await expect(page.getByRole("dialog", { name: "Activity" })).toBeHidden();
    await expect(page.locator("#all-executions")).toHaveAttribute("open", "");
    await expect(page.locator("#execution-1")).toHaveAttribute("open", "");
    await expect(page.locator("#execution-1 > summary")).toBeFocused();
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(page.locator("#execution-1")).toHaveAttribute("open", "");
    await expect(draft).toHaveValue("Keep the planting note");
    expect(commands).toEqual([]);
});

test("global Ask Goblin has its own draft and leaves Work context scoped", async ({
    page,
}) => {
    const commands = await workspace(page);
    await page.goto("/work?item=1");
    await page
        .getByPlaceholder("Add context to this work…")
        .fill("Check the introduction again");
    await page.getByRole("button", { name: "Ask Goblin", exact: true }).click();
    await expect(page.getByPlaceholder("Add to the conversation…")).toHaveValue(
        "",
    );
    await page
        .getByPlaceholder("Add to the conversation…")
        .fill("An unrelated idea");
    await page.getByRole("button", { name: /Polish the welcome page/ }).click();
    await expect(
        page.getByPlaceholder("Add context to this work…"),
    ).toHaveValue("Check the introduction again");
    await page.getByRole("button", { name: "Ask Goblin", exact: true }).click();
    await expect(page.getByPlaceholder("Add to the conversation…")).toHaveValue(
        "An unrelated idea",
    );
    expect(commands).toEqual([]);
});

test("a delayed conversation reservation and resend stay bound to the submitted draft", async ({
    page,
}) => {
    await workspace(page);
    const { promise: requested, resolve: reservedStarted } =
        Promise.withResolvers<void>();
    const { promise: waitForReservation, resolve: releaseReservation } =
        Promise.withResolvers<void>();
    const commands: unknown[] = [];
    await page.route("**/api/identities", async (route) => {
        expect(route.request().postDataJSON()).toEqual({
            kinds: ["Message", "Conversation"],
        });
        reservedStarted();
        await waitForReservation;
        await route.fulfill({ json: { ids: ["21", "22"] } });
    });
    await page.route("**/api/conversations/commands", (route) => {
        commands.push(route.request().postDataJSON());
        return commands.length === 1
            ? route.fulfill({
                  status: 503,
                  json: { error: { message: "Save unconfirmed" } },
              })
            : route.fulfill({ json: {} });
    });
    await page.goto("/work?item=1");
    const workDraft = page.getByPlaceholder("Add context to this work…");
    await workDraft.fill("Keep the Work draft");
    await page.getByRole("button", { name: "Ask Goblin", exact: true }).click();
    const conversationDraft = page.getByPlaceholder("Add to the conversation…");
    await conversationDraft.fill("Save this conversation draft");
    await page.getByRole("button", { name: "Send message" }).click();
    await requested;
    await page
        .getByRole("button", { name: "Ideas for the next release" })
        .click();
    releaseReservation();
    const resend = page.getByRole("button", { name: "Resend command" });
    await expect(resend).toBeVisible();
    await expect(page).toHaveURL(/\?conversation=8$/);
    await conversationDraft.fill("Keep the other conversation draft");
    await resend.click();
    await expect(resend).toHaveCount(0);
    await expect(page).toHaveURL(/\?conversation=8$/);
    await expect(conversationDraft).toHaveValue(
        "Keep the other conversation draft",
    );
    await page.getByRole("button", { name: /Polish the welcome page/ }).click();
    await expect(workDraft).toHaveValue("Keep the Work draft");
    await page.getByRole("button", { name: "Ask Goblin", exact: true }).click();
    await expect(conversationDraft).toHaveValue("");

    const sent = {
        conversationId: "22",
        messageId: "21",
        text: "Save this conversation draft",
    };
    expect(commands).toEqual([sent, sent]);
});

test("uncertain execution stays unresolved, and runtime details never become milestones or Activity", async ({
    page,
}) => {
    await workspace(page);
    const unsafe = '<img src=x onerror="alert(1)">';
    const now = "2026-09-23T00:00:00Z";
    await page.route("**/api/work", (route) =>
        route.fulfill({
            json: [
                {
                    version: "6",
                    createdAt: now,
                    updatedAt: now,
                    work: {
                        id: "1",
                        objective: "Validate the release",
                        status: "NeedsAttention",
                        attention: { reason: "UncertainExecution" },
                        agentId: "1",
                        attempts: [
                            {
                                id: "1",
                                agentId: "4",
                                status: "Succeeded",
                                target: { runtime: "codex" },
                            },
                            {
                                id: "2",
                                agentId: "5",
                                status: "Uncertain",
                                target: { runtime: "codex" },
                                session: { model: "historical-model" },
                                queuedAt: now,
                            },
                        ],
                        results: [
                            {
                                attemptId: "1",
                                text: "Earlier proposal",
                                requestedChanges: "Recheck the rollout",
                            },
                        ],
                        decisions: [
                            {
                                id: "8",
                                attemptId: "1",
                                question: "Which rollout?",
                                answer: unsafe,
                                requestedAt: now,
                                answeredAt: now,
                            },
                        ],
                        artifacts: [
                            {
                                name: "Unsafe reference",
                                reference: "javascript:alert(1)",
                                attemptId: "1",
                            },
                        ],
                        history: [
                            {
                                sequence: "1",
                                kind: "Created",
                                text: "Validate the release",
                                occurredAt: now,
                            },
                            {
                                sequence: "2",
                                kind: "ExecutionClaimed",
                                attemptId: "2",
                                occurredAt: now,
                            },
                            {
                                sequence: "3",
                                kind: "ProgressReported",
                                text: "Read a file",
                                attemptId: "2",
                                occurredAt: now,
                            },
                            {
                                sequence: "4",
                                kind: "ExecutionUncertain",
                                attemptId: "2",
                                occurredAt: now,
                            },
                        ],
                    },
                },
            ],
        }),
    );
    await page.goto("/work?item=1");
    await expect(
        page.locator("#progress-execution .milestone-icon"),
    ).not.toHaveClass(/complete/);
    await expect(page.locator("#progress-review .row-meta")).toHaveText(
        "Not complete",
    );
    await expect(
        page.getByRole("button", { name: "Reconcile execution", exact: true }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Retry work", exact: true }),
    ).toHaveCount(0);
    await expect(page.locator(".activity-item")).toHaveCount(2);
    await expect(page.locator(".activity-panel")).not.toContainText(
        "Read a file",
    );
    await page.locator("#decision-8 summary").click();
    await expect(page.locator("#decision-8 .inspection")).toContainText(unsafe);
    await expect(page.locator('img[src="x"]')).toHaveCount(0);
    await page.locator("#progress-execution summary").click();
    await page
        .getByRole("button", { name: "Inspect execution", exact: true })
        .click();
    await expect(page.locator("#execution-2")).toHaveAttribute("open", "");
    await expect(
        page.locator("#execution-2 .execution-properties"),
    ).toContainText("historical-model");
    await expect(
        page.locator("#execution-2 .execution-properties"),
    ).toContainText("5");
    await page.locator("#execution-events-2 summary").click();
    await expect(page.locator("#execution-events-2")).toContainText(
        "Read a file",
    );
    await expect(page.locator('a[href^="javascript:"]')).toHaveCount(0);
});

test("the scoped composer retains command identity and draft until the selected Work save is confirmed", async ({
    page,
}) => {
    await workspace(page);
    const commands: Record<string, unknown>[] = [];
    await page.route("**/api/identities", (route) =>
        route.fulfill({ json: { ids: ["9007199254740993"] } }),
    );
    await page.route("**/api/work/commands", (route) => {
        commands.push(route.request().postDataJSON());
        return commands.length === 1
            ? route.fulfill({
                  status: 503,
                  json: { error: { message: "Save unconfirmed" } },
              })
            : route.fulfill({ json: {} });
    });
    await page.goto("/work?item=2");
    const draft = page.getByPlaceholder("Add context to this work…");
    await draft.fill("Check connection pooling");
    await page.getByRole("button", { name: "Send message" }).click();
    await expect(
        page.getByRole("button", { name: "Resend command" }),
    ).toBeVisible();
    await expect(draft).toHaveValue("Check connection pooling");
    await expect(
        page.getByRole("button", { name: "Send message" }),
    ).toBeDisabled();
    await expect(page.locator(".activity-panel")).not.toContainText(
        "Check connection pooling",
    );
    await page.reload();
    await expect(
        page.getByRole("button", { name: "Resend command" }),
    ).toBeVisible();
    expect(commands).toHaveLength(1);
    await page.getByRole("button", { name: "Resend command" }).click();
    await expect(draft).toHaveValue("");
    expect(commands).toHaveLength(2);
    expect(commands[0]).toMatchObject({
        workId: "2",
        expectedVersion: "1",
        action: "AddContext",
        text: "Check connection pooling",
        commandId: "9007199254740993",
    });
    expect(commands[1]).toEqual(commands[0]);
});

test("corrupt command recovery does not prevent the workspace from opening", async ({
    page,
}) => {
    await workspace(page);
    await page.addInitScript(() =>
        sessionStorage.setItem("goblin.pendingCommand", "{"),
    );
    await page.goto("/work?item=2");
    await expect(page.getByRole("alert")).toContainText(
        "saved command could not be recovered",
    );
    await expect(
        page.getByRole("button", { name: "Send message" }),
    ).toBeEnabled();
    await expect(
        page.getByRole("button", { name: "Resend command" }),
    ).toHaveCount(0);
});
