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

test("Work state is primary, Activity stays beside it, and inspection preserves drafts", async ({
    page,
}) => {
    const commands = await workspace(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto("/");
    await expect(page.locator(".detail h2")).toHaveText(
        "Polish the welcome page",
    );
    await expect(page.getByRole("tab")).toHaveCount(0);
    await expect(page.locator(".work-section h3")).toHaveText([
        "Goal",
        "Progress",
        "Ready for your review",
        "Decisions",
        "Recent outputs",
    ]);
    await expect(
        page.getByRole("complementary", { name: "Activity" }),
    ).toBeVisible();
    await expect(
        page.getByRole("button", { name: "Approve & complete" }),
    ).toBeVisible();
    const center = (await page.locator(".main-shell").boundingBox())!;
    const activity = (await page.locator(".activity-panel").boundingBox())!;
    const navigation = (await page.locator(".sidebar").boundingBox())!;
    expect(center.width).toBeGreaterThan(activity.width * 2);
    expect(center.x).toBe(navigation.width);
    expect(activity.x).toBe(center.x + center.width);
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
    await page.locator(".detail-body").evaluate((el) => {
        el.scrollTop = 0;
    });
    await page.screenshot({ path: "test-results/workspace-work.png" });
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
    await page.goto("/");
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
    await page.getByRole("button", { name: /^Needs Attention/ }).click();
    await expect(list.getByRole("button")).toHaveCount(1);
    await expect(page.locator(".detail h2")).toHaveText(
        "Polish the welcome page",
    );
    await page.getByRole("button", { name: /^Completed/ }).click();
    await expect(list.getByRole("button")).toHaveCount(1);
    await page.getByRole("button", { name: /Write the release notes/ }).click();
    await expect(page.locator(".detail .status-pill")).toHaveText("Completed");
    await page.getByRole("button", { name: /Assigned to Goblin/ }).click();
    await expect(list.getByRole("button")).toHaveCount(2);
    await search.fill("release notes");
    await expect(list.getByRole("button")).toHaveCount(1);
    await expect(list.getByRole("button")).toContainText(
        "Write the release notes",
    );
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
            path: `test-results/workspace-work-${width}.png`,
        });
    });
}

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

test("saved workspace inspection needs no compute and renders files as text", async ({
    page,
}) => {
    await workspace(page);
    const checkpoint = "9007199254740993";
    const posts: string[] = [];
    await page.route("**/api/work/1/workspace**", (route) => {
        const url = new URL(route.request().url());
        if (route.request().method() === "POST") posts.push(url.pathname);
        let json: unknown = {
            checkpoints: [
                {
                    id: checkpoint,
                    attemptId: "1",
                    turnNumber: 1,
                    branch: "goblin/1/1",
                    commitSha: "a".repeat(40),
                    createdAt: "2026-09-23T00:00:00Z",
                },
            ],
            sessions: [],
            terminalAvailable: true,
        };
        if (url.pathname.endsWith("/files"))
            json = url.searchParams.has("path")
                ? {
                      path: "repository/report.txt",
                      text: "<script>window.workspaceInjected=true</script>\nSaved output",
                  }
                : {
                      files: [{ path: "repository/report.txt", size: 80 }],
                      truncated: false,
                  };
        return route.fulfill({ json });
    });
    await page.goto("/work?item=1");
    await page
        .getByRole("button", { name: "Open workspace", exact: true })
        .click();
    const dialog = page.getByRole("dialog", { name: "Workspace", exact: true });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("button", { name: "report.txt" }).click();
    await expect(dialog.getByLabel("File preview")).toContainText(
        "Saved output",
    );
    expect(
        await page.evaluate(() => Reflect.get(window, "workspaceInjected")),
    ).toBeUndefined();
    expect(posts).toEqual([]);
    await page.keyboard.press("Escape");
    await expect(dialog).not.toBeVisible();
    await expect(
        page.getByRole("button", { name: "Open workspace", exact: true }),
    ).toBeFocused();
});

test("inspection reserves a bigint ID and reuses it after an unconfirmed open", async ({
    page,
}) => {
    await workspace(page);
    const checkpoint = "9007199254740993",
        session = "9007199254740995";
    let reservations = 0;
    const opens: Record<string, unknown>[] = [];
    await page.route("**/api/identities", (route) => {
        expect(route.request().postDataJSON()).toEqual({
            kinds: ["Inspection"],
        });
        reservations++;
        return route.fulfill({ json: { ids: [session] } });
    });
    await page.route("**/api/work/1/workspace**", (route) => {
        const url = new URL(route.request().url());
        if (route.request().method() === "POST") {
            opens.push(route.request().postDataJSON());
            return opens.length === 1
                ? route.fulfill({
                      status: 503,
                      json: { error: { message: "Open unconfirmed" } },
                  })
                : route.fulfill({ json: {} });
        }
        if (url.pathname.endsWith("/files")) {
            expect(url.pathname).toContain(`/${checkpoint}/files`);
            return route.fulfill({ json: { files: [], truncated: false } });
        }
        return route.fulfill({
            json: {
                checkpoints: [
                    {
                        id: checkpoint,
                        attemptId: "1",
                        turnNumber: 1,
                        branch: "goblin/1/1",
                        commitSha: "a".repeat(40),
                        createdAt: "2026-09-23T00:00:00Z",
                    },
                ],
                sessions:
                    opens.length > 1
                        ? [
                              {
                                  id: session,
                                  attemptId: "1",
                                  checkpointId: checkpoint,
                                  state: "Queued",
                              },
                          ]
                        : [],
                terminalAvailable: true,
            },
        });
    });
    await page.goto("/work?item=1");
    await page
        .getByRole("button", { name: "Open workspace", exact: true })
        .click();
    const dialog = page.getByRole("dialog", { name: "Workspace", exact: true });
    await dialog.getByRole("button", { name: "Start inspection" }).click();
    await expect(dialog.getByRole("alert")).toHaveText("Open unconfirmed");
    await dialog.getByRole("button", { name: "Resend open request" }).click();
    await expect(dialog).toContainText("Waiting for capacity");
    expect(reservations).toBe(1);
    expect(opens).toEqual([
        { id: session, attemptId: "1", checkpointId: checkpoint },
        { id: session, attemptId: "1", checkpointId: checkpoint },
    ]);
});
