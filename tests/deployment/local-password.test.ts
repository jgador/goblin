import { environmentVariables as Env } from "../../config/environment.mjs";
import { once } from "node:events";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { pbkdf2Sync } from "node:crypto";
import {
    access,
    mkdir,
    mkdtemp,
    readFile,
    readdir,
    rm,
    stat,
    symlink,
    writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { goblinctl } from "../support/goblinctl.js";

test("local credentials preserve password bytes, private permissions, and the verifier on rerun", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-password-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const path = join(root, ".goblin-secrets/owner-password");
    const chosen = " a-'quoted'-$HOME-π ";
    const run = (password: string, replace = false) =>
        execFileSync(
            goblinctl,
            [
                "password",
                "set",
                "--path",
                path,
                ...(replace ? ["--replace"] : []),
            ],
            {
                env: {
                    ...process.env,
                    [Env.GOBLIN_LOCAL_PASSWORD.name]: password,
                },
                stdio: "pipe",
            },
        );
    run(chosen);
    const verifier = await readFile(path, "utf8");
    const [algorithm, iterations, salt, digest] = verifier.trim().split("$");
    assert.equal(algorithm, "pbkdf2-sha256");
    assert.equal(iterations, "600000");
    assert.deepEqual(
        pbkdf2Sync(chosen, Buffer.from(salt!, "base64"), 600000, 32, "sha256"),
        Buffer.from(digest!, "base64"),
    );
    assert.equal((await stat(path)).mode & 0o777, 0o600);
    assert.equal(
        (await stat(join(root, ".goblin-secrets"))).mode & 0o777,
        0o700,
    );
    assert.deepEqual(await readdir(join(root, ".goblin-secrets")), [
        "owner-password",
    ]);
    run("must-not-replace");
    assert.equal(await readFile(path, "utf8"), verifier);
    assert.throws(() => run("line\nbreak", true));
    assert.equal(await readFile(path, "utf8"), verifier);
    run("replacement-test", true);
    assert.notEqual(await readFile(path, "utf8"), verifier);
});

test("invalid, missing, corrupt and symlinked credentials fail without fallback or partial writes", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-invalid-password-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const path = join(root, "private/owner-password");
    const run = (password?: string) => {
        const env = { ...process.env };
        delete env[Env.GOBLIN_LOCAL_PASSWORD.name];
        if (password !== undefined)
            env[Env.GOBLIN_LOCAL_PASSWORD.name] = password;
        return execFileSync(goblinctl, ["password", "set", "--path", path], {
            env,
            stdio: "pipe",
        });
    };
    for (const password of [
        undefined,
        "",
        "   ",
        "x".repeat(129),
        "line\nbreak",
        "😀".repeat(65),
    ]) {
        assert.throws(() => run(password));
        await assert.rejects(access(path));
        await assert.rejects(access(join(root, "private")));
    }
    await mkdir(join(root, "private"));
    await writeFile(path, "broken-verifier");
    assert.throws(() => run("must-not-replace"), /invalid/);
    assert.equal(await readFile(path, "utf8"), "broken-verifier");
    await rm(path);
    const target = join(root, "target");
    await writeFile(target, "keep");
    await symlink(target, path);
    assert.throws(() => run("must-not-replace"), /symbolic/);
    assert.equal(await readFile(target, "utf8"), "keep");
});

test("Git includes only the empty secret directory placeholder", async () => {
    const paths = [
        ".goblin-secrets/.gitkeep",
        ".goblin-secrets/owner-password",
        ".goblin-secrets/nested/credential",
        ".goblin-secrets/.owner-password-temporary",
    ];
    const result = execFileSync(
        "git",
        ["check-ignore", "--no-index", ...paths],
        { encoding: "utf8" },
    )
        .trim()
        .split("\n");
    assert.deepEqual(result, paths.slice(1));
    assert.equal((await readFile(".goblin-secrets/.gitkeep")).length, 0);
});

test("development startup validates the configured verifier and removes plaintext from the app environment", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-dev-launch-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const bin = join(root, "bin");
    await mkdir(bin);
    const path = join(root, "private/owner-password");
    execFileSync(goblinctl, ["password", "set", "--path", path], {
        env: {
            ...process.env,
            [Env.GOBLIN_LOCAL_PASSWORD.name]: "launcher-test",
        },
    });
    await writeFile(
        join(bin, "dotnet"),
        `#!${process.execPath}\nconsole.log(JSON.stringify({args:process.argv.slice(2),verifier:process.env["${Env.GOBLIN_PASSWORD_HASH_FILE.name}"],plaintext:process.env["${Env.GOBLIN_LOCAL_PASSWORD.name}"]}));\n`,
        { mode: 0o700 },
    );
    const { resolve } = await import("node:path");
    const run = () =>
        execFileSync(
            resolve("target/debug/xtask"),
            ["dev", "--without-database"],
            {
                encoding: "utf8",
                stdio: "pipe",
                env: {
                    ...process.env,
                    [Env.PATH.name]: bin,
                    [Env.GOBLIN_PASSWORD_HASH_FILE.name]: path,
                    [Env.GOBLIN_LOCAL_PASSWORD.name]: "never-forward-test",
                },
            },
        );
    const output = JSON.parse(run());
    assert.equal(output.verifier, path);
    assert.equal(output.plaintext, undefined);
    assert.match(output.args[0], /Goblin.Web\.dll$/);
    await writeFile(path, "broken-verifier");
    assert.throws(run, /invalid/);
});

test("interactive password confirmation is hidden and mismatch preserves existing credentials", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-password-tty-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const path = join(root, "private/owner-password");
    execFileSync(goblinctl, ["password", "set", "--path", path], {
        env: {
            ...process.env,
            [Env.GOBLIN_LOCAL_PASSWORD.name]: "initial-test",
        },
    });
    const before = await readFile(path);
    const { spawn } = await import("node:child_process");
    const env = { ...process.env };
    delete env[Env.GOBLIN_LOCAL_PASSWORD.name];
    const quote = (value: string) => "'" + value.replaceAll("'", "'\\''") + "'";
    const child = spawn(
        "script",
        [
            "--quiet",
            "--return",
            "--command",
            `${quote(goblinctl)} password set --replace --path ${quote(path)}`,
            "/dev/null",
        ],
        { env },
    );
    t.after(() => child.kill("SIGKILL"));
    const exited = once(child, "exit");
    let output = "";
    let first = false;
    let second = false;
    child.stdout.setEncoding("utf8").on("data", (data) => {
        output += data;
        if (!first && output.includes("Goblin password:")) {
            first = true;
            setTimeout(() => child.stdin.write("hidden-first-test\n"), 30);
        }
        if (!second && output.includes("Confirm Goblin password:")) {
            second = true;
            setTimeout(() => child.stdin.write("hidden-mismatch-test\n"), 30);
        }
    });
    const timer = setTimeout(() => child.kill("SIGKILL"), 10000);
    const [code] = await exited;
    clearTimeout(timer);
    assert.equal(code, 1);
    assert.ok(first && second);
    assert.match(output, /do not match/);
    assert.doesNotMatch(output, /hidden-first-test|hidden-mismatch-test/);
    assert.deepEqual(await readFile(path), before);
});
