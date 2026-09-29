import { test, expect } from "@playwright/test";
import { readFile } from "node:fs/promises";

test("recommended Goblin is selected and changing version changes both Azure assets", async ({
    page,
}) => {
    const catalog = {
        schemaVersion: 1,
        recommended: "0.1.0",
        releases: [{ version: "0.2.0-preview.1" }, { version: "0.1.0" }],
    };
    await page.route("**/release-install/**", async (route) => {
        const path = new URL(route.request().url()).pathname.replace(
            "/release-install/",
            "",
        );
        if (path === "releases.json") return route.fulfill({ json: catalog });
        if (path === "assets/icon.svg")
            return route.fulfill({
                contentType: "image/svg+xml",
                body: await readFile("assets/branding/svg/icon-light.svg"),
            });
        const name = path || "index.html";
        if (!["index.html", "app.js", "style.css"].includes(name))
            return route.abort();
        await route.fulfill({
            contentType: name.endsWith("html")
                ? "text/html"
                : name.endsWith("js")
                  ? "text/javascript"
                  : "text/css",
            body: await readFile(`deploy/install/${name}`),
        });
    });
    await page.goto("/release-install/");
    const version = page.getByLabel("Goblin version");
    await expect(version).toHaveValue("0.1.0");
    await expect(
        page.getByRole("link", { name: "Deploy to Azure" }),
    ).toHaveAttribute(
        "href",
        /0\.1\.0%2Fazuredeploy\.portal\.json.*0\.1\.0%2FcreateUiDefinition\.json/,
    );
    await version.selectOption("0.2.0-preview.1");
    await expect(
        page.getByRole("link", { name: "Deploy to Azure" }),
    ).toHaveAttribute(
        "href",
        /0\.2\.0-preview\.1%2Fazuredeploy\.portal\.json.*0\.2\.0-preview\.1%2FcreateUiDefinition\.json/,
    );
    await page.goto("/release-install/?version=0.1.0");
    await expect(version).toHaveValue("0.1.0");
    await page.goto("/release-install/?version=master");
    await expect(version).toHaveValue("");
    await expect(page.locator("#deploy")).not.toHaveAttribute("href");
    catalog.recommended = "";
    await page.goto("/release-install/");
    await expect(version).toHaveValue("");
    await expect(page.locator("#deploy")).not.toHaveAttribute("href");
});
