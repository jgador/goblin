import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
    copyFile,
    mkdir,
    mkdtemp,
    readFile,
    rename,
    rm,
    writeFile,
} from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";
import { environmentVariables as Env } from "../config/environment.mts";
import {
    changedFiles,
    comparison,
    loadPolicy,
    parsePlan,
    parsePolicy,
    report,
    select,
} from "./ci.mts";
import type { Plan } from "./ci.mts";

const root = resolve(import.meta.dirname, "..");
const policy = await loadPolicy(root);
const artifacts = resolve(root, ".artifacts/ci-selection");
const enabled = (plan: Plan) =>
    Object.entries(plan.groups)
        .filter(([, group]) => group.selected)
        .map(([id]) => id);
function jobResults(
    plan: Plan,
): Record<string, { result: string; outputs?: Record<string, string> }> {
    return {
        changes: {
            result: "success",
            outputs: {
                plan: JSON.stringify(plan),
                ...Object.fromEntries(
                    Object.entries(plan.groups).map(([id, group]) => [
                        id,
                        String(group.selected),
                    ]),
                ),
            },
        },
        ...Object.fromEntries(
            Object.entries(plan.groups).map(([id, group]) => [
                id,
                { result: group.selected ? "success" : "skipped" },
            ]),
        ),
    };
}
test("documentation-only changes select no runtime checks and satisfy the required result", () => {
    const plan = select(policy, ["README.md", "anywhere/instructions.MD"]);
    assert.equal(plan.documentationOnly, true);
    assert.deepEqual(enabled(plan), []);
    const result = report(policy, jobResults(plan));
    assert.deepEqual(result.errors, []);
    assert.match(result.summary, /Documentation-only change/);
    assert.match(result.summary, /Intentionally skipped/);
    assert.match(result.summary, /test_results: Passed/);
});
test("language extensions select their checks anywhere, including Rust under backend", () => {
    for (const [file, expected] of [
        ["backend/new/compiler.rs", ["rust", "integration", "installer"]],
        ["tools/component.cs", ["csharp", "integration", "installer"]],
        ["other/component.ts", ["javascript", "integration", "installer"]],
        ["other/component.mjs", ["javascript", "integration", "installer"]],
    ] as const)
        assert.deepEqual(enabled(select(policy, [file])), expected);
    assert.deepEqual(
        enabled(select(policy, ["code.cs", "code.rs", "README.md"])),
        ["csharp", "rust", "integration", "installer"],
    );
});
test("Markdown agentic workflows select verification because they are executable configuration", () => {
    const plan = select(policy, [".github/workflows/documentation.md"]);
    assert.equal(plan.documentationOnly, false);
    assert.deepEqual(enabled(plan), Object.keys(policy.groups));
});
test("shared Node fixtures and pinned runtime inputs also select their C# consumers", () => {
    for (const file of [
        "tests/fixtures/fake-codex.mts",
        "package.json",
        "package-lock.json",
    ])
        assert.deepEqual(enabled(select(policy, [file])), [
            "csharp",
            "javascript",
            "integration",
            "installer",
        ]);
});
test("recognized dependency/toolchain filenames classify without their generic extension", () => {
    for (const name of [
        "Cargo.lock",
        "rust-toolchain.toml",
        ".rustfmt.toml",
        ".cargo/config.toml",
        ".config/nextest.toml",
    ])
        assert.deepEqual(enabled(select(policy, ["backend/new/" + name])), [
            "rust",
            "integration",
            "installer",
        ]);
    for (const name of ["global.json", "NuGet.Config", "Directory.Build.props"])
        assert.deepEqual(enabled(select(policy, ["elsewhere/" + name])), [
            "csharp",
            "integration",
            "installer",
        ]);
    for (const name of [
        "package-lock.json",
        "pnpm-lock.yaml",
        "tsconfig.build.json",
    ])
        assert.deepEqual(enabled(select(policy, ["elsewhere/" + name])), [
            "javascript",
            "integration",
            "installer",
        ]);
});

test("registered manifests select their language and a new project cannot silently escape its runner", () => {
    for (const [file, language] of [
        ["tools/goblinctl/Cargo.toml", "rust"],
        ["backend/src/Goblin.Core/Goblin.Core.csproj", "csharp"],
        ["frontend/package.json", "javascript"],
    ]) {
        const plan = select(policy, [file!]);
        assert.ok(plan.groups[language!]!.selected);
        assert.deepEqual(plan.errors, []);
    }
    for (const file of [
        "backend/new/Cargo.toml",
        "elsewhere/module.csproj",
        "new/package.json",
    ]) {
        const plan = select(policy, [file]);
        assert.deepEqual(enabled(plan), Object.keys(policy.groups));
        assert.ok(
            report(policy, jobResults(plan)).errors.some((error) =>
                error.includes("Unregistered"),
            ),
        );
    }
});
test("shared and unclassified files conservatively select every existing check", () => {
    for (const file of [
        "Makefile",
        "Makefile.dev",
        ".github/workflows/ci.yml",
        "data/config.json",
        "data/config.toml",
        "Dockerfile",
        "backend/schema.sql",
        "asset.svg",
        "source.newlang",
    ])
        assert.deepEqual(
            enabled(select(policy, [file])),
            Object.keys(policy.groups),
        );
});
test("known unconfigured languages cannot pass by running only existing suites", () => {
    for (const file of [
        "backend/main.go",
        "go.mod",
        "feature.py",
        "Package.swift",
    ]) {
        const plan = select(policy, [file]);
        assert.ok(plan.errors.length > 0);
        assert.deepEqual(enabled(plan), Object.keys(policy.groups));
        assert.ok(
            report(policy, jobResults(plan)).errors.some((error) =>
                error.includes("not configured"),
            ),
        );
    }
});
test("a newly registered group overrides its former unsupported-language classification", () => {
    const updated = structuredClone(policy);
    updated.groups.go = {
        label: "Go",
        extensions: [".go"],
        filenames: ["go.mod", "go.sum"],
        requires: [],
        commands: ["go test ./..."],
    };
    const plan = select(parsePolicy(updated), ["backend/new/main.go"]);
    assert.deepEqual(enabled(plan), ["go"]);
    assert.deepEqual(plan.errors, []);
    // Registration must include a CI job: missing results still block merging.
    const jobs = jobResults(plan);
    delete jobs.go;
    assert.ok(
        report(updated, jobs).errors.some((error) => error.includes("go")),
    );
});

test("deleting unsupported source or a removed project still verifies removal without requiring a new runner", () => {
    const plan = select(
        policy,
        ["old/main.go", "old/Cargo.toml", "old/main.rs"],
        false,
        ["README.md"],
    );
    assert.deepEqual(enabled(plan), Object.keys(policy.groups));
    assert.deepEqual(plan.errors, []);
    assert.deepEqual(report(policy, jobResults(plan)).errors, []);
});
test("manual/full selection ignores documentation-only optimization", () => {
    assert.deepEqual(
        enabled(select(policy, ["README.md"], true)),
        Object.keys(policy.groups),
    );
    assert.equal(select(policy, ["README.md"], true).documentationOnly, false);
    assert.deepEqual(report(policy, jobResults(select(policy, []))).errors, []);
});
test("selected failure, cancellation, unexpected skip, and missing result all block merging", () => {
    const plan = select(policy, ["main.rs"]);
    for (const status of ["failure", "cancelled", "skipped", "unknown"]) {
        const jobs = jobResults(plan);
        jobs.rust!.result = status;
        assert.ok(
            report(policy, jobs).errors.some((error) => error.includes("rust")),
        );
    }
    const missing = jobResults(plan);
    delete missing.installer;
    assert.ok(
        report(policy, missing).errors.some((error) =>
            error.includes("installer"),
        ),
    );
    const unexpected = jobResults(plan);
    unexpected.csharp!.result = "success";
    assert.ok(
        report(policy, unexpected).errors.some((error) =>
            error.includes("Unselected csharp"),
        ),
    );
});
test("detection failure and malformed or inconsistent outputs fail the required check", () => {
    const plan = select(policy, ["README.md"]);
    for (const status of ["failure", "cancelled", "skipped"]) {
        const jobs = jobResults(plan);
        jobs.changes!.result = status;
        assert.match(report(policy, jobs).summary, /test_results: Failed/);
    }
    for (const output of ["", "{", JSON.stringify({ ...plan, version: 2 })]) {
        const jobs = jobResults(plan);
        jobs.changes!.outputs!.plan = output;
        assert.ok(report(policy, jobs).errors.length > 0);
    }
    const mismatch = jobResults(plan);
    mismatch.changes!.outputs!.rust = "true";
    assert.ok(
        report(policy, mismatch).errors.some((error) =>
            error.includes("disagrees"),
        ),
    );
    const invalid = select(policy, ["main.rs"]);
    invalid.groups.installer!.selected = false;
    assert.throws(
        () => parsePlan(policy, invalid),
        /missing required verification/,
    );
});
test("selection/report bounds and escapes untrusted filenames without losing decisions", () => {
    const files = Array.from({ length: 3000 }, (_, i) => "folder/" + i + ".cs");
    const plan = select(policy, files);
    assert.equal(plan.changedFiles, 3000);
    assert.ok(JSON.stringify(plan).length < 24000);
    const hostile = select(policy, [
        "<script>|" + String.fromCharCode(96) + "\ncode.cs",
    ]);
    const summary = report(policy, jobResults(hostile)).summary;
    assert.ok(!summary.includes("<script>"));
    assert.match(summary, /&lt;script&gt;&#124;&#96;/);
    assert.throws(() => select(policy, ["../code.rs"]), /Invalid changed-file/);
});
test("configuration rejects missing commands, invalid extensions, and unknown dependencies", () => {
    const invalid = structuredClone(policy);
    invalid.groups.rust!.commands = [];
    assert.throws(() => parsePolicy(invalid), /requires verification commands/);
    invalid.groups.rust!.commands = ["true"];
    invalid.groups.rust!.requires = ["missing"];
    assert.throws(() => parsePolicy(invalid), /unknown or self dependency/);
    invalid.groups.rust!.requires = [];
    invalid.groups.rust!.extensions = ["rs"];
    assert.throws(() => parsePolicy(invalid), /invalid extension/);
});
const base = "a".repeat(40),
    head = "b".repeat(40);
test("event comparisons use PR merge base, complete push range, merge group base, and full manual/new-branch snapshots", () => {
    assert.deepEqual(
        comparison(
            "pull_request",
            { pull_request: { base: { sha: base }, head: { sha: head } } },
            head,
        ),
        { base, head, mergeBase: true, full: false },
    );
    assert.deepEqual(comparison("push", { before: base, after: head }, head), {
        base,
        head,
        mergeBase: false,
        full: false,
    });
    assert.deepEqual(
        comparison(
            "merge_group",
            { merge_group: { base_sha: base, head_sha: head } },
            head,
        ),
        { base, head, mergeBase: false, full: false },
    );
    assert.deepEqual(comparison("workflow_dispatch", {}, head), {
        head,
        mergeBase: false,
        full: true,
    });
    assert.deepEqual(
        comparison("push", { before: "0".repeat(40), after: head }, head),
        { base: undefined, head, mergeBase: false, full: true },
    );
    assert.throws(
        () => comparison("push", { before: "main", after: head }, head),
        /invalid comparison/,
    );
    assert.throws(
        () => comparison("schedule", {}, head),
        /Unsupported CI event/,
    );
    assert.throws(() => comparison("pull_request", {}, head));
});
function git(directory: string, args: string[]): string {
    const result = spawnSync("git", args, { cwd: directory, encoding: "utf8" });
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stderr);
    return result.stdout.trim();
}
async function fixture(action: (directory: string) => Promise<void>) {
    await mkdir(artifacts, { recursive: true });
    const directory = await mkdtemp(join(artifacts, "fixture-"));
    try {
        await action(directory);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
}
async function repository(directory: string) {
    git(directory, ["init", "--initial-branch=main"]);
    // Identity belongs only to this synthetic Git fixture, never the user's repo.
    git(directory, ["config", "user.name", "Fixture Author"]);
    git(directory, ["config", "user.email", "fixture@example.invalid"]);
}
function commit(directory: string, message: string): string {
    git(directory, ["add", "--all"]);
    git(directory, ["-c", "core.hooksPath=/dev/null", "commit", "-m", message]);
    return git(directory, ["rev-parse", "HEAD"]);
}
test("real Git diffs retain additions/deletions and both rename sides, including unusual filenames", async () => {
    await fixture(async (directory) => {
        await repository(directory);
        await writeFile(join(directory, "source.rs"), "source\n");
        const first = commit(directory, "initial fixture");
        await rename(
            join(directory, "source.rs"),
            join(directory, "reference.md"),
        );
        await writeFile(join(directory, "spaced file\nname.cs"), "source\n");
        const second = commit(directory, "renamed fixture");
        const files = changedFiles(directory, {
            base: first,
            head: second,
            mergeBase: false,
            full: false,
        });
        assert.deepEqual(
            files.sort(),
            ["source.rs", "reference.md", "spaced file\nname.cs"].sort(),
        );
        assert.deepEqual(enabled(select(policy, files)), [
            "csharp",
            "rust",
            "integration",
            "installer",
        ]);
        await rm(join(directory, "spaced file\nname.cs"));
        const third = commit(directory, "deleted fixture");
        assert.deepEqual(
            changedFiles(directory, {
                base: second,
                head: third,
                mergeBase: false,
                full: false,
            }),
            ["spaced file\nname.cs"],
        );
        assert.throws(
            () =>
                changedFiles(directory, {
                    base: base,
                    head: third,
                    mergeBase: false,
                    full: false,
                }),
            /Git comparison failed/,
        );
    });
});
test("PR comparison excludes unrelated target-branch changes while push covers every commit", async () => {
    await fixture(async (directory) => {
        await repository(directory);
        await writeFile(join(directory, "README.md"), "initial\n");
        const common = commit(directory, "initial fixture");
        git(directory, ["switch", "-c", "feature"]);
        await writeFile(join(directory, "code.rs"), "source\n");
        commit(directory, "source fixture");
        await writeFile(join(directory, "README.md"), "feature docs\n");
        const feature = commit(directory, "documentation fixture");
        git(directory, ["switch", "main"]);
        await writeFile(join(directory, "unrelated.cs"), "target-only\n");
        const target = commit(directory, "target fixture");
        const pr = changedFiles(directory, {
            base: target,
            head: feature,
            mergeBase: true,
            full: false,
        });
        assert.ok(pr.includes("code.rs"));
        assert.ok(!pr.includes("unrelated.cs"));
        assert.ok(
            changedFiles(directory, {
                base: common,
                head: feature,
                mergeBase: false,
                full: false,
            }).includes("code.rs"),
        );
        assert.deepEqual(
            changedFiles(directory, {
                head: feature,
                mergeBase: false,
                full: true,
            }).sort(),
            ["README.md", "code.rs"],
        );
    });
});
test("CLI emits selection outputs and writes the required summary with matching exit status", async () => {
    await fixture(async (directory) => {
        await repository(directory);
        await mkdir(join(directory, "config"));
        await copyFile(
            join(root, "config/ci-verification.json"),
            join(directory, "config/ci-verification.json"),
        );
        await writeFile(join(directory, "README.md"), "initial\n");
        const before = commit(directory, "initial fixture");
        await writeFile(join(directory, "README.md"), "updated\n");
        const after = commit(directory, "docs fixture");
        const event = join(directory, "event.json"),
            output = join(directory, "outputs"),
            summary = join(directory, "summary.md");
        await writeFile(event, JSON.stringify({ before, after }));
        const env = {
            ...process.env,
            [Env.GITHUB_EVENT_NAME.name]: "push",
            [Env.GITHUB_EVENT_PATH.name]: event,
            [Env.GITHUB_SHA.name]: after,
            [Env.GITHUB_OUTPUT.name]: output,
        };
        const detect = spawnSync(
            process.execPath,
            [join(root, "scripts/ci.mts"), "detect"],
            { cwd: directory, env, encoding: "utf8" },
        );
        assert.equal(detect.status, 0, detect.stderr);
        const outputs = Object.fromEntries(
            (await readFile(output, "utf8"))
                .trim()
                .split("\n")
                .map((line) => {
                    const split = line.indexOf("=");
                    return [line.slice(0, split), line.slice(split + 1)];
                }),
        );
        const plan = parsePlan(policy, JSON.parse(outputs.plan!));
        assert.equal(plan.documentationOnly, true);
        const jobs = jobResults(plan);
        const invokeReport = () =>
            spawnSync(
                process.execPath,
                [join(root, "scripts/ci.mts"), "report"],
                {
                    cwd: directory,
                    encoding: "utf8",
                    env: {
                        ...env,
                        [Env.CI_JOB_RESULTS.name]: JSON.stringify(jobs),
                        [Env.GITHUB_STEP_SUMMARY.name]: summary,
                    },
                },
            );
        assert.equal(invokeReport().status, 0);
        assert.match(await readFile(summary, "utf8"), /test_results: Passed/);
        jobs.changes!.result = "failure";
        assert.notEqual(invokeReport().status, 0);
        assert.match(await readFile(summary, "utf8"), /test_results: Failed/);
    });
});
test("registered group commands stop at the first failure", async () => {
    await fixture(async (directory) => {
        await mkdir(join(directory, "config"));
        const custom = structuredClone(policy);
        custom.groups.rust!.commands = [
            "printf started > first.txt",
            "exit 17",
            "printf unexpected > last.txt",
        ];
        await writeFile(
            join(directory, "config/ci-verification.json"),
            JSON.stringify(custom),
        );
        const result = spawnSync(
            process.execPath,
            [join(root, "scripts/ci.mts"), "run", "rust"],
            { cwd: directory, encoding: "utf8" },
        );
        assert.notEqual(result.status, 0);
        assert.equal(
            await readFile(join(directory, "first.txt"), "utf8"),
            "started",
        );
        await assert.rejects(readFile(join(directory, "last.txt")));
    });
});
test("workflow contains every configured job, condition, direct reporting dependency, and preserved triggers", async () => {
    const source = await readFile(
        join(root, ".github/workflows/ci.yml"),
        "utf8",
    );
    assert.match(source, /fetch-depth: 0/);
    for (const trigger of [
        "pull_request:",
        "push:",
        "merge_group:",
        "workflow_dispatch:",
    ])
        assert.ok(source.includes(trigger));
    assert.ok(!source.includes("paths-ignore:"));
    for (const id of Object.keys(policy.groups)) {
        assert.ok(source.includes("  " + id + ":"));
        assert.ok(
            source.includes("if: needs.changes.outputs." + id + " == 'true'"),
        );
        assert.ok(source.includes("run: node scripts/ci.mts run " + id));
    }
    assert.match(
        source,
        /needs: \[changes, csharp, rust, javascript, integration, installer\]\s+if: always\(\)/,
    );
    assert.ok(
        source.includes("CI_JOB_RESULTS: " + "$" + "{{ toJSON(needs) }}"),
    );
});
