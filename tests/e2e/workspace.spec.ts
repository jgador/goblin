import { test, expect, type Page } from "@playwright/test";

// Layout fixtures only. The durable Work journeys exercise the real API and database.
type WorkFixture = {
    id: string;
    objective: string;
    status: string;
    checkpointId?: string;
    attention?: { reason: string };
};
const welcomeResult =
    "The welcome page is ready for review. The introduction is shorter, the primary action is clear, and the mobile layout keeps the important content in view.";

async function workspace(
    page: Page,
    connected = true,
    extraItems: WorkFixture[] = [],
) {
    const items = () =>
        [
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
            {
                id: "3",
                objective: "Write the release notes",
                status: "Completed",
            },
            ...extraItems,
        ].map(({ checkpointId, ...work }: WorkFixture) => ({
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
                        checkpointId,
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
                        attemptId: "1",
                        text: welcomeResult,
                        occurredAt: "2026-09-21T00:02:00Z",
                    },
                ],
                decisions: [],
                results: [{ attemptId: "1", text: welcomeResult }],
                artifacts: [],
            },
        }));
    const commands: string[] = [];
    await page.route("**/api/**", (route) => {
        const path = new URL(route.request().url()).pathname;
        if (route.request().resourceType() === "script")
            return route.continue();
        if (/^\/api\/work\/\d+\/changes$/.test(path))
            return route.fulfill({ status: 404, json: {} });
        if (route.request().method() === "POST") commands.push(path);
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: true };
        else if (path === "/api/preferences")
            json = { timeZone: "Asia/Manila" };
        if (path === "/api/work") json = items();
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

async function selectWorkFromSearch(page: Page, title: string) {
    await page.getByRole("button", { name: "Search", exact: true }).click();
    await page.getByRole("searchbox").fill(title);
    await page
        .getByRole("region", { name: "Search results", exact: true })
        .getByRole("button", { name: title, exact: true })
        .click();
}

test("Changes previews the patch before opening the side-by-side diff", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1720, height: 960 });
    const commands = await workspace(page);
    const base = "b".repeat(40);
    const head = "a".repeat(40);
    const patch = [
        `From ${head} Mon Sep 17 00:00:00 2001`,
        "Subject: [PATCH] Update welcome",
        "",
        "diff --git a/example.txt b/example.txt",
        "---",
        " welcome.txt | 2 +-",
        " 3 files changed",
        "",
        "diff --git a/welcome.txt b/welcome.txt",
        "--- a/welcome.txt",
        "+++ b/welcome.txt",
        "@@ -1,81 +1,81 @@",
        "-Hello",
        "+<welcome>",
        ...Array.from(
            { length: 80 },
            (_, index) => ` Context line ${index + 1}`,
        ),
        String.raw`diff --git "a/docs/caf\303\251.txt" "b/docs/caf\303\251.txt"`,
        "similarity index 80%",
        "rename from docs/cafe.txt",
        String.raw`rename to "docs/caf\303\251.txt"`,
        "@@ -1 +1 @@",
        "-Cafe",
        "+Café",
        "diff --git a/assets/theme.png b/assets/theme.png",
        "GIT binary patch",
        "literal 4",
        "hidden-binary-data",
        "",
    ].join("\n");
    let patchReads = 0;
    await page.route("**/api/work/1/changes", (route) => {
        patchReads++;
        return route.fulfill({
            json: {
                checkpoint: {
                    id: "91",
                    workId: "1",
                    repository: "owner/project",
                    branch: "goblin/work-1",
                    commitSha: head,
                },
                baseCommitSha: base,
                patch,
            },
        });
    });
    await page.goto("/work?item=1");
    const trigger = page.getByRole("button", { name: "Changes", exact: true });
    const conversation = page.getByRole("region", {
        name: "Conversation about this Work",
    });
    const detail = page.getByRole("region", { name: "Selected work" });
    const closedPosition = await trigger.boundingBox();
    const workBounds = (await detail.boundingBox())!;
    const preview = page.getByRole("region", { name: "Changes", exact: true });
    const previewEntry = preview.getByRole("button", {
        name: /Review changes for owner\/project · goblin\/work-1/,
    });
    await expect(trigger).toHaveAttribute("aria-expanded", "true");
    await expect(previewEntry).toHaveAccessibleName(
        /owner\/project · goblin\/work-1 3 files changed 2 added, 2 removed/,
    );
    await expect(previewEntry.getByText("+2")).toBeVisible();
    await expect(previewEntry.getByText("−2")).toBeVisible();
    await expect(page.getByRole("group", { name: "Diff view" })).toHaveCount(0);
    await expect(
        page.getByRole("region", { name: "Conversation about this Work" }),
    ).toBeVisible();
    await page.screenshot({
        path: test.info().outputPath("changes-preview.png"),
    });
    await previewEntry.press("Enter");
    const review = page.getByRole("region", { name: "Changes", exact: true });
    await expect(
        review.getByRole("heading", { name: "Changes" }),
    ).toBeFocused();
    await expect(conversation).toBeVisible();
    await expect(previewEntry).toBeHidden();
    const splitWork = (await detail.boundingBox())!;
    const diffBounds = (await review.boundingBox())!;
    expect(splitWork.width).toBeLessThan(workBounds.width);
    expect(diffBounds.x).toBeCloseTo(splitWork.x + splitWork.width + 1);
    expect(diffBounds.x + diffBounds.width).toBeCloseTo(
        workBounds.x + workBounds.width,
    );
    expect(diffBounds.y).toBe(workBounds.y);
    expect(diffBounds.height).toBe(workBounds.height);
    await expect(review.getByText("3 files changed")).toBeVisible();
    expect(patchReads).toBe(1);
    expect(await trigger.boundingBox()).toEqual(closedPosition);
    expect(
        await page.evaluate(() => document.documentElement.scrollHeight),
    ).toBe(960);
    await expect(
        review.getByRole("button", { name: "All files", pressed: true }),
    ).toBeVisible();
    await expect(review.getByText("+<welcome>", { exact: true })).toBeVisible();
    await page.screenshot({
        path: test.info().outputPath("changes-review.png"),
    });
    await expect(review.getByText("Binary file changed.")).toBeVisible();
    await expect(review.getByText("example.txt")).toHaveCount(0);
    await expect(review.getByText("hidden-binary-data")).toHaveCount(0);
    await expect(
        review.getByRole("link", { name: "Download patch" }),
    ).toHaveCount(0);
    await review.getByRole("button", { name: "By file" }).click();
    const files = review.getByRole("navigation", { name: "Changed files" });
    await files.getByRole("button", { name: /café.txt/ }).click();
    await expect(
        review.getByText("docs/cafe.txt", { exact: true }),
    ).toBeVisible();
    await expect(review.getByText("+<welcome>", { exact: true })).toHaveCount(
        0,
    );
    await expect(
        files.getByRole("button", { name: /café.txt/ }),
    ).toHaveAttribute("aria-current", "true");
    await review.getByRole("button", { name: "All files" }).click();
    await expect(review.getByText("+<welcome>", { exact: true })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("group", { name: "Diff view" })).toBeHidden();
    await expect(previewEntry).toBeFocused();
    await expect(trigger).toHaveAttribute("aria-expanded", "true");
    expect(patchReads).toBe(1);
    await previewEntry.press("Escape");
    await expect(previewEntry).toBeVisible();
    await trigger.press("Enter");
    await expect(trigger).toBeFocused();
    await expect(trigger).toHaveAttribute("aria-expanded", "false");
    await expect(preview).toBeHidden();
    await trigger.press("Enter");
    await expect(previewEntry).toBeVisible();
    await expect(
        preview.getByRole("heading", { name: "Changes" }),
    ).toBeFocused();
    expect(commands).toEqual([]);
});

test("Changes follows new checkpoints while the list control keeps it hidden when requested", async ({
    page,
}) => {
    const work: WorkFixture = {
        id: "4",
        objective: "Update the release checklist",
        status: "InProgress",
    };
    await workspace(page, true, [work]);
    let patchReads = 0;
    await page.route("**/api/work/4/changes", (route) => {
        patchReads++;
        if (!work.checkpointId) return route.fulfill({ status: 404, json: {} });
        const lines = work.checkpointId === "11" ? 1 : 2;
        return route.fulfill({
            json: {
                checkpoint: {
                    repository: "owner/project",
                    branch: "goblin/work-4",
                    commitSha: "a".repeat(40),
                },
                baseCommitSha: "b".repeat(40),
                patch: `diff --git a/notes.txt b/notes.txt\n@@ -0,0 +1,${lines} @@\n${"+Check the release\n".repeat(lines)}`,
            },
        });
    });

    await page.goto("/work?item=4");
    const toggle = page.getByRole("button", { name: "Changes", exact: true });
    const preview = page.getByRole("region", { name: "Changes", exact: true });
    const refresh = page.getByRole("button", { name: "Refresh", exact: true });
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    await expect(preview.getByRole("status")).toHaveText(
        "No changes have been saved for review.",
    );
    work.checkpointId = "11";
    await refresh.click();
    const reviewEntry = preview.getByRole("button", {
        name: /Review changes for owner\/project · goblin\/work-4/,
    });
    await expect(reviewEntry).toBeVisible();
    expect(patchReads).toBe(2);

    await reviewEntry.click();
    const diff = page.getByRole("group", { name: "Diff view" });
    await expect(diff).toBeVisible();
    work.checkpointId = "12";
    work.objective = "Review the updated checklist";
    await refresh.click();
    await expect(
        page
            .getByLabel("Work objective")
            .getByText(work.objective, { exact: true }),
    ).toBeVisible();
    expect(patchReads).toBe(2);
    await page.getByRole("button", { name: "Close diff" }).click();
    await expect(diff).toBeHidden();
    await expect(reviewEntry.getByText("+2")).toBeVisible();
    await expect(reviewEntry).toBeFocused();
    expect(patchReads).toBe(3);

    await toggle.click();
    work.checkpointId = "13";
    work.objective = "Finish the checklist review";
    await refresh.click();
    await expect(
        page
            .getByLabel("Work objective")
            .getByText(work.objective, { exact: true }),
    ).toBeVisible();
    await expect(toggle).toHaveAttribute("aria-expanded", "false");
    await expect(preview).toBeHidden();
    expect(patchReads).toBe(3);

    await toggle.click();
    await expect(reviewEntry).toBeVisible();
    expect(patchReads).toBe(4);

    await toggle.click();
    await page.getByRole("button", { name: "Back to work" }).click();
    await page
        .getByRole("button", { name: "Investigate slow repository clones" })
        .click();
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    await expect(preview.getByRole("status")).toHaveText(
        "No changes have been saved for review.",
    );
});

test("Changes keeps long files readable in the narrow in-page diff", async ({
    page,
}) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await workspace(page);
    const lines = Array.from({ length: 400 }, () => "+" + "x".repeat(420));
    await page.route("**/api/work/1/changes", (route) =>
        route.fulfill({
            json: {
                checkpoint: {
                    repository: "owner/project-with-a-long-repository-name",
                    branch: "goblin/a-long-branch-for-checking-the-preview-layout",
                    commitSha: "a".repeat(40),
                },
                baseCommitSha: "b".repeat(40),
                patch: [
                    "diff --git a/docs/long.txt b/docs/long.txt",
                    "@@ -0,0 +1,401 @@",
                    ...lines,
                    "+++ final line",
                    "",
                ].join("\n"),
            },
        }),
    );
    await page.goto("/work?item=1");
    const trigger = page.getByRole("button", { name: "Changes", exact: true });
    const detail = page.getByRole("region", { name: "Selected work" });
    const workBounds = (await detail.boundingBox())!;
    const preview = page.getByRole("region", { name: "Changes", exact: true });
    const entry = preview.getByRole("button", {
        name: /Review changes for owner\/project-with-a-long-repository-name/,
    });
    await expect(entry).toBeVisible();
    await page.getByRole("button", { name: "Activity", exact: true }).click({
        trial: true,
    });
    await page.getByLabel("Work objective").click({ trial: true });
    const bounds = (await preview.boundingBox())!;
    expect(bounds.x).toBeGreaterThanOrEqual(0);
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(390);
    await expect(
        entry.getByText("goblin/a-long-branch-for-checking-the-preview-layout"),
    ).toBeVisible();
    await expect(entry.getByText("+401")).toBeInViewport();
    await expect(
        page.getByRole("region", { name: "Conversation about this Work" }),
    ).toBeVisible();
    await page.screenshot({
        path: test.info().outputPath("changes-preview-narrow.png"),
    });
    await entry.click();
    const review = page.getByRole("region", { name: "Changes", exact: true });
    await expect(trigger).toBeVisible();
    await expect(trigger).toHaveAttribute("aria-expanded", "true");
    await expect(detail).toBeHidden();
    await expect(entry).toBeHidden();
    expect(await review.boundingBox()).toEqual(workBounds);
    await page.screenshot({
        path: test.info().outputPath("changes-review-narrow.png"),
    });
    await review.getByRole("button", { name: "By file" }).click();
    await expect(
        review.getByRole("combobox", { name: "Changed file" }),
    ).toHaveValue("0");
    await review.getByRole("button", { name: "Show next 2 lines" }).click();
    await expect(review.locator(".diff-added").last()).toContainText("401");
    await expect(
        review.getByText("+++ final line", { exact: true }),
    ).toBeVisible();
    await review.getByRole("button", { name: "Close diff" }).click();
    await expect(page.getByRole("group", { name: "Diff view" })).toBeHidden();
    await expect(entry).toBeFocused();
    await expect(trigger).toHaveAttribute("aria-expanded", "true");
    await expect(
        page.getByRole("region", { name: "Conversation about this Work" }),
    ).toBeVisible();
});

test("review feedback uses the Work composer without consuming the context draft", async ({
    page,
}) => {
    await page.setViewportSize({ width: 1600, height: 900 });
    await workspace(page);
    const at = "2026-09-21T00:00:00Z";
    const history = [
        {
            sequence: "1",
            kind: "ResultProposed",
            attemptId: "1",
            text: welcomeResult,
            occurredAt: at,
        },
    ];
    const commands: Record<string, unknown>[] = [];
    await page.route("**/api/work", (route) =>
        route.fulfill({
            json: [
                {
                    version: String(commands.length + 1),
                    createdAt: at,
                    updatedAt: at,
                    work: {
                        id: "1",
                        objective: "Review the welcome page",
                        status: commands.length ? "Ready" : "NeedsAttention",
                        attention: commands.length
                            ? undefined
                            : { reason: "ResultReview" },
                        agentId: "1",
                        attempts: [
                            {
                                id: "1",
                                status: "Succeeded",
                                target: {
                                    runtime: "codex",
                                    repository: {
                                        repository: "owner/project",
                                        grant: { branch: "goblin/work-1" },
                                    },
                                },
                            },
                        ],
                        results: [
                            {
                                attemptId: "1",
                                text: welcomeResult,
                                requestedChanges: commands[0]?.text,
                            },
                        ],
                        decisions: [],
                        artifacts: [],
                        history,
                    },
                },
            ],
        }),
    );
    await page.route("**/api/identities", (route) =>
        route.fulfill({ json: { ids: [String(commands.length + 90)] } }),
    );
    await page.route("**/api/work/commands", (route) => {
        const command = route.request().postDataJSON();
        commands.push(command);
        history.push({
            sequence: String(history.length + 1),
            kind:
                command.action === "RequestChanges"
                    ? "ChangesRequested"
                    : "ContextAdded",
            text: command.text,
            occurredAt: at,
            attemptId: "1",
        });
        return route.fulfill({ json: {} });
    });
    await page.route("**/api/work/1/changes", (route) =>
        route.fulfill({ status: 404, json: {} }),
    );
    await page.goto("/work");
    await page
        .getByRole("button", { name: "Review the welcome page", exact: true })
        .click();
    await expect(page).toHaveURL(/\/work\?item=1$/);
    const thread = page.getByRole("region", {
        name: "Conversation about this Work",
    });
    const normal = page.getByPlaceholder("Add context to this work…");
    const feedback = page.getByPlaceholder(
        "What would you like Goblin to change?",
    );
    const ask = page.getByRole("button", {
        name: "Ask for changes",
        exact: true,
    });
    await normal.fill("Keep the introduction short.");
    await ask.click();
    await expect(feedback).toBeFocused();
    await expect(feedback).toHaveValue("");
    await feedback.fill("Add a keyboard navigation example.");
    const changes = page
        .getByRole("banner")
        .getByRole("button", { name: "Changes", exact: true });
    const preview = page.getByRole("region", { name: "Changes", exact: true });
    await expect(preview.getByRole("status")).toHaveText(
        "No changes have been saved for review.",
    );
    await expect(preview.getByRole("button")).toHaveCount(0);
    await expect(thread).toBeVisible();
    await expect(feedback).toHaveValue("Add a keyboard navigation example.");
    await feedback.click();
    await expect(preview).toBeVisible();
    await changes.click();
    await expect(preview).toBeHidden();
    await expect(changes).toHaveAttribute("aria-expanded", "false");
    await feedback.click();
    await expect(feedback).toBeFocused();
    await page.getByRole("button", { name: "Cancel change request" }).click();
    await expect(normal).toHaveValue("Keep the introduction short.");
    await ask.click();
    await expect(feedback).toHaveValue("Add a keyboard navigation example.");
    await page.getByRole("button", { name: "Send change request" }).click();
    await expect(thread).toContainText("Requested changes");
    await expect(thread).toContainText("Add a keyboard navigation example.");
    await expect(ask).toHaveCount(0);
    await expect(normal).toHaveValue("Keep the introduction short.");
    await page.getByRole("button", { name: "Send message" }).click();
    await expect(thread).toContainText("Keep the introduction short.");
    await expect(normal).toHaveValue("");
    expect(commands).toMatchObject([
        {
            action: "RequestChanges",
            attemptId: "1",
            expectedVersion: "1",
            text: "Add a keyboard navigation example.",
        },
        {
            action: "AddContext",
            expectedVersion: "2",
            text: "Keep the introduction short.",
        },
    ]);
});

test("approval from the conversation submits the displayed result's attempt", async ({
    page,
}) => {
    await workspace(page);
    const commands: unknown[] = [];
    await page.route("**/api/identities", (route) =>
        route.fulfill({ json: { ids: ["90"] } }),
    );
    await page.route("**/api/work/commands", (route) => {
        commands.push(route.request().postDataJSON());
        return route.fulfill({ json: {} });
    });
    await page.goto("/work?item=1");
    const result = page
        .locator("#work-conversation article")
        .filter({ hasText: welcomeResult });
    await result.getByRole("button", { name: "Approve & complete" }).click();
    await expect
        .poll(() => commands)
        .toEqual([
            {
                action: "Approve",
                attemptId: "1",
                commandId: "90",
                workId: "1",
                expectedVersion: "1",
            },
        ]);
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
    await expect(page.locator(".work-objective-full")).toHaveText(
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
    await page.getByRole("button", { name: "Back to work" }).click();
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

test("the Work conversation keeps the objective, review and supporting records accessible", async ({
    page,
}) => {
    const commands = await workspace(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.goto("/work?item=1");
    await expect(
        page.getByRole("heading", { name: "Work 1", exact: true }),
    ).toBeVisible();
    await expect(page.getByRole("tab")).toHaveCount(0);
    const conversation = page.getByRole("region", {
        name: "Conversation about this Work",
    });
    const objective = conversation.locator("#work-objective");
    await expect(objective.locator("p")).toBeHidden();
    await objective.locator("summary").click();
    await expect(objective.locator("p")).toHaveText("Polish the welcome page");
    const result = conversation
        .locator("article")
        .filter({ hasText: welcomeResult });
    await expect(result).toBeInViewport();
    await expect(page.locator(".detail .status-pill")).toHaveText(
        "Ready for review",
    );
    await expect(page.locator(".activity-panel")).toBeHidden();
    await expect(
        result.getByRole("button", { name: "Approve & complete" }),
    ).toBeVisible();
    const center = (await page.locator(".main-shell").boundingBox())!;
    const navigation = (await page.locator(".sidebar").boundingBox())!;
    expect(center.x).toBe(navigation.width);
    expect(center.x + center.width).toBe(1440);
    await page.getByRole("button", { name: "Activity", exact: true }).click();
    await expect(page.getByRole("dialog", { name: "Activity" })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(conversation).toBeVisible();
    const draft = page.getByPlaceholder("Add context to this work…");
    await draft.fill("Keep the review draft");
    await page.getByRole("button", { name: "Outputs", exact: true }).click();
    await expect(page.locator("#work-outputs")).toHaveAttribute("open", "");
    await expect(page.locator("#result-1 .inspection")).toBeVisible();
    await expect(page.locator("#work-outputs")).not.toContainText(
        "fixture-model",
    );
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(objective).toHaveAttribute("open", "");
    await expect(page.locator("#work-outputs")).toHaveAttribute("open", "");
    await expect(draft).toHaveValue("Keep the review draft");
    await page.locator("#result-1 > summary").click();
    await page.getByRole("button", { name: "Refresh", exact: true }).click();
    await expect(page.locator("#result-1")).not.toHaveAttribute("open", "");
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
    await selectWorkFromSearch(page, "Polish the welcome page");
    await expect(draft).toHaveValue("Keep the review draft");
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await expect(
        page.getByPlaceholder("Describe the intended outcome…"),
    ).toHaveValue("A second idea");
    expect(commands).toEqual([]);
    expect(errors).toEqual([]);
});

test("workspace search finds Work content without changing the open item", async ({
    page,
}) => {
    await workspace(page);
    await page.goto("/work?item=1");
    await expect(page.locator(".work-objective-full")).toHaveText(
        "Polish the welcome page",
    );
    await page.getByRole("button", { name: "Search", exact: true }).click();
    const search = page.getByRole("searchbox");
    const results = page.getByRole("region", {
        name: "Search results",
        exact: true,
    });
    const items = results.locator('[data-action="search-work"]');
    await expect(search).toBeFocused();
    await search.fill("repository clones");
    await expect(items).toHaveCount(1);
    await search.fill("shorter");
    await expect(items).toHaveCount(3);
    await expect(page.locator(".work-objective-full")).toHaveText(
        "Polish the welcome page",
    );
    await results
        .getByRole("button", { name: "Write the release notes" })
        .click();
    await expect(page.locator(".detail h2")).toBeFocused();
    await expect(page.locator(".detail .status-pill")).toHaveText("Completed");
    await expect(search).toBeHidden();
    await expect(page.getByRole("combobox", { name: "Work view" })).toHaveCount(
        0,
    );
});

test("Work inbox opens in Board, scopes status to List, and restores inbox state after viewing Work", async ({
    page,
}) => {
    const longTitle =
        "Draft the release checklist with acceptance criteria and follow-up checks for every supported environment and integration";
    const commands = await workspace(page, true, [
        { id: "4", objective: longTitle, status: "Ready" },
        { id: "5", objective: "Queue the build", status: "Queued" },
        { id: "6", objective: "Stop the build", status: "Cancelling" },
        {
            id: "7",
            objective: "Confirm the release window",
            status: "NeedsAttention",
            attention: { reason: "InputRequired" },
        },
        {
            id: "8",
            objective: "Fix the failed export",
            status: "NeedsAttention",
            attention: { reason: "Failure" },
        },
        { id: "9", objective: "Cancel the old release", status: "Cancelled" },
    ]);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/work");

    const inbox = page.getByRole("region", { name: "Work inbox", exact: true });
    const sidebar = page.getByRole("navigation", { name: "Work navigation" });
    const navItem = sidebar.getByRole("button", {
        name: "Work inbox",
        exact: true,
    });
    const list = inbox.getByRole("list", { name: "Work list" });
    const status = page.getByRole("combobox", { name: "Status", exact: true });
    const modes = page.getByRole("group", { name: "Work layout" });
    const boardMode = modes.getByRole("button", { name: "Board", exact: true });
    const listMode = modes.getByRole("button", { name: "List", exact: true });
    const search = page.getByRole("searchbox");
    const lane = (name: string) =>
        inbox.getByRole("region", { name, exact: true });

    await expect(modes.getByRole("button")).toHaveCount(2);
    await expect(boardMode).toHaveAttribute("aria-pressed", "true");
    await expect(status).toHaveCount(0);
    await expect(inbox.getByRole("status")).toHaveText("9 work items");
    await expect(lane("Ready").getByRole("listitem")).toHaveCount(1);
    await listMode.click();
    await expect(listMode).toHaveAttribute("aria-pressed", "true");
    await expect(status).toHaveValue("all");
    await expect(list.getByRole("listitem")).toHaveCount(9);
    await expect(navItem).toHaveCount(1);
    await expect(navItem).toHaveAccessibleDescription(
        "3 Work items need attention",
    );
    await expect(
        page.getByRole("navigation", { name: "Recent work" }),
    ).toHaveCount(0);
    await expect(
        inbox.getByRole("group", { name: "Work status summary" }),
    ).toHaveCount(0);
    await expect(inbox.getByRole("group", { name: "Filter work" })).toHaveCount(
        0,
    );

    await status.selectOption("done");
    await expect(list.getByRole("button")).toHaveText(
        /Write the release notes/,
    );
    await status.selectOption("active");
    await expect(list.getByRole("listitem")).toHaveCount(3);
    await status.selectOption("attention");
    await expect(list.getByRole("listitem")).toHaveCount(3);
    await expect(inbox.getByRole("status")).toHaveText("3 of 9 work items");
    for (const label of [
        "Ready for review",
        "Input required",
        "Execution failed",
    ])
        await expect(list.getByText(label, { exact: true })).toBeVisible();
    await page.screenshot({
        path: test.info().outputPath("work-inbox-list.png"),
    });

    await boardMode.press("Enter");
    await expect(boardMode).toBeFocused();
    await expect(boardMode).toHaveAttribute("aria-pressed", "true");
    await expect(status).toHaveCount(0);
    await expect(inbox.getByRole("status")).toHaveText("9 work items");
    for (const [name, count] of [
        ["Ready", 1],
        ["In progress", 3],
        ["Needs attention", 3],
        ["Closed", 2],
    ] as const) {
        await expect(lane(name).locator(".board-lane-count")).toHaveText(
            String(count),
        );
        await expect(lane(name).getByRole("listitem")).toHaveCount(count);
    }
    await expect(
        lane("Closed").getByText("Cancelled", { exact: true }),
    ).toBeVisible();
    const cardHeights = await inbox
        .getByRole("button")
        .evaluateAll((cards) =>
            cards.map((card) => card.getBoundingClientRect().height),
        );
    expect(new Set(cardHeights).size).toBe(1);
    const titleFits = await lane("Ready")
        .getByText(longTitle)
        .evaluate((title) => {
            const lineHeight = parseFloat(getComputedStyle(title).lineHeight);
            return (
                title.clientHeight <= Math.ceil(2 * lineHeight) &&
                title.scrollHeight > title.clientHeight
            );
        });
    expect(titleFits).toBe(true);
    await page.screenshot({
        path: test.info().outputPath("work-inbox-board.png"),
    });

    await page.keyboard.press("Control+k");
    await search.fill("release notes");
    await expect(inbox.locator(".board-lane")).toHaveCount(1);
    const closed = lane("Closed").getByRole("button", {
        name: "Write the release notes",
    });
    await expect(closed).toBeVisible();
    await expect(navItem).toHaveAccessibleDescription(
        "3 Work items need attention",
    );
    await closed.press("Enter");
    await expect(page).toHaveURL(/\/work\?item=3$/);
    await expect(inbox).toHaveCount(0);
    await expect(
        page.getByRole("heading", { name: "Work 3", exact: true }),
    ).toBeFocused();
    await page.goBack();
    await expect(closed).toBeFocused();
    await expect(boardMode).toHaveAttribute("aria-pressed", "true");
    await expect(search).toHaveValue("release notes");

    await listMode.click();
    await expect(status).toHaveValue("attention");
    await expect(inbox.getByText("No work matches this status.")).toBeVisible();
    await search.fill("shorter");
    const row = list.getByRole("button", { name: "Polish the welcome page" });
    await expect(row).toHaveAccessibleDescription(/Ready for review.*Updated/);
    await search.fill("polish the welcome page");
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await expect(navItem).toHaveAccessibleDescription(
        "3 Work items need attention",
    );
    await row.press("Enter");
    await expect(page).toHaveURL(/\/work\?item=1$/);
    await expect(inbox).toHaveCount(0);
    const detail = page.getByRole("region", { name: "Selected work" });
    await expect(
        detail.getByRole("heading", { name: "Work 1", exact: true }),
    ).toBeFocused();
    await expect(
        detail.getByRole("button", { name: "Outputs", exact: true }),
    ).toBeVisible();
    await expect(
        detail.getByRole("region", { name: "Conversation about this Work" }),
    ).toBeVisible();
    await expect(
        detail.getByRole("button", { name: "Activity", exact: true }),
    ).toBeVisible();
    await page.keyboard.press("Control+k");
    await search.fill("export");
    await expect(search).toHaveValue("export");
    await search.press("Escape");
    await expect(
        page.getByRole("region", { name: "Search results" }),
    ).toBeHidden();
    await expect(search).toHaveValue("export");
    await detail.getByRole("button", { name: "Back to work" }).click();
    await expect(row).toBeFocused();
    await expect(listMode).toHaveAttribute("aria-pressed", "true");
    await expect(status).toHaveValue("attention");
    await expect(search).toHaveValue("polish the welcome page");

    await search.fill("no matching work");
    await expect(inbox.getByText("No work matches your search.")).toBeVisible();
    await boardMode.click();
    await expect(inbox.locator(".board-lane")).toHaveCount(0);
    await expect(inbox.getByText("No work matches your search.")).toBeVisible();
    await search.press("Escape");
    await expect(search).toHaveValue("");
    await expect(inbox.locator(".board-lane")).toHaveCount(4);
    expect(commands).toEqual([]);
});

test("the workspace menu supports mouse and keyboard and returns to the same inbox", async ({
    page,
}) => {
    await workspace(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto("/work");
    const sidebar = page.getByRole("complementary", { name: "Workspace" });
    const nav = sidebar.getByRole("navigation", { name: "Work navigation" });
    await expect(nav.locator("button, a")).toHaveText([
        "New work",
        "SearchCtrl K",
        /Work inbox/,
        "Knowledge",
    ]);
    await expect(
        sidebar.getByRole("navigation", { name: "Saved conversations" }),
    ).toBeHidden();
    await expect(
        sidebar.getByRole("button", { name: "Ask Goblin", exact: true }),
    ).toBeVisible();

    const menu = page.getByLabel("Workspace menu", { exact: true });
    const menuItems = page.locator("#workspace-menu").getByRole("button");
    await expect(sidebar.locator("#workspace-menu")).toHaveCount(1);
    await menu.click();
    await expect(menuItems).toHaveText([
        "Settings",
        "System",
        "Lock workspace",
    ]);
    await page.screenshot({
        path: test.info().outputPath("workspace-menu-desktop.png"),
    });
    await page
        .getByRole("heading", { name: "Work inbox", exact: true })
        .click();
    await expect(menuItems).toHaveCount(0);

    await page.getByRole("button", { name: "List", exact: true }).click();
    const status = page.getByRole("combobox", { name: "Status", exact: true });
    await status.selectOption("attention");
    await nav.getByRole("button", { name: "Search", exact: true }).click();
    await page.getByRole("searchbox").fill("welcome");
    const list = page.getByRole("list", { name: "Work list", exact: true });
    await expect(list.getByRole("button")).toHaveCount(1);

    await menu.press("Enter");
    await page.keyboard.press("Tab");
    await expect(menuItems.first()).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(menu).toBeFocused();
    await expect(menuItems).toHaveCount(0);
    await menu.press("Space");
    await page.keyboard.press("Tab");
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/settings\?section=connections$/);
    const back = page
        .getByRole("banner")
        .getByRole("button", { name: "Back to work", exact: true });
    await expect(back).toBeVisible();
    await expect(
        page
            .getByRole("banner")
            .getByRole("button", { name: "Refresh", exact: true }),
    ).toHaveCount(0);
    await back.click();
    await expect(menu).toBeFocused();
    await expect(
        page.getByRole("button", { name: "List", exact: true }),
    ).toHaveAttribute("aria-pressed", "true");
    await expect(page.getByRole("searchbox")).toHaveValue("welcome");
    await expect(status).toHaveValue("attention");
    await expect(list.getByRole("button")).toHaveCount(1);

    await page.setViewportSize({ width: 320, height: 800 });
    await expect(
        page.getByRole("button", { name: "Show sidebar", exact: true }),
    ).toBeVisible();
    await menu.click();
    await page
        .locator("#workspace-menu")
        .getByRole("button", { name: "System", exact: true })
        .click();
    await expect(page).toHaveURL(/\/settings\?section=system$/);
    await expect(
        page.getByRole("heading", { name: "System", exact: true }),
    ).toBeVisible();
    await back.click();
    await expect(status).toHaveValue("attention");
    await expect(page.getByRole("searchbox")).toHaveValue("welcome");
});

test("Work inbox refresh updates results while preserving keyboard focus", async ({
    page,
}) => {
    const now = new Date("2026-09-23T00:00:00Z");
    await page.clock.install({ time: now });
    await page.clock.pauseAt(now);
    const added: WorkFixture = {
        id: "4",
        objective: "Check the export",
        status: "NeedsAttention",
        attention: { reason: "InputRequired" },
    };
    const extra: WorkFixture[] = [];
    await workspace(page, true, extra);
    await page.goto("/work");
    const inbox = page.getByRole("region", { name: "Work inbox", exact: true });
    await page.getByRole("button", { name: "List", exact: true }).click();
    const status = page.getByRole("combobox", { name: "Status", exact: true });
    const list = inbox.getByRole("list", { name: "Work list" });
    await expect(list.getByRole("listitem")).toHaveCount(3);
    await status.selectOption("attention");
    await expect(list.getByRole("listitem")).toHaveCount(1);

    await status.focus();
    await page.keyboard.press("Space");
    const all = status.getByRole("option", {
        name: "All statuses",
        exact: true,
    });
    await expect(all).toBeVisible();
    extra.push(added);
    await page.clock.runFor(3000);
    await expect(list.getByRole("listitem")).toHaveCount(2);
    await expect(all).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(status).toBeFocused();
    await expect(status).toHaveValue("attention");

    const row = list.getByRole("button", { name: added.objective });
    await row.focus();
    added.objective = "Confirm the export";
    await page.clock.runFor(3000);
    const updated = list.getByRole("button", { name: added.objective });
    await expect(updated).toBeFocused();

    added.status = "Ready";
    delete added.attention;
    await page.clock.runFor(3000);
    await expect(updated).toHaveCount(0);
    await expect(status).toBeFocused();

    await page.keyboard.press("Control+k");
    const search = page.getByRole("searchbox");
    await search.fill("welcome");
    await search.evaluate((input: HTMLInputElement) =>
        input.setSelectionRange(1, 4),
    );
    added.objective = "Check the welcome flow";
    added.status = "NeedsAttention";
    added.attention = { reason: "InputRequired" };
    await page.clock.runFor(3000);
    await expect(list.getByRole("listitem")).toHaveCount(2);
    await expect(search).toBeFocused();
    const selection = await search.evaluate((input: HTMLInputElement) => [
        input.selectionStart,
        input.selectionEnd,
    ]);
    expect(selection).toEqual([1, 4]);
});

test("both Work layouts fit a narrow viewport with the sidebar closed", async ({
    page,
}) => {
    await workspace(page);
    await page.setViewportSize({ width: 320, height: 844 });
    await page.goto("/work");
    const inbox = page.getByRole("region", { name: "Work inbox", exact: true });
    const status = page.getByRole("combobox", { name: "Status", exact: true });
    const modes = page.getByRole("group", { name: "Work layout" });
    const boardMode = modes.getByRole("button", { name: "Board", exact: true });
    const listMode = modes.getByRole("button", { name: "List", exact: true });
    const list = inbox.getByRole("list", { name: "Work list" });
    const checkWidths = async () => {
        for (const locator of [page.locator(".workspace-header"), inbox]) {
            const dimensions = await locator.evaluate((el) => ({
                name: el.className,
                scrollWidth: el.scrollWidth,
                clientWidth: el.clientWidth,
            }));
            expect(
                dimensions.scrollWidth,
                JSON.stringify(dimensions),
            ).toBeLessThanOrEqual(dimensions.clientWidth);
        }
    };
    await expect(boardMode).toHaveAttribute("aria-pressed", "true");
    await expect(inbox.locator(".board-lane")).toHaveCount(3);
    await checkWidths();
    await page
        .getByRole("button", { name: "Show sidebar", exact: true })
        .click();
    const sidebar = page.getByRole("dialog", {
        name: "Workspace",
        exact: true,
    });
    await expect(
        sidebar.getByRole("button", { name: "New work" }),
    ).toBeVisible();
    await expect(
        sidebar.getByRole("button", { name: "Ask Goblin" }),
    ).toBeVisible();
    await expect(sidebar.getByLabel("Workspace menu")).toBeVisible();
    await sidebar.getByRole("button", { name: "Search", exact: true }).click();
    await expect(sidebar).toBeHidden();
    const search = page.getByRole("searchbox");
    expect((await search.boundingBox())?.width).toBeGreaterThan(100);
    await listMode.click();
    await status.selectOption("attention");
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await expect(status).toBeInViewport();
    await expect(modes).toBeInViewport();
    await checkWidths();
    await page.screenshot({
        path: test.info().outputPath("work-inbox-list-narrow.png"),
    });

    await boardMode.click();
    await expect(inbox.locator(".board-lane")).toHaveCount(3);
    const lastLane = inbox.getByRole("region", { name: "Closed", exact: true });
    await lastLane.scrollIntoViewIfNeeded();
    await expect(lastLane).toBeInViewport();
    await checkWidths();
    await listMode.click();
    await expect(status).toHaveValue("attention");
    await expect(list.getByRole("listitem")).toHaveCount(1);
    await checkWidths();

    await page.keyboard.press("Control+k");
    await search.fill("welcome page");
    await list.getByRole("button", { name: "Polish the welcome page" }).click();
    await expect(inbox).toHaveCount(0);
    const detail = page.getByRole("region", { name: "Selected work" });
    await expect(detail).toBeVisible();
    await expect(
        page.getByRole("region", { name: "Search results" }),
    ).toHaveCount(0);
    await detail.getByRole("button", { name: "Back to work" }).click();
    await expect(inbox).toBeVisible();
    await expect(search).toHaveValue("welcome page");
    await expect(status).toHaveValue("attention");
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
            await selectWorkFromSearch(
                page,
                "Investigate slow repository clones",
            );
            await expect(
                page.getByRole("heading", { name: "Work 2", exact: true }),
            ).toBeFocused();
            await expect(
                page.getByRole("dialog", { name: "Workspace" }),
            ).toHaveCount(0);
            // Search remains usable while the navigation drawer is closed.
            await page.keyboard.press("Control+k");
            await page.getByRole("searchbox").fill("Polish");
            await page
                .getByRole("region", { name: "Search results" })
                .getByRole("button", { name: "Polish the welcome page" })
                .click();
            await expect(page.locator(".work-objective-full")).toHaveText(
                "Polish the welcome page",
            );
            await expect(page.getByRole("searchbox")).toBeHidden();
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
    await expect(page.locator(".work-objective-full")).toHaveText(objective);
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
    await page.getByRole("button", { name: "Outputs", exact: true }).click();
    await expect(page.locator("#all-outputs")).toHaveAttribute("open", "");
    await expect(
        page.locator("#work-outputs .output-row > summary:visible"),
    ).toHaveCount(4);
    await page.locator("#work-reference > summary").click();
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
    await page.getByRole("button", { name: "Back to work" }).click();
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
    await page.getByRole("button", { name: "Outputs", exact: true }).click();
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
    await selectWorkFromSearch(page, "Polish the welcome page");
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
    await selectWorkFromSearch(page, "Polish the welcome page");
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
    await page.locator("#work-reference > summary").click();
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
