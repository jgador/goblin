import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
    copyFile,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    writeFile,
} from "node:fs/promises";
import { join, resolve } from "node:path";
import test from "node:test";
import { environmentVariables as Env } from "../config/environment.mts";
import { loadPolicy } from "./ci.mts";
import {
    authorized,
    boundedDiff,
    coverageProblems,
    documentCandidates,
    documentationTarget,
    inventory,
    prepare,
    proposedPolicy,
    snapshotProposal,
    validate,
    validateProposal,
} from "./agentic-maintenance.mts";
const root = resolve(import.meta.dirname, "..");
const policy = await loadPolicy(root);
const owner = {
    [Env.GITHUB_REPOSITORY.name]: "jgador/goblin",
    [Env.GITHUB_ACTOR.name]: "jgador",
    [Env.GITHUB_TRIGGERING_ACTOR.name]: "jgador",
    [Env.GITHUB_EVENT_NAME.name]: "push",
    [Env.GITHUB_REF.name]: "refs/heads/feature",
};
function git(directory: string, args: string[]) {
    const r = spawnSync("git", args, { cwd: directory, encoding: "utf8" });
    assert.equal(r.status, 0, r.stderr);
    return r.stdout.trim();
}
async function fixture(action: (directory: string) => Promise<void>) {
    await mkdir(join(root, ".artifacts/agentic-maintenance/tests"), {
        recursive: true,
    });
    const directory = await mkdtemp(
        join(root, ".artifacts/agentic-maintenance/tests/", "fixture-"),
    );
    try {
        git(directory, ["init", "--quiet"]);
        git(directory, ["config", "user.name", "Fixture Author"]);
        git(directory, ["config", "user.email", "fixture@example.invalid"]);
        git(directory, ["config", "core.hooksPath", "/dev/null"]);
        await mkdir(join(directory, "config"));
        await copyFile(
            join(root, "config/ci-verification.json"),
            join(directory, "config/ci-verification.json"),
        );
        await writeFile(join(directory, ".gitignore"), ".artifacts/\n");
        await writeFile(join(directory, "README.md"), "# Fixture\n");
        await action(directory);
    } finally {
        await rm(directory, { recursive: true, force: true });
    }
}
function commit(directory: string) {
    git(directory, ["add", "."]);
    git(directory, ["commit", "--quiet", "-m", "fixture snapshot"]);
    return git(directory, ["rev-parse", "HEAD"]);
}
test("owner identity is exact and gates other actors, reruns, repositories, events, bots, tags and automation branches", () => {
    assert.equal(authorized({ ref: "refs/heads/feature" }, owner), true);
    for (const [key, value] of [
        [Env.GITHUB_ACTOR.name, "contributor"],
        [Env.GITHUB_TRIGGERING_ACTOR.name, "contributor"],
        [Env.GITHUB_REPOSITORY.name, "contributor/goblin"],
        [Env.GITHUB_EVENT_NAME.name, "pull_request"],
        [Env.GITHUB_ACTOR.name, "github-actions[bot]"],
    ])
        assert.equal(
            authorized(
                { ref: "refs/heads/feature" },
                { ...owner, [key!]: value },
            ),
            false,
        );
    assert.equal(authorized({ ref: "refs/tags/v1" }, owner), false);
    assert.equal(
        authorized({ ref: "refs/heads/gh-aw/documentation-1" }, owner),
        false,
    );
    assert.equal(
        authorized({ ref: "refs/heads/feature", deleted: true }, owner),
        false,
    );
    assert.equal(
        authorized(
            {},
            { ...owner, [Env.GITHUB_EVENT_NAME.name]: "workflow_dispatch" },
        ),
        true,
    );
});
test("inventory proposals discover manifests anywhere and remove obsolete entries without modifying triggers or commands", () => {
    const files = [
        "new/backend/Cargo.toml",
        "package.json",
        "new/component.csproj",
    ];
    const entries = inventory(policy, files);
    assert.deepEqual(entries.rust!.added, ["new/backend/Cargo.toml"]);
    assert.ok(entries.rust!.removed.includes("tools/goblinctl/Cargo.toml"));
    const proposal = proposedPolicy(policy, files);
    assert.deepEqual(proposal.groups.rust!.projectManifests, [
        "new/backend/Cargo.toml",
    ]);
    assert.deepEqual(
        proposal.groups.rust!.commands,
        policy.groups.rust!.commands,
    );
    assert.deepEqual(proposal.fullVerification, policy.fullVerification);
    assert.notDeepEqual(
        proposal.groups.rust!.projectManifests,
        policy.groups.rust!.projectManifests,
    );
});
test("candidate routing finds direct references, subtree guides and affected command guides without touching imported skills", () => {
    const docs = [
        { path: "docs/runtime.md", text: "Uses src/Runner.cs to dispatch." },
        { path: "website/README.md", text: "Website guide" },
        { path: "docs/repository-layout.md", text: "Build commands" },
        { path: ".agents/skills/emil-design-eng/SKILL.md", text: "Runner.cs" },
        { path: "docs/README.md", text: "Guide index" },
    ];
    assert.deepEqual(
        documentCandidates(["src/Runner.cs"], docs).map((c) => c.path),
        ["docs/runtime.md"],
    );
    assert.ok(
        documentCandidates(["website/install/install.js"], docs).some(
            (c) => c.path === "website/README.md",
        ),
    );
    assert.ok(
        documentCandidates(["new/project.csproj"], docs).some(
            (c) => c.path === "docs/repository-layout.md",
        ),
    );
    assert.deepEqual(
        documentCandidates(["unreferenced/Thing.cs"], docs).map((c) => c.path),
        ["docs/README.md"],
    );
    assert.deepEqual(
        documentCandidates([".agents/skills/emil-design-eng/SKILL.md"], docs),
        [],
    );
    assert.equal(documentationTarget("AGENTS.md"), false);
    assert.equal(documentationTarget(".github/workflows/ci-policy.md"), false);
});
test("diff packets bound token context and disclose truncation", () => {
    const result = boundedDiff("a".repeat(100000));
    assert.equal(Buffer.byteLength(result.diff), 24000);
    assert.equal(result.diffTruncated, true);
    assert.equal(boundedDiff("small").diffTruncated, false);
});
test("current runners cover registered projects, while adding an uncovered manifest is rejected", async () => {
    const files = git(root, ["ls-files"]).split("\n");
    assert.deepEqual(await coverageProblems(root, policy, files), []);
    const proposal = structuredClone(policy);
    proposal.groups.rust!.projectManifests!.push("elsewhere/Cargo.toml");
    assert.ok(
        (
            await coverageProblems(root, proposal, [
                ...files,
                "elsewhere/Cargo.toml",
            ])
        ).some((p) => p.includes("elsewhere/Cargo.toml")),
    );
});
test("preparation consumes complete push ranges, skips unrelated doc-only CI maintenance, and writes owner-approved context without API credentials", async () => {
    await fixture(async (directory) => {
        await copyFile(join(root, "Makefile"), join(directory, "Makefile"));
        await mkdir(join(directory, "docs"));
        await writeFile(join(directory, "docs/README.md"), "# Documentation\n");
        const before = commit(directory);
        await writeFile(join(directory, "README.md"), "# Updated fixture\n");
        const after = commit(directory);
        const current = await loadPolicy(directory);
        for (const rule of Object.values(current.groups))
            if (rule.projectManifests) rule.projectManifests = [];
        await writeFile(
            join(directory, "config/ci-verification.json"),
            JSON.stringify(current),
        );
        const head = commit(directory);
        const env = {
            ...owner,
            [Env.GITHUB_SHA.name]: head,
            [Env.GITHUB_OUTPUT.name]: join(directory, ".artifacts/outputs"),
        };
        await mkdir(join(directory, ".artifacts"));
        assert.equal(
            await prepare(
                directory,
                "ci-policy",
                { before: after, after: head, ref: "refs/heads/feature" },
                env,
            ),
            true,
        );
        await writeFile(join(directory, "README.md"), "# Another doc edit\n");
        const docs = commit(directory);
        env[Env.GITHUB_SHA.name] = docs;
        assert.equal(
            await prepare(
                directory,
                "ci-policy",
                { before: head, after: docs, ref: "refs/heads/feature" },
                env,
            ),
            false,
        );
        const packet = JSON.parse(
            await readFile(
                join(
                    directory,
                    ".artifacts/agentic-maintenance/context/packet.json",
                ),
                "utf8",
            ),
        );
        assert.equal(packet.base, head);
        assert.equal(packet.head, docs);
        assert.equal(packet.omittedFiles, 0);
        await writeFile(join(directory, "main.go"), "package main\n");
        const go = commit(directory);
        env[Env.GITHUB_SHA.name] = go;
        assert.equal(
            await prepare(
                directory,
                "ci-policy",
                { before: docs, after: go, ref: "refs/heads/feature" },
                env,
            ),
            true,
        );
        const uncovered = JSON.parse(
            await readFile(
                join(
                    directory,
                    ".artifacts/agentic-maintenance/context/packet.json",
                ),
                "utf8",
            ),
        );
        assert.ok(
            uncovered.coverageProblems.some((problem: string) =>
                problem.includes("Go verification is not configured"),
            ),
        );
        assert.ok(uncovered.changedFiles.includes("main.go"));
        await assert.rejects(
            prepare(
                directory,
                "documentation",
                { before, after: docs, ref: "refs/heads/feature" },
                { ...env, [Env.GITHUB_TRIGGERING_ACTOR.name]: "contributor" },
            ),
            /restricted/,
        );
    });
});
test("documentation validation accepts scoped repairs and rejects source edits and broken links", async () => {
    await fixture(async (directory) => {
        const head = commit(directory);
        await mkdir(join(directory, ".artifacts/agentic-maintenance"), {
            recursive: true,
        });
        await writeFile(
            join(directory, ".artifacts/agentic-maintenance/packet.json"),
            JSON.stringify({ mode: "documentation", head }),
        );
        await writeFile(
            join(directory, "README.md"),
            "# Updated documentation\n",
        );
        await validate(directory, "documentation");
        await writeFile(
            join(directory, "README.md"),
            "[Missing](docs/missing.md)\n",
        );
        await assert.rejects(
            validate(directory, "documentation"),
            /Broken documentation link/,
        );
        await writeFile(join(directory, "README.md"), "# Valid\n");
        await mkdir(join(directory, "docs"));
        await writeFile(
            join(directory, "docs/new.md"),
            "[Missing](absent.md)\n",
        );
        await assert.rejects(
            validate(directory, "documentation"),
            /Broken documentation link/,
        );
        await rm(join(directory, "docs/new.md"));
        await writeFile(join(directory, "source.cs"), "class Source {}");
        await assert.rejects(
            validate(directory, "documentation"),
            /unauthorized/,
        );
    });
});
test("CI validation rejects weakened trigger and dependency coverage even when inventory matches", async () => {
    await fixture(async (directory) => {
        await copyFile(join(root, "Makefile"), join(directory, "Makefile"));
        const baseline = await loadPolicy(directory);
        for (const rule of Object.values(baseline.groups))
            if (rule.projectManifests) rule.projectManifests = [];
        const path = join(directory, "config/ci-verification.json");
        await writeFile(path, JSON.stringify(baseline, null, 2) + "\n");
        const head = commit(directory);
        await mkdir(join(directory, ".artifacts/agentic-maintenance"), {
            recursive: true,
        });
        await writeFile(
            join(directory, ".artifacts/agentic-maintenance/packet.json"),
            JSON.stringify({ mode: "ci-policy", head }),
        );
        await validate(directory, "ci-policy");
        for (const weaken of [
            (p: typeof baseline) => {
                p.groups.csharp!.requires = [];
            },
            (p: typeof baseline) => {
                p.groups.rust!.extensions = [];
            },
            (p: typeof baseline) => {
                delete p.unsupportedLanguages.Go;
            },
            (p: typeof baseline) => {
                delete p.groups.rust!.projectManifests;
            },
            (p: typeof baseline) => {
                p.groups.javascript!.dependentGroups = [];
            },
        ]) {
            const proposal = structuredClone(baseline);
            weaken(proposal);
            await writeFile(path, JSON.stringify(proposal, null, 2) + "\n");
            await assert.rejects(
                validate(directory, "ci-policy"),
                /Cannot remove|guardrails/,
            );
        }
    });
});
test("deleting obsolete documentation requires repairing encoded, titled and reference incoming links", async () => {
    await fixture(async (directory) => {
        await mkdir(join(directory, "docs"));
        await writeFile(
            join(directory, "docs/old guide.md"),
            "# Obsolete guide\n",
        );
        await writeFile(
            join(directory, "README.md"),
            '[Old](docs/old%20guide.md "Guide")\n[Other][obsolete]\n\n[obsolete]: <docs/old guide.md>\n',
        );
        const head = commit(directory);
        await mkdir(join(directory, ".artifacts/agentic-maintenance"), {
            recursive: true,
        });
        await writeFile(
            join(directory, ".artifacts/agentic-maintenance/packet.json"),
            JSON.stringify({ mode: "documentation", head }),
        );
        await rm(join(directory, "docs/old guide.md"));
        await assert.rejects(
            validate(directory, "documentation"),
            /incoming links/,
        );
        await writeFile(
            join(directory, "README.md"),
            "# Current documentation\n",
        );
        await validate(directory, "documentation");
    });
});
test("proposal snapshots preserve feature history and bundles must contain the committed scoped edits", async () => {
    await fixture(async (directory) => {
        const baseline = commit(directory);
        git(directory, ["switch", "-c", "feature"]);
        await writeFile(join(directory, "source.cs"), "class Source {}\n");
        const head = commit(directory);
        const artifacts = join(directory, ".artifacts/agentic-maintenance");
        await mkdir(artifacts, { recursive: true });
        await writeFile(
            join(artifacts, "packet.json"),
            JSON.stringify({ mode: "documentation", head }),
        );
        await writeFile(join(directory, "README.md"), "# Repaired guide\n");
        await assert.rejects(
            snapshotProposal(directory, "documentation", {
                ...owner,
                [Env.GITHUB_TRIGGERING_ACTOR.name]: "contributor",
            }),
            /owner-initiated/,
        );
        const branch = "gh-aw/documentation-" + head.slice(0, 12);
        await writeFile(
            join(artifacts, "agent_output.json"),
            JSON.stringify({
                items: [{ type: "create_pull_request", branch }],
            }),
        );
        await assert.rejects(
            validateProposal(directory, "documentation", artifacts),
            /no committed/,
        );
        assert.equal(
            await snapshotProposal(directory, "documentation", owner),
            true,
        );
        assert.equal(git(directory, ["branch", "--show-current"]), branch);
        assert.equal(
            git(directory, ["diff", "--name-only", head]),
            "README.md",
        );
        assert.ok(
            git(directory, ["diff", "--name-only", baseline]).includes(
                "source.cs",
            ),
        );
        const snapshot = git(directory, ["rev-parse", "HEAD"]);
        const bundle = join(artifacts, "aw-proposal.bundle");
        git(directory, ["bundle", "create", bundle, branch, "^" + head]);
        await validateProposal(directory, "documentation", artifacts);
        await writeFile(
            join(directory, "README.md"),
            "# Later uncommitted repair\n",
        );
        await assert.rejects(
            validateProposal(directory, "documentation", artifacts),
            /Uncommitted/,
        );
        git(directory, ["restore", "README.md"]);
        git(directory, ["branch", "old-proposal", head]);
        await rm(bundle);
        git(directory, [
            "bundle",
            "create",
            bundle,
            "old-proposal",
            "^" + baseline,
        ]);
        await assert.rejects(
            validateProposal(directory, "documentation", artifacts),
            /bundle omits/,
        );
        assert.equal(git(directory, ["rev-parse", "HEAD"]), snapshot);
    });
});
test("snapshots skip unchanged documents and reject unauthorized files before creating a commit", async () => {
    await fixture(async (directory) => {
        const head = commit(directory);
        await mkdir(join(directory, ".artifacts/agentic-maintenance"), {
            recursive: true,
        });
        await writeFile(
            join(directory, ".artifacts/agentic-maintenance/packet.json"),
            JSON.stringify({ mode: "documentation", head }),
        );
        assert.equal(
            await snapshotProposal(directory, "documentation", owner),
            false,
        );
        await writeFile(join(directory, "source.cs"), "class Source {}\n");
        await assert.rejects(
            snapshotProposal(directory, "documentation", owner),
            /unauthorized/,
        );
        assert.equal(git(directory, ["rev-parse", "HEAD"]), head);
    });
});
test("compiled workflows enforce owner-only inference and publication, cost limits, no broad tools, and successful validation before publication", async () => {
    for (const mode of ["ci-policy", "documentation"]) {
        const source = await readFile(
            join(root, ".github/workflows/" + mode + ".md"),
            "utf8",
        );
        const compiled = await readFile(
            join(root, ".github/workflows/" + mode + ".lock.yml"),
            "utf8",
        );
        assert.ok(
            !/pull_request:|workflow_run:|issue_comment:|schedule:/.test(
                source,
            ),
        );
        assert.ok(source.includes("max-ai-credits: 40"));
        assert.ok(source.includes("max-ai-credits: 10"));
        assert.ok(source.includes("max-turns: 12"));
        assert.ok(source.includes('GH_AW_HARNESS_MAX_RETRIES: "0"'));
        assert.ok(source.includes("github: false"));
        assert.ok(source.includes("queue: max"));
        assert.ok(source.includes('base-branch: "${{ github.ref_name }}"'));
        const config = compiled
            .split("GH_AW_SAFE_OUTPUTS_HANDLER_CONFIG: ")[1]!
            .split("\n")[0]!;
        const handler = JSON.parse(JSON.parse(config));
        assert.equal(
            handler.create_pull_request.base_branch,
            "${{ github.ref_name }}",
        );
        const codexConfigLine = compiled
            .split("GH_AW_CODEX_CONFIG_JSON: '")[1]!
            .split("'\n")[0]!;
        const codexConfig = JSON.parse(codexConfigLine);
        for (const name of [
            Env.GITHUB_ACTOR.name,
            Env.GITHUB_TRIGGERING_ACTOR.name,
            Env.GITHUB_REPOSITORY.name,
            Env.GITHUB_EVENT_NAME.name,
            Env.GITHUB_REF.name,
        ])
            assert.ok(
                codexConfig.defaults.shell_environment_policy.include_only.includes(
                    name,
                ),
                "Snapshot authorization metadata must reach the isolated shell: " +
                    name,
            );
        assert.match(compiled, /GH_AW_REPORT_INCOMPLETE_CREATE_ISSUE: "false"/);
        assert.ok(!compiled.includes('create_report_incomplete_issue\\":{}'));
        for (const job of [
            "prepare",
            "activation",
            "agent",
            "detection",
            "safe_outputs",
            "conclusion",
        ]) {
            const block = compiled
                .split("  " + job + ":\n")[1]!
                .split(/\n  [a-z_]+:\n/)[0]!;
            assert.match(block, /github\.actor == 'jgador'/);
            assert.match(block, /github\.triggering_actor == 'jgador'/);
            if (job === "detection" || job === "safe_outputs")
                assert.ok(block.includes("needs.agent.result == 'success'"));
        }
        assert.match(compiled, /--exclude-env OPENAI_API_KEY/);
        assert.ok(
            compiled.includes(
                "node scripts/agentic-maintenance.mts validate-proposal " +
                    mode,
            ),
        );
        assert.ok(!compiled.includes("github-mcp-server"));
    }
});
