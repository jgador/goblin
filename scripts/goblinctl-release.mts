import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
    appendFileSync,
    mkdtempSync,
    readFileSync,
    rmSync,
    writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

type Ref = { ref: string; sha: string; repo: { full_name: string } | null };
type Pull = {
    number: number;
    state: string;
    draft: boolean;
    head: Ref;
    base: Ref;
    body: string | null;
    html_url: string;
};
type Candidate = { parent: number | null; source: string; version: string };
type Context = { pr: number | null; head: string; merge: string; base: string };
type Report = {
    schemaVersion: number;
    outcome:
        "ready" | "release-required" | "pin-required" | "verification-failed";
    message: string;
    changedInputs: string[];
};

const dependencyWorkflow = "goblinctl-dependency.yml";
const marker = "<!-- goblinctl-release-dependency -->";
const botPrefix = "automation/goblinctl/";
const repository = process.env.GITHUB_REPOSITORY ?? "jgador/goblin";

function requireValue(value: string | undefined, name: string): string {
    if (!value) throw new Error(`Missing ${name}`);
    return value;
}

async function api<T>(
    path: string,
    method = "GET",
    body?: unknown,
): Promise<T> {
    const response = await fetch(
        `https://api.github.com/repos/${repository}/${path}`,
        {
            method,
            headers: {
                Authorization: `Bearer ${requireValue(process.env.GH_TOKEN, "GH_TOKEN")}`,
                Accept: "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
                "Content-Type": "application/json",
            },
            body: body === undefined ? undefined : JSON.stringify(body),
        },
    );
    if (!response.ok)
        throw new Error(
            `GitHub ${method} ${path}: ${response.status} ${await response.text()}`,
        );
    return response.status === 204
        ? (undefined as T)
        : ((await response.json()) as T);
}

function event<T>(): T {
    return JSON.parse(
        readFileSync(
            requireValue(process.env.GITHUB_EVENT_PATH, "GITHUB_EVENT_PATH"),
            "utf8",
        ),
    ) as T;
}

function output(values: Record<string, string | number>): void {
    const destination = requireValue(
        process.env.GITHUB_OUTPUT,
        "GITHUB_OUTPUT",
    );
    for (const [key, value] of Object.entries(values)) {
        if (String(value).includes("\n"))
            throw new Error("Multiline workflow output");
        appendFileSync(destination, `${key}=${value}\n`);
    }
}

export function parseCandidate(body: string | null): Candidate {
    const match = body?.match(/<!-- goblinctl-candidate (\{[^\n]+\}) -->/);
    if (!match) throw new Error("This is not a goblinctl companion release PR");
    const value = JSON.parse(match[1]) as Candidate;
    if (
        !(
            value.parent === null ||
            (Number.isSafeInteger(value.parent) && value.parent > 0)
        ) ||
        !/^[a-f0-9]{40}$/.test(value.source) ||
        !/^\d+\.\d+\.\d+$/.test(value.version)
    ) {
        throw new Error("Invalid release candidate metadata");
    }
    return value;
}

export function bumpVersion(
    manifest: string,
    lock: string,
    version: string,
): [string, string] {
    if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version))
        throw new Error("Invalid release version");
    let manifestCount = 0;
    manifest = manifest.replace(
        /(\[workspace\.package\][\s\S]*?\nversion = ")[^"]+("\n)/,
        (_, before: string, after: string) => {
            manifestCount++;
            return before + version + after;
        },
    );
    let lockCount = 0;
    lock = lock.replace(
        /(\[\[package\]\]\nname = "(?:goblinctl|xtask)"\nversion = ")[^"]+("\n)/g,
        (_, before: string, after: string) => {
            lockCount++;
            return before + version + after;
        },
    );
    if (manifestCount !== 1 || lockCount !== 2)
        throw new Error("Unexpected Cargo workspace version layout");
    return [manifest, lock];
}

export function nextVersion(versions: string[]): string {
    const triples = versions
        .filter((v) => /^\d+\.\d+\.\d+$/.test(v))
        .map((v) => v.split(".").map(Number));
    triples.sort((a, b) => b[0] - a[0] || b[1] - a[1] || b[2] - a[2]);
    const [major, minor, patch] = triples[0] ?? [0, 1, 0];
    if (![major, minor, patch + 1].every(Number.isSafeInteger))
        throw new Error("Version exceeds safe integer range");
    return `${major}.${minor}.${patch + 1}`;
}

async function contents(path: string, ref: string): Promise<string> {
    const value = await api<{ content: string; encoding: string }>(
        `contents/${path}?ref=${encodeURIComponent(ref)}`,
    );
    if (value.encoding !== "base64")
        throw new Error(`Unexpected content encoding for ${path}`);
    return Buffer.from(value.content, "base64").toString("utf8");
}

async function branchHead(ref: string): Promise<string> {
    const value = await api<{ object: { sha: string } }>(
        `git/ref/heads/${encodeURIComponent(ref)}`,
    );
    return value.object.sha;
}

async function pulls(): Promise<Pull[]> {
    const result: Pull[] = [];
    for (let page = 1; ; page++) {
        const batch = await api<Pull[]>(
            `pulls?state=all&per_page=100&page=${page}`,
        );
        result.push(...batch);
        if (batch.length < 100) return result;
    }
}

async function comment(number: number, message: string): Promise<void> {
    const comments = await api<
        { id: number; body: string; user: { type: string } }[]
    >(`issues/${number}/comments?per_page=100`);
    const existing = comments.find(
        (value) => value.user.type === "Bot" && value.body.startsWith(marker),
    );
    const body = `${marker}\n${message}`;
    if (existing)
        await api(`issues/comments/${existing.id}`, "PATCH", { body });
    else await api(`issues/${number}/comments`, "POST", { body });
}

async function dispatch(pr: number): Promise<void> {
    // workflow_dispatch is intentionally explicit: GITHUB_TOKEN-created pushes/PRs do not trigger CI.
    await api(`actions/workflows/${dependencyWorkflow}/dispatches`, "POST", {
        ref: "master",
        inputs: { pr: String(pr) },
    });
}

async function commitFiles(
    branch: string,
    parent: string,
    changes: Record<string, string>,
    message: string,
): Promise<string> {
    const commit = await api<{ tree: { sha: string } }>(
        `git/commits/${parent}`,
    );
    const tree = await api<{ sha: string }>("git/trees", "POST", {
        base_tree: commit.tree.sha,
        tree: Object.entries(changes).map(([path, content]) => ({
            path,
            mode: "100644",
            type: "blob",
            content,
        })),
    });
    const created = await api<{ sha: string }>("git/commits", "POST", {
        message,
        tree: tree.sha,
        parents: [parent],
    });
    await api(`git/refs/heads/${branch}`, "PATCH", {
        sha: created.sha,
        force: false,
    });
    return created.sha;
}

export async function prepare(
    pr: Pull | null,
    sha: string,
    report: Report,
): Promise<void> {
    if (pr?.head.repo?.full_name !== repository && pr !== null) {
        await comment(
            pr.number,
            `${report.message}\n\nA maintainer must prepare an installer release from a branch in this repository. Fork code is never given release credentials.`,
        );
        return;
    }
    const history = (await pulls()).filter(
        (value) =>
            value.head.repo?.full_name === repository &&
            value.head.ref.startsWith(botPrefix),
    );
    const existing = history.filter((value) => value.state === "open");
    const baseBranch = `${botPrefix}${pr ? `pr-${pr.number}` : `master-${sha.slice(0, 12)}`}`;
    const branchPattern = new RegExp(`^${baseBranch}(?:-[1-9][0-9]*)?$`);
    const companion = existing.find(
        (value) =>
            branchPattern.test(value.head.ref) &&
            value.base.ref === (pr?.head.ref ?? "master"),
    );
    if (companion) {
        // Never overwrite a reviewed candidate or human changes. New parent changes require an explicit refresh.
        const candidate = parseCandidate(companion.body);
        if (candidate.parent !== (pr?.number ?? null))
            throw new Error("Companion candidate does not match its parent");
        if (pr)
            await comment(
                pr.number,
                `${report.message}\n\nRelease PR: ${companion.html_url}\n${candidate.source === sha ? "" : "The parent branch changed. Merge its latest changes into the companion branch, resolve any conflicts, then review and publish the new candidate SHA."}`,
            );
        return;
    }
    // Closed PR branches and branches left by interrupted preparation belong to
    // earlier work. Reserve their names even if GitHub deleted the branch.
    const refs = await api<{ ref: string }[]>(
        `git/matching-refs/heads/${baseBranch}`,
    );
    const occupied = new Set([
        ...history.map((value) => `refs/heads/${value.head.ref}`),
        ...refs.map((value) => value.ref),
    ]);
    let branch = baseBranch;
    for (let suffix = 2; occupied.has(`refs/heads/${branch}`); suffix++) {
        branch = `${baseBranch}-${suffix}`;
    }
    const manifest = await contents("Cargo.toml", sha);
    const lock = await contents("Cargo.lock", sha);
    const currentVersion = manifest.match(
        /\[workspace\.package\][\s\S]*?\nversion = "([^"]+)"/,
    )?.[1];
    if (!currentVersion) throw new Error("Missing Cargo version");
    const released: string[] = [];
    for (let page = 1; ; page++) {
        const batch = await api<{ tag_name: string }[]>(
            `releases?per_page=100&page=${page}`,
        );
        released.push(
            ...batch.map((value) => value.tag_name.replace(/^goblinctl-v/, "")),
        );
        if (batch.length < 100) break;
    }
    const version =
        report.outcome === "pin-required"
            ? currentVersion
            : nextVersion([
                  currentVersion,
                  ...released,
                  ...existing.map(
                      (value) => parseCandidate(value.body).version,
                  ),
              ]);
    const [cargo, cargoLock] = bumpVersion(manifest, lock, version);
    const candidate: Candidate = {
        parent: pr?.number ?? null,
        source: sha,
        version,
    };
    await api("git/refs", "POST", { ref: `refs/heads/${branch}`, sha });
    await commitFiles(
        branch,
        sha,
        { "Cargo.toml": cargo, "Cargo.lock": cargoLock },
        `chore: prepare goblinctl ${version}`,
    );
    const body = `Goblin ${pr ? `#${pr.number}` : `master at ${sha}`} needs a published installer before it can merge or deploy.\n\n${report.message}\n\nReview the installer changes in the parent PR and this version bump (patch is only a proposal). Run **goblinctl native release** from master with this PR number and its exact approved head SHA. The workflow requires approval in the goblinctl-release environment before publishing. It then updates this PR with the verified pin and ARM templates. Merge this PR into ${pr?.head.ref ?? "master"} once its checks pass.\n\nChanged installer inputs:\n${report.changedInputs
        .slice(0, 100)
        .map((path) => `- \`${path.replaceAll("`", "")}\``)
        .join(
            "\n",
        )}\n\n<!-- goblinctl-candidate ${JSON.stringify(candidate)} -->`;
    const created = await api<Pull>("pulls", "POST", {
        title: `Release goblinctl ${version}${pr ? ` for #${pr.number}` : " to repair master"}`,
        head: branch,
        base: pr?.head.ref ?? "master",
        body,
    });
    if (pr)
        await comment(
            pr.number,
            `${report.message}\n\nRelease PR: ${created.html_url}\n\nMerging remains blocked until a compatible installer is published and pinned.`,
        );
    await dispatch(created.number);
    if (report.outcome === "pin-required") {
        await api("actions/workflows/goblinctl-pin.yml/dispatches", "POST", {
            ref: "master",
            inputs: { pr: String(created.number), version },
        });
    }
}

export async function resolve(): Promise<void> {
    const value = event<{
        pull_request?: Pull;
        inputs?: { pr?: string };
        merge_group?: { head_sha: string; base_ref: string };
    }>();
    const number =
        value.pull_request?.number ??
        (value.inputs?.pr ? Number(value.inputs.pr) : null);
    let context: Context;
    if (number !== null) {
        if (!Number.isSafeInteger(number) || number < 1)
            throw new Error("Invalid PR number");
        const pull = await api<Pull>(`pulls/${number}`);
        if (pull.state !== "open") throw new Error("PR is no longer open");
        // PR base.sha can retain an older snapshot after its target branch moves.
        const base = await branchHead(pull.base.ref);
        const merge = await api<{ object: { sha: string } }>(
            `git/ref/pull/${number}/merge`,
        );
        const mergeCommit = await api<{ parents: { sha: string }[] }>(
            `git/commits/${merge.object.sha}`,
        );
        if (
            ![pull.head.sha, base].every((sha) =>
                mergeCommit.parents.some((parent) => parent.sha === sha),
            )
        ) {
            throw new Error(
                "GitHub has not prepared the current merge commit yet, or the PR has conflicts. Update the branch and rerun validation.",
            );
        }
        context = {
            pr: number,
            head: pull.head.sha,
            merge: merge.object.sha,
            base: pull.base.ref,
        };
    } else {
        const sha =
            value.merge_group?.head_sha ??
            requireValue(process.env.GITHUB_SHA, "GITHUB_SHA");
        context = { pr: null, head: sha, merge: sha, base: "master" };
    }
    writeFileSync(
        join(
            requireValue(process.env.RUNNER_TEMP, "RUNNER_TEMP"),
            "release-context.json",
        ),
        JSON.stringify(context),
    );
    output({
        sha: context.merge,
        head: context.head,
        pr: context.pr ?? "",
        base: context.base,
    });
}

async function notify(): Promise<void> {
    const { workflow_run: run } = event<{
        workflow_run: {
            id: number;
            name: string;
            event: string;
            head_repository: { full_name: string };
            html_url: string;
            conclusion: string;
        };
    }>();
    if (
        run.name !== "Goblin installer dependency" ||
        run.head_repository.full_name !== repository
    )
        return;
    const directory = mkdtempSync(join(tmpdir(), "goblinctl-report-"));
    try {
        execFileSync(
            "gh",
            [
                "run",
                "download",
                String(run.id),
                "--repo",
                repository,
                "--name",
                "goblinctl-check-context",
                "--dir",
                directory,
            ],
            { stdio: "inherit" },
        );
        const context = JSON.parse(
            readFileSync(join(directory, "release-context.json"), "utf8"),
        ) as Context;
        if (
            ![context.head, context.merge].every((sha) =>
                /^[a-f0-9]{40}$/.test(sha),
            )
        )
            throw new Error("Invalid check context");
        const pull =
            context.pr === null ? null : await api<Pull>(`pulls/${context.pr}`);
        if (pull && (pull.state !== "open" || pull.head.sha !== context.head))
            return;
        if (pull) {
            const merge = await api<{ object: { sha: string } }>(
                `git/ref/pull/${pull.number}/merge`,
            );
            if (merge.object.sha !== context.merge) return; // The base changed while CI was running.
        }
        if (!pull) {
            if (run.event !== "push") return; // Merge queue checks report through the native workflow check.
            const master = await api<{ object: { sha: string } }>(
                "git/ref/heads/master",
            );
            if (master.object.sha !== context.head) return;
        }
        // Explicit dispatches need a commit status because their workflow check belongs to master.
        if (pull && run.event === "workflow_dispatch") {
            await api(`statuses/${pull.head.sha}`, "POST", {
                state: run.conclusion === "success" ? "success" : "failure",
                context: "goblinctl-release-ready",
                target_url: run.html_url,
                description:
                    "Published installer dependency and compatibility checks",
            });
        }
        let report: Report;
        try {
            execFileSync(
                "gh",
                [
                    "run",
                    "download",
                    String(run.id),
                    "--repo",
                    repository,
                    "--name",
                    "goblinctl-release-report",
                    "--dir",
                    directory,
                ],
                { stdio: "inherit" },
            );
            report = JSON.parse(
                readFileSync(join(directory, "goblinctl-check.json"), "utf8"),
            ) as Report;
        } catch {
            report = {
                schemaVersion: 1,
                outcome: "verification-failed",
                message:
                    "The workflow failed before it could produce a dependency report.",
                changedInputs: [],
            };
        }
        if (report.schemaVersion !== 1 || !Array.isArray(report.changedInputs))
            throw new Error("Invalid dependency report");
        if (pull?.head.ref.startsWith(botPrefix)) {
            if (run.conclusion === "success")
                await comment(
                    pull.number,
                    `The published installer matches this PR and compatibility checks passed. Review the pin and merge this companion into its parent branch.\n\n${run.html_url}`,
                );
            return; // A companion never creates another companion.
        }
        if (
            report.outcome === "release-required" ||
            report.outcome === "pin-required"
        )
            await prepare(pull, context.head, report);
        else if (pull && run.conclusion !== "success")
            await comment(
                pull.number,
                `Installer dependency check failed. ${report.message}\n\n${run.html_url}\n\nMerging remains blocked; verification failures do not authorize a release.`,
            );
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}

export async function approveCandidate(): Promise<void> {
    const number = Number(requireValue(process.env.RELEASE_PR, "RELEASE_PR"));
    const sha = requireValue(process.env.RELEASE_SHA, "RELEASE_SHA");
    if (
        !Number.isSafeInteger(number) ||
        number < 1 ||
        !/^[a-f0-9]{40}$/.test(sha)
    )
        throw new Error("Select a PR and exact commit SHA");
    const actor = requireValue(
        process.env.GITHUB_TRIGGERING_ACTOR ?? process.env.GITHUB_ACTOR,
        "GITHUB_ACTOR",
    );
    const permission = await api<{ permission: string }>(
        `collaborators/${encodeURIComponent(actor)}/permission`,
    );
    if (!["admin", "maintain", "write"].includes(permission.permission))
        throw new Error("Publication requires a maintainer");
    const pull = await api<Pull>(`pulls/${number}`);
    if (
        pull.state !== "open" ||
        pull.draft ||
        pull.head.repo?.full_name !== repository ||
        pull.head.sha !== sha ||
        !pull.head.ref.startsWith(botPrefix)
    )
        throw new Error(
            "Candidate is not the selected open, reviewed repository PR",
        );
    const candidate = parseCandidate(pull.body);
    if (candidate.parent !== null) {
        const parent = await api<Pull>(`pulls/${candidate.parent}`);
        if (
            parent.state !== "open" ||
            parent.base.ref !== "master" ||
            parent.head.ref !== pull.base.ref
        )
            throw new Error("Candidate parent changed or closed");
        const comparison = await api<{ status: string }>(
            `compare/${parent.head.sha}...${sha}`,
        );
        if (!["ahead", "identical"].includes(comparison.status))
            throw new Error(
                "Merge the latest parent changes into the candidate before publishing",
            );
        const base = await branchHead(parent.base.ref);
        const baseComparison = await api<{ status: string }>(
            `compare/${base}...${sha}`,
        );
        if (!["ahead", "identical"].includes(baseComparison.status)) {
            throw new Error(
                "Update the candidate from master before publishing; its eventual merge must use the tested installer inputs",
            );
        }
    } else {
        if (pull.base.ref !== "master")
            throw new Error("Master repair must target master");
        const comparison = await api<{ status: string }>(
            `compare/master...${sha}`,
        );
        if (!["ahead", "identical"].includes(comparison.status))
            throw new Error(
                "Update the repair candidate from master before publishing",
            );
    }
    const cargo = await contents("Cargo.toml", sha);
    if (!cargo.includes(`version = "${candidate.version}"`))
        throw new Error("Candidate version differs from Cargo");
    output({ sha, version: candidate.version, pr: number });
}

export async function pinCommit(): Promise<void> {
    const number = Number(requireValue(process.env.RELEASE_PR, "RELEASE_PR"));
    const expected = requireValue(process.env.RELEASE_SHA, "RELEASE_SHA");
    const pull = await api<Pull>(`pulls/${number}`);
    if (
        pull.state !== "open" ||
        pull.head.sha !== expected ||
        !pull.head.ref.startsWith(botPrefix) ||
        pull.head.repo?.full_name !== repository
    )
        throw new Error(
            "Candidate changed while pinning; rerun against its current SHA",
        );
    const directory = requireValue(process.env.PIN_DIRECTORY, "PIN_DIRECTORY");
    const changes: Record<string, string> = {};
    for (const path of [
        "deploy/goblinctl-release.json",
        "deploy/azure/azuredeploy.json",
        "deploy/azure/azuredeploy.portal.json",
    ]) {
        changes[path] = readFileSync(join(directory, path), "utf8");
    }
    await commitFiles(
        pull.head.ref,
        expected,
        changes,
        "chore: pin verified goblinctl release",
    );
    await comment(
        number,
        "The published installer and its provenance were verified. This PR now contains the release pin and regenerated Azure templates. Review and merge it into the parent branch after compatibility checks pass.",
    );
    await dispatch(number);
    const candidate = parseCandidate(pull.body);
    if (candidate.parent !== null) await dispatch(candidate.parent);
}

async function resolvePin(): Promise<void> {
    const number = Number(requireValue(process.env.RELEASE_PR, "RELEASE_PR"));
    if (!Number.isSafeInteger(number) || number < 1)
        throw new Error("Invalid PR number");
    const pull = await api<Pull>(`pulls/${number}`);
    if (
        pull.state !== "open" ||
        pull.head.repo?.full_name !== repository ||
        !pull.head.ref.startsWith(botPrefix)
    )
        throw new Error("Expected an open companion PR");
    const candidate = parseCandidate(pull.body);
    if (candidate.version !== process.env.RELEASE_VERSION)
        throw new Error("Pin version differs from the reviewed release PR");
    if (process.env.RELEASE_SHA && pull.head.sha !== process.env.RELEASE_SHA)
        throw new Error(
            "Candidate changed after publication; inspect it and dispatch pinning again",
        );
    output({ sha: pull.head.sha });
}

async function publish(): Promise<void> {
    await approveCandidate(); // Recheck after the environment approval wait.
    const directory = requireValue(
        process.env.RELEASE_DIRECTORY,
        "RELEASE_DIRECTORY",
    );
    const manifest = JSON.parse(
        readFileSync(join(directory, "release.json"), "utf8"),
    ) as {
        schemaVersion: number;
        sourceRevision: string;
        sourceDirty: boolean;
        version: string;
        sha256: string;
        target: string;
    };
    if (
        manifest.schemaVersion !== 1 ||
        manifest.sourceRevision !== process.env.RELEASE_SHA ||
        manifest.sourceDirty ||
        manifest.version !== process.env.RELEASE_VERSION ||
        manifest.target !== "x86_64-unknown-linux-musl"
    )
        throw new Error("Artifact does not identify the approved candidate");
    const archive = join(
        directory,
        "goblinctl-x86_64-unknown-linux-musl.tar.gz",
    );
    if (
        createHash("sha256").update(readFileSync(archive)).digest("hex") !==
        manifest.sha256
    )
        throw new Error("Artifact checksum mismatch");
    // Creation fails if the tag already exists. Inspect failed publication; never overwrite it.
    const tag = `goblinctl-v${manifest.version}`;
    await api("git/refs", "POST", {
        ref: `refs/tags/${tag}`,
        sha: manifest.sourceRevision,
    });
    execFileSync(
        "gh",
        [
            "release",
            "create",
            tag,
            archive,
            join(directory, "release.json"),
            join(directory, "SHA256SUMS"),
            "--repo",
            repository,
            "--verify-tag",
            "--title",
            tag,
            "--notes",
            `Native installer built and tested from ${manifest.sourceRevision}. Installer dependency metadata and archive provenance are attached.`,
        ],
        { stdio: "inherit" },
    );
}

async function main(): Promise<void> {
    switch (process.argv[2]) {
        case "resolve":
            return resolve();
        case "notify":
            return notify();
        case "approve-candidate":
            return approveCandidate();
        case "pin-commit":
            return pinCommit();
        case "resolve-pin":
            return resolvePin();
        case "publish":
            return publish();
        default:
            throw new Error(
                "Expected resolve, notify, approve-candidate, resolve-pin, pin-commit, or publish",
            );
    }
}

if (
    process.argv[1] &&
    import.meta.url === pathToFileURL(process.argv[1]).href
) {
    await main();
}
