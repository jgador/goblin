import assert from "node:assert/strict";
import {
    existsSync,
    mkdtempSync,
    readFileSync,
    rmSync,
    writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, test, type TestContext } from "node:test";
import {
    approveCandidate,
    bumpVersion,
    nextVersion,
    parseCandidate,
    pinCommit,
    prepare,
    resolve,
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
const cargoManifestFixture = `[workspace.package]
version = "0.1.2"
`;
const cargoLockFixture = `version = 4

[[package]]
name = "goblinctl"
version = "0.1.2"

[[package]]
name = "xtask"
version = "0.1.2"
`;

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
        if (result instanceof Response) return result;
        return new Response(JSON.stringify(result ?? {}), {
            status: 200,
            headers: { "content-type": "application/json" },
        });
    };
}

function workflowDirectory(context: TestContext): string {
    const directory = mkdtempSync(join(tmpdir(), "goblin-release-test-"));
    context.after(() => rmSync(directory, { recursive: true, force: true }));
    process.env.RUNNER_TEMP = directory;
    process.env.GITHUB_EVENT_PATH = join(directory, "event.json");
    process.env.GITHUB_OUTPUT = join(directory, "output");
    return directory;
}

const currentBase = "d".repeat(40);
const mergeSha = "e".repeat(40);

for (const trigger of ["workflow_dispatch", "pull_request"]) {
    test(`resolution uses the current base ref with stale PR metadata: ${trigger}`, async (context) => {
        const directory = workflowDirectory(context);
        writeFileSync(
            process.env.GITHUB_EVENT_PATH!,
            JSON.stringify(
                trigger === "workflow_dispatch"
                    ? { inputs: { pr: "13" } }
                    : { pull_request: parent },
            ),
        );
        mockApi((path, method) => {
            assert.equal(method, "GET");
            if (path === "pulls/13") return parent;
            if (path === "git/ref/heads/master")
                return { object: { sha: currentBase } };
            if (path === "git/ref/pull/13/merge")
                return { object: { sha: mergeSha } };
            if (path === `git/commits/${mergeSha}`)
                return {
                    parents: [{ sha: currentBase }, { sha: parent.head.sha }],
                };
            throw new Error(`Unexpected API request: ${path}`);
        });
        await resolve();
        assert.deepEqual(
            JSON.parse(
                readFileSync(join(directory, "release-context.json"), "utf8"),
            ),
            {
                pr: parent.number,
                head: parent.head.sha,
                merge: mergeSha,
                base: "master",
            },
        );
        assert.equal(
            readFileSync(process.env.GITHUB_OUTPUT!, "utf8"),
            `sha=${mergeSha}\nhead=${parent.head.sha}\npr=13\nbase=master\n`,
        );
    });
}

for (const scenario of [
    {
        name: "stale target branch",
        parents: [parent.base.sha, parent.head.sha],
    },
    { name: "stale PR head", parents: [currentBase, head] },
]) {
    test(`resolution rejects a merge containing a ${scenario.name}`, async (context) => {
        const directory = workflowDirectory(context);
        writeFileSync(
            process.env.GITHUB_EVENT_PATH!,
            JSON.stringify({ inputs: { pr: "13" } }),
        );
        mockApi((path, method) => {
            assert.equal(method, "GET");
            if (path === "pulls/13") return parent;
            if (path === "git/ref/heads/master")
                return { object: { sha: currentBase } };
            if (path === "git/ref/pull/13/merge")
                return { object: { sha: mergeSha } };
            if (path === `git/commits/${mergeSha}`)
                return { parents: scenario.parents.map((sha) => ({ sha })) };
            throw new Error(`Unexpected API request: ${path}`);
        });
        await assert.rejects(resolve, /not prepared the current merge commit/);
        assert.equal(
            existsSync(join(directory, "release-context.json")),
            false,
        );
        assert.equal(existsSync(process.env.GITHUB_OUTPUT!), false);
    });
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

for (const scenario of [
    {
        name: "first companion",
        history: [],
        refs: [],
        branch: candidate.head.ref,
    },
    {
        name: "merged companion with its branch retained",
        history: [
            {
                ...candidate,
                state: "closed",
                merged_at: "2026-09-27T21:37:09Z",
            },
        ],
        refs: [candidate.head.ref],
        branch: `${candidate.head.ref}-2`,
    },
    {
        name: "closed companion with its branch deleted",
        history: [{ ...candidate, state: "closed" }],
        refs: [],
        branch: `${candidate.head.ref}-2`,
    },
    {
        name: "branch left without a PR",
        history: [],
        refs: [candidate.head.ref],
        branch: `${candidate.head.ref}-2`,
    },
    {
        name: "repeated recovery with occupied suffixes",
        history: [
            { ...candidate, state: "closed" },
            {
                ...candidate,
                state: "closed",
                head: { ...candidate.head, ref: `${candidate.head.ref}-2` },
            },
        ],
        refs: [`${candidate.head.ref}-3`],
        branch: `${candidate.head.ref}-4`,
    },
]) {
    test(`release preparation preserves history and dispatches CI: ${scenario.name}`, async () => {
        const writes: { path: string; body: Record<string, unknown> }[] = [];
        mockApi((path, method, body) => {
            if (method !== "GET") writes.push({ path, body });
            if (path.startsWith("pulls?")) {
                assert.equal(path, "pulls?state=all&per_page=100&page=1");
                return scenario.history;
            }
            if (path === `git/matching-refs/heads/${candidate.head.ref}`)
                return scenario.refs.map((ref) => ({
                    ref: `refs/heads/${ref}`,
                }));
            if (path.startsWith("releases?"))
                return [{ tag_name: "goblinctl-v0.1.2" }];
            if (path.startsWith("contents/"))
                return {
                    encoding: "base64",
                    content: Buffer.from(
                        path.includes("Cargo.lock")
                            ? cargoLockFixture
                            : cargoManifestFixture,
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
        assert.equal(pull.body.head, scenario.branch);
        assert.deepEqual(parseCandidate(String(pull.body.body)), {
            parent: parent.number,
            source: sha,
            version: "0.1.3",
        });
        assert.match(String(pull.body.title), /0.1.3/);
        assert.deepEqual(
            writes.find((value) => value.path === "git/refs")?.body,
            {
                ref: `refs/heads/${scenario.branch}`,
                sha,
            },
        );
        assert.ok(
            writes.some(
                (value) =>
                    value.path ===
                    "actions/workflows/goblinctl-dependency.yml/dispatches",
            ),
        );
        assert.deepEqual(
            writes
                .filter((value) => value.path.startsWith("git/refs/heads/"))
                .map((value) => ({
                    path: value.path,
                    force: value.body.force,
                })),
            [{ path: `git/refs/heads/${scenario.branch}`, force: false }],
        );
    });
}

for (const ref of [candidate.head.ref, `${candidate.head.ref}-2`]) {
    test(`rerunning preparation preserves the reviewed companion ${ref}`, async () => {
        const writes: string[] = [];
        mockApi((path, method) => {
            if (method !== "GET") writes.push(path);
            if (path.startsWith("pulls?"))
                return [
                    { ...candidate, state: "closed" },
                    { ...candidate, head: { ...candidate.head, ref } },
                ];
            if (path.includes("/comments?")) return [];
            return {};
        });
        await prepare(parent, "d".repeat(40), report);
        assert.deepEqual(writes, ["issues/13/comments"]);
    });
}

test("branch discovery failures stop preparation before any writes", async () => {
    mockApi((path, method) => {
        assert.equal(method, "GET");
        if (path.startsWith("pulls?")) return [];
        assert.equal(path, `git/matching-refs/heads/${candidate.head.ref}`);
        return new Response("Forbidden", { status: 403 });
    });
    await assert.rejects(prepare(parent, sha, report), /403 Forbidden/);
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

for (const status of ["behind", "ahead"]) {
    test(`publication checks the live master ref when the candidate is ${status}`, async (context) => {
        workflowDirectory(context);
        process.env.RELEASE_PR = "14";
        process.env.RELEASE_SHA = head;
        process.env.GITHUB_ACTOR = "maintainer";
        mockApi((path, method) => {
            assert.equal(method, "GET");
            if (path.startsWith("collaborators/"))
                return { permission: "admin" };
            if (path === "pulls/14") return candidate;
            if (path === "pulls/13") return parent;
            if (path === `compare/${parent.head.sha}...${head}`)
                return { status: "ahead" };
            if (path === "git/ref/heads/master")
                return { object: { sha: currentBase } };
            if (path === `compare/${currentBase}...${head}`) return { status };
            if (path === `contents/Cargo.toml?ref=${head}`)
                return {
                    encoding: "base64",
                    content: Buffer.from(
                        '[workspace.package]\nversion = "0.1.1"\n',
                    ).toString("base64"),
                };
            throw new Error(`Unexpected API request: ${path}`);
        });
        if (status === "behind") {
            await assert.rejects(
                approveCandidate,
                /Update the candidate from master/,
            );
            assert.equal(existsSync(process.env.GITHUB_OUTPUT!), false);
        } else {
            await approveCandidate();
            assert.equal(
                readFileSync(process.env.GITHUB_OUTPUT!, "utf8"),
                `sha=${head}\nversion=0.1.1\npr=14\n`,
            );
        }
    });
}

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
