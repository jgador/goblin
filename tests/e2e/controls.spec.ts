import { test, expect, type Page } from "@playwright/test";

// UI edge cases use fixtures; live Codex and repository journeys are checked separately.
async function setup(page: Page, assigned = true, waiting = false) {
    const commands: unknown[] = [];
    await page.route("**/api/**", (route) => {
        const path = new URL(route.request().url()).pathname;
        if (route.request().method() === "POST")
            commands.push(route.request().postDataJSON());
        const data: Record<string, unknown> = {
            "/api/session": { authenticated: true },
            "/api/agents": [
                { id: "1", name: "Goblin", connectionId: "1" },
                { id: "2", name: "Release reviewer", connectionId: "1" },
            ],
            "/api/runtimes": [{ runtime: "codex", repositoryExecution: true }],
            "/api/identities": { ids: ["900"] },
            "/api/connections": [
                {
                    id: "1",
                    runtime: "codex",
                    name: "Codex",
                    availability: "Available",
                },
            ],
            "/api/github": {
                configured: true,
                login: "owner",
                status: "Connected",
            },
            "/api/github/repositories": [
                {
                    id: "1",
                    name: "owner/project",
                    defaultBranch: "main",
                    enabled: true,
                },
                {
                    id: "2",
                    name: "owner/another-project",
                    defaultBranch: "develop",
                    enabled: true,
                },
            ],
            "/api/cluster": { available: true },
            "/api/work": [
                {
                    version: "1",
                    createdAt: "2026-09-22T00:00:00Z",
                    updatedAt: "2026-09-22T00:00:00Z",
                    work: {
                        id: "1",
                        objective: "Review the release checklist",
                        status: waiting ? "NeedsAttention" : "Ready",
                        attention: waiting ? { reason: "InputRequired" } : null,
                        agentId: assigned ? "1" : null,
                        attempts: waiting
                            ? [
                                  {
                                      id: "10",
                                      status: "Waiting",
                                      target: { runtime: "codex" },
                                  },
                              ]
                            : [],
                        history: [],
                        decisions: waiting
                            ? [
                                  {
                                      id: "20",
                                      question:
                                          "Please provide repository contents",
                                  },
                              ]
                            : [],
                        results: [],
                        artifacts: [],
                    },
                },
            ],
        };
        if (path === "/api/connections/1/models") {
            const models = Array.from({ length: 12 }, (_, index) => ({
                id: `model-${index}`,
                model:
                    index === 0
                        ? "gpt-6-sol"
                        : index === 1
                          ? "gpt-6-luna"
                          : `gpt-test-${index}`,
                displayName:
                    index === 0
                        ? "GPT-6 Sol"
                        : index === 1
                          ? "GPT-6 Luna"
                          : `GPT Test ${index}`,
                defaultReasoningEffort: index === 1 ? "high" : "medium",
                supportedReasoningEfforts:
                    index === 1
                        ? ["low", "high"]
                        : ["low", "medium", "high", "xhigh"],
                isDefault: index === 0,
                isNew: false,
            }));
            const limit = Number(
                new URL(route.request().url()).searchParams.get("limit") ?? "3",
            );
            return route.fulfill({
                json: {
                    models: models.slice(0, limit),
                    hasMore: true,
                    defaultModel: "gpt-6-sol",
                    stale: false,
                    refreshing: false,
                    unavailable: false,
                },
            });
        }
        return route.fulfill({ json: data[path] ?? [] });
    });
    await page.goto("/work?item=1");
    await expect(page.locator(".detail h2")).toHaveText(
        "Review the release checklist",
    );
    return commands;
}

test("compact model picker snaps the slider and keeps the choice across composers", async ({
    page,
}) => {
    const commands = await setup(page);
    const picker = page.locator(".model-picker-trigger");
    await expect(picker).toHaveAttribute(
        "aria-label",
        "Model: GPT-6 Sol, reasoning effort: Medium",
    );
    await picker.click();
    const popover = page.getByRole("dialog", {
        name: "Model and reasoning effort",
    });
    await expect(popover).toBeVisible();
    await expect(popover).toBeInViewport({ ratio: 1 });
    await page.screenshot({
        path: test.info().outputPath("compact-model-picker.png"),
        fullPage: true,
    });
    await expect(popover.locator(".model-row")).toContainText("GPT-6 Sol");
    const slider = popover.getByRole("slider", { name: "Reasoning effort" });
    await expect(slider).toHaveValue("1");
    await expect(popover.locator(".model-effort-visual")).toHaveAttribute(
        "data-level",
        "1",
    );
    await slider.focus();
    await page.keyboard.press("ArrowRight");
    await expect(picker).toHaveAttribute(
        "aria-label",
        "Model: GPT-6 Sol, reasoning effort: High",
    );
    const track = await slider.boundingBox();
    expect(track).not.toBeNull();
    await page.mouse.move(
        track!.x + track!.width / 2,
        track!.y + track!.height / 2,
    );
    await page.mouse.down();
    await page.mouse.move(
        track!.x + track!.width - 8,
        track!.y + track!.height / 2,
        { steps: 5 },
    );
    await page.mouse.up();
    await expect(slider).toHaveValue("3");
    await expect(slider).toHaveAttribute("aria-valuetext", "Extra High");
    await expect(popover.locator(".model-effort-visual")).toHaveAttribute(
        "data-level",
        "3",
    );
    expect(
        await popover
            .locator(".model-effort-fill")
            .evaluate((fill) =>
                Math.round(
                    fill.getBoundingClientRect().width /
                        fill.parentElement!.getBoundingClientRect().width,
                ),
            ),
    ).toBe(1);
    await expect(picker).toHaveAttribute(
        "aria-label",
        "Model: GPT-6 Sol, reasoning effort: Extra High",
    );
    await popover.locator(".model-row").click();
    await expect(
        popover.locator(".model-option[data-action='select-model']"),
    ).toHaveCount(4);
    await popover
        .getByRole("button", { name: "Show more models (up to 10)" })
        .click();
    await expect(
        popover.locator(".model-option[data-action='select-model']"),
    ).toHaveCount(11);
    await popover.getByRole("button", { name: "GPT-6 Luna" }).click();
    await expect(slider).toHaveValue("2");
    await expect(
        popover.getByRole("button", { name: "Extra High" }),
    ).toBeDisabled();
    await popover.getByRole("button", { name: "Low", exact: true }).click();
    await expect(picker).toHaveAttribute(
        "aria-label",
        "Model: GPT-6 Luna, reasoning effort: Low",
    );
    await slider.focus();
    await page.keyboard.press("ArrowRight");
    await expect(slider).toHaveValue("2");
    await expect(picker).toHaveAttribute(
        "aria-label",
        "Model: GPT-6 Luna, reasoning effort: High",
    );
    await popover.getByRole("button", { name: "Low", exact: true }).click();
    await page.keyboard.press("Escape");
    await expect(popover).toHaveCount(0);
    await expect(picker).toBeFocused();
    await page.reload();
    await expect(picker).toHaveAttribute(
        "aria-label",
        "Model: GPT-6 Luna, reasoning effort: Low",
    );
    await page.getByRole("button", { name: "Start work", exact: true }).click();
    await expect
        .poll(() =>
            commands.find(
                (command) =>
                    (command as { action?: string }).action === "Execute",
            ),
        )
        .toMatchObject({
            action: "Execute",
            model: "gpt-6-luna",
            reasoningEffort: "low",
            modelSelectionProvided: true,
        });
    await page
        .getByRole("button", { name: "New work", exact: true })
        .first()
        .click();
    await expect(picker).toBeVisible();
    await page
        .getByRole("button", { name: "New conversation", exact: true })
        .click();
    await expect(picker).toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    await picker.click();
    await expect(popover).toBeInViewport({ ratio: 1 });
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
});

test("agent picker stays open across background refresh and retains its selection", async ({
    page,
}) => {
    await setup(page, false);
    const agent = page.getByLabel("Agent", { exact: true });
    await agent.click();
    await expect
        .poll(() => agent.evaluate((element) => element.matches(":open")))
        .toBe(true);
    await page.waitForResponse(
        (response) => new URL(response.url()).pathname === "/api/work",
    );
    await expect
        .poll(() => agent.evaluate((element) => element.matches(":open")))
        .toBe(true);
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(agent).toHaveValue("2");
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await page
        .getByRole("button", { name: /Review the release checklist/ })
        .click();
    await expect(agent).toHaveValue("2");
});

test("repository setup validates locally and keeps the branch and Git identity through navigation", async ({
    page,
}) => {
    const commands = await setup(page);
    await page.locator("#repository-options summary").click();
    const start = page.getByRole("button", {
        name: "Start repository work",
        exact: true,
    });
    await start.click();
    await expect(
        page.getByText("Choose an enabled repository.", { exact: true }),
    ).toBeVisible();
    await expect(
        page.getByText("Enter a valid email for the agent’s commits."),
    ).toBeVisible();
    await expect(page.getByLabel("Repository", { exact: true })).toBeFocused();
    await page
        .getByLabel("Repository", { exact: true })
        .selectOption("owner/another-project");
    await expect(page.getByLabel("Default branch")).toHaveValue("develop");
    await page.getByLabel("Agent Git name").fill(" ");
    await page.getByLabel("Agent Git email").fill("invalid");
    await start.click();
    await expect(page.getByLabel("Agent Git name")).toBeFocused();
    await expect(
        page.getByText("Enter a name for the agent’s commits."),
    ).toBeVisible();
    await page.getByLabel("Agent Git name").fill("Release reviewer");
    await page.getByLabel("Agent Git email").fill("reviewer@example.com");
    await page.getByRole("button", { name: "Manage repositories" }).click();
    await page.getByRole("button", { name: "Close settings" }).click();
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await page
        .getByRole("button", { name: /Review the release checklist/ })
        .click();
    await expect(page.locator("#repository-options")).toHaveAttribute(
        "open",
        "",
    );
    await expect(page.getByLabel("Default branch")).toHaveValue("develop");
    await expect(page.getByLabel("Agent Git name")).toHaveValue(
        "Release reviewer",
    );
    await expect(page.getByLabel("Agent Git email")).toHaveValue(
        "reviewer@example.com",
    );
    await expect(page.locator(".field-error:visible")).toHaveCount(0);
    expect(commands).toEqual([]);
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(
        page.getByRole("button", { name: "Show sidebar", exact: true }),
    ).toBeVisible();
    await start.scrollIntoViewIfNeeded();
    await expect(start).toBeInViewport();
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
});

test("Settings keeps one content scroller and exposes repository loading without losing navigation", async ({
    page,
}) => {
    await setup(page);
    let finish: () => void = () => {};
    const pending = new Promise<void>((resolve) => {
        finish = resolve;
    });
    await page.route(
        "**/api/github/available-repositories?*",
        async (route) => {
            await pending;
            await route.fulfill({
                json: Array.from({ length: 30 }, (_, i) => ({
                    id: String(i),
                    name: `owner/repository-with-a-long-name-${i}`,
                    defaultBranch: "main",
                    canPush: true,
                })),
            });
        },
    );
    await page.locator('.sidebar [data-action="settings"]').click();
    const dialog = page.getByRole("dialog", { name: "Settings" });
    await dialog.getByRole("button", { name: "GitHub", exact: true }).click();
    await dialog.getByRole("button", { name: "Choose repositories" }).click();
    await expect(
        dialog.getByText("Loading repositories…", { exact: true }),
    ).toBeVisible();
    finish();
    await expect(
        dialog.locator(".repository-picker .repository-row"),
    ).toHaveCount(30);
    await expect(
        dialog.getByRole("button", { name: "Choose repositories" }),
    ).toBeFocused();
    for (const viewport of [
        { width: 1280, height: 720 },
        { width: 390, height: 844 },
        { width: 844, height: 390 },
    ]) {
        await page.setViewportSize(viewport);
        await expect(
            dialog.getByRole("button", { name: "Close settings" }),
        ).toBeInViewport();
        const content = dialog.locator(".settings-content");
        await content.evaluate((element) => {
            element.scrollTop = element.scrollHeight;
        });
        await expect(
            dialog.locator(".repository-picker .repository-row").last(),
        ).toBeInViewport();
        expect(
            await dialog.evaluate(
                (element) => element.scrollHeight <= element.clientHeight + 1,
            ),
        ).toBe(true);
        expect(
            await dialog.evaluate(
                (element) => element.scrollWidth <= innerWidth,
            ),
        ).toBe(true);
    }
    await dialog.getByRole("button", { name: "Cluster", exact: true }).click();
    await expect(
        dialog.getByRole("heading", { name: "Cluster", exact: true }),
    ).toBeInViewport();
    await page.keyboard.press("Escape");
    await expect(dialog).not.toBeVisible();
});

test("a waiting conversation can authorize repository access without creating another Work", async ({
    page,
}) => {
    const commands = await setup(page, true, true);
    await page.locator("#repository-options summary").click();
    await page
        .getByLabel("Repository", { exact: true })
        .selectOption("owner/project");
    await page.getByLabel("Agent Git email").fill("agent@example.com");
    await page
        .getByRole("button", { name: "Authorize & continue", exact: true })
        .click();
    await expect
        .poll(() => commands)
        .toContainEqual(
            expect.objectContaining({
                action: "AuthorizeRepository",
                workId: "1",
                expectedVersion: "1",
                repository: {
                    repository: "owner/project",
                    gitAuthorName: "Goblin",
                    gitAuthorEmail: "agent@example.com",
                },
            }),
        );
});
