import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import {
    access,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    symlink,
    writeFile,
} from "node:fs/promises";
import { join } from "node:path";
import { test } from "node:test";
import { environmentVariables as Env } from "../config/environment.mts";

const root = new URL("../", import.meta.url).pathname;
const shellPath =
    "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
const quote = (value: string) => "'" + value.replaceAll("'", "'\\''") + "'";
async function fixture(t: { after: (fn: () => Promise<void>) => void }) {
    const artifacts = join(root, ".artifacts/k3s-reset-tests");
    await mkdir(artifacts, { recursive: true });
    const directory = await mkdtemp(join(artifacts, "fixture-"));
    t.after(() => rm(directory, { recursive: true, force: true }));
    const repo = join(directory, "repo");
    const system = join(directory, "system");
    const bin = join(directory, "bin");
    await mkdir(repo);
    await mkdir(bin);
    execFileSync("git", ["init", "-q", repo]);
    for (const name of [
        ".goblin-platform",
        ".goblin-local",
        ".goblin-secrets",
        ".goblin-browser-test",
        ".artifacts/playwright",
    ]) {
        await mkdir(join(repo, name), { recursive: true });
        await writeFile(join(repo, name, ".gitkeep"), "");
        await writeFile(join(repo, name, "state"), "fixture state");
    }
    execFileSync("git", ["-C", repo, "add", "."]);
    execFileSync("git", [
        "-C",
        repo,
        "rm",
        "--cached",
        "--quiet",
        "--",
        ".goblin-platform/state",
        ".goblin-local/state",
        ".goblin-secrets/state",
        ".goblin-browser-test/state",
        ".artifacts/playwright/state",
    ]);
    await writeFile(
        join(repo, ".goblin-secrets/owner-password"),
        "fixture verifier",
    );
    await mkdir(join(system, "proc/self"), { recursive: true });
    await writeFile(
        join(system, "proc/version"),
        "Linux Microsoft WSL fixture",
    );
    await writeFile(
        join(system, "proc/self/mountinfo"),
        "1 0 0:1 / / rw - ext4 fixture rw\n",
    );
    await mkdir(join(system, "var/lib/goblin/local-test"), { recursive: true });
    const owner = join(system, "var/lib/goblin/local-test/config.json");
    await writeFile(owner, JSON.stringify({ mode: "direct", repo }));
    for (const name of [
        "etc/rancher/k3s",
        "var/lib/rancher/k3s",
        "var/lib/kubelet",
        "opt/goblin/setup",
        "opt/goblin/bin",
        "etc/systemd/system",
        "usr/local/bin",
        "var/log",
    ]) {
        await mkdir(join(system, name), { recursive: true });
    }
    const log = join(directory, "systemctl.log");
    await writeFile(
        join(bin, "systemctl"),
        "#!/bin/bash\nprintf '%s\\n' \"$*\" >> " + quote(log) + "\n",
        { mode: 0o755 },
    );
    await writeFile(join(bin, "id"), "#!/bin/bash\nprintf '0\\n'\n", {
        mode: 0o755,
    });
    const uninstall = join(directory, "uninstalled");
    await writeFile(
        join(system, "usr/local/bin/k3s-uninstall.sh"),
        "#!/bin/bash\ntouch " +
            quote(uninstall) +
            '\nrm -f -- "$0" ' +
            quote(join(system, "etc/systemd/system/k3s.service")) +
            "\n",
        { mode: 0o755 },
    );
    await writeFile(
        join(system, "opt/goblin/bin/retained-cli"),
        "fixture tooling",
    );
    for (const name of [
        "goblin-installer.service",
        "goblin-setup.service",
        "goblin-local.socket",
        "goblin-local-postgres.socket",
        "k3s.service",
    ]) {
        await writeFile(
            join(system, "etc/systemd/system", name),
            "fixture unit",
        );
    }
    await writeFile(
        join(system, "var/log/goblin-installer.log"),
        "fixture log",
    );
    const overrides = join(system, "etc/systemd/system/goblin-setup.service.d");
    await mkdir(overrides);
    await writeFile(join(overrides, "listener.conf"), "owned listener");
    await writeFile(join(overrides, "custom.conf"), "operator configuration");
    const run = () =>
        spawnSync("bash", ["-s", "--", "--yes", "--repo", repo, system], {
            encoding: "utf8",
            input: readFileSync(join(root, "scripts/k3s-reset.sh"), "utf8"),
            env: { [Env.PATH.name]: bin + ":" + shellPath },
            cwd: repo,
        });
    return { directory, repo, system, owner, uninstall, log, run };
}
const exists = async (path: string) =>
    access(path).then(
        () => true,
        () => false,
    );

test("full reset clears owned data and password while retaining tracked files and tooling", async (t) => {
    const f = await fixture(t);
    const result = f.run();
    assert.equal(result.status, 0, result.stderr);
    assert.equal(await exists(f.uninstall), true);
    assert.equal(await exists(f.owner), false);
    assert.equal(
        await exists(join(f.repo, ".goblin-secrets/owner-password")),
        false,
    );
    assert.equal(await exists(join(f.repo, ".goblin-platform/state")), false);
    assert.equal(
        await exists(join(f.repo, ".artifacts/playwright/state")),
        false,
    );
    assert.equal(await exists(join(f.repo, ".goblin-secrets/.gitkeep")), true);
    assert.equal(
        await exists(join(f.system, "opt/goblin/bin/retained-cli")),
        true,
    );
    assert.equal(
        await exists(
            join(
                f.system,
                "etc/systemd/system/goblin-setup.service.d/listener.conf",
            ),
        ),
        false,
    );
    assert.equal(
        await exists(
            join(
                f.system,
                "etc/systemd/system/goblin-setup.service.d/custom.conf",
            ),
        ),
        true,
    );
    const calls = (await readFile(f.log, "utf8")).split("\n");
    assert.ok(
        calls.indexOf("disable --now goblin-installer.service") <
            calls.indexOf("disable --now goblin-setup.service"),
    );
    const repeated = f.run();
    assert.equal(repeated.status, 0, repeated.stderr);
});

test("reset refuses another checkout before stopping services or deleting data", async (t) => {
    const f = await fixture(t);
    await writeFile(
        f.owner,
        JSON.stringify({ mode: "direct", repo: join(f.directory, "other") }),
    );
    const result = f.run();
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /another checkout/);
    assert.equal(await exists(f.log), false);
    assert.equal(
        await exists(join(f.repo, ".goblin-secrets/owner-password")),
        true,
    );
});

test("reset refuses mounted runtime storage and symlinked roots", async (t) => {
    const f = await fixture(t);
    await writeFile(
        join(f.system, "proc/self/mountinfo"),
        `1 0 0:1 / / rw - ext4 fixture rw\n2 1 0:2 / ${f.repo}/.goblin-platform rw - ext4 fixture rw\n`,
    );
    assert.match(f.run().stderr, /contains a mount/);
    assert.equal(await exists(f.log), false);
    await writeFile(
        join(f.system, "proc/self/mountinfo"),
        "1 0 0:1 / / rw - ext4 fixture rw\n",
    );
    await rm(join(f.repo, ".goblin-platform"), { recursive: true });
    await symlink(
        join(f.system, "opt/goblin/bin"),
        join(f.repo, ".goblin-platform"),
    );
    assert.match(f.run().stderr, /symbolic link/);
    assert.equal(
        await exists(join(f.system, "opt/goblin/bin/retained-cli")),
        true,
    );
});

test("reset refuses a tracked password verifier", async (t) => {
    const f = await fixture(t);
    execFileSync("git", [
        "-C",
        f.repo,
        "add",
        ".goblin-secrets/owner-password",
    ]);
    const result = f.run();
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /saved password is tracked/);
    assert.equal(await exists(f.log), false);
});
