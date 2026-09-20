import assert from "node:assert/strict";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createServer } from "node:net";
import { once } from "node:events";
import { createHash } from "node:crypto";
import { chromium, expect } from "@playwright/test";
import { startBackend, writePasswordHash } from "../tests/support/backend.js";

// Opt-in integration: a real, pinned Headlamp backed by a real cluster. No saved
// Goblin/OpenAI/GitHub credentials are used. The Azure hostname is resolved locally
// to exercise the browser's actual Host/Origin behavior without Azure resources.
const headlampUrl = process.env.GOBLIN_TEST_HEADLAMP_URL;
if (!headlampUrl)
    throw new Error(
        "Set GOBLIN_TEST_HEADLAMP_URL to the private Headlamp origin (for example a localhost port-forward).",
    );
const azureHost = "goblin-headlamp-test.southeastasia.cloudapp.azure.com";
const browser = await chromium.launch({
    args: [
        `--host-resolver-rules=MAP ${azureHost} 127.0.0.1`,
        "--no-proxy-server",
    ],
});
await mkdir("test-results", { recursive: true });
try {
    for (const hostname of ["127.0.0.1", azureHost]) {
        const dataDir = await mkdtemp(
            join(tmpdir(), "goblin-headlamp-runtime-"),
        );
        const portFinder = createServer().listen(0, "127.0.0.1");
        await once(portFinder, "listening");
        const { port } = portFinder.address() as { port: number };
        await new Promise<void>((resolve) => portFinder.close(() => resolve()));
        const publicOrigin = `http://${hostname}:${port}`;
        const passwordHashFile = join(dataDir, "owner-password");
        await writePasswordHash(passwordHashFile, "headlamp-runtime-test");
        const app = await startBackend({
            dataDir,
            passwordHashFile,
            headlampUrl,
            publicOrigin,
            allowInsecureHttp: true,
            listenUrl: `http://127.0.0.1:${port}`,
        });
        const context = await browser.newContext();
        const page = await context.newPage();
        const errors: string[] = [];
        page.on("pageerror", (error) => errors.push(error.message));
        page.on("websocket", (socket) => {
            if (socket.url().includes("/log?container=headlamp"))
                socket.on("socketerror", (error) => errors.push(error));
        });
        page.on("console", (message) => {
            if (
                message.type() === "error" &&
                /Content Security Policy|Refused to/.test(message.text())
            )
                errors.push(message.text());
        });
        try {
            await page.goto(publicOrigin + "/headlamp/");
            await expect(page).toHaveURL(publicOrigin + "/?returnTo=headlamp");
            await page
                .getByLabel("Goblin password", { exact: true })
                .fill("headlamp-runtime-test");
            await page.getByRole("button", { name: "Open workspace" }).click();
            await expect(page).toHaveURL(/\/headlamp\//);
            await expect(
                page.getByText("goblin", { exact: true }).first(),
            ).toBeVisible({ timeout: 30_000 });
            const check = await page.evaluate(async () => {
                const read = async (path: string) => {
                    const response = await fetch(
                        "/headlamp/clusters/goblin" + path,
                    );
                    return {
                        status: response.status,
                        body: await response.json(),
                    };
                };
                const review = await fetch(
                    "/headlamp/clusters/goblin/apis/authorization.k8s.io/v1/selfsubjectaccessreviews",
                    {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({
                            apiVersion: "authorization.k8s.io/v1",
                            kind: "SelfSubjectAccessReview",
                            spec: {
                                resourceAttributes: {
                                    namespace: "goblin",
                                    resource: "pods",
                                    verb: "delete",
                                },
                            },
                        }),
                    },
                );
                return {
                    namespaces: await read("/api/v1/namespaces"),
                    pods: await read("/api/v1/namespaces/goblin/pods"),
                    secrets: await read("/api/v1/namespaces/goblin/secrets"),
                    review: await review.json(),
                };
            });
            assert.equal(check.namespaces.status, 200);
            assert.equal(check.pods.status, 200);
            assert.ok(
                check.pods.body.items.some(
                    (pod: { metadata: { name: string } }) =>
                        pod.metadata.name === "goblin-auth",
                ),
            );
            assert.equal(check.secrets.status, 403);
            assert.equal(check.review.status.allowed, false);
            const logPod = check.pods.body.items.find(
                (pod: { metadata: { labels?: { app?: string } } }) =>
                    pod.metadata.labels?.app === "goblin-headlamp",
            );
            assert.ok(logPod);
            const streamed = await page.evaluate(
                (pod: string) =>
                    new Promise<string>((resolve) => {
                        const socket = new WebSocket(
                            `ws://${location.host}/headlamp/clusters/goblin/api/v1/namespaces/goblin/pods/${pod}/log?container=headlamp&follow=true&tailLines=1`,
                            ["base64.binary.k8s.io"],
                        );
                        const done = (result: string) => {
                            clearTimeout(timer);
                            socket.close();
                            resolve(result);
                        };
                        const timer = setTimeout(() => done("timeout"), 10_000);
                        socket.onmessage = (event) => {
                            // Kubernetes sends an empty initial frame before the log data.
                            if (
                                typeof event.data === "string" &&
                                event.data.length > 0
                            )
                                done("received");
                        };
                        socket.onerror = () => done("WebSocket error");
                    }),
                logPod.metadata.name,
            );
            assert.equal(
                streamed,
                "received",
                `Pod logs should stream over WebSocket through Goblin: ${errors.join("; ")}`,
            );
            // Verify the CSP hashes against this pinned Headlamp's actual HTML.
            const { html, csp } = await page.evaluate(async () => {
                const response = await fetch("/headlamp/");
                return {
                    html: await response.text(),
                    csp: response.headers.get("content-security-policy")!,
                };
            });
            for (const [, script] of html.matchAll(
                /<script\b[^>]*>([\s\S]*?)<\/script>/g,
            )) {
                if (!script) continue;
                const hash = createHash("sha256")
                    .update(script)
                    .digest("base64");
                assert.ok(csp.includes(`'sha256-${hash}'`));
            }
            await page.goto(publicOrigin + "/headlamp/c/goblin/pods");
            await expect(
                page.getByText("goblin-auth", { exact: true }).first(),
            ).toBeVisible({ timeout: 30_000 });
            await page.reload();
            await expect(
                page.getByText("goblin-auth", { exact: true }).first(),
            ).toBeVisible({ timeout: 30_000 });
            await page.screenshot({
                path: `test-results/headlamp-${hostname === azureHost ? "azure-host" : "local"}.png`,
                fullPage: true,
            });
            assert.deepEqual(errors, []);
            await page.evaluate(() =>
                fetch("/api/session/lock", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: "{}",
                }),
            );
            await page.reload();
            await expect(page).toHaveURL(publicOrigin + "/?returnTo=headlamp");
            console.log(
                `PASS: ${hostname} — login, cluster resources, read-only permissions, log streaming, subpath refresh, CSP, logout`,
            );
        } finally {
            await context.close();
            await app.close();
            await rm(dataDir, { recursive: true, force: true });
        }
    }
} finally {
    await browser.close();
}
