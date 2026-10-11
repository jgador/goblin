import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
    copyFile,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    symlink,
    writeFile,
} from "node:fs/promises";
import { basename, join } from "node:path";
import { test } from "node:test";

const root = new URL("../", import.meta.url);
const artifacts = new URL(".artifacts/make-workflows/", root);

async function workflow(goals: string[], fail = "") {
    await mkdir(artifacts, { recursive: true });
    const directory = await mkdtemp(join(artifacts.pathname, "fixture-"));
    try {
        await copyFile(new URL("Makefile", root), join(directory, "Makefile"));
        const bin = join(directory, "node_modules/.bin");
        await mkdir(bin, { recursive: true });
        const script = join(bin, "tool.mjs");
        const log = join(directory, "commands.jsonl");
        await writeFile(
            script,
            `#!${process.execPath}
import { appendFileSync } from "node:fs";
import { basename } from "node:path";
const command = [basename(process.argv[1]), ...process.argv.slice(2)];
appendFileSync(${JSON.stringify(log)}, JSON.stringify({ command, stack: process.env.RUST_MIN_STACK, profile: process.env.NEXTEST_PROFILE }) + "\\n");
if (command.join(" ") === ${JSON.stringify(fail)}) process.exit(17);
`,
            { mode: 0o755 },
        );
        for (const name of [
            "npm",
            "node",
            "tsc",
            "cargo",
            "dotnet",
            "rustup",
            "playwright",
        ])
            await symlink(basename(script), join(bin, name));
        const result = spawnSync(
            "make",
            ["--no-print-directory", "-j8", ...goals],
            {
                cwd: directory,
                encoding: "utf8",
                env: {
                    ...process.env,
                    MAKEFLAGS: "",
                },
            },
        );
        assert.ifError(result.error);
        const entries = (await readFile(log, "utf8"))
            .trim()
            .split("\n")
            .map(
                (line) =>
                    JSON.parse(line) as {
                        command: string[];
                        stack?: string;
                        profile?: string;
                    },
            );
        return {
            result,
            entries,
            commands: entries.map((entry) => entry.command.join(" ")),
        };
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
}

test("parallel Make invocation preserves the full test lifecycle and shares build prerequisites", async () => {
    const { result, entries, commands } = await workflow(["test"]);
    assert.equal(result.status, 0, result.stderr);
    const stages = [
        "node scripts/generate-contract-values.mts --check",
        "node --test scripts/generate-contract-values.test.mts",
        "cargo xtask dependencies check --locked",
        "cargo build --locked --workspace",
        "npm --prefix frontend run build",
        "node scripts/clean-output.mts",
        "tsc -p tsconfig.scripts.json",
        "tsc -p tsconfig.json",
        "dotnet build backend/Goblin.slnx --nologo",
        "cargo xtask azure",
        "cargo nextest run --locked --no-fail-fast --workspace",
        "cargo test --locked --doc --workspace",
        "dotnet run --file backend/scripts/GenerateProtocol.cs -- --self-test --check",
        "dotnet run --file backend/scripts/GenerateKubernetes.cs -- --self-test --check",
        "dotnet test backend/Goblin.slnx --no-build --nologo",
    ];
    let previous = -1;
    for (const stage of stages) {
        const index = commands.indexOf(stage);
        assert.ok(
            index > previous,
            `${stage} must run after the preceding stage`,
        );
        assert.equal(commands.filter((command) => command === stage).length, 1);
        previous = index;
    }
    const nextest = entries.find((entry) => entry.command[1] === "nextest");
    assert.equal(nextest?.stack, "8388608");
    assert.equal(nextest?.profile, "local");
    assert.ok(commands.at(-1)?.startsWith("node --test dist/tests/frontend/"));
});

test("frontend build failure stops later build and test stages", async () => {
    const failed = "npm --prefix frontend run build";
    const { result, commands } = await workflow(["test"], failed);
    assert.notEqual(result.status, 0);
    assert.equal(commands.at(-1), failed);
    assert.ok(!commands.some((command) => command.startsWith("dotnet ")));
    assert.ok(!commands.includes("cargo xtask azure"));
});

test("install keeps packages separate and local reset requires explicit consent flags", async () => {
    const installed = await workflow(["install"]);
    assert.equal(installed.result.status, 0, installed.result.stderr);
    assert.deepEqual(installed.commands, [
        "npm ci",
        "npm --prefix frontend ci",
        "rustup show active-toolchain",
        "cargo fetch --locked",
    ]);
    const reset = await workflow(["local-reset"]);
    assert.equal(reset.result.status, 0, reset.result.stderr);
    assert.deepEqual(reset.commands, [
        "cargo run --locked -q -p goblinctl -- local reset",
    ]);
    const explicit = await workflow(["local-reset", "ARGS=--yes"]);
    assert.deepEqual(explicit.commands, [
        "cargo run --locked -q -p goblinctl -- local reset --yes",
    ]);
    const selected = await workflow([
        "test-rust",
        "CARGO_ARGS=-p goblinctl",
        "ARGS=--filter-expr 'test(password)'",
    ]);
    assert.equal(selected.result.status, 0, selected.result.stderr);
    assert.equal(
        selected.commands[0],
        "cargo nextest run --locked --no-fail-fast -p goblinctl --filter-expr test(password)",
    );
    assert.equal(
        selected.commands[1],
        "cargo test --locked --doc -p goblinctl",
    );
});

test("database workflows forward the chosen profile without changing Cargo selectors", async () => {
    for (const [target, task] of [
        ["dev", "dev"],
        ["db-migrate", "database-migrate"],
        ["db-scaffold", "database-scaffold"],
        ["test-postgres", "test-postgres"],
    ]) {
        const { result, commands } = await workflow([
            target!,
            "DB_PROFILE=azure-dev",
        ]);
        assert.equal(result.status, 0, result.stderr);
        assert.equal(
            commands.at(-1),
            `cargo xtask ${task} --profile azure-dev`,
        );
        if (target === "dev")
            assert.ok(commands.includes("cargo build --locked --workspace"));
    }
});
