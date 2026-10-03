import test from "node:test";
import assert from "node:assert/strict";
import {
    mkdtemp,
    mkdir,
    copyFile,
    readFile,
    writeFile,
    rm,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import {
    csharpValues,
    rustValues,
    generate,
} from "./generate-contract-values.mts";
import {
    WorkStatus,
    AttentionReason,
    WorkEventKind,
    WorkAction,
    IdentityKind,
    GitRepositoryAuthorizationStatus,
    GitRepositoryOperationState,
    isContractValue,
} from "../frontend/src/api/values.ts";

test("C# extraction preserves explicit wire names and rejects syntax it cannot safely interpret", () => {
    assert.deepEqual(
        csharpValues(
            'public enum Example { [JsonStringEnumMemberName("apiKey")] ApiKey, ChatGPT }',
            ["Example"],
        ),
        { Example: { ApiKey: "apiKey", ChatGPT: "ChatGPT" } },
    );
    for (const source of [
        "public enum Example { One = 1 }",
        "public enum Example { One, One }",
        'public enum Example { [JsonStringEnumMemberName("same")] One, [JsonStringEnumMemberName("same")] Two }',
    ])
        assert.throws(() => csharpValues(source, ["Example"]));
    assert.throws(() => csharpValues("public enum Other { One }", ["Example"]));
});

test("Rust extraction preserves CLI spellings and rejects duplicate or opaque definitions", () => {
    assert.deepEqual(
        rustValues(
            'contract_values!(Action { PullRequest => "pull-request", Ready => "ready" });',
        ),
        { Action: { PullRequest: "pull-request", Ready: "ready" } },
    );
    assert.throws(() =>
        rustValues(
            'contract_values!(Action { One => "same", Two => "same" });',
        ),
    );
    assert.throws(() =>
        rustValues("contract_values!(Action { One => UNKNOWN });"),
    );
});

test("generated files stay in sync and the check fails after a browser-only edit", async (t) => {
    await generate(process.cwd(), true);
    const root = await mkdtemp(join(tmpdir(), "goblin-contracts-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const script = await readFile(
        "scripts/generate-contract-values.mts",
        "utf8",
    );
    const paths = [...script.matchAll(/"(backend\/src\/[^"]+\.cs)":/g)].map(
        (match) => match[1]!,
    );
    paths.push("tools/goblinctl/src/contract_values.rs");
    for (const path of paths) {
        await mkdir(dirname(join(root, path)), { recursive: true });
        await copyFile(path, join(root, path));
    }
    for (const dir of ["frontend/src/api", "deploy/azure/setup"])
        await mkdir(join(root, dir), { recursive: true });
    await generate(root, false);
    await generate(root, true);
    await writeFile(
        join(root, "frontend/src/api/values.ts"),
        "export const WorkStatus = {};\n",
    );
    await assert.rejects(generate(root, true), /Stale generated contracts/);
});

test("runtime guards reject values outside the owning definition", () => {
    assert.equal(isContractValue(WorkStatus, "NeedsAttention"), true);
    for (const value of ["Working", "needsattention", 2, null, {}, " Ready"])
        assert.equal(isContractValue(WorkStatus, value), false);
});

test("Git repository identifiers match C# while existing wire values remain stable", () => {
    assert.equal(AttentionReason.GitRepositoryRequired, "RepositoryRequired");
    assert.equal(WorkEventKind.GitRepositoryRequested, "RepositoryRequested");
    assert.equal(WorkEventKind.GitRepositoryAuthorized, "RepositoryAuthorized");
    assert.equal(WorkEventKind.GitRepositoryDenied, "RepositoryDenied");
    assert.equal(
        WorkEventKind.GitRepositoryAuthorizationInvalidated,
        "RepositoryAuthorizationInvalidated",
    );
    assert.equal(WorkAction.PrepareGitRepository, "PrepareRepository");
    assert.equal(WorkAction.AuthorizeGitRepository, "AuthorizeRepository");
    assert.equal(WorkAction.DenyGitRepository, "DenyRepository");
    assert.equal(IdentityKind.GitRepositoryOperation, "RepositoryOperation");
    assert.equal(GitRepositoryAuthorizationStatus.Pending, "Pending");
    assert.equal(GitRepositoryOperationState.Succeeded, "Succeeded");
    assert.equal(
        isContractValue(AttentionReason, "GitRepositoryRequired"),
        false,
    );
    assert.equal(isContractValue(WorkAction, "PrepareGitRepository"), false);
});

test("setup decoder rejects unsupported status, phase, step IDs, and step states", async () => {
    const { isSetupState } = await import(
        new URL("../deploy/azure/setup/contract-values.js", import.meta.url)
            .href
    );
    const state = {
        version: 1,
        status: "running",
        phase: "installing",
        steps: [{ id: "database", status: "waiting" }],
    };
    assert.equal(isSetupState(state), true);
    for (const changed of [
        { ...state, status: "finished" },
        { ...state, phase: "running" },
        { ...state, steps: [{ id: "unknown", status: "waiting" }] },
        { ...state, steps: [{ id: "database", status: "ready" }] },
    ])
        assert.equal(isSetupState(changed), false);
});
