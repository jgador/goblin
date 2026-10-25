import { goblinctl } from "../support/goblinctl.js";
import test from "node:test";
import assert from "node:assert/strict";
import { access, readFile, rm, stat, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
import { setupRetryPassword, startSetup } from "../support/setup.js";

test("setup serves the official Goblin icon from its standalone bundle", async (t) => {
    const setup = await startSetup();
    t.after(() => setup.close());
    const html = await (await fetch(setup.url)).text();
    assert.match(
        html,
        /<img\b[^>]*class="brand-logo"[^>]*src="\/setup\/icon\.svg"/,
    );
    assert.match(html, /<link\b[^>]*rel="icon"[^>]*href="\/setup\/icon\.svg"/);
    assert.match(
        html,
        /<script\b[^>]*src="\/setup\/app\.js"[^>]*type="module"/,
    );
    for (const weight of ["medium", "semibold"]) {
        const font = await fetch(setup.url + `/setup/inter-${weight}.ttf`);
        assert.equal(font.status, 200);
        assert.equal(font.headers.get("content-type"), "font/ttf");
        assert.deepEqual(
            Buffer.from(await font.arrayBuffer()),
            await readFile(`assets/branding/fonts/inter-18pt-${weight}.ttf`),
        );
    }
    const license = await fetch(setup.url + "/setup/OFL.txt");
    assert.equal(
        await license.text(),
        await readFile("assets/branding/fonts/OFL.txt", "utf8"),
    );
    const contracts = await fetch(setup.url + "/setup/contract-values.js");
    assert.equal(contracts.status, 200);
    assert.equal(
        contracts.headers.get("content-type"),
        "text/javascript; charset=utf-8",
    );
    assert.deepEqual(
        Buffer.from(await contracts.arrayBuffer()),
        await readFile("deploy/azure/setup/contract-values.js"),
    );
    const response = await fetch(setup.url + "/setup/icon.svg");
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("content-type"), "image/svg+xml");
    assert.equal(response.headers.get("x-content-type-options"), "nosniff");
    assert.deepEqual(
        Buffer.from(await response.arrayBuffer()),
        await readFile("assets/branding/svg/icon-light.svg"),
    );
});

test("the standalone UI serves only status and assets, with no mutation or file access", async (t) => {
    const setup = await startSetup();
    t.after(() => setup.close());
    await writeFile(
        join(setup.root, "owner-password"),
        "private-test-verifier",
    );
    for (const path of [
        "/owner-password",
        "/setup/../owner-password",
        "/setup/status/../../owner-password",
        "/logs",
        "/readyz",
        "/setup/retry",
    ]) {
        const response = await fetch(setup.url + path);
        assert.equal(response.status, 404);
        assert.ok(!(await response.text()).includes("private-test-verifier"));
    }
    assert.equal(
        (await fetch(setup.url + "/setup/status", { method: "POST" })).status,
        405,
    );
    // Without the root helper, the page cannot start a retry.
    const unavailable = await fetch(setup.url + "/setup/retry", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ password: "private-test-verifier" }),
    });
    assert.equal(unavailable.status, 503);
    assert.deepEqual(await unavailable.json(), { result: "unavailable" });
    let response = await fetch(setup.url + "/setup/status");
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.equal((await response.json()).status, "waiting");
    setup.transition("begin");
    setup.transition("start", "k3s");
    setup.transition("failed");
    response = await fetch(setup.url + "/setup/status");
    const status = await response.json();
    assert.equal(status.status, "failed");
    assert.equal(
        status.steps.find((step: { id: string }) => step.id === "k3s").status,
        "failed",
    );
    setup.transition("begin");
    setup.transition("start", "k3s");
    const retried = JSON.parse(await readFile(setup.path, "utf8"));
    assert.equal(retried.attempt, 2);
    assert.equal(
        retried.steps.find((step: { id: string }) => step.id === "k3s").attempt,
        2,
    );
    assert.equal((await stat(setup.path)).mode & 0o777, 0o644);
    const html = await fetch(setup.url);
    assert.match(
        html.headers.get("content-security-policy")!,
        /frame-ancestors 'none'/,
    );
    assert.match(await html.text(), /Getting things ready/);
});

test("local setup health cannot report ready when the public port is occupied", async (t) => {
    const setup = await startSetup();
    t.after(() => setup.close());
    const socket = join(setup.root, "health.sock");
    assert.throws(
        () =>
            execFileSync(
                goblinctl,
                [
                    "internal",
                    "serve",
                    "--state",
                    setup.path,
                    "--host",
                    "127.0.0.1",
                    "--port",
                    new URL(setup.url).port,
                    "--health-socket",
                    socket,
                ],
                { stdio: ["ignore", "pipe", "pipe"] },
            ),
        /Cannot bind setup listener/,
    );
    await assert.rejects(access(socket));
});

test("setup streams retained progress and resumes by event ID without serving private logs", async (t) => {
    const setup = await startSetup();
    t.after(() => setup.close());
    await writeFile(
        join(setup.root, "goblin-installer.log"),
        "secret-command-output",
    );
    setup.transition("begin");
    setup.transition("start", "image");
    setup.transition("detail", "Installing frontend dependencies");
    const response = await fetch(setup.url + "/setup/events");
    assert.equal(response.headers.get("content-type"), "text/event-stream");
    assert.equal(response.headers.get("cache-control"), "no-store");
    const batch = await response.text();
    assert.match(batch, /Installing frontend dependencies/);
    assert.ok(!batch.includes("secret-command-output"));
    const id = batch.match(/^id: (.+)$/m)![1];
    const heartbeat = await fetch(setup.url + "/setup/events", {
        headers: { "Last-Event-ID": id },
    });
    assert.match(await heartbeat.text(), /keepalive/);
    setup.transition("start", "k3s");
    const resumed = await (
        await fetch(setup.url + "/setup/events", {
            headers: { "Last-Event-ID": id },
        })
    ).text();
    assert.match(resumed, /Install Kubernetes started/);
    assert.notEqual(resumed.match(/^id: (.+)$/m)![1], id);
    assert.equal(
        (await fetch(setup.url + "/setup/events", { method: "POST" })).status,
        405,
    );
});

function retry(url: string, body: unknown, type = "application/json") {
    return fetch(url + "/setup/retry", {
        method: "POST",
        headers: { "Content-Type": type },
        body: typeof body === "string" ? body : JSON.stringify(body),
    });
}

test("the setup page retries only a failed installation, and only with the owner password", async (t) => {
    const setup = await startSetup({ control: true });
    t.after(() => setup.close());
    let response = await retry(setup.url, { password: setupRetryPassword });
    assert.equal(response.status, 409);
    assert.deepEqual(await response.json(), { result: "not-failed" });
    setup.transition("begin");
    setup.transition("start", "k3s");
    setup.transition("failed");
    // A JSON body forces a CORS preflight, which the server never grants.
    for (const [body, type] of [
        [{ password: setupRetryPassword }, "text/plain"],
        ["not json", "application/json"],
        [{ password: setupRetryPassword, extra: true }, "application/json"],
    ] as const) {
        response = await retry(setup.url, body, type);
        assert.equal(response.status, 400);
    }
    response = await retry(setup.url, { password: "wrong password" });
    assert.equal(response.status, 401);
    assert.deepEqual(await response.json(), { result: "unauthorized" });
    assert.deepEqual(await setup.systemctl(), []);
    await writeFile(join(setup.root, "installer-active"), "");
    response = await retry(setup.url, { password: setupRetryPassword });
    assert.equal(response.status, 409);
    assert.deepEqual(await response.json(), { result: "running" });
    await rm(join(setup.root, "installer-active"));
    response = await retry(setup.url, { password: setupRetryPassword });
    assert.equal(response.status, 202);
    assert.deepEqual(await response.json(), { result: "started" });
    assert.deepEqual((await setup.systemctl()).slice(-2), [
        "is-active --quiet goblin-installer.service",
        "enable --now goblin-installer.service",
    ]);
    // A correct password clears earlier failures.
    await assert.rejects(access(join(setup.root, "retry-throttle.json")));
    const state = JSON.parse(await readFile(setup.path, "utf8"));
    assert.ok(!JSON.stringify(state).includes(setupRetryPassword));
});

test("repeated wrong passwords lock retries from the setup page", async (t) => {
    const setup = await startSetup({ control: true });
    t.after(() => setup.close());
    setup.transition("begin");
    setup.transition("failed");
    for (let attempt = 0; attempt < 5; attempt++)
        assert.equal(
            (await retry(setup.url, { password: `wrong ${attempt}` })).status,
            401,
        );
    const throttle = join(setup.root, "retry-throttle.json");
    assert.equal((await stat(throttle)).mode & 0o777, 0o600);
    const response = await retry(setup.url, { password: setupRetryPassword });
    assert.equal(response.status, 429);
    assert.deepEqual(await response.json(), { result: "throttled" });
    assert.deepEqual(await setup.systemctl(), []);
});
