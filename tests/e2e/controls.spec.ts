import { test, expect, type Page } from "@playwright/test";

// UI edge cases use fixtures; live Codex and repository journeys are checked separately.
async function setup(page: Page, assigned = true) {
    const commands: unknown[] = [];
    await page.route("**/api/**", (route) => {
        const path = new URL(route.request().url()).pathname;
        if (route.request().method() === "POST")
            commands.push(route.request().postDataJSON());
        const data: Record<string, unknown> = {
            "/api/session": { authenticated: true },
            "/api/agents": [
                { id: "1", name: "Goblin" },
                { id: "2", name: "Release reviewer" },
            ],
            "/api/runtimes": [{ runtime: "codex", repositoryExecution: true }],
            "/api/connections": [
                { id: "1", name: "Codex", availability: "Available" },
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
                        status: "Ready",
                        agentId: assigned ? "1" : null,
                        attempts: [],
                        history: [],
                        decisions: [],
                        results: [],
                        artifacts: [],
                    },
                },
            ],
        };
        return route.fulfill({ json: data[path] ?? [] });
    });
    await page.goto("/work?item=1");
    await expect(page.locator(".detail h2")).toHaveText(
        "Review the release checklist",
    );
    return commands;
}

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
