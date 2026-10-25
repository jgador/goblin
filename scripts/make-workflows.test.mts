import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
    copyFile,
    mkdir,
    mkdtemp,
    readFile,
    readdir,
    rm,
    symlink,
    writeFile,
} from "node:fs/promises";
import { basename, join } from "node:path";
import { test } from "node:test";

const root = new URL("../", import.meta.url);
const artifacts = new URL(".artifacts/make-workflows/", root);

async function copyMakefiles(directory: string) {
    await Promise.all(
        ["Makefile", "Makefile.dev"].map((filename) =>
            copyFile(new URL(filename, root), join(directory, filename)),
        ),
    );
}

async function workflow(goals: string[], fail = "", imageTags: string[] = []) {
    await mkdir(artifacts, { recursive: true });
    const directory = await mkdtemp(join(artifacts.pathname, "fixture-"));
    try {
        await copyMakefiles(directory);
        const bin = join(directory, "node_modules/.bin");
        await mkdir(bin, { recursive: true });
        const script = join(bin, "tool.mjs");
        const log = join(directory, "commands.jsonl");
        await writeFile(log, "");
        await writeFile(
            script,
            `#!${process.execPath}
import { appendFileSync } from "node:fs";
import { basename } from "node:path";
const command = [basename(process.argv[1]), ...process.argv.slice(2)];
appendFileSync(${JSON.stringify(log)}, JSON.stringify({ command, stack: process.env.RUST_MIN_STACK, profile: process.env.NEXTEST_PROFILE }) + "\\n");
if (command.join(" ") === ${JSON.stringify(fail)}) process.exit(17);
if (command.slice(0, 3).join(" ") === "docker image ls")
    for (const tag of ${JSON.stringify(imageTags)}) console.log(tag);
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
            "docker",
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
        const contents = (await readFile(log, "utf8")).trim();
        const entries = (contents ? contents.split("\n") : []).map(
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

test("clean-artifacts recursively removes output while preserving the root marker and symlink targets", async () => {
    await mkdir(artifacts, { recursive: true });
    const directory = await mkdtemp(join(artifacts.pathname, "cleanup-"));
    try {
        await copyMakefiles(directory);
        const output = join(directory, ".artifacts");
        const nested = join(output, "nested directory", "child");
        await mkdir(nested, { recursive: true });
        await writeFile(join(output, ".gitkeep"), "keep root marker\n");
        await writeFile(join(output, ".hidden-output"), "temporary output\n");
        await writeFile(join(nested, ".gitkeep"), "nested marker\n");
        await writeFile(join(nested, "output.log"), "temporary output\n");
        const unrelated = join(directory, "unrelated");
        await mkdir(unrelated);
        await writeFile(join(unrelated, "keep.txt"), "keep unrelated file\n");
        await symlink(unrelated, join(output, "linked-directory"));
        await writeFile(join(directory, "clean-artifacts"), "target name\n");

        const result = spawnSync(
            "make",
            ["--no-print-directory", "clean-artifacts"],
            {
                cwd: directory,
                encoding: "utf8",
            },
        );
        assert.ifError(result.error);
        assert.equal(result.status, 0, result.stderr);
        assert.equal(result.stdout, "Cleaned .artifacts (kept .gitkeep).\n");
        assert.deepEqual(await readdir(output), [".gitkeep"]);
        assert.equal(
            await readFile(join(output, ".gitkeep"), "utf8"),
            "keep root marker\n",
        );
        assert.equal(
            await readFile(join(unrelated, "keep.txt"), "utf8"),
            "keep unrelated file\n",
        );
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
});

test("parallel Make invocation preserves the full test lifecycle and shares build prerequisites", async () => {
    const { result, entries, commands } = await workflow(["test"]);
    assert.equal(result.status, 0, result.stderr);
    const stages = [
        "node scripts/generate-contract-values.mts --check",
        "node --test scripts/generate-contract-values.test.mts",
        "node --test scripts/ci.test.mts",
        "node --test scripts/agentic-maintenance.test.mts",
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

test("the default goal lists shared and included development targets without running tools", async () => {
    const { result, commands } = await workflow([]);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(commands, []);
    const targets = result.stdout
        .trim()
        .split("\n")
        .map((line) => line.trim().split(/\s+/)[0]);
    for (const target of [
        "build:",
        "dev:",
        "local-start:",
        "docker-build:",
        "docker-images-clean:",
        "docker-cache-clean:",
    ])
        assert.ok(targets.includes(target));
    assert.equal(new Set(targets).size, targets.length);
    assert.doesNotMatch(result.stderr, /overriding recipe/);
});

test("Docker builds report elapsed time, forward flags, and propagate failures", async () => {
    const built = await workflow(["docker-build", "ARGS=--no-cache"]);
    assert.equal(built.result.status, 0, built.result.stderr);
    assert.deepEqual(built.commands, [
        "docker build --platform linux/amd64 --progress=plain -t goblin-platform:local --no-cache .",
    ]);
    assert.match(built.result.stderr, /real\s+\d+m[\d.]+s/);
    const failed = await workflow(
        ["docker-build"],
        "docker build --platform linux/amd64 --progress=plain -t goblin-platform:local .",
    );
    assert.notEqual(failed.result.status, 0);
});

test("Docker image cleanup removes only Goblin repository tags across registries", async () => {
    const goblinTags = [
        "goblin-platform:local",
        "goblin-platform:benchmark",
        "localhost/goblin-platform:abc123",
        "registry.example.invalid/team/goblin-website:local",
    ];
    const { result, commands } = await workflow(["docker-images-clean"], "", [
        ...goblinTags,
        "redis:latest",
        "goblin-team/other-app:local",
        "other-goblin-app:local",
        "<none>:<none>",
    ]);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(commands, [
        "docker image ls --format {{.Repository}}:{{.Tag}}",
        `docker image rm ${goblinTags.join(" ")}`,
    ]);
});

test("Docker image cleanup handles no matches and rejects image-list failures", async () => {
    const listed = "docker image ls --format {{.Repository}}:{{.Tag}}";
    const empty = await workflow(["docker-images-clean"], "", ["redis:latest"]);
    assert.equal(empty.result.status, 0, empty.result.stderr);
    assert.deepEqual(empty.commands, [listed]);
    const failed = await workflow(["docker-images-clean"], listed);
    assert.notEqual(failed.result.status, 0);
    assert.deepEqual(failed.commands, [listed]);
});

test("Docker cache cleanup explicitly prunes all unused default-builder cache", async () => {
    const { result, commands } = await workflow(["docker-cache-clean"]);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(commands, ["docker builder prune --all --force"]);
});
