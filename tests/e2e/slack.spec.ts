import { test, expect } from "@playwright/test";

test("Slack setup supports manual code entry and explicit owner linking", async ({
    page,
}) => {
    for (const endpoint of [
        "work",
        "conversations",
        "agents",
        "runtimes",
        "connections",
        "github/repositories",
    ])
        await page.route(`**/api/${endpoint}`, (route) =>
            route.fulfill({ json: [] }),
        );
    await page.route("**/api/session", (route) =>
        route.fulfill({ json: { authenticated: true } }),
    );
    await page.route("**/api/preferences", (route) =>
        route.fulfill({ json: { timeZone: "Asia/Manila" } }),
    );
    await page.route("**/api/status", (route) =>
        route.fulfill({
            json: { account: null, login: null, runtimeReady: false },
        }),
    );
    await page.route("**/api/github", (route) =>
        route.fulfill({ json: { configured: false } }),
    );
    await page.route("**/api/system", (route) =>
        route.fulfill({
            json: {
                status: "unsupported",
                notice: null,
                machine: null,
                history: [],
            },
        }),
    );
    const state = {
        available: true,
        connection: {
            status: "Disconnected",
            workspace: null as string | null,
            workspaceId: null as string | null,
            appId: null as string | null,
            botUserId: null as string | null,
        },
        setup: { status: "Idle", command: null as string | null },
        identities: [] as { id: string; userId: string }[],
        link: null as {
            id: string;
            userId: string | null;
            expiresAt: string;
        } | null,
    };
    await page.route("**/api/integrations/slack**", async (route) => {
        const path = new URL(route.request().url()).pathname;
        if (path.endsWith("/setup"))
            state.setup = {
                status: "AwaitingAuthorization",
                command: "/slackauthticket fictional-ticket",
            };
        if (path === "/api/integrations/slack/confirm") {
            expect(route.request().postDataJSON()).toEqual({
                code: "confirmation",
            });
            state.connection = {
                status: "Connected",
                workspace: "Test workspace",
                workspaceId: "T123",
                appId: "A123",
                botUserId: "U123",
            };
            state.setup = { status: "Complete", command: null };
        }
        if (path.endsWith("/link")) {
            state.link = {
                id: "17",
                userId: null,
                expiresAt: new Date(Date.now() + 300000).toISOString(),
            };
            await route.fulfill({
                json: { id: "17", code: "fictional-link-code" },
            });
            return;
        }
        if (path.endsWith("/link/confirm")) {
            expect(route.request().postDataJSON()).toEqual({ id: "17" });
            state.identities = [{ id: "23", userId: "U456" }];
            state.link = null;
        }
        await route.fulfill({ json: state });
    });
    await page.goto("/?settings=integrations");
    await page.getByRole("link", { name: "Open integrations" }).click();
    await page
        .getByRole("button", { name: "Connect Slack", exact: true })
        .click();
    await page
        .getByRole("dialog", { name: "Settings", exact: true })
        .getByRole("button", { name: "Connect Slack", exact: true })
        .click();
    await expect(page.getByLabel("Slack authorization command")).toHaveValue(
        "/slackauthticket fictional-ticket",
    );
    await page
        .getByLabel("Confirmation code", { exact: true })
        .fill("confirmation");
    await page.waitForResponse(
        (response) =>
            response.request().method() === "GET" &&
            new URL(response.url()).pathname === "/api/integrations/slack",
    );
    await expect(
        page.getByLabel("Confirmation code", { exact: true }),
    ).toHaveValue("confirmation");
    await page.getByRole("button", { name: "Finish setup" }).click();
    await expect(
        page.getByRole("heading", { name: "Test workspace" }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Link a Slack identity" }).click();
    await expect(page.getByLabel("Slack linking command")).toHaveValue(
        "link fictional-link-code",
    );
    state.link!.userId = "U456";
    await page
        .getByRole("button", { name: "Grant local owner access" })
        .click();
    await expect(page.getByText("U456 → local owner")).toBeVisible();
    const image = page.getByRole("img", { name: "Goblin app icon" });
    await expect(image).toBeVisible();
    expect(
        await image.evaluate(
            (element: HTMLImageElement) => element.naturalWidth,
        ),
    ).toBe(1024);
});
