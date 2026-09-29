import { goblinctl } from "../support/goblinctl.js";
import test from "node:test";
import assert from "node:assert/strict";
import { access, readFile, stat, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
import { startSetup } from "../support/setup.js";

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
