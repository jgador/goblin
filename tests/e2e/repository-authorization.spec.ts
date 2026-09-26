import { test, expect } from "@playwright/test";

test("chat shows enablement and exact Git scope before submitting only the saved approval ID", async ({
    page,
}) => {
    const commands: Record<string, unknown>[] = [];
    const work = {
        id: "1",
        objective: "Fix owner/project and open a PR",
        agentId: "1",
        status: "NeedsAttention",
        attention: { reason: "RepositoryRequired" },
        attempts: [],
        history: [],
        decisions: [],
        results: [],
        artifacts: [],
        repositoryRequest: {
            repositories: ["owner/project"],
            target: { runtime: "codex" },
        },
        repositoryAuthorization: {
            id: "91",
            status: "Pending",
            enableRepository: true,
            retry: false,
            target: {
                runtime: "codex",
                repository: {
                    repository: "owner/project",
                    gitAuthorName: "Goblin",
                    gitAuthorEmail: "agent@example.com",
                    grant: {
                        login: "connected-owner",
                        branch: "goblin/1/91",
                        baseBranch: "develop",
                        allowPush: true,
                        allowPullRequest: true,
                    },
                },
            },
        },
    };
    await page.route("**/api/**", async (route) => {
        const path = new URL(route.request().url()).pathname;
        if (path === "/api/work/commands") {
            commands.push(route.request().postDataJSON());
            work.repositoryAuthorization.status = "Authorized";
            work.status = "Queued";
            work.attention.reason = "";
        }
        const view = {
            version: "4",
            work,
            createdAt: "2026-09-26T00:00:00Z",
            updatedAt: "2026-09-26T00:00:00Z",
        };
        const data: Record<string, unknown> = {
            "/api/session": { authenticated: true },
            "/api/work": [view],
            "/api/work/commands": view,
            "/api/agents": [{ id: "1", name: "Goblin", connectionId: "1" }],
            "/api/connections": [
                {
                    id: "1",
                    runtime: "codex",
                    name: "Codex",
                    availability: "Available",
                },
            ],
            "/api/runtimes": [{ runtime: "codex", repositoryExecution: true }],
            "/api/github": {
                configured: true,
                login: "connected-owner",
                status: "Connected",
            },
            "/api/github/repositories": [],
            "/api/conversations": [],
            "/api/identities": { ids: ["101"] },
        };
        await route.fulfill({ json: data[path] ?? {} });
    });
    await page.goto("/work");
    await page
        .getByRole("button", { name: /Fix owner\/project and open a PR/ })
        .click();
    const preview = page.getByRole("region", { name: "GitHub authorization" });
    await expect(preview).toContainText("connected-owner");
    await expect(preview).toContainText("develop");
    await expect(preview).toContainText("goblin/1/91");
    await expect(preview).toContainText("Open a draft pull request");
    await expect(preview).toContainText(
        "enables the repository in Goblin for future requests",
    );
    expect(commands).toEqual([]);
    await page.locator("#repository-options summary").click();
    await page.getByLabel("Git actions").selectOption("pr");
    work.repositoryAuthorization.status = "Invalidated";
    await expect(
        page.getByText("Context changed. Review repository access again."),
    ).toBeVisible();
    await expect(page.getByLabel("Git actions")).toHaveValue("message");
    await expect(
        page.getByRole("button", {
            name: "Enable repository & authorize this Work",
        }),
    ).toHaveCount(0);
    work.repositoryAuthorization.id = "92";
    work.repositoryAuthorization.status = "Pending";
    work.repositoryAuthorization.target.repository.grant.allowPush = false;
    work.repositoryAuthorization.target.repository.grant.allowPullRequest = false;
    work.repositoryAuthorization.target.repository.grant.branch = "goblin/1/92";
    await expect(preview).toContainText("without pushing");
    await expect(page.getByLabel("Git actions")).toHaveValue("local");
    await page.setViewportSize({ width: 390, height: 844 });
    const approve = preview.getByRole("button", {
        name: "Enable repository & authorize this Work",
    });
    await approve.scrollIntoViewIfNeeded();
    await expect(approve).toBeInViewport();
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
    await approve.click();
    await expect.poll(() => commands.length).toBe(1);
    expect(commands[0]).toMatchObject({
        action: "AuthorizeRepository",
        workId: "1",
        expectedVersion: "4",
        authorizationId: "92",
    });
    expect(commands[0]).not.toHaveProperty("repository");
    expect(commands[0]).not.toHaveProperty("delivery");
});
