import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { access, readFile, readdir, rm } from "node:fs/promises";
import { join } from "node:path";
import {
    bootstrap,
    fixtureDirectory,
    fakePassword,
} from "../support/azure-fixture.js";

test("a pull failure lets running tasks finish but starts no dependent task", async (t) => {
    const root = await fixtureDirectory("goblin-pull-failure-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(
        bootstrap(root, fakePassword, "ready", {
            state: "pull-failed",
            holdBuildUntilFailure: true,
        }),
        { status: 1 },
    );
    // The application build finished instead of being cancelled, so a retry reuses it.
    await access(join(root, "built-image"));
    const pid = Number(await readFile(join(root, "build-pid"), "utf8"));
    assert.throws(() => process.kill(pid, 0), { code: "ESRCH" });
    const progress = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    const status = (id: string) =>
        progress.steps.find((step: { id: string }) => step.id === id).status;
    assert.equal(progress.status, "failed");
    assert.equal(status("image"), "complete");
    assert.equal(status("prefetch"), "failed");
    for (const id of ["database", "import", "migrate", "deploy"])
        assert.equal(status(id), "waiting");
    assert.ok(
        progress.logs.some((entry: { message: string }) =>
            entry.message.startsWith("Finishing "),
        ),
    );
    // A failed image is attempted three times before the step fails.
    const pulls = (await readFile(join(root, "pull-events.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const first = pulls[0].image;
    assert.equal(
        pulls.filter(
            (event: { event: string; image: string }) =>
                event.event === "start" && event.image === first,
        ).length,
        3,
    );
    execFileSync("flock", [
        "-n",
        join(root, "var/lib/goblin/install/installer.lock"),
        "true",
    ]);
    await access(
        join(root, "var/lib/goblin/install/private/work/goblin-source.tar.gz"),
    );
});

test("failed or corrupt k3s downloads install nothing and start no dependent task", async (t) => {
    for (const state of ["k3s-download-failed", "k3s-corrupt"] as const) {
        const root = await fixtureDirectory("goblin-k3s-failed-");
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(bootstrap(root, fakePassword, "ready", { state }));
        await assert.rejects(access(join(root, "usr/local/bin/k3s")));
        await assert.rejects(access(join(root, "k3s-installed-version")));
        const cache = join(root, "var/cache/goblin/downloads");
        assert.deepEqual(
            (await readdir(cache)).filter((name) => !name.endsWith(".part")),
            [],
        );
        const progress = JSON.parse(
            await readFile(
                join(root, "var/lib/goblin/install/status.json"),
                "utf8",
            ),
        );
        const status = (id: string) =>
            progress.steps.find((step: { id: string }) => step.id === id)
                .status;
        assert.equal(progress.status, "failed");
        assert.equal(status("k3s"), "failed");
        for (const id of ["prefetch", "cert-manager", "sandbox", "import"])
            assert.equal(status(id), "waiting");
        if (state === "k3s-download-failed") {
            const attempts = (
                await readFile(join(root, "k3s-downloads.jsonl"), "utf8")
            )
                .trim()
                .split("\n");
            assert.equal(attempts.length, 12, "six attempts for each file");
        }
    }
});

test("installer cannot report ready when Agent Sandbox, Docker, the application build, pod, Headlamp, logging, or ingress fails", async (t) => {
    for (const state of [
        "sandbox-failed",
        "docker-failed",
        "build-failed",
        "not-ready",
        "headlamp-failed",
        "logs-failed",
        "collector-failed",
        "ingress-failed",
    ] as const) {
        const root = await fixtureDirectory("goblin-platform-failed-");
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(
            bootstrap(root, fakePassword, "ready", { state }),
            { status: 1 },
        );
        assert.equal(
            await readFile(
                join(root, "var/lib/goblin/bootstrap-status"),
                "utf8",
            ),
            "setup-ready\n",
        );
        assert.equal(
            JSON.parse(
                await readFile(
                    join(root, "var/lib/goblin/install/status.json"),
                    "utf8",
                ),
            ).status,
            "failed",
        );
    }
});

test("installer fails clearly if the API is ready but no node ever registers", async (t) => {
    const root = await fixtureDirectory("goblin-node-missing-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(bootstrap(root, fakePassword, "missing"), {
        status: 1,
    });
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const logs = await readFile(
        join(root, "var/log/goblin-installer.log"),
        "utf8",
    );
    assert.match(logs, /Kubernetes node did not register/);
    assert.match(logs, /Installation failed/);
    const calls: string[][] = (
        await readFile(join(root, "k3s-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        !calls.some((args) => args[1] === "wait" && args.includes("node")),
    );
    assert.ok(!calls.some((args) => args[1] === "apply"));
    await assert.rejects(access(join(root, "secret.json")));
});

test("installer does not continue when a registered node fails its readiness wait", async (t) => {
    const root = await fixtureDirectory("goblin-node-not-ready-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(bootstrap(root, fakePassword, "not-ready"), {
        status: 1,
    });
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const logs = await readFile(
        join(root, "var/log/goblin-installer.log"),
        "utf8",
    );
    assert.match(logs, /timed out waiting for the condition/);
    assert.match(logs, /Installation failed/);
    await assert.rejects(access(join(root, "secret.json")));
});
