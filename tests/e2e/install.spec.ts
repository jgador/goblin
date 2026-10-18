import { test, expect } from "@playwright/test";
import { readFile } from "node:fs/promises";

type Catalog = {
    schemaVersion: number;
    recommended: string | null;
    releases: { version: string }[];
};
async function installationPage(
    page: import("@playwright/test").Page,
    catalog: Catalog,
    unavailable = () => false,
) {
    await page.route("**/*", async (route) => {
        const request = new URL(route.request().url());
        if (request.origin !== "http://127.0.0.1:8798") return route.abort();
        const path = request.pathname;
        if (path === "/install/azure/releases.json")
            return route.fulfill(
                unavailable() ? { status: 503 } : { json: catalog },
            );
        const name =
            path === "/install/" ? "install/index.html" : path.slice(1);
        if (
            ![
                "install/index.html",
                "install/install.js",
                "install/install.css",
                "website.css",
                "assets/goblin.svg",
                "assets/github.svg",
                "assets/inter-18pt-medium.ttf",
                "assets/inter-18pt-semibold.ttf",
            ].includes(name)
        )
            return route.abort();
        await route.fulfill({
            contentType: name.endsWith("html")
                ? "text/html"
                : name.endsWith("js")
                  ? "text/javascript"
                  : name.endsWith("css")
                    ? "text/css"
                    : name.endsWith("svg")
                      ? "image/svg+xml"
                      : "font/ttf",
            body: await readFile(`website/${name}`),
        });
    });
}

test("recommended Goblin is selected and changing version changes both website-hosted Azure assets", async ({
    page,
}) => {
    const catalog: Catalog = {
        schemaVersion: 1,
        recommended: "0.1.0",
        releases: [{ version: "0.2.0-preview.1" }, { version: "0.1.0" }],
    };
    await installationPage(page, catalog);
    await page.goto("/install/");
    const version = page.getByRole("combobox", { name: /Goblin version/ });
    await expect(version).toContainText("0.1.0");
    await expect(page.locator("#version-badge")).toBeVisible();
    await expect(
        page.getByRole("link", { name: "Deploy to Azure" }),
    ).toHaveAttribute(
        "href",
        /0\.1\.0%2Fazuredeploy\.portal\.json.*0\.1\.0%2FcreateUiDefinition\.json/,
    );
    const link = decodeURIComponent(
        (await page.locator("#deploy").getAttribute("href"))!,
    );
    expect(
        link.match(
            /http:\/\/127\.0\.0\.1:8798\/install\/azure\/versions\/0\.1\.0\//g,
        ),
    ).toHaveLength(2);
    await expect(page.locator("#template")).toHaveAttribute(
        "href",
        "http://127.0.0.1:8798/install/azure/versions/0.1.0/azuredeploy.json",
    );
    await version.click();
    await expect(page.getByRole("listbox")).toBeVisible();
    await expect(page.getByRole("option", { selected: true })).toContainText(
        "0.1.0",
    );
    await page
        .getByRole("option", {
            name: "0.2.0-preview.1 Preview release",
            exact: true,
        })
        .click();
    await expect(page.getByRole("listbox")).toBeHidden();
    await expect(version).toBeFocused();
    await expect(page.locator("#version-badge")).toBeHidden();
    await expect(
        page.getByRole("link", { name: "Deploy to Azure" }),
    ).toHaveAttribute(
        "href",
        /0\.2\.0-preview\.1%2Fazuredeploy\.portal\.json.*0\.2\.0-preview\.1%2FcreateUiDefinition\.json/,
    );
    await expect(page.locator("#status")).toContainText("Preview release");
    await page.goto("/install/?version=0.1.0");
    await expect(version).toContainText("0.1.0");
    await page.goto("/install/?version=master");
    await expect(version).toContainText("Choose a version");
    await expect(page.locator("#deploy")).not.toHaveAttribute("href");
    catalog.recommended = null;
    await page.goto("/install/");
    await expect(version).toContainText("Choose a version");
    await expect(page.locator("#deploy")).not.toHaveAttribute("href");
});

test("installation remains disabled with no releases and recovers after an explicit retry", async ({
    page,
}) => {
    const catalog: Catalog = {
        schemaVersion: 1,
        recommended: null,
        releases: [],
    };
    let unavailable = false;
    await installationPage(page, catalog, () => unavailable);
    await page.goto("/install/");
    await expect(page.locator("#status")).toContainText("first Goblin release");
    await expect(page.getByRole("combobox")).toBeDisabled();
    await expect(page.locator("#deploy")).not.toHaveAttribute("href");
    unavailable = true;
    await page.reload();
    await expect(page.locator("#retry")).toBeVisible();
    await expect(page.locator("#deploy")).not.toHaveAttribute("href");
    unavailable = false;
    catalog.recommended = "0.1.0";
    catalog.releases = [{ version: "0.1.0" }];
    await page.getByRole("button", { name: "Try again" }).click();
    await expect(page.getByRole("combobox")).toContainText("0.1.0");
    await expect(page.locator("#deploy")).toHaveAttribute(
        "href",
        /portal\.azure\.com/,
    );
    await expect(page.locator("#retry")).toBeHidden();
});

test("release picker supports keyboard selection and dismisses without committing highlighted releases", async ({
    page,
}) => {
    await installationPage(page, {
        schemaVersion: 1,
        recommended: "0.1.0",
        releases: [{ version: "0.2.0-preview.1" }, { version: "0.1.0" }],
    });
    await page.goto("/install/");
    const version = page.getByRole("combobox", { name: /Goblin version/ });
    await expect(version).toBeEnabled();
    await version.focus();
    await version.press("ArrowDown");
    await expect(version).toHaveAttribute("aria-expanded", "true");
    await expect(version).toHaveAttribute(
        "aria-activedescendant",
        "version-option-1",
    );
    await version.press("Home");
    await expect(version).toHaveAttribute(
        "aria-activedescendant",
        "version-option-0",
    );
    await expect(version).toContainText("0.1.0");
    await version.press("Escape");
    await expect(version).toHaveAttribute("aria-expanded", "false");
    await expect(version).not.toHaveAttribute("aria-activedescendant");
    await expect(version).toContainText("0.1.0");
    await version.press("Home");
    await version.press("Enter");
    await expect(version).toContainText("0.2.0-preview.1");
    await expect(version).toBeFocused();
    await expect(page.getByRole("listbox")).toBeHidden();
    await version.press("Space");
    await version.press("End");
    await version.press("Space");
    await expect(version).toContainText("0.1.0");
    await version.pressSequentially("0.2");
    await expect(version).toHaveAttribute(
        "aria-activedescendant",
        "version-option-0",
    );
    await version.press("Enter");
    await expect(version).toContainText("0.2.0-preview.1");
    await version.press("ArrowDown");
    await version.press("End");
    await version.press("Tab");
    await expect(page.getByRole("listbox")).toBeHidden();
    await expect(page.locator("#deploy")).toBeFocused();
    await expect(version).toContainText("0.2.0-preview.1");
    await version.click();
    await page.getByRole("heading", { name: "Install on Azure" }).click();
    await expect(page.getByRole("listbox")).toBeHidden();
    await expect(version).toContainText("0.2.0-preview.1");
});

test("release picker stays within the mobile viewport and scrolls long catalogs", async ({
    page,
}) => {
    await page.setViewportSize({ width: 320, height: 720 });
    await installationPage(page, {
        schemaVersion: 1,
        recommended: "0.1.0-preview.20",
        releases: Array.from({ length: 20 }, (_, i) => ({
            version: `0.1.0-preview.${20 - i}`,
        })),
    });
    await page.goto("/install/");
    const version = page.getByRole("combobox");
    await expect(version).toBeEnabled();
    await version.click();
    const bounds = await page.getByRole("listbox").boundingBox();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(320);
    expect(bounds!.y).toBeGreaterThanOrEqual(0);
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(720);
    await version.press("End");
    await version.press("Enter");
    await expect(version).toContainText("0.1.0-preview.1");
    await expect(page.locator("#deploy")).toHaveAttribute(
        "href",
        /0\.1\.0-preview\.1%2Fazuredeploy\.portal\.json/,
    );
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= innerWidth,
        ),
    ).toBe(true);
});

test.describe("touch release picker", () => {
    test.use({
        isMobile: true,
        hasTouch: true,
        viewport: { width: 390, height: 844 },
    });

    test("tapping a release selects it and scrolling the menu keeps it open", async ({
        page,
    }) => {
        await installationPage(page, {
            schemaVersion: 1,
            recommended: "0.1.0-preview.20",
            releases: Array.from({ length: 20 }, (_, i) => ({
                version: `0.1.0-preview.${20 - i}`,
            })),
        });
        await page.goto("/install/");
        const version = page.getByRole("combobox");
        await expect(version).toBeEnabled();
        await version.tap();
        await expect(page.getByRole("listbox")).toBeVisible();
        await page
            .getByRole("option", {
                name: "0.1.0-preview.19 Preview release",
                exact: true,
            })
            .tap();
        await expect(version).toContainText("0.1.0-preview.19");
        await expect(version).toBeFocused();
        await expect(page.getByRole("listbox")).toBeHidden();
        await version.tap();
        const bounds = (await page.getByRole("listbox").boundingBox())!;
        const client = await page.context().newCDPSession(page);
        const point = (y: number) => [{ x: bounds.x + bounds.width / 2, y }];
        const bottom = bounds.y + bounds.height - 24;
        await client.send("Input.dispatchTouchEvent", {
            type: "touchStart",
            touchPoints: point(bottom),
        });
        for (let distance = 20; distance <= 120; distance += 20)
            await client.send("Input.dispatchTouchEvent", {
                type: "touchMove",
                touchPoints: point(bottom - distance),
            });
        await client.send("Input.dispatchTouchEvent", {
            type: "touchEnd",
            touchPoints: [],
        });
        await expect(page.getByRole("listbox")).toBeVisible();
        await expect
            .poll(() =>
                page
                    .getByRole("listbox")
                    .evaluate((element) => element.scrollTop),
            )
            .toBeGreaterThan(0);
        await client.detach();
    });
});

test("installation shows Azure available and future providers without deployment actions", async ({
    page,
}) => {
    await installationPage(page, {
        schemaVersion: 1,
        recommended: "0.1.0",
        releases: [{ version: "0.1.0" }],
    });
    await page.goto("/install/");
    const providers = page.getByRole("region", {
        name: "Your cloud, your workspace.",
    });
    await expect(
        providers.getByRole("heading", { name: "Microsoft Azure" }),
    ).toBeVisible();
    await expect(providers).toContainText("Available now");
    for (const name of [
        "Amazon Web Services",
        "Google Cloud Platform",
        "Other hosting providers",
    ]) {
        const provider = providers
            .getByRole("listitem")
            .filter({ has: page.getByRole("heading", { name, exact: true }) });
        await expect(provider).toContainText("Planned");
        await expect(provider.getByRole("button")).toHaveCount(0);
        await expect(provider.getByRole("link")).toHaveCount(0);
    }
    await expect(
        page.getByRole("link", { name: "Deploy to Azure" }),
    ).toHaveAttribute("href", /install%2Fazure%2Fversions/);
});
