import { test, expect } from "@playwright/test";

test.describe("durable Work", () => {
    test.skip(
        !process.env.GOBLIN_TEST_POSTGRES_APP,
        "Real PostgreSQL is required for the Work browser journey.",
    );
    async function unlock(page: import("@playwright/test").Page) {
        await page.goto("/work");
        await page.getByLabel("Goblin password", { exact: true }).fill("a");
        await page.getByRole("button", { name: "Unlock", exact: true }).click();
        await expect(
            page.getByRole("heading", { name: "Work", exact: true }),
        ).toBeVisible();
        const result = await page.request.post("/api/auth/api-key", {
            headers: { Origin: "http://127.0.0.1:8798" },
            data: { apiKey: "sk-work-browser-fixture-not-a-real-secret" },
        });
        expect([200, 409]).toContain(result.status());
        await page
            .getByRole("button", { name: "Refresh", exact: true })
            .click();
    }
    async function create(
        page: import("@playwright/test").Page,
        objective: string,
    ) {
        await page
            .getByRole("button", { name: "Create work", exact: true })
            .first()
            .click();
        await page
            .getByRole("textbox", { name: "Describe the intended outcome…" })
            .fill(objective);
        await page.getByRole("button", { name: "Send message" }).click();
        await expect(page.locator(".detail h2")).toHaveText(objective);
        await page
            .getByRole("button", { name: "Assign agent", exact: true })
            .click();
    }
    test("decisions, revisions, provenance, and approval survive refresh and another browser", async ({
        page,
        browser,
    }) => {
        const errors: string[] = [];
        page.on("pageerror", (error) => errors.push(error.message));
        await unlock(page);
        const objective = "A decision for the release " + Date.now();
        await create(page, objective);
        await page
            .getByRole("button", { name: "Start work", exact: true })
            .click();
        await expect(
            page.getByRole("heading", { name: "Needs your input" }),
        ).toBeVisible({ timeout: 15000 });
        await page.getByRole("textbox").fill("Prioritize a small release.");
        await page.getByRole("button", { name: "Send message" }).click();
        await page
            .getByRole("button", { name: "Start work", exact: true })
            .click();
        await expect(
            page.getByRole("button", { name: "Approve & complete" }),
        ).toBeVisible({ timeout: 15000 });
        await page.getByRole("button", { name: "Ask for changes" }).click();
        await page.getByRole("textbox").fill("Include validation steps.");
        await page.getByRole("button", { name: "Send message" }).click();
        await page
            .getByRole("button", { name: "Start work", exact: true })
            .click();
        await page
            .getByRole("button", { name: "Approve & complete" })
            .click({ timeout: 15000 });
        await expect(page.locator(".detail .status-pill")).toHaveText(
            "Completed",
        );
        await page.reload();
        await page.getByRole("button", { name: new RegExp(objective) }).click();
        await page.getByRole("tab", { name: "Outputs" }).click();
        await expect(
            page.getByRole("heading", { name: "Approved result" }),
        ).toBeVisible();
        await page.getByRole("tab", { name: "Activity" }).click();
        await expect(
            page.getByRole("heading", { name: "Result Approved" }),
        ).toBeVisible();
        await expect(page.locator(".detail-body")).toContainText(
            "fixture-model",
        );
        const context = await browser.newContext();
        const other = await context.newPage();
        await unlock(other);
        await other
            .getByRole("button", { name: new RegExp(objective) })
            .click();
        await expect(other.locator(".detail .status-pill")).toHaveText(
            "Completed",
        );
        await context.close();
        expect(errors).toEqual([]);
        await expect(page.locator("[style]")).toHaveCount(0);
    });
    test("a failed execution needs attention and is not automatically retried", async ({
        page,
    }) => {
        await unlock(page);
        await create(page, "Test failure " + Date.now());
        await page
            .getByRole("button", { name: "Start work", exact: true })
            .click();
        await expect(
            page.getByRole("heading", { name: "Needs attention", exact: true }),
        ).toBeVisible({ timeout: 15000 });
        const before = (
            await (await page.request.get("/api/work")).json()
        ).find((x: { work: { objective: string } }) =>
            x.work.objective.startsWith("Test failure"),
        );
        await page.reload();
        await expect(
            page.getByRole("button", { name: "Retry work", exact: true }),
        ).toBeVisible();
        const after = await (
            await page.request.get("/api/work/" + before.work.id)
        ).json();
        expect(after.work.attempts).toHaveLength(1);
    });
    test("conversation tracking preserves context and safely renders user text", async ({
        page,
    }) => {
        await unlock(page);
        await page
            .getByRole("button", { name: "Conversations", exact: true })
            .click();
        await page
            .getByRole("button", { name: "New conversation", exact: true })
            .click();
        const text =
            "Release context " + Date.now() + ' <img src=x onerror="alert(1)">';
        await page.getByRole("textbox").fill(text);
        await page.getByRole("button", { name: "Send message" }).click();
        await page
            .getByRole("button", { name: "Track this work", exact: true })
            .click();
        await expect(page.locator(".detail h2")).toHaveText(text);
        await page.reload();
        await page
            .getByRole("button", { name: text, exact: false })
            .first()
            .click();
        await expect(page.locator(".detail .message-text").first()).toHaveText(
            text,
        );
        await expect(page.locator('img[src="x"]')).toHaveCount(0);
        await page.screenshot({
            path: test.info().outputPath("desktop-work.png"),
            fullPage: true,
        });
        await page.setViewportSize({ width: 390, height: 844 });
        await expect(page.locator(".detail h2")).toBeVisible();
        expect(
            await page.evaluate(
                () => document.documentElement.scrollWidth <= window.innerWidth,
            ),
        ).toBe(true);
        await page.screenshot({
            path: test.info().outputPath("mobile-work.png"),
            fullPage: true,
        });
        await page.getByRole("button", { name: "Work", exact: true }).click();
        await expect(
            page.getByRole("heading", { name: "Work", exact: true }),
        ).toBeVisible();
    });

    test("a lost response can be resent after refresh without creating duplicate Work", async ({
        page,
    }) => {
        await unlock(page);
        const objective = "Lost response " + Date.now();
        await page.route(
            "**/api/work/commands",
            async (route) => {
                await route.fetch(); // The server commits, but the browser loses the response.
                await route.abort("failed");
            },
            { times: 1 },
        );
        await page
            .getByRole("button", { name: "Create work", exact: true })
            .click();
        await page.getByRole("textbox").fill(objective);
        await page.getByRole("button", { name: "Send message" }).click();
        await expect(
            page.getByRole("button", { name: "Resend command" }),
        ).toBeVisible();
        await page.reload();
        await page.getByRole("button", { name: "Resend command" }).click();
        await expect(page.locator(".detail h2")).toHaveText(objective);
        const saved = await (await page.request.get("/api/work")).json();
        expect(
            saved.filter(
                (x: { work: { objective: string } }) =>
                    x.work.objective === objective,
            ),
        ).toHaveLength(1);
    });

    test("connection failures leave Work history and controls available", async ({
        page,
    }) => {
        await page.route("**/api/connections", (route) =>
            route.fulfill({
                status: 503,
                contentType: "application/json",
                body: JSON.stringify({
                    error: { message: "Runtime unavailable" },
                }),
            }),
        );
        await page.route("**/api/github", (route) =>
            route.fulfill({
                status: 502,
                contentType: "application/json",
                body: JSON.stringify({
                    error: { message: "GitHub unavailable" },
                }),
            }),
        );
        await unlock(page);
        await create(page, "History remains available " + Date.now());
        await expect(
            page.getByRole("button", { name: "Start work", exact: true }),
        ).toBeVisible();
        expect((await page.request.get("/readyz")).status()).toBe(200);
        await page.reload();
        await expect(page.locator(".detail h2")).toContainText(
            "History remains available",
        );
    });
});
