import { test, expect } from "@playwright/test";
import { setupRetryPassword, startSetup } from "../support/setup.js";

test("setup summarizes concurrent tasks and preserves disclosures and focus across updates", async ({
    page,
}) => {
    const setup = await startSetup();
    try {
        await page.goto(setup.url);
        const progress = page.getByRole("progressbar", {
            name: "Installation progress",
        });
        const serverStage = page
            .locator(".setup-stage")
            .filter({ hasText: "Prepare your server" });
        const serverSummary = serverStage.locator("summary");
        await expect(progress).toHaveAttribute("value", "0");
        await expect(serverStage).not.toHaveAttribute("open", "");
        await serverSummary.focus();
        await page.keyboard.press("Enter");
        await expect(serverStage).toHaveAttribute("open", "");
        setup.transition("begin");
        setup.transition("start", "prepare");
        setup.transition("complete");
        setup.transition("start", "k3s");
        setup.transition("start", "image");
        await expect(page.locator("#activity")).toHaveText(
            "2 tasks running on your server.",
        );
        await expect(
            page.locator('.setup-stage[data-status="running"]'),
        ).toHaveCount(2);
        await expect(serverSummary).toBeFocused();
        await expect(serverStage).toHaveAttribute("open", "");
        await expect(page.locator(".installation-log")).not.toHaveAttribute(
            "open",
            "",
        );
        setup.transition("complete", "k3s");
        await expect(progress).toHaveAttribute(
            "aria-valuetext",
            "2 of 12 tasks complete",
        );
        await expect(page.locator("#completed-count")).toHaveText("2");
        await expect(serverSummary).toBeFocused();
        await page.keyboard.press("Enter");
        setup.transition("detail", "Building application assets");
        await expect(serverStage).not.toHaveAttribute("open", "");
        setup.transition("fail-step", "image");
        setup.transition("failed");
        await expect(page.locator("#setup-status")).toHaveText(
            "Needs attention",
        );
        await expect(page.locator(".installation-log")).toHaveAttribute(
            "open",
            "",
        );
        await expect(
            page.locator('.setup-stage[data-status="failed"]'),
        ).toHaveAttribute("open", "");
        await expect(page.getByRole("alert")).toContainText(
            "Your progress is saved.",
        );
        await page.locator(".installation-log > summary").click();
        await page.waitForResponse((response) =>
            response.url().endsWith("/setup/status"),
        );
        await expect(page.locator(".installation-log")).not.toHaveAttribute(
            "open",
            "",
        );
        await expect(progress).toHaveAttribute("value", "2");
    } finally {
        await setup.close();
    }
});

test("installation logs stream concurrent progress, survive refresh, and render output as text", async ({
    page,
}) => {
    const setup = await startSetup();
    try {
        await page.goto(setup.url);
        setup.transition("begin");
        setup.transition("start", "k3s");
        setup.transition("start", "image");
        setup.transition("detail", "Downloading image 1/8");
        await expect(page.locator('li[data-status="running"]')).toHaveCount(2);
        await expect(page.locator("#message")).toHaveCount(0);
        await expect(
            page.getByRole("timer", { name: "Elapsed time" }),
        ).toHaveText(/\d+m \d+s/);
        await page.locator(".installation-log > summary").click();
        const log = page.getByRole("log", { name: "Installation log" });
        await expect(log).toContainText("Downloading image 1/8");
        setup.transition("detail", "<img src=x onerror=alert(1)>");
        await expect(log).toContainText("<img src=x onerror=alert(1)>");
        await expect(log.locator("img")).toHaveCount(0);
        for (let index = 2; index <= 24; index++) {
            setup.transition("detail", `Downloading image layer ${index}/24`);
        }
        await expect(log).toContainText("Downloading image layer 24/24");
        const distanceFromLatest = () =>
            log.evaluate(
                (element) =>
                    element.scrollHeight -
                    element.clientHeight -
                    element.scrollTop,
            );
        await expect.poll(distanceFromLatest).toBeLessThan(2);
        await log.evaluate((element) => {
            element.scrollTop = 0;
        });
        await expect(page.getByLabel("Follow latest")).not.toBeChecked();
        setup.transition("detail", "Download completed");
        await expect(log).toContainText("Download completed");
        await expect(page.getByLabel("Follow latest")).not.toBeChecked();
        expect(await log.evaluate((element) => element.scrollTop)).toBe(0);
        await page.getByLabel("Follow latest").check();
        await expect.poll(distanceFromLatest).toBeLessThan(2);
        await page.reload();
        await page.locator(".installation-log > summary").click();
        await expect(log).toContainText("Downloading image 1/8");
        await expect(log).toContainText("Download completed");
        setup.transition("failed");
        await expect(log).toContainText("Installation stopped");
        setup.transition("begin");
        await expect(log).toContainText("Installation attempt 2 started");
        await page.setViewportSize({ width: 375, height: 800 });
        expect(
            await page
                .locator("body")
                .evaluate((element) => element.scrollWidth <= innerWidth),
        ).toBe(true);
    } finally {
        await setup.close();
    }
});

test("setup displays its bundled brand icon at desktop and mobile sizes", async ({
    page,
}) => {
    const setup = await startSetup();
    try {
        await page.goto(setup.url);
        const logo = page.locator(".brand-logo");
        await expect(logo).toBeVisible();
        await expect(logo).toHaveAttribute("src", "/setup/icon.svg");
        await expect
            .poll(() =>
                logo.evaluate(
                    (image: HTMLImageElement) =>
                        image.complete && image.naturalWidth > 0,
                ),
            )
            .toBe(true);
        for (const width of [1280, 375]) {
            await page.setViewportSize({ width, height: 800 });
            const bounds = await logo.boundingBox();
            expect(bounds!.width).toBe(34);
            expect(bounds!.height).toBeCloseTo((34 * 602.02) / 650, 1);
            expect(
                await page
                    .locator("body")
                    .evaluate((element) => element.scrollWidth <= innerWidth),
            ).toBe(true);
            const steps = await page.locator(".setup-steps").boundingBox();
            const log = await page.locator(".installation-log").boundingBox();
            expect(log!.y).toBeGreaterThan(steps!.y + steps!.height);
            expect(log!.x).toBeCloseTo(steps!.x, 0);
            await expect(page.locator(".setup-stage")).toHaveCount(4);
            await expect(
                page.getByRole("progressbar", {
                    name: "Installation progress",
                }),
            ).toBeVisible();
            await expect(page.locator(".installation-log")).not.toHaveAttribute(
                "open",
                "",
            );
        }
    } finally {
        await setup.close();
    }
});

test("setup shows live progress, failure and a successful retry across refreshes", async ({
    page,
}) => {
    const setup = await startSetup();
    try {
        await page.goto(setup.url);
        await expect(
            page.getByRole("heading", { name: "Getting things ready." }),
        ).toBeVisible();
        await expect(page.locator(".stage-tasks li")).toHaveCount(12);
        setup.transition("begin");
        setup.transition("start", "k3s");
        await expect(page.locator('li[data-status="running"]')).toContainText(
            "Install Kubernetes",
        );
        setup.transition("failed");
        await expect(page.getByRole("heading", { level: 1 })).toHaveText(
            "Setup needs attention.",
        );
        await expect(page.locator("#recovery")).toBeVisible();
        await page.reload();
        await expect(page.locator('li[data-status="failed"]')).toContainText(
            "Install Kubernetes",
        );
        setup.transition("begin");
        setup.transition("start", "k3s");
        setup.transition("complete");
        setup.transition("start", "cert-manager");
        await expect(page.locator('li[data-status="complete"]')).toContainText(
            "Install Kubernetes",
        );
        await expect(page.locator('li[data-status="running"]')).toContainText(
            "Install certificate manager",
        );
        await expect(page.locator("#recovery")).toBeHidden();
        expect(
            await page
                .locator("body")
                .evaluate((element) => element.scrollWidth <= innerWidth),
        ).toBe(true);
    } finally {
        await setup.close();
    }
});

test("a failed installation can be retried from the page with the Goblin password", async ({
    page,
}) => {
    const setup = await startSetup({ control: true });
    try {
        await page.goto(setup.url);
        setup.transition("begin");
        setup.transition("start", "k3s");
        setup.transition(
            "progress",
            "Downloading Kubernetes: 12.5 of 263.6 MB",
            "k3s",
        );
        setup.transition("failed");
        const recovery = page.locator("#recovery");
        await expect(recovery).toBeVisible();
        // The task keeps the activity it stopped during.
        await expect(page.locator('li[data-status="failed"]')).toContainText(
            "Downloading Kubernetes: 12.5 of 263.6 MB",
        );
        const password = recovery.getByLabel("Goblin password");
        const submit = recovery.getByRole("button", {
            name: "Retry installation",
        });
        await submit.click();
        await expect(recovery.getByRole("status")).toHaveText(
            "Enter your Goblin password to retry.",
        );
        await expect(password).toBeFocused();
        await password.fill("wrong password");
        await password.press("Enter");
        await expect(recovery.getByRole("status")).toHaveText(
            "That password didn’t match. Try again.",
        );
        await expect(submit).toBeEnabled();
        expect(await setup.systemctl()).toEqual([]);
        await password.fill(setupRetryPassword);
        await submit.click();
        await expect(recovery.getByRole("status")).toHaveText(
            "Retry started. Progress will update here.",
        );
        await expect(password).toHaveValue("");
        expect(await setup.systemctl()).toContain(
            "enable --now goblin-installer.service",
        );
        // The installer service starts the next attempt; the form leaves with the failure.
        setup.transition("begin");
        setup.transition("start", "k3s");
        await expect(recovery).toBeHidden();
        setup.transition("failed");
        await expect(recovery).toBeVisible();
        await expect(submit).toBeEnabled();
        await expect(page.locator("#retry-message")).toBeEmpty();
        expect(
            await page
                .locator("body")
                .evaluate((element) => element.scrollWidth <= innerWidth),
        ).toBe(true);
    } finally {
        await setup.close();
    }
});

test("setup tolerates the handoff gap and opens the Kubernetes application", async ({
    page,
}) => {
    const setup = await startSetup();
    try {
        await page.goto(setup.url);
        setup.transition("begin");
        setup.transition("start", "activate");
        setup.transition("handoff");
        await expect(page.locator('li[data-status="running"]')).toContainText(
            "Open Goblin",
        );
        await page.route("**/setup/status", (route) =>
            route.fulfill({ status: 404 }),
        );
        await page.route("**/readyz", (route) =>
            route.fulfill({ status: 503, json: { ready: false } }),
        );
        await expect(page.locator("#connection")).toHaveText(
            "Connecting to Goblin…",
        );
        await page.unroute("**/readyz");
        await page.route("**/readyz", (route) =>
            route.fulfill({ json: { ready: true } }),
        );
        await page.route(setup.url + "/", (route) =>
            route.fulfill({
                contentType: "text/html",
                body: "<h1>Goblin workspace</h1>",
            }),
        );
        await expect(page.getByRole("heading", { level: 1 })).toHaveText(
            "Goblin workspace",
            {
                timeout: 15000,
            },
        );
    } finally {
        await setup.close();
    }
});

test("a localhost visit moves to the configured origin and opens Goblin even if it misses the handoff update", async ({
    page,
}) => {
    const setup = await startSetup();
    try {
        const alias = setup.url.replace("127.0.0.1", "localhost");
        await page.goto(alias);
        await expect(page.locator(".stage-tasks li")).toHaveCount(12);
        setup.transition("begin");
        setup.transition("public-url", setup.url);
        await expect(page).toHaveURL(setup.url + "/");
        setup.transition("start", "verify");
        await expect(page.locator('li[data-status="running"]')).toContainText(
            "Check application readiness",
        );
        // The last observed step can still be readiness when setup releases its port.
        await page.route("**/setup/status", (route) =>
            route.fulfill({ status: 404 }),
        );
        await page.route("**/readyz", (route) =>
            route.fulfill({ status: 503, json: { ready: false } }),
        );
        await expect(page.locator("#connection")).toContainText("Reconnecting");
        await expect(page).toHaveURL(setup.url + "/");
        await page.unroute("**/readyz");
        await page.route("**/readyz", (route) =>
            route.fulfill({ json: { ready: true } }),
        );
        await page.route(setup.url + "/", (route) =>
            route.fulfill({
                contentType: "text/html",
                body: "<h1>Goblin workspace</h1>",
            }),
        );
        await expect(page.getByRole("heading", { level: 1 })).toHaveText(
            "Goblin workspace",
            {
                timeout: 15000,
            },
        );
    } finally {
        await setup.close();
    }
});
