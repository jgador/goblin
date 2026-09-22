import { test, expect, type Page } from "@playwright/test";

async function home(page: Page) {
    const now = new Date("2026-09-23T00:00:00Z");
    await page.clock.install({ time: now });
    await page.clock.pauseAt(now);
    const state = {
        authenticated: true,
        workUnavailable: false,
        objective: "Existing work",
        connectionName: "Codex",
        availability: "Disconnected",
        connectionWait: null as Promise<void> | null,
    };
    await page.route("**/api/**", async (route) => {
        const path = new URL(route.request().url()).pathname;
        if (path === "/api/connections") await state.connectionWait;
        if (path === "/api/work" && state.workUnavailable) {
            await route.fulfill({ status: 503, json: { error: {} } });
            return;
        }
        const data: Record<string, unknown> = {
            "/api/session": { authenticated: state.authenticated },
            "/api/work": [
                {
                    version: "1",
                    work: {
                        id: "1",
                        objective: state.objective,
                        status: "Ready",
                        attempts: [],
                        history: [],
                        decisions: [],
                        results: [],
                        artifacts: [],
                    },
                },
            ],
            "/api/connections": [
                {
                    id: "1",
                    name: state.connectionName,
                    availability: state.availability,
                },
            ],
            "/api/github": { configured: true },
        };
        await route.fulfill({ json: data[path] ?? [] });
    });
    await page.goto("/?new=work");
    await expect(page.locator(".work-title")).toHaveText(state.objective);
    await expect(page.locator(".connection-nudge")).toBeVisible();
    return state;
}

for (const viewport of [
    { width: 1280, height: 720 },
    { width: 390, height: 500 },
]) {
    for (const mode of ["manual", "background"] as const) {
        test(`${mode} home refresh keeps the page mounted at ${viewport.width}px`, async ({
            page,
        }) => {
            await page.setViewportSize(viewport);
            const state = await home(page);
            const errors: string[] = [];
            page.on("pageerror", (error) => errors.push(error.message));
            const draft = page.getByPlaceholder(
                "Describe the intended outcome…",
            );
            const text = Array.from(
                { length: 20 },
                (_, i) => `Draft line ${i}`,
            ).join("\n");
            await draft.fill(text);
            await draft.evaluate((element: HTMLTextAreaElement) => {
                element.style.height = "128px";
                element.setSelectionRange(5, 18);
                element.scrollTop = 50;
            });
            const homeElement = page.locator(".home");
            await homeElement.evaluate((element) => {
                element.scrollTop = 30;
            });
            const scroll = await homeElement.evaluate(
                (element) => element.scrollTop,
            );
            const contentBounds = await page
                .locator(".home-content")
                .boundingBox();
            const replyScroll = await draft.evaluate(
                (element) => element.scrollTop,
            );
            const retained = await Promise.all([
                homeElement.elementHandle(),
                page.locator(".home-portrait").elementHandle(),
                draft.elementHandle(),
                page
                    .getByRole("button", { name: "Refresh", exact: true })
                    .elementHandle(),
            ]);
            let release = () => {};
            state.connectionWait = new Promise<void>((resolve) => {
                release = resolve;
            });
            state.objective = "New server state";
            if (mode === "manual")
                await page
                    .getByRole("button", { name: "Refresh", exact: true })
                    .click();
            else await page.clock.runFor(3000);

            // Work finishes first; connection checks must not hide the existing
            // prompt or recenter the home while their response is outstanding.
            await expect(page.locator(".work-title")).toHaveText(
                state.objective,
            );
            await expect(page.locator(".connection-nudge")).toBeVisible();
            expect(await page.locator(".home-content").boundingBox()).toEqual(
                contentBounds,
            );
            state.connectionName = "Updated provider";
            release();
            await expect(page.locator(".connection-choice")).toHaveText(
                "Updated provider",
            );

            for (const element of retained)
                expect(
                    await element!.evaluate((node) => node.isConnected),
                ).toBe(true);
            await expect(draft).toHaveValue(text);
            expect(
                await draft.evaluate((element: HTMLTextAreaElement) => [
                    element.selectionStart,
                    element.selectionEnd,
                    element.scrollTop,
                    element.style.height,
                ]),
            ).toEqual([5, 18, replyScroll, "128px"]);
            expect(
                await homeElement.evaluate((element) => element.scrollTop),
            ).toBe(scroll);
            if (mode === "background") await expect(draft).toBeFocused();
            else
                await expect(
                    page.getByRole("button", { name: "Refresh", exact: true }),
                ).toBeFocused();
            expect(errors).toEqual([]);
        });
    }
}

test("home refresh applies connection changes, reports failures, and clears a locked workspace", async ({
    page,
}) => {
    const state = await home(page);
    const retained = await page.locator(".home").elementHandle();
    const refresh = page.getByRole("button", { name: "Refresh", exact: true });
    const create = page.getByRole("button", {
        name: "Create work",
        exact: true,
    });
    state.objective = "Connection updated";
    state.availability = "Available";
    await refresh.click();
    await expect(page.locator(".work-title")).toHaveText(state.objective);
    await expect(page.locator(".connection-nudge")).toBeHidden();

    state.workUnavailable = true;
    await refresh.click();
    await expect(page.getByRole("alert")).toContainText(
        "Work could not be loaded",
    );
    await expect(create).toBeDisabled();
    expect(await retained!.evaluate((node) => node.isConnected)).toBe(true);

    state.workUnavailable = false;
    await refresh.click();
    await expect(page.getByRole("alert")).toHaveCount(0);
    await expect(create).toBeEnabled();
    expect(await retained!.evaluate((node) => node.isConnected)).toBe(true);

    await page
        .getByPlaceholder("Describe the intended outcome…")
        .fill("Private draft");
    state.authenticated = false;
    await refresh.click();
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    await expect(page.locator(".home")).toHaveCount(0);
    await expect(page.getByText("Private draft")).toHaveCount(0);
});
