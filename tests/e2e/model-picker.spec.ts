import { test, expect, type Page, type Route } from "@playwright/test";
import type { ModelCatalog } from "../../frontend/src/work/model-selection.js";

async function workspace(page: Page) {
    const now = new Date("2026-09-23T00:00:00Z");
    await page.clock.install({ time: now });
    await page.clock.pauseAt(now);
    const catalog: ModelCatalog = {
        models: Array.from({ length: 10 }, (_, i) => ({
            id: String(i),
            model: `model-${i}`,
            displayName: `Model ${i}`,
            defaultReasoningEffort: "low",
            supportedReasoningEfforts: ["low", "high"],
            isDefault: i === 0,
            isNew: false,
        })),
        hasMore: true,
        fetchedAt: now.toISOString(),
        stale: false,
        refreshing: false,
        unavailable: false,
    };
    const state = {
        catalog,
        modelReads: 0,
        modelResponse: undefined as
            ((route: Route) => Promise<void>) | undefined,
    };
    await page.route("**/api/**", async (route) => {
        const url = new URL(route.request().url());
        const path = url.pathname;
        if (route.request().resourceType() === "script")
            return route.continue();
        if (path === "/api/connections/1/models") {
            state.modelReads++;
            if (state.modelResponse) return state.modelResponse(route);
            const limit = Number(url.searchParams.get("limit"));
            return route.fulfill({
                json: {
                    ...state.catalog,
                    models: state.catalog.models.slice(0, limit),
                },
            });
        }
        const data: Record<string, unknown> = {
            "/api/session": { authenticated: true },
            "/api/preferences": { timeZone: "Asia/Manila" },
            "/api/agents": [
                { id: "1", name: "Goblin", connectionId: "1", isDefault: true },
            ],
            "/api/connections": [
                {
                    id: "1",
                    name: "Codex",
                    runtime: "codex",
                    availability: "Available",
                },
            ],
            "/api/work": [
                {
                    version: "1",
                    createdAt: now.toISOString(),
                    updatedAt: now.toISOString(),
                    work: {
                        id: "1",
                        objective: "Select execution options",
                        agentId: "1",
                        status: "Ready",
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
    await expect(page.locator(".model-picker-trigger")).toHaveAttribute(
        "aria-label",
        "Options: Model 0, reasoning effort: Low",
    );
    return state;
}

test("model choices, supported keyboard stops, and expanded choices survive reload", async ({
    page,
}) => {
    await workspace(page);
    await page.locator(".model-picker-trigger").click();
    const dialog = page.getByRole("dialog", {
        name: "Model and reasoning effort",
    });
    await dialog.locator(".model-row").click();
    await expect(dialog.locator('[data-action="select-model"]')).toHaveCount(4);
    await dialog
        .getByRole("button", { name: "Show more models (up to 10)" })
        .click();
    await expect(dialog.locator('[data-action="select-model"]')).toHaveCount(
        11,
    );
    await dialog
        .locator('[data-action="select-model"][data-value="model-9"]')
        .click();
    const slider = dialog.getByRole("slider", { name: "Reasoning effort" });
    await slider.press("ArrowRight");
    await expect(slider).toHaveAttribute("aria-valuetext", "High");
    await expect(
        dialog.getByRole("button", { name: "Medium", exact: true }),
    ).toBeDisabled();
    await slider.press("Escape");
    await expect(dialog).toHaveCount(0);
    await expect(page.locator(".model-picker-trigger")).toBeFocused();
    await page.reload();
    await expect(page.locator(".model-picker-trigger")).toHaveAttribute(
        "aria-label",
        "Options: Model 9, reasoning effort: High",
    );
    await page.locator(".model-picker-trigger").click();
    await dialog.locator(".model-row").click();
    await expect(dialog.locator('[data-action="select-model"]')).toHaveCount(
        11,
    );
    await dialog.locator('[data-action="select-model"][data-value=""]').click();
    await expect(page.locator(".model-picker-trigger")).toHaveAttribute(
        "aria-label",
        "Options: Model 0, reasoning effort: Low",
    );
});

test("a late refresh failure after a connection reset cannot replace the current picker", async ({
    page,
}) => {
    const state = await workspace(page);
    let release!: () => void;
    let started!: () => void;
    const waiting = new Promise<void>((resolve) => {
        release = resolve;
    });
    const intercepted = new Promise<void>((resolve) => {
        started = resolve;
    });
    state.modelResponse = async (route) => {
        started();
        await waiting;
        await route.fulfill({
            status: 503,
            json: { error: { message: "Obsolete model error" } },
        });
    };
    await page.locator(".model-picker-trigger").click();
    const dialog = page.getByRole("dialog", {
        name: "Model and reasoning effort",
    });
    await dialog.locator(".model-row").click();
    await dialog
        .getByRole("button", { name: "Refresh models", exact: true })
        .click();
    await intercepted;
    state.modelResponse = undefined;
    state.catalog.models[0].displayName = "Current model";
    await page.evaluate(() => {
        window.dispatchEvent(new Event("goblin-connections-changed"));
    });
    await expect(page.locator(".model-picker-trigger")).toHaveAttribute(
        "aria-label",
        "Options: Current model, reasoning effort: Low",
    );
    const completed = page.waitForResponse(
        (response) =>
            response.status() === 503 && response.url().includes("/models?"),
    );
    release();
    await completed;
    await expect(
        dialog.getByRole("button", { name: "Refresh models", exact: true }),
    ).toBeEnabled();
    await expect(dialog).not.toContainText("Obsolete model error");
    await expect(page.locator(".model-picker-trigger")).toHaveAttribute(
        "aria-label",
        "Options: Current model, reasoning effort: Low",
    );
});

test("an expired catalog cannot cause a render and request loop", async ({
    page,
}) => {
    const state = await workspace(page);
    state.catalog.fetchedAt = "2020-01-01T00:00:00Z";
    await page.evaluate(() => {
        window.dispatchEvent(new Event("goblin-connections-changed"));
    });
    await expect.poll(() => state.modelReads).toBe(2);
    await page.clock.runFor(3000);
    await expect(page.locator(".model-picker-trigger")).toHaveAttribute(
        "aria-label",
        "Options: Model 0, reasoning effort: Low",
    );
    expect(state.modelReads).toBe(2);
});
