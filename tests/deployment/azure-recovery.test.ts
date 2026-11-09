import { goblinctl } from "../support/goblinctl.js";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { access, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import {
    bootstrap,
    fixtureDirectory,
    fakePassword,
    origin,
} from "../support/azure-fixture.js";

test("NGINX publishes progress before certificate issuance and enables Goblin only after trusted TLS", async (t) => {
    const root = await fixtureDirectory("goblin-https-handoff-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword);
    const routes = (await readFile(join(root, "ingress-routes.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(
        routes.map(
            (route) =>
                route.metadata.annotations["nginx.org/websocket-services"],
        ),
        ["platform", undefined, "platform"],
    );
    for (const route of routes) {
        assert.equal(
            route.spec.rules[0].http.paths[0].backend.service.name,
            "cm-acme-http-solver",
        );
        assert.equal(
            route.metadata.annotations[
                "acme.cert-manager.io/http01-edit-in-place"
            ],
            "true",
        );
    }
    const events: { name: string; args: string[] }[] = (
        await readFile(join(root, "command-events.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const selected = (service: string) =>
        events.findIndex(
            (event) =>
                event.name === "k3s" &&
                event.args[1] === "patch" &&
                event.args[2] === "ingress" &&
                event.args.at(-1)?.includes(`\"name\":\"${service}\"`),
        );
    const published = events.findIndex(
        (event) =>
            event.name === "k3s" &&
            event.args[1] === "patch" &&
            event.args.at(-1)?.includes('"type":"LoadBalancer"'),
    );
    const certificate = events.findIndex(
        (event) =>
            event.name === "k3s" &&
            event.args.includes("certificate/goblin-public-tls") &&
            event.args.includes("--for=condition=Ready"),
    );
    const verified = events.findIndex(
        (event) =>
            event.name === "curl" &&
            event.args.includes(`${origin}/setup/healthz`),
    );
    assert.ok(
        selected("goblin-installation") >= 0 &&
            published > selected("goblin-installation"),
    );
    assert.ok(
        certificate > published &&
            verified > certificate &&
            selected("platform") > verified,
    );
    for (const event of events.filter((event) => event.name === "curl")) {
        assert.ok(
            !event.args.includes("--insecure") && !event.args.includes("-k"),
        );
    }
    const cluster = await readFile(
        join(root, "etc/rancher/k3s/config.yaml"),
        "utf8",
    );
    assert.match(cluster, /disable:\n  - traefik/);
});

test("unissued or untrusted HTTPS never completes installation and restores HTTP progress", async (t) => {
    for (const state of ["tls-failed", "tls-untrusted"] as const) {
        const root = await fixtureDirectory("goblin-https-failed-");
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(bootstrap(root, fakePassword, "ready", { state }));
        const progress = JSON.parse(
            await readFile(
                join(root, "var/lib/goblin/install/status.json"),
                "utf8",
            ),
        );
        assert.equal(progress.status, "failed");
        assert.equal(progress.currentStep, "https");
        assert.equal(
            await readFile(join(root, "ingress-mode"), "utf8"),
            "ClusterIP",
        );
        await assert.rejects(
            access(
                join(
                    root,
                    "etc/systemd/system/goblin-setup.service.d/listener.conf",
                ),
            ),
        );
        assert.ok(
            !progress.steps.some(
                (step: { id: string; status: string }) =>
                    step.id === "activate" && step.status === "complete",
            ),
        );
    }
});

test("Azure provisioning returns with a status page before any cluster or application work", async (t) => {
    const root = await fixtureDirectory("goblin-early-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "missing", { bootstrapOnly: true });
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const status = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    assert.equal(status.status, "waiting");
    assert.ok(
        status.steps.every(
            (step: { status: string }) => step.status === "waiting",
        ),
    );
    for (const file of [
        "k3s-requests.jsonl",
        "docker-requests.jsonl",
        "apt-requests.jsonl",
    ])
        await assert.rejects(access(join(root, file)));
    const services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        services.some(
            (args) => args.join(" ") === "enable --now goblin-setup.service",
        ),
    );
    assert.ok(
        services.some(
            (args) =>
                args.join(" ") === "enable --now goblin-installer.service",
        ),
    );
});

test("cert-manager readiness gates database setup and migrations before application startup", async (t) => {
    const databaseFiles = [
        "backend/src/Goblin.Web/appsettings.json",
        "deploy/postgres/postgres.yaml",
        "deploy/postgres/setup.sh",
    ];
    const before = await Promise.all(
        databaseFiles.map((path) => readFile(path)),
    );
    for (const state of ["ready", "cert-failed", "webhook-failed"] as const) {
        const root = await fixtureDirectory("goblin-certs-");
        t.after(() => rm(root, { recursive: true, force: true }));
        if (state === "ready") await bootstrap(root, fakePassword);
        else
            await assert.rejects(
                bootstrap(root, fakePassword, "ready", { state }),
                { status: 1 },
            );
        const calls: string[][] = (
            await readFile(join(root, "k3s-requests.jsonl"), "utf8")
        )
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        assert.ok(
            calls.some((args) =>
                args.includes("deployment/cert-manager-webhook"),
            ),
        );
        if (state !== "cert-failed")
            assert.ok(calls.some((args) => args.includes("--dry-run=server")));
        assert.equal(
            calls.some(
                (args) =>
                    args[0] === "kubectl" &&
                    args.includes("statefulset/goblin-postgres"),
            ),
            state === "ready",
        );
        if (state === "ready") {
            const migration = calls.findIndex((args) =>
                args.some((value) => value.includes("goblin-test-migration")),
            );
            const application = calls.findIndex(
                (args) =>
                    args.includes("-k") &&
                    args.some((value) => value.endsWith("/platform")),
            );
            assert.ok(migration >= 0, "the schema migration job ran");
            assert.ok(
                application > migration,
                "migrations finish before the application is deployed",
            );
        }
        const status = JSON.parse(
            await readFile(
                join(root, "var/lib/goblin/install/status.json"),
                "utf8",
            ),
        );
        assert.equal(status.status, state === "ready" ? "ready" : "failed");
        if (state !== "ready") {
            assert.equal(status.failedStep, "cert-manager");
            assert.equal(status.currentStep, "cert-manager");
            assert.ok(
                !calls.some(
                    (args) =>
                        args[1] === "create" &&
                        args[2] === "secret" &&
                        args.includes("goblin-postgres-admin"),
                ),
            );
        }
    }
    for (let i = 0; i < databaseFiles.length; i++)
        assert.ok(
            before[i].equals(await readFile(databaseFiles[i])),
            "database configuration is unchanged",
        );
});

test("a failed handoff restores private ingress and UI, then retry preserves credentials and completes", async (t) => {
    const root = await fixtureDirectory("goblin-retry-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(
        bootstrap(root, fakePassword, "ready", { state: "public-failed" }),
        { status: 1 },
    );
    const statePath = join(root, "var/lib/goblin/install/status.json");
    let state = JSON.parse(await readFile(statePath, "utf8"));
    assert.equal(state.status, "failed");
    assert.equal(state.currentStep, "https");
    assert.equal(
        await readFile(join(root, "ingress-mode"), "utf8"),
        "ClusterIP",
    );
    const before = await readFile(join(root, "secret.json"), "utf8");
    let services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(services.at(-1), [
        "enable",
        "--now",
        "goblin-setup.service",
    ]);
    assert.ok(
        !services.some(
            (args) =>
                args[0] === "disable" && args.includes("goblin-setup.service"),
        ),
    );
    await bootstrap(root, fakePassword, "ready", { resume: true });
    state = JSON.parse(await readFile(statePath, "utf8"));
    assert.equal(state.status, "ready");
    assert.equal(state.attempt, 2);
    assert.ok(
        state.steps.every(
            (step: { id: string; status: string; attempt: number }) =>
                step.status === "complete" &&
                step.attempt === (step.id === "activate" ? 1 : 2),
        ),
    );
    assert.equal(await readFile(join(root, "secret.json"), "utf8"), before);
    assert.equal(
        await readFile(join(root, "ingress-mode"), "utf8"),
        "LoadBalancer",
    );
    services = (await readFile(join(root, "systemctl-requests.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(services.at(-1), [
        "disable",
        "goblin-setup.service",
        "goblin-installer.service",
    ]);
    await assert.rejects(
        access(join(root, "var/lib/goblin/install/private/work")),
    );
    await access(join(root, "opt/goblin/bin/goblinctl"));
    await access(join(root, "var/log/goblin-installer.log"));
    const requests: string[][] = (
        await readFile(join(root, "curl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.equal(
        requests.filter((args) =>
            args.some((arg) => arg.includes("codeload.github.com")),
        ).length,
        1,
    );
});

test("the native downloader fits Azure Custom Script limits", async () => {
    const script = await readFile("deploy/azure/bootstrap.sh", "utf8");
    assert.ok(Buffer.byteLength(script) < 64 * 1024);
    assert.doesNotMatch(script, /python3|cargo |rustc /);
    assert.match(script, /sha256sum --check/);
    execFileSync("bash", ["-n"], { input: script });
});

test("a killed worker's recovery restores the UI from its persisted handoff marker", async (t) => {
    const root = await fixtureDirectory("goblin-killed-");
    t.after(() => rm(root, { recursive: true, force: true }));
    // A handoff starts only after the watched NGINX chart has been installed.
    await bootstrap(root, fakePassword);
    const statePath = join(root, "var/lib/goblin/install/status.json");
    for (const transition of [["begin"], ["start", "activate"], ["handoff"]])
        execFileSync(goblinctl, [
            "internal",
            "state",
            ...transition,
            "--path",
            statePath,
        ]);
    await writeFile(join(root, "var/lib/goblin/install/private/handoff"), "");
    await writeFile(join(root, "ingress-mode"), "LoadBalancer");
    await bootstrap(root, fakePassword, "ready", {
        resume: true,
        recovery: true,
    });
    const status = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    assert.equal(status.status, "failed");
    assert.equal(status.currentStep, "activate");
    assert.equal(
        await readFile(join(root, "ingress-mode"), "utf8"),
        "ClusterIP",
    );
    await assert.rejects(
        access(join(root, "var/lib/goblin/install/private/handoff")),
    );
    const services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(services.at(-1), [
        "enable",
        "--now",
        "goblin-setup.service",
    ]);
});

test("a concurrent installer cannot mutate state while the installation lock is held", async (t) => {
    const root = await fixtureDirectory("goblin-locked-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "ready", { bootstrapOnly: true });
    const statePath = join(root, "var/lib/goblin/install/status.json");
    const before = await readFile(statePath, "utf8");
    const locker = spawn(
        "flock",
        [
            join(root, "var/lib/goblin/install/installer.lock"),
            "sh",
            "-c",
            "printf locked; cat",
        ],
        { stdio: ["pipe", "pipe", "pipe"] },
    );
    const exited = once(locker, "exit");
    try {
        await once(locker.stdout, "data");
        await bootstrap(root, fakePassword, "ready", { resume: true });
        assert.equal(await readFile(statePath, "utf8"), before);
        await assert.rejects(access(join(root, "k3s-requests.jsonl")));
    } finally {
        locker.stdin.end();
        await exited;
    }
});
