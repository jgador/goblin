import { test, expect, type Page } from "@playwright/test";

async function fixture(page: Page) {
    const now = Date.now();
    const state = {
        status: "live",
        notice: null as string | null,
        machine: {
            observedAt: new Date(now).toISOString(),
            name: "my-goblin-vm",
            environment: "WSL",
            operatingSystem: "Ubuntu 24.04",
            uptimeSeconds: 7380,
            cpu: { total: 8, used: 2, available: 6, percent: 25 },
            memory: {
                total: 16 * 1024 ** 3,
                used: 12 * 1024 ** 3,
                available: 4 * 1024 ** 3,
                percent: 75,
            },
            disk: {
                total: 100 * 1024 ** 3,
                used: 45 * 1024 ** 3,
                available: 50 * 1024 ** 3,
                percent: 45,
            },
            warnings: [] as string[],
            services: [
                {
                    name: "app",
                    namespace: "goblin",
                    phase: "Running",
                    ready: true,
                    restarts: 0,
                    cpuCores: 0.2,
                    memoryBytes: 300 * 1024 ** 2,
                },
            ],
        },
        history: Array.from({ length: 21 }, (_, i) => ({
            at: new Date(now - (20 - i) * 15000).toISOString(),
            cpu: 15 + i / 2,
            memory: 70 + i / 4,
        })),
    };
    let locked = false;
    await page.route("**/api/**", async (route) => {
        const path = new URL(route.request().url()).pathname;
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: !locked };
        else if (path === "/api/session/lock") {
            locked = true;
            json = {};
        } else if (path === "/api/system") json = state;
        else if (path === "/api/cluster") json = { available: true };
        else if (path === "/api/connections")
            json = [{ id: "1", name: "Codex", availability: "Available" }];
        await route.fulfill({ json });
    });
    return state;
}

test("VM resources are one click away, preserve drafts, and distinguish stale readings", async ({
    page,
}) => {
    const state = await fixture(page);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/");
    await page
        .getByPlaceholder("Describe the intended outcome…")
        .fill("Keep my work draft");
    await expect(
        page.getByRole("button", { name: "System resources", exact: true }),
    ).toContainText("CPU 25% · Memory 75%");
    await page
        .getByRole("button", { name: "System resources", exact: true })
        .click();
    const panel = page.getByRole("region", { name: "System resources" });
    await expect(
        panel.getByRole("heading", { name: "Your system" }),
    ).toBeVisible();
    await expect(panel.getByText("No resource warnings")).toBeVisible();
    await expect(
        panel.getByText("4 GiB available", { exact: true }),
    ).toBeVisible();
    await expect(
        panel.getByRole("img", { name: /CPU and memory usage over/ }),
    ).toBeVisible();
    await expect(panel.getByText(/Windows memory/)).toBeVisible();
    await page.screenshot({
        path: "test-results/system-desktop.png",
        fullPage: true,
    });
    await panel
        .getByRole("button", { name: /Open detailed cluster view/ })
        .click();
    await expect(
        page.getByRole("link", { name: "Open cluster" }),
    ).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(
        page.getByRole("button", { name: "System resources", exact: true }),
    ).toBeFocused();
    await expect(
        page.getByPlaceholder("Describe the intended outcome…"),
    ).toHaveValue("Keep my work draft");
    state.status = "stale";
    state.notice =
        "Updates are interrupted. These are the last reported readings.";
    await page.reload();
    await expect(
        page.getByRole("button", { name: "System resources", exact: true }),
    ).toContainText("Readings out of date");
    await page
        .getByRole("button", { name: "System resources", exact: true })
        .click();
    await expect(panel.getByText("No resource warnings")).not.toBeVisible();
    await expect(panel.getByText(/Current health is unknown/)).toBeVisible();
    expect(errors).toEqual([]);
});

test("System supports mobile, pending sandboxes, unavailable metrics, and lock clearing", async ({
    page,
}) => {
    const state = await fixture(page);
    state.machine.warnings = [
        "Some Goblin services or agent sandboxes are not ready.",
    ];
    state.machine.services.push({
        name: "run-123456789123456789-1",
        namespace: "agents",
        phase: "Pending",
        ready: false,
        restarts: 0,
        cpuCores: 0,
        memoryBytes: 0,
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/?settings=system");
    const dialog = page.getByRole("dialog", { name: "Settings" });
    const panel = page.getByRole("region", { name: "System resources" });
    await expect(
        panel.getByText("run-123456789123456789-1", { exact: true }),
    ).toBeVisible();
    await expect(
        panel.getByText("Agent sandbox", { exact: true }),
    ).toBeVisible();
    await expect(panel.getByText("Pending", { exact: true })).toBeVisible();
    await expect(panel.getByText("1 / 2 ready", { exact: true })).toBeVisible();
    expect(
        await dialog.evaluate(
            (element) => element.scrollWidth <= window.innerWidth,
        ),
    ).toBe(true);
    expect(
        await panel.evaluate(
            (element) => element.scrollWidth <= element.clientWidth,
        ),
    ).toBe(true);
    await page.screenshot({
        path: "test-results/system-mobile.png",
        fullPage: true,
    });
    await dialog.getByRole("button", { name: "Lock workspace" }).click();
    await expect(dialog).not.toBeVisible();
    await expect(page.getByText("my-goblin-vm")).not.toBeAttached();
});

test("Unavailable monitoring does not claim zero or healthy", async ({
    page,
}) => {
    await fixture(page);
    await page.route("**/api/system", (route) =>
        route.fulfill({
            json: {
                status: "unavailable",
                notice: "System metrics are temporarily unavailable.",
                machine: null,
                history: [],
            },
        }),
    );
    await page.goto("/?settings=system");
    const panel = page.getByRole("region", { name: "System resources" });
    await expect(
        panel.getByText("System metrics are temporarily unavailable."),
    ).toBeVisible();
    await expect(panel.getByRole("progressbar")).toHaveCount(0);
    await expect(panel.getByText("No resource warnings")).not.toBeVisible();
});
