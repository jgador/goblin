import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { afterEach, test } from "node:test";
import {
    approveCandidate,
    bumpVersion,
    nextVersion,
    parseCandidate,
    pinCommit,
    prepare,
} from "./goblinctl-release.mts";

const originalFetch = globalThis.fetch;
const originalEnvironment = { ...process.env };
afterEach(() => {
    globalThis.fetch = originalFetch;
    process.env = { ...originalEnvironment };
});
const sha = "a".repeat(40);
const head = "b".repeat(40);
const parent = {
    number: 13,
    state: "open",
    draft: false,
    body: "",
    html_url: "https://github.com/jgador/goblin/pull/13",
    head: { ref: "feature", sha, repo: { full_name: "jgador/goblin" } },
    base: {
        ref: "master",
        sha: "c".repeat(40),
        repo: { full_name: "jgador/goblin" },
    },
};
const candidate = {
    ...parent,
    number: 14,
    head: { ...parent.head, ref: "automation/goblinctl/pr-13", sha: head },
    base: { ...parent.base, ref: "feature" },
    body: `<!-- goblinctl-candidate ${JSON.stringify({ parent: 13, source: sha, version: "0.1.1" })} -->`,
};
const report = {
    schemaVersion: 1,
    outcome: "release-required" as const,
    message: "Embedded installer changed",
    changedInputs: ["deploy/azure/install-app.sh"],
};

function mockApi(
    handler: (
        path: string,
        method: string,
        body: Record<string, unknown>,
    ) => unknown,
): void {
    process.env.GH_TOKEN = "test-token";
    globalThis.fetch = async (input, init) => {
        const path = String(input).replace(
            "https://api.github.com/repos/jgador/goblin/",
            "",
        );
        const result = handler(
            path,
            init?.method ?? "GET",
            JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>,
        );
        return new Response(JSON.stringify(result ?? {}), {
            status: 200,
            headers: { "content-type": "application/json" },
        });
    };
}

test("version proposal accounts for published and reserved versions", () => {
    assert.equal(
        nextVersion(["0.1.0", "0.1.2", "0.1.10", "unrelated-tag"]),
        "0.1.11",
    );
    const cargo = readFileSync("Cargo.toml", "utf8");
    const lock = readFileSync("Cargo.lock", "utf8");
    const [nextCargo, nextLock] = bumpVersion(cargo, lock, "0.1.11");
    assert.match(nextCargo, /\[workspace\.package\]\nversion = "0.1.11"/);
    assert.match(nextLock, /name = "goblinctl"\nversion = "0.1.11"/);
    assert.match(nextLock, /name = "xtask"\nversion = "0.1.11"/);
    const current = cargo.match(
        /\[workspace\.package\][\s\S]*?\nversion = "([^"]+)"/,
    )![1];
    assert.equal(bumpVersion(nextCargo, nextLock, current)[1], lock);
    assert.throws(() => bumpVersion(cargo, "unexpected lockfile", "0.1.1"));
    assert.throws(() => bumpVersion(cargo, lock, "01.1.1"));
});

test("release preparation targets the feature branch and explicitly dispatches CI", async () => {
    const writes: { path: string; body: Record<string, unknown> }[] = [];
    mockApi((path, method, body) => {
        if (method !== "GET") writes.push({ path, body });
        if (path.startsWith("pulls?")) return [];
        if (path.startsWith("releases?"))
            return [{ tag_name: "goblinctl-v0.1.2" }];
        if (path.startsWith("contents/"))
            return {
                encoding: "base64",
                content: Buffer.from(
                    readFileSync(
                        path.includes("Cargo.lock")
                            ? "Cargo.lock"
                            : "Cargo.toml",
                    ),
                ).toString("base64"),
            };
        if (path === `git/commits/${sha}`) return { tree: { sha } };
        if (path === "git/trees" || path === "git/commits")
            return { sha: head };
        if (path === "pulls")
            return { ...candidate, html_url: "https://example.test/14" };
        if (path.includes("/comments?")) return [];
        return {};
    });
    await prepare(parent, sha, report);
    const pull = writes.find((value) => value.path === "pulls")!;
    assert.equal(pull.body.base, "feature");
    assert.match(String(pull.body.title), /0.1.3/);
    assert.ok(
        writes.some(
            (value) =>
                value.path ===
                "actions/workflows/goblinctl-dependency.yml/dispatches",
        ),
    );
    assert.ok(
        writes
            .filter((value) => value.path.startsWith("git/refs/heads/"))
            .every((value) => value.body.force === false),
    );
});

test("rerunning preparation preserves an existing reviewed companion", async () => {
    const writes: string[] = [];
    mockApi((path, method) => {
        if (method !== "GET") writes.push(path);
        if (path.startsWith("pulls?")) return [candidate];
        if (path.includes("/comments?")) return [];
        return {};
    });
    await prepare(parent, "d".repeat(40), report);
    assert.deepEqual(writes, ["issues/13/comments"]);
});

test("publication rejects a moved candidate or an outdated parent", async () => {
    process.env.RELEASE_PR = "14";
    process.env.RELEASE_SHA = sha;
    process.env.GITHUB_ACTOR = "maintainer";
    mockApi((path) => {
        if (path.startsWith("collaborators/")) return { permission: "admin" };
        if (path === "pulls/14") return candidate;
        if (path === "pulls/13") return parent;
        if (path.startsWith("compare/")) return { status: "diverged" };
        throw new Error(`Unexpected API request: ${path}`);
    });
    await assert.rejects(approveCandidate, /selected open/);
    process.env.RELEASE_SHA = head;
    await assert.rejects(approveCandidate, /latest parent changes/);
});

test("pin updates reject stale candidate SHAs before writing", async () => {
    process.env.RELEASE_PR = "14";
    process.env.RELEASE_SHA = sha;
    mockApi((path, method) => {
        assert.equal(method, "GET");
        assert.equal(path, "pulls/14");
        return candidate;
    });
    await assert.rejects(pinCommit, /changed while pinning/);
});

test("pin commits keep catalog and lock updates with the authenticated release outputs", async () => {
    process.env.RELEASE_PR = "14";
    process.env.RELEASE_SHA = head;
    process.env.PIN_DIRECTORY = ".";
    let committed: { path: string; content: string }[] = [];
    mockApi((path, method, body) => {
        if (path === "pulls/14") return candidate;
        if (path === `git/commits/${head}`) return { tree: { sha: head } };
        if (path === "git/trees") {
            committed = body.tree as typeof committed;
            return { sha };
        }
        if (path === "git/commits") return { sha };
        if (path.includes("/comments?")) return [];
        if (method === "PATCH") assert.equal(body.force, false);
        return {};
    });
    await pinCommit();
    assert.deepEqual(
        committed.map((value) => value.path),
        [
            "dependencies.toml",
            "dependencies.lock.json",
            "deploy/goblinctl-release.json",
            "deploy/azure/azuredeploy.json",
            "deploy/azure/azuredeploy.portal.json",
        ],
    );
    assert.equal(
        committed[0].content,
        readFileSync("dependencies.toml", "utf8"),
    );
});

test("candidate metadata cannot supply shell expressions or unrelated PRs", () => {
    assert.equal(parseCandidate(candidate.body).parent, 13);
    assert.throws(() => parseCandidate("ordinary PR"));
    assert.throws(() =>
        parseCandidate(
            `<!-- goblinctl-candidate {"parent":-1,"source":"${sha}","version":"0.1.1"} -->`,
        ),
    );
    assert.throws(() =>
        parseCandidate(
            `<!-- goblinctl-candidate {"parent":13,"source":"${sha}","version":"$(command)"} -->`,
        ),
    );
});
