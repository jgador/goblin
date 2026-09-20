import { test, expect, type Page } from "@playwright/test";

async function settingsFixture(page: Page) {
    const github = {
        configured: true,
        status: "Disconnected",
        login: null as string | null,
        userCode: null as string | null,
        notice: null,
    };
    const repo = {
        id: "42",
        name: "owner/project",
        defaultBranch: "main",
        canPush: true,
        enabled: false,
    };
    await page.route("**/api/**", async (route) => {
        const request = route.request(),
            path = new URL(request.url()).pathname;
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: true };
        else if (path === "/api/cluster") json = { available: true };
        else if (path === "/api/status")
            json = {
                account: {
                    type: "chatgpt",
                    email: "owner@example.test",
                    planType: "plus",
                },
                login: null,
                notice: null,
                verification: "accepted",
                runtimeReady: true,
            };
        else if (path === "/api/prompt")
            json = {
                reply: "OK",
                model: "test",
                durationMs: 1,
                authType: "chatgpt",
            };
        else if (path === "/api/github") json = github;
        else if (path === "/api/github/connect") {
            github.status = "Connecting";
            github.userCode = "ABCD-1234";
            json = github;
        } else if (path === "/api/github/cancel") {
            github.status = "Disconnected";
            github.userCode = null;
            json = github;
        } else if (path === "/api/github/available-repositories") json = [repo];
        else if (path === "/api/github/repositories") {
            if (request.method() === "POST")
                repo.enabled = request.postDataJSON().enabled === "true";
            json = repo.enabled ? [repo] : [];
        } else if (path === "/api/connections")
            json = [{ id: "1", name: "Codex", availability: "Available" }];
        await route.fulfill({ json });
    });
    return { github, repo };
}

test("Settings keeps the Work draft and uses both connection panels", async ({
    page,
}) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    const { github } = await settingsFixture(page);
    await page.goto("/work");
    await page.getByRole("button", { name: "New work", exact: true }).click();
    await page
        .getByPlaceholder("Describe the intended outcome…")
        .fill("Keep this draft while I connect GitHub");
    await page.locator('.sidebar [data-action="settings"]').click();
    const dialog = page.getByRole("dialog", { name: "Settings" });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText("owner@example.test · plus")).toBeVisible();
    await dialog.getByRole("button", { name: "Cluster", exact: true }).click();
    const cluster = dialog.getByRole("link", { name: "Open cluster" });
    await expect(cluster).toHaveAttribute("href", "/headlamp/");
    await expect(cluster).toHaveAttribute("target", "_blank");
    await expect(
        dialog.getByText(/Your Goblin login gives you read-only access/),
    ).toBeVisible();
    await dialog.getByRole("button", { name: "GitHub", exact: true }).click();
    await dialog
        .getByRole("button", { name: "Connect GitHub", exact: true })
        .click();
    await expect(dialog.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await expect(
        dialog.getByRole("link", { name: "Open GitHub sign-in" }),
    ).toHaveAttribute("href", "https://github.com/login/device");
    await page.keyboard.press("Escape");
    await expect(dialog).not.toBeVisible();
    await expect(
        page.getByPlaceholder("Describe the intended outcome…"),
    ).toHaveValue("Keep this draft while I connect GitHub");
    await page.locator('.sidebar [data-action="settings-github"]').click();
    await expect(dialog.getByText("ABCD-1234", { exact: true })).toBeVisible();
    github.status = "Connected";
    github.login = "owner";
    github.userCode = null;
    await expect(dialog.getByText("@owner · Connected")).toBeVisible();
    await dialog.getByRole("button", { name: "Choose repositories" }).click();
    await dialog.getByRole("button", { name: "Enable", exact: true }).click();
    await expect(
        dialog.getByRole("button", { name: "Disable owner/project" }),
    ).toBeVisible();
    await page.screenshot({
        path: "test-results/settings-github.png",
        fullPage: true,
    });
    await dialog.getByRole("button", { name: "Close settings" }).click();
    await expect(
        page.locator('.sidebar [data-action="settings-github"]'),
    ).toBeFocused();
    expect(errors).toEqual([]);
});

test("GitHub sign-in survives refresh and can be cancelled on mobile", async ({
    page,
}) => {
    await settingsFixture(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/work");
    await page.locator('.preview-right [data-action="settings"]').click();
    const dialog = page.getByRole("dialog", { name: "Settings" });
    await dialog.getByRole("button", { name: "GitHub", exact: true }).click();
    await dialog
        .getByRole("button", { name: "Connect GitHub", exact: true })
        .click();
    await page.reload();
    await page.locator('.preview-right [data-action="settings"]').click();
    await dialog.getByRole("button", { name: "GitHub", exact: true }).click();
    await expect(dialog.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await page.screenshot({
        path: "test-results/settings-mobile.png",
        fullPage: true,
    });
    await dialog.getByRole("button", { name: "Cancel sign-in" }).click();
    await expect(
        dialog.getByRole("button", { name: "Connect GitHub", exact: true }),
    ).toBeVisible();
    expect(
        await dialog.evaluate(
            (element) => element.scrollWidth <= window.innerWidth,
        ),
    ).toBe(true);
});
