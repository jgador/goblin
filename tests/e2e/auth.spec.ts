import { test, expect } from "@playwright/test";
import { writeFile, unlink } from "node:fs/promises";
import { resolve } from "node:path";

test("owner can use a one-character deployment password, connect ChatGPT, and switch to an API key", async ({
    page,
}) => {
    const errors: string[] = [];
    const checks: unknown[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    page.on("request", (request) => {
        if (request.url().endsWith("/api/prompt"))
            checks.push(request.postDataJSON());
    });
    const checkGate = Promise.withResolvers<void>();
    await page.route(
        "**/api/prompt",
        async (route) => {
            const response = await route.fetch();
            await checkGate.promise;
            await route.fulfill({ response });
        },
        { times: 1 },
    );
    await page.goto("/?settings=codex");
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    await expect(
        page.getByLabel("Goblin password", { exact: true }),
    ).toHaveAttribute("autocomplete", "current-password");
    await expect(
        page.getByLabel("Goblin password", { exact: true }),
    ).toHaveValue("");
    await page
        .getByLabel("Goblin password", { exact: true })
        .fill("incorrect-password");
    await page.getByRole("button", { name: "Open workspace" }).click();
    await expect(page.getByRole("alert")).toContainText(
        "The Goblin password is incorrect.",
    );
    await expect(
        page.getByLabel("Goblin password", { exact: true }),
    ).toHaveValue("");
    await page.getByLabel("Goblin password", { exact: true }).fill("a");
    await page.getByRole("button", { name: "Open workspace" }).click();
    await expect(
        page.getByRole("heading", { name: "Choose how to sign in" }),
    ).toBeVisible();
    await page.screenshot({
        path: "test-results/authentication-connect.png",
        fullPage: true,
    });

    await page.getByRole("button", { name: "Continue with ChatGPT" }).click();
    await expect(page.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await expect(
        page.getByRole("link", { name: "Open OpenAI sign-in" }),
    ).toHaveAttribute("href", "https://auth.openai.com/codex/device");
    await page.reload();
    await expect(page.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Cancel sign-in" }).click();
    await expect(
        page.getByRole("heading", { name: "Choose how to sign in" }),
    ).toBeVisible();

    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole("button", { name: "Continue with ChatGPT" }).click();
    await expect(page.getByText("ABCD-1234", { exact: true })).toBeVisible();
    await page.screenshot({
        path: "test-results/authentication-device-mobile.png",
        fullPage: true,
    });
    const completion = resolve(".goblin-browser-test/codex/complete-login");
    await writeFile(completion, "complete");
    await expect(
        page.getByText("owner@example.test · plus", { exact: true }),
    ).toBeVisible({ timeout: 8000 });
    await unlink(completion);
    await expect(page.locator("#connection-status")).toHaveText(
        "Verifying connection…",
    );
    await expect(page.locator("#check-connection-button")).toBeDisabled();
    await expect(
        page.getByText("Connected", { exact: true }),
    ).not.toBeVisible();
    await expect(page.getByRole("dialog").locator("textarea")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Send prompt" })).toHaveCount(
        0,
    );
    checkGate.resolve();
    await expect(page.locator("#connection-status")).toHaveText("Connected");
    await expect(page.locator("#connection-description")).toHaveText(
        "Your ChatGPT account is ready to use.",
    );
    expect(checks).toEqual([{ prompt: "Reply with only OK." }]);
    await expect(page.locator("body")).not.toContainText(
        "Hello from the connected account.",
    );
    await page.screenshot({
        path: "test-results/connection-status-mobile.png",
        fullPage: true,
    });

    // Exercise a real failed turn, including the backend's sanitization of partial replies.
    await page.route(
        "**/api/prompt",
        async (route) => {
            const response = await route.fetch({
                postData: { prompt: "Trigger a simulated failure." },
            });
            await route.fulfill({ response });
        },
        { times: 1 },
    );
    await page.getByRole("button", { name: "Check again" }).click();
    await expect(page.locator("#connection-status")).toHaveText(
        "Sign-in expired",
    );
    await expect(page.locator("#connection-check")).toHaveAttribute(
        "data-status",
        "failed",
    );
    await expect(
        page.getByText("Connected", { exact: true }),
    ).not.toBeVisible();
    await expect(page.locator("body")).not.toContainText("THIS-MUST-NOT-LEAK");
    await expect(page.locator("body")).not.toContainText(
        "Hello from the connected account.",
    );
    await page.screenshot({
        path: "test-results/connection-failed-mobile.png",
        fullPage: true,
    });
    await page.getByRole("button", { name: "Retry check" }).click();
    await expect(page.locator("#connection-status")).toHaveText("Connected");
    expect(checks).toHaveLength(3);
    await page.getByRole("button", { name: "Lock workspace" }).click();
    await expect(
        page.getByRole("heading", { name: "Open your workspace" }),
    ).toBeVisible();
    await expect(page.locator("#account-detail")).toHaveCount(0);
    await page.getByLabel("Goblin password", { exact: true }).fill("a");
    await page.getByRole("button", { name: "Open workspace" }).click();
    await expect(
        page.getByText("owner@example.test · plus", { exact: true }),
    ).toBeVisible();
    await expect(page.locator("#connection-status")).toHaveText("Connected");
    expect(checks).toHaveLength(4);
    await page.getByRole("button", { name: "Disconnect Codex" }).click();
    await expect(
        page.getByRole("heading", { name: "Choose how to sign in" }),
    ).toBeVisible();

    await page
        .getByText("Use an OpenAI API key instead", { exact: false })
        .click();
    await page
        .getByLabel("OpenAI API key", { exact: true })
        .fill("sk-invalid-fake-browser-test-key");
    await page.getByRole("button", { name: "Connect API key" }).click();
    await expect(page.getByRole("dialog").getByRole("alert")).toContainText(
        "OpenAI rejected this API key",
    );
    await expect(
        page.getByLabel("OpenAI API key", { exact: true }),
    ).toHaveValue("");
    await page
        .getByLabel("OpenAI API key", { exact: true })
        .fill("sk-valid-fake-browser-test-key");
    await page.getByRole("button", { name: "Connect API key" }).click();
    await expect(
        page.getByText("Billed to your OpenAI Platform project", {
            exact: true,
        }),
    ).toBeVisible();
    await expect(page.locator("#connection-status")).toHaveText("Connected");
    await expect(page.locator("#connection-description")).toHaveText(
        "Your OpenAI API key is ready to use.",
    );
    expect(checks).toHaveLength(5);
    expect(
        await page.evaluate(() => ({
            local: localStorage.length,
            session: sessionStorage.length,
        })),
    ).toEqual({ local: 0, session: 0 });
    await expect(page.locator("body")).not.toContainText(
        "sk-valid-fake-browser-test-key",
    );
    expect(
        await page.evaluate(
            () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
    ).toBe(true);
    expect(errors).toEqual([]);
});
