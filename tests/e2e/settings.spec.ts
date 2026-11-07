import { test, expect, type Page } from "@playwright/test";

async function settingsFixture(
    page: Page,
    { cluster = true, logs = true } = {},
) {
    const github = {
        configured: true,
        status: "Disconnected",
        login: null as string | null,
        userCode: null as string | null,
        notice: null,
    };
    const gitHubRepository = {
        id: "42",
        name: "owner/project",
        defaultBranch: "main",
        canPush: true,
        enabled: false,
    };
    await page.route("**/api/**", async (route) => {
        const request = route.request(),
            path = new URL(request.url()).pathname;
        if (route.request().resourceType() === "script")
            return route.continue();
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: true };
        else if (path === "/api/preferences")
            json = { timeZone: "Asia/Manila" };
        else if (path === "/api/cluster") json = { available: cluster };
        else if (path === "/api/logs") json = { available: logs };
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
        } else if (path === "/api/github/available-repositories")
            json = [gitHubRepository];
        else if (path === "/api/github/repositories") {
            if (request.method() === "POST")
                gitHubRepository.enabled =
                    request.postDataJSON().enabled === "true";
            json = gitHubRepository.enabled ? [gitHubRepository] : [];
        } else if (path === "/api/connections")
            json = [
                {
                    id: "1",
                    runtime: "codex",
                    name: "Codex",
                    availability: "Available",
                },
            ];
        await route.fulfill({ json });
    });
    return { github, gitHubRepository };
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
    const dialog = page.getByRole("region", { name: "Settings" });
    await expect(dialog).toBeVisible();
    await expect(
        dialog.getByRole("heading", { name: "Connections" }),
    ).toBeVisible();
    await dialog
        .getByRole("link", { name: "Manage Codex", exact: true })
        .click();
    await expect(dialog.getByText("owner@example.test · plus")).toBeVisible();
    await dialog
        .getByRole("link", { name: "Connections", exact: true })
        .click();
    await dialog.locator("#connection-github").click();
    await dialog
        .getByRole("button", { name: "Connect GitHub", exact: true })
        .click();
    await expect(dialog.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await expect(
        dialog.getByRole("link", { name: "Open GitHub sign-in" }),
    ).toHaveAttribute("href", "https://github.com/login/device");
    await dialog.getByRole("button", { name: "Back to workspace" }).click();
    await expect(dialog).not.toBeVisible();
    await expect(
        page.getByPlaceholder("Describe the intended outcome…"),
    ).toHaveValue("Keep this draft while I connect GitHub");
    await page.locator('.sidebar [data-action="settings"]').click();
    await dialog
        .getByRole("link", { name: "Connections", exact: true })
        .click();
    await dialog.locator("#connection-github").click();
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
        path: test.info().outputPath("settings-github.png"),
        fullPage: true,
    });
    await dialog.getByRole("button", { name: "Back to workspace" }).click();
    await expect(
        page.locator('.sidebar [data-action="settings"]'),
    ).toBeFocused();
    expect(errors).toEqual([]);
});

for (const width of [1280, 390]) {
    test(`Cluster and Logs open directly from Tools at ${width}px without leaving Settings`, async ({
        page,
        context,
    }) => {
        await settingsFixture(page);
        await page.setViewportSize({ width, height: 844 });
        await context.route(/\/(headlamp|logs)\//, (route) =>
            route.fulfill({
                contentType: "text/html",
                body: "<title>Tool fixture</title><h1>Tool opened</h1>",
            }),
        );
        await page.goto("/settings?section=connections&provider=codex");
        await expect(page.getByText("owner@example.test · plus")).toBeVisible();
        const tools = page.getByRole("navigation", {
            name: "Tools",
            exact: true,
        });
        for (const [label, path] of [
            ["Cluster", "/headlamp/"],
            ["Logs", "/logs/"],
        ]) {
            const link = tools.getByRole("link", {
                name: `${label} (opens in a new tab)`,
                exact: true,
            });
            await expect(link).toBeVisible();
            await expect(link).toHaveAttribute("href", path!);
            await expect(link).toHaveAttribute("rel", "noopener");
            const opened = page.waitForEvent("popup");
            if (label === "Cluster") {
                await link.focus();
                await page.keyboard.press("Enter");
            } else await link.click();
            const popup = await opened;
            await expect(popup).toHaveURL(new RegExp(`${path}$`));
            await popup.close();
            await expect(page).toHaveURL(
                /\/settings\?section=connections&provider=codex$/,
            );
            await expect(
                page.getByText("owner@example.test · plus"),
            ).toBeVisible();
        }
        await expect(
            page.locator(
                '[data-provider-panel="cluster"], [data-provider-panel="logs"]',
            ),
        ).toHaveCount(0);
        expect(
            await page
                .locator(".settings-page")
                .evaluate(
                    (element) => element.scrollWidth <= element.clientWidth,
                ),
        ).toBe(true);
        await page.screenshot({
            path: test.info().outputPath(`settings-tools-${width}.png`),
            fullPage: true,
        });
    });
}

test("an unconfigured tool explains its availability while the other tool stays accessible", async ({
    page,
    context,
}) => {
    await settingsFixture(page, { cluster: false });
    await context.route("**/logs/", (route) =>
        route.fulfill({
            contentType: "text/html",
            body: "<title>Logs fixture</title>",
        }),
    );
    await page.goto("/settings?section=connections");
    const tools = page.getByRole("navigation", { name: "Tools", exact: true });
    const cluster = tools.locator('[data-settings-tool="cluster"]');
    await expect(cluster).toHaveAttribute("aria-disabled", "true");
    await expect(cluster).not.toHaveAttribute("href", /./);
    await expect(tools.getByRole("status")).toHaveText(
        "Cluster is not configured for this installation.",
    );
    const opened = page.waitForEvent("popup");
    await tools
        .getByRole("link", { name: "Logs (opens in a new tab)" })
        .click();
    const popup = await opened;
    await expect(popup).toHaveURL(/\/logs\/$/);
    await popup.close();
});

test("GitHub sign-in survives refresh and can be cancelled on mobile", async ({
    page,
}) => {
    await settingsFixture(page);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/work");
    await page
        .getByRole("button", { name: "Show sidebar", exact: true })
        .click();
    await page.locator('.sidebar [data-action="settings"]').click();
    const dialog = page.getByRole("region", { name: "Settings" });
    await dialog.locator("#connection-github").click();
    await dialog
        .getByRole("button", { name: "Connect GitHub", exact: true })
        .click();
    await page.reload();
    await expect(page).toHaveURL(
        /\/settings\?section=connections&provider=github$/,
    );
    await expect(dialog.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await page.screenshot({
        path: test.info().outputPath("settings-mobile.png"),
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

test("settings sections have direct links and restore browser history without losing a Work draft", async ({
    page,
}) => {
    await settingsFixture(page);
    await page.goto("/?new=work");
    const draft = page.getByPlaceholder("Describe the intended outcome…");
    await draft.fill("Keep this idea through settings navigation");
    await page.locator('.sidebar [data-action="settings"]').click();
    await expect(page).toHaveURL(/\/settings\?section=connections$/);
    const settings = page.getByRole("region", {
        name: "Settings",
        exact: true,
    });
    await settings.locator("#connection-github").click();
    await expect(page).toHaveURL(
        /\/settings\?section=connections&provider=github$/,
    );
    await page.goBack();
    await expect(
        settings.getByRole("heading", { name: "Connections" }),
    ).toBeVisible();
    await page.goBack();
    await expect(draft).toHaveValue(
        "Keep this idea through settings navigation",
    );
    await page.goForward();
    await page.goForward();
    await expect(
        settings.getByRole("heading", { name: "GitHub", exact: true }),
    ).toBeVisible();
    await page.reload();
    await expect(
        settings.getByRole("heading", { name: "GitHub", exact: true }),
    ).toBeVisible();
    await expect(
        page.getByRole("dialog", { name: "Settings", exact: true }),
    ).toHaveCount(0);
    await expect(
        page.locator('.sidebar [data-action="settings"]'),
    ).toHaveAttribute("aria-current", "page");
});

test("settings direct links retain the workspace access gate, including trailing slashes", async ({
    page,
}) => {
    for (const path of [
        "/settings?section=connections&provider=github",
        "/settings/?section=connections&provider=codex",
    ]) {
        const response = await page.goto(path);
        expect(response?.status()).toBe(200);
        await expect(
            page.getByRole("heading", { name: "Open your workspace" }),
        ).toBeVisible();
        await expect(
            page.getByRole("region", { name: "Settings", exact: true }),
        ).toHaveCount(0);
    }
});
