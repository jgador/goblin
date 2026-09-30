import { test, expect, type BrowserContext } from "@playwright/test";

type State = {
    timeZone: string | null;
    writes: unknown[];
    failSave?: boolean;
    failRead?: boolean;
    ipResponse?: unknown;
    ipStatus?: number;
    ipRequests?: number;
};
async function fixture(context: BrowserContext, state: State) {
    state.ipRequests = 0;
    await context.route("https://ipwho.is/**", async (route) => {
        state.ipRequests!++;
        await route.fulfill({
            status: state.ipStatus ?? 200,
            json: state.ipResponse ?? {
                success: true,
                timezone: { id: "Asia/Manila" },
            },
            headers: { "access-control-allow-origin": "*" },
        });
    });
    await context.route("**/api/**", async (route) => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (path === "/api/values.js") return route.continue();
        let json: unknown = [];
        if (path === "/api/session") json = { authenticated: true };
        else if (path === "/api/preferences/timezones")
            json = ["UTC", ...Intl.supportedValuesOf("timeZone")];
        else if (path === "/api/preferences") {
            if (state.failRead)
                return route.fulfill({
                    status: 503,
                    json: { error: { message: "Preferences unavailable" } },
                });
            json = { timeZone: state.timeZone };
        } else if (path === "/api/preferences/timezone") {
            const body = request.postDataJSON();
            state.writes.push(body);
            if (state.failSave)
                return route.fulfill({
                    status: 503,
                    json: {
                        error: {
                            message:
                                "Workspace preferences could not be saved. Try again.",
                        },
                    },
                });
            if (body.expectedTimeZone !== state.timeZone)
                return route.fulfill({
                    status: 409,
                    json: {
                        error: {
                            message:
                                "The workspace timezone changed in another session. Review it and save again.",
                        },
                    },
                });
            state.timeZone = body.timeZone;
            json = { timeZone: state.timeZone };
        }
        await route.fulfill({ json });
    });
}

test("first access preselects Philippine time and saves only the confirmed workspace choice", async ({
    page,
    context,
    browser,
}) => {
    const state: State = { timeZone: null, writes: [] };
    await fixture(context, state);
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.goto("/");
    const setup = page.getByRole("dialog", { name: "Choose your timezone" });
    await expect(setup).toBeVisible();
    await expect(setup.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    expect(state.writes).toEqual([]);
    expect(await setup.locator("option").count()).toBeGreaterThan(400);
    await expect(setup.locator("[data-timezone-suggestion]")).toContainText(
        "Your IP location suggests Philippines — Manila",
    );
    expect(state.ipRequests).toBe(1);
    await setup.getByLabel("Find a timezone").fill("Philippines Manila");
    await expect(setup.locator("option")).toHaveCount(1);
    await expect(setup.locator("option")).toHaveText(
        "Philippines — Manila (UTC+08:00)",
    );
    await expect(setup.locator("#timezone-matches")).toContainText(
        "1 matching timezone.",
    );
    await expect(setup.locator("#timezone-preview")).toContainText(
        "Current time:",
    );
    await page.screenshot({
        path: test.info().outputPath("timezone-onboarding.png"),
        fullPage: true,
    });
    await setup.getByRole("button", { name: "Use timezone" }).click();
    await expect(setup).not.toBeVisible();
    expect(state.writes).toEqual([
        { timeZone: "Asia/Manila", expectedTimeZone: null },
    ]);
    await page.reload();
    await expect(setup).not.toBeVisible();

    const other = await browser.newContext({
        timezoneId: "Asia/Manila",
    });
    try {
        await fixture(other, state);
        const second = await other.newPage();
        await second.goto("http://127.0.0.1:8798/?settings=timezone");
        await expect(
            second.getByRole("dialog", { name: "Choose your timezone" }),
        ).not.toBeVisible();
        await expect(
            second.getByLabel("Timezone", { exact: true }),
        ).toHaveValue("Asia/Manila");
        expect(state.writes).toHaveLength(1);
        await second
            .getByLabel("Timezone", { exact: true })
            .selectOption("UTC");
        await second.getByRole("button", { name: "Save timezone" }).click();
        await expect(
            second.getByRole("status").filter({ hasText: "Timezone saved" }),
        ).toBeVisible();
        await page.goto("/?settings=timezone");
        // A saved choice takes precedence over the detected Philippine timezone.
        await expect(page.getByLabel("Timezone", { exact: true })).toHaveValue(
            "UTC",
        );
        await page
            .getByLabel("Timezone", { exact: true })
            .selectOption("Asia/Manila");
        await page.getByRole("button", { name: "Save timezone" }).click();
        await expect(
            page.getByRole("status").filter({ hasText: "Timezone saved" }),
        ).toBeVisible();
        await second.reload();
        await expect(
            second.getByLabel("Timezone", { exact: true }),
        ).toHaveValue("Asia/Manila");
        const rendered = await page.evaluate(async () => {
            // Philippine time stays UTC+8 throughout the year.
            const modulePath = "/settings/timezone.js";
            const { formatTimestamp } = await import(modulePath);
            return ["2026-01-15T12:00:00Z", "2026-07-15T12:00:00Z"].map(
                (value) =>
                    formatTimestamp(value, {
                        hourCycle: "h23",
                        timeZoneName: "longOffset",
                    }),
            );
        });
        expect(rendered[0]).toContain("20:00:00 GMT+08:00");
        expect(rendered[1]).toContain("20:00:00 GMT+08:00");
    } finally {
        await other.close();
    }
    expect(errors).toEqual([]);
});

test("a failed first save keeps the selection and offers an explicit retry", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: null, writes: [], failSave: true };
    await fixture(context, state);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto("/");
    const setup = page.getByRole("dialog", { name: "Choose your timezone" });
    await expect(setup.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    await setup.getByRole("button", { name: "Use timezone" }).click();
    await expect(setup.getByRole("alert")).toContainText("could not be saved");
    await expect(setup.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    expect(state.timeZone).toBeNull();
    expect(
        await setup.evaluate(
            (element) => element.scrollWidth <= window.innerWidth,
        ),
    ).toBe(true);
    await page.screenshot({
        path: test.info().outputPath("timezone-mobile.png"),
        fullPage: true,
    });
    state.failSave = false;
    await setup.getByRole("button", { name: "Use timezone" }).click();
    await expect(setup).not.toBeVisible();
    expect(state.timeZone).toBe("Asia/Manila");
});

test("Settings detects concurrent changes and a failed read never overwrites the preference", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: "UTC", writes: [] };
    await fixture(context, state);
    await page.goto("/?settings=timezone");
    const panel = page.getByRole("dialog", { name: "Settings" });
    await expect(panel.getByLabel("Timezone", { exact: true })).toHaveValue(
        "UTC",
    );
    await panel
        .getByLabel("Timezone", { exact: true })
        .selectOption("Asia/Manila");
    state.timeZone = "Asia/Manila";
    await panel.getByRole("button", { name: "Save timezone" }).click();
    await expect(panel.getByRole("alert")).toContainText(
        "changed in another session",
    );
    expect(state.timeZone).toBe("Asia/Manila");
    await panel.getByRole("button", { name: "Reload settings" }).click();
    await expect(panel.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    state.failRead = true;
    await page.reload();
    await expect(panel.getByRole("alert")).toContainText("could not be loaded");
    await expect(
        panel.getByRole("button", { name: "Save timezone" }),
    ).toBeDisabled();
    expect(state.writes).toHaveLength(1);
});

test("UTC is preselected when browser detection fails and is saved only after confirmation", async ({
    page,
    context,
}) => {
    const state: State = {
        timeZone: null,
        writes: [],
        ipResponse: { success: false },
    };
    await fixture(context, state);
    await page.addInitScript(() => {
        const original = Intl.DateTimeFormat.prototype.resolvedOptions;
        Intl.DateTimeFormat.prototype.resolvedOptions = function () {
            return { ...original.call(this), timeZone: "" };
        };
    });
    await page.goto("/");
    const setup = page.getByRole("dialog", { name: "Choose your timezone" });
    await expect(setup.getByLabel("Timezone", { exact: true })).toHaveValue(
        "UTC",
    );
    await expect(
        setup.getByText(/UTC is selected as a starting point/),
    ).toBeVisible();
    expect(state.writes).toEqual([]);
    await setup.getByRole("button", { name: "Use timezone" }).click();
    await expect(setup).not.toBeVisible();
    expect(state.writes).toEqual([{ timeZone: "UTC", expectedTimeZone: null }]);
    await page.reload();
    await expect(setup).not.toBeVisible();
});

test("country and city search keeps the chosen timezone until a matching option is selected", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: "Asia/Manila", writes: [] };
    await fixture(context, state);
    await page.goto("/?settings=timezone");
    const panel = page.getByRole("dialog", { name: "Settings" });
    const select = panel.getByLabel("Timezone", { exact: true });
    const search = panel.getByLabel("Find a timezone");
    await expect(select).toHaveValue("Asia/Manila");
    const total = await select.locator("option").count();
    for (const query of [
        "Philippines",
        "manila",
        " PHILIPPINES   Manila ",
        "Asia/Manila",
        "Mánila",
    ]) {
        await search.fill(query);
        await expect(select.locator("option")).toHaveCount(1);
        await expect(select.locator("option")).toHaveText(
            "Philippines — Manila (UTC+08:00)",
        );
        await expect(panel.locator("#timezone-matches")).toContainText(
            "1 matching timezone.",
        );
    }
    await search.fill("no-such-place");
    await expect(panel.locator("#timezone-matches")).toContainText(
        "No matching timezones",
    );
    await expect(select).toHaveValue("Asia/Manila");
    await expect(select.locator("optgroup")).toHaveAttribute(
        "label",
        "Current selection",
    );
    await search.fill("");
    await expect(select.locator("option")).toHaveCount(total);
    await expect(panel.locator("#timezone-matches")).toBeHidden();
    await select.selectOption("UTC");
    await search.fill("Philippines");
    await expect(select).toHaveValue("UTC");
    await expect(
        select.locator('optgroup[label="Search results"] option'),
    ).toHaveText("Philippines — Manila (UTC+08:00)");
    await select.click();
    await page
        .getByRole("option", {
            name: "Philippines — Manila (UTC+08:00)",
            exact: true,
        })
        .click();
    await expect(select).toHaveValue("Asia/Manila");
    expect(state.writes).toEqual([]);
    expect(state.ipRequests).toBe(0);
    const missingPlaces = await page.evaluate(async () => {
        const modulePath = "/settings/timezone-places.js";
        const { timeZonePlaces } = await import(modulePath);
        return Intl.supportedValuesOf("timeZone").filter(
            (zone) => !timeZonePlaces[zone],
        );
    });
    expect(missingPlaces).toEqual([]);
    await page.screenshot({
        path: test.info().outputPath("timezone-country-search.png"),
        fullPage: true,
    });
});

test("IP timezone takes precedence over a browser reporting UTC during onboarding", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: null, writes: [] };
    await fixture(context, state);
    await page.addInitScript(() => {
        const original = Intl.DateTimeFormat.prototype.resolvedOptions;
        Intl.DateTimeFormat.prototype.resolvedOptions = function () {
            return { ...original.call(this), timeZone: "UTC" };
        };
    });
    await page.goto("/");
    await expect(page.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    await expect(page.locator("[data-timezone-suggestion]")).toContainText(
        "Your IP location suggests Philippines — Manila",
    );
    expect(state.writes).toEqual([]);
});

test("unavailable and invalid IP results fall back to the Philippine browser timezone", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: null, writes: [] };
    await fixture(context, state);
    for (const response of [
        { status: 429, body: { success: false } },
        {
            status: 200,
            body: { success: false, timezone: { id: "Asia/Manila" } },
        },
        {
            status: 200,
            body: { success: true, timezone: { id: "Asia/Manila<script>" } },
        },
        { status: 200, body: { success: true } },
    ]) {
        state.ipStatus = response.status;
        state.ipResponse = response.body;
        await page.goto("/");
        await expect(page.getByLabel("Timezone", { exact: true })).toHaveValue(
            "Asia/Manila",
        );
        await expect(page.locator("[data-timezone-suggestion]")).toContainText(
            "IP location was unavailable. Your browser suggests Philippines — Manila",
        );
    }
    expect(state.writes).toEqual([]);
});

test("a stalled IP request times out and leaves onboarding usable", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: null, writes: [] };
    await fixture(context, state);
    await context.route("https://ipwho.is/**", () => {});
    await page.goto("/");
    await expect(page.getByLabel("Timezone", { exact: true })).toBeEnabled({
        timeout: 7000,
    });
    await expect(page.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    await expect(page.locator("[data-timezone-suggestion]")).toContainText(
        "IP location was unavailable",
    );
    expect(state.writes).toEqual([]);
});

test("Settings checks a new IP location without replacing the saved timezone", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: "UTC", writes: [] };
    await fixture(context, state);
    await page.goto("/?settings=timezone");
    const select = page.getByLabel("Timezone", { exact: true });
    await expect(select).toHaveValue("UTC");
    expect(state.ipRequests).toBe(0);
    await page.getByRole("button", { name: "Detect from IP" }).click();
    await expect(page.locator("[data-timezone-suggestion]")).toContainText(
        "Your IP location suggests Philippines — Manila",
    );
    await expect(select).toHaveValue("UTC");
    expect(state.timeZone).toBe("UTC");
    expect(state.writes).toEqual([]);
    await page.getByRole("button", { name: "Use suggestion" }).click();
    await expect(select).toHaveValue("Asia/Manila");
    expect(state.writes).toEqual([]);
    await page.getByRole("button", { name: "Save timezone" }).click();
    await expect(
        page.getByRole("status").filter({ hasText: "Timezone saved" }),
    ).toBeVisible();
    expect(state.writes).toEqual([
        { timeZone: "Asia/Manila", expectedTimeZone: "UTC" },
    ]);
});

test("closing Settings cancels its pending IP lookup", async ({
    page,
    context,
}) => {
    const state: State = { timeZone: "Asia/Manila", writes: [] };
    await fixture(context, state);
    await context.route("https://ipwho.is/**", () => {});
    await page.goto("/?settings=timezone");
    await expect(page.getByLabel("Timezone", { exact: true })).toHaveValue(
        "Asia/Manila",
    );
    await page.getByRole("button", { name: "Detect from IP" }).click();
    await expect(page.locator("[data-timezone-suggestion]")).toContainText(
        "Detecting timezone",
    );
    const aborted = page.waitForEvent("requestfailed", (request) =>
        request.url().startsWith("https://ipwho.is/"),
    );
    await page.getByRole("button", { name: "Close settings" }).click();
    await aborted;
    await expect(
        page.getByRole("dialog", { name: "Settings" }),
    ).not.toBeVisible();
    expect(state.timeZone).toBe("Asia/Manila");
    expect(state.writes).toEqual([]);
});
