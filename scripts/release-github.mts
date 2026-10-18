import { environmentVariables as Env } from "../config/environment.mts";
// GitHub orchestration; Rust owns candidate selection and release verification.
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
    appendFileSync,
    existsSync,
    mkdirSync,
    mkdtempSync,
    readFileSync,
    rmSync,
} from "node:fs";
import { join, resolve } from "node:path";
import {
    installationUrl,
    releaseFiles,
    validVersion,
    azureInstallationUrl,
} from "./installation-assets.mts";
export { releaseFiles, validVersion } from "./installation-assets.mts";

const githubRepository = "jgador/goblin";
const installationDirectory = resolve(".artifacts/installation-assets");
export function deployUrl(version: string): string {
    const base = `${azureInstallationUrl}/versions/${validVersion(version)}`;
    return `https://portal.azure.com/#create/Microsoft.Template/uri/${encodeURIComponent(`${base}/azuredeploy.portal.json`)}/createUIDefinitionUri/${encodeURIComponent(`${base}/createUiDefinition.json`)}`;
}
export function missingAssets(
    existing: Record<string, string>,
    wanted: Record<string, string>,
): string[] {
    for (const [name, hash] of Object.entries(existing)) {
        assert.equal(hash, wanted[name], `Existing asset differs: ${name}`);
    }
    return Object.keys(wanted).filter((name) => !(name in existing));
}
const sha = (path: string) =>
    createHash("sha256").update(readFileSync(path)).digest("hex");
const json = (path: string) => JSON.parse(readFileSync(path, "utf8"));
function run(command: string, args: string[], cwd = process.cwd()): string {
    return execFileSync(command, args, {
        cwd,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"],
    }).trim();
}
function api(route: string, method = "GET", body?: unknown): any {
    const result = spawnSync(
        "gh",
        [
            "api",
            `repos/${githubRepository}/${route}`,
            "--method",
            method,
            ...(body === undefined ? [] : ["--input", "-"]),
        ],
        {
            input: body === undefined ? undefined : JSON.stringify(body),
            encoding: "utf8",
        },
    );
    if (
        method === "GET" &&
        result.status !== 0 &&
        result.stderr.includes("(HTTP 404)")
    )
        return null;
    assert.equal(
        result.status,
        0,
        `GitHub API ${method} ${route} failed: ${result.stderr}`,
    );
    return result.stdout.trim() ? JSON.parse(result.stdout) : null;
}
function summary(text: string) {
    console.log(text);
    const summaryPath = process.env[Env.GITHUB_STEP_SUMMARY.name];
    if (summaryPath) appendFileSync(summaryPath, `${text}\n`);
}
function verify(directory: string) {
    run("cargo", ["xtask", "release", "verify", "--directory", directory]);
}
function download(tag: string, directory: string, names: string[]) {
    mkdirSync(directory, { recursive: true });
    if (names.length)
        run("gh", [
            "release",
            "download",
            tag,
            "--repo",
            githubRepository,
            "--dir",
            directory,
            ...names.flatMap((name) => ["--pattern", name]),
        ]);
}
function assertTag(tag: string, source: string) {
    const ref = api(`git/ref/tags/${tag}`);
    assert.equal(
        ref?.object?.type,
        "commit",
        "Expected a release tag pointing directly to the source commit",
    );
    assert.equal(
        ref.object.sha,
        source,
        "Release tag identifies another source",
    );
}
export function assertApprovalEnvironment(environment: any) {
    assert.ok(
        environment?.protection_rules?.some(
            (rule: any) =>
                rule.type === "required_reviewers" &&
                rule.reviewers?.length > 0,
        ),
        "Configure required reviewers on the goblin-release environment before preparing a release",
    );
}
function preflight() {
    const environment = api("environments/goblin-release");
    assertApprovalEnvironment(environment);
    if (environment.deployment_branch_policy?.custom_branch_policies) {
        const policies = api(
            "environments/goblin-release/deployment-branch-policies",
        );
        assert.ok(
            policies.branch_policies.some(
                (policy: any) =>
                    policy.name === "master" && policy.type === "branch",
            ),
            "Allow master on the goblin-release approval environment; release source is selected separately",
        );
    }
    summary(
        "Coordinated release approval is configured. Website installation deployment is reported separately.",
    );
}
export interface Publication {
    directory: string;
    tag: string;
    source: string;
    prerelease: boolean;
    title: string;
    notes: string;
    names: string[];
}
export interface PublicationHost {
    api: typeof api;
    run: typeof run;
    download: typeof download;
    summary: typeof summary;
}
const github: PublicationHost = { api, run, download, summary };
function publicationScratch(): string {
    const base = resolve(".artifacts/release-publication");
    mkdirSync(base, { recursive: true });
    return mkdtempSync(join(base, "assets-"));
}
function desiredHashes(spec: Publication) {
    return Object.fromEntries(
        spec.names.map((name) => [name, sha(join(spec.directory, name))]),
    );
}
function publicationMarker(spec: Publication) {
    return (
        "<!-- goblin-manifest:" +
        sha(join(spec.directory, "release.json")) +
        " -->"
    );
}
function findPublication(tag: string, host: PublicationHost) {
    const published = host.api("releases/tags/" + tag);
    if (published) return published;
    // The tag endpoint excludes drafts, even for their author. The release list
    // includes drafts visible to this token, including interrupted publications.
    for (let page = 1; ; page++) {
        const releases = host.api("releases?per_page=100&page=" + page);
        assert.ok(Array.isArray(releases), "Could not list release drafts");
        const released = releases.find((entry: any) => entry.tag_name === tag);
        if (released) return released;
        if (releases.length < 100) return null;
    }
}
export function inspectPublication(
    spec: Publication,
    host: PublicationHost = github,
) {
    const reference = host.api("git/ref/tags/" + spec.tag);
    if (reference) {
        assert.equal(
            reference.object?.type,
            "commit",
            "Release tag must point directly to a commit",
        );
        assert.equal(
            reference.object.sha,
            spec.source,
            "Release tag identifies another source",
        );
    }
    const released = findPublication(spec.tag, host);
    if (!released) return null;
    assert.ok(reference, "Existing release has no source tag");
    assert.equal(
        released.prerelease,
        spec.prerelease,
        "Release channel differs",
    );
    if (released.draft)
        assert.ok(
            released.body?.includes(publicationMarker(spec)),
            "Draft belongs to another candidate; recover the original run",
        );
    const names = released.assets.map((asset: any) => asset.name) as string[];
    assert.equal(new Set(names).size, names.length, "Duplicate release asset");
    const staging = publicationScratch();
    try {
        assert.ok(
            names.every((name) => spec.names.includes(name)),
            "Release contains unexpected assets",
        );
        host.download(spec.tag, staging, names);
        const missing = missingAssets(
            Object.fromEntries(
                names.map((name) => [name, sha(join(staging, name))]),
            ),
            desiredHashes(spec),
        );
        if (!released.draft)
            assert.equal(
                missing.length,
                0,
                "Published release is incomplete; never overwrite it",
            );
        return { released, missing };
    } finally {
        rmSync(staging, { recursive: true, force: true });
    }
}
export function publishAssets(
    spec: Publication,
    host: PublicationHost = github,
) {
    let state = inspectPublication(spec, host);
    if (!host.api("git/ref/tags/" + spec.tag))
        host.api("git/refs", "POST", {
            ref: "refs/tags/" + spec.tag,
            sha: spec.source,
        });
    if (!state) {
        const released = host.api("releases", "POST", {
            tag_name: spec.tag,
            name: spec.title,
            draft: true,
            prerelease: spec.prerelease,
            make_latest: "false",
            body: spec.notes + "\n\n" + publicationMarker(spec),
        });
        state = { released, missing: spec.names };
    }
    if (state.missing.length)
        host.run("gh", [
            "release",
            "upload",
            spec.tag,
            "--repo",
            githubRepository,
            ...state.missing.map((name) => join(spec.directory, name)),
        ]);
    // Verify every uploaded byte while still a draft. Only then expose the release.
    const verified = inspectPublication(spec, host);
    assert.ok(
        verified,
        `Release ${spec.tag} could not be found after uploading assets; recover the original run`,
    );
    assert.equal(verified.missing.length, 0, "Uploaded release is incomplete");
    if (verified.released.draft)
        host.api("releases/" + verified.released.id, "PATCH", {
            draft: false,
            make_latest: "false",
        });
    const published = inspectPublication(spec, host);
    assert.ok(
        published && !published.released.draft,
        "Publication did not complete",
    );
    host.summary(
        "Published [" +
            spec.title +
            "](https://github.com/" +
            githubRepository +
            "/releases/tag/" +
            spec.tag +
            "). Installation assets and website deployment are reported separately.",
    );
}
export function publishPair(
    installer: Publication,
    goblin: Publication,
    origin: "built" | "published",
    host: PublicationHost = github,
) {
    // Reject conflicts in either version before publishing either component.
    const existing = inspectPublication(installer, host);
    inspectPublication(goblin, host);
    switch (origin) {
        case "built":
            publishAssets(installer, host);
            break;
        case "published":
            assert.ok(
                existing && !existing.released.draft,
                "The reused installer must remain fully published",
            );
            break;
        default:
            throw new Error("Unknown installer origin");
    }
    publishAssets(goblin, host);
}
function authenticateCandidate(directory: string, record: any) {
    run("gh", [
        "attestation",
        "verify",
        join(directory, "release.json"),
        "--repo",
        githubRepository,
        "--signer-workflow",
        githubRepository + "/.github/workflows/platform-release.yml",
        "--signer-digest",
        record.workflowRevision,
        "--source-ref",
        "refs/heads/master",
        "--deny-self-hosted-runners",
    ]);
}
function publish(directory: string) {
    verify(directory);
    const record = json(join(directory, "release.json"));
    assert.equal(
        record.schemaVersion,
        2,
        "Only coordinated candidates can be published",
    );
    assert.equal(
        sha(join(directory, "release.json")),
        process.env[Env.CANDIDATE_SHA256.name],
        "Approved candidate changed",
    );
    assert.equal(record.source.revision, process.env[Env.RELEASE_SOURCE.name]);
    assert.equal(record.runId, process.env[Env.GITHUB_RUN_ID.name]);
    assert.equal(record.runAttempt, process.env[Env.PREPARED_ATTEMPT.name]);
    assert.equal(record.workflowRevision, run("git", ["rev-parse", "HEAD"]));
    authenticateCandidate(directory, record);
    preflight();
    const check = api("check-runs/" + record.source.checkRunId);
    assert.equal(check?.name, "test_results");
    assert.equal(check?.app?.id, 15368);
    assert.equal(check?.head_sha, record.source.revision);
    assert.equal(check?.status, "completed");
    assert.equal(
        check?.conclusion,
        "success",
        "The recorded source checks no longer pass",
    );
    run("git", [
        "fetch",
        "--no-tags",
        "origin",
        "refs/heads/" + record.source.branch,
    ]);
    run("git", [
        "merge-base",
        "--is-ancestor",
        record.source.revision,
        "FETCH_HEAD",
    ]);
    run("cargo", [
        "xtask",
        "release",
        "authenticate-installer",
        "--directory",
        join(directory, "installer"),
    ]);
    publishPair(
        {
            directory: join(directory, "installer"),
            tag: "goblinctl-v" + record.installer.version,
            source: record.installer.sourceRevision,
            prerelease: false,
            title: "goblinctl " + record.installer.version,
            notes:
                "Native installer built and tested from " +
                record.installer.sourceRevision +
                ".",
            names: [
                "goblinctl-x86_64-unknown-linux-musl.tar.gz",
                "release.json",
                "SHA256SUMS",
            ],
        },
        {
            directory,
            tag: "goblin-v" + validVersion(record.version),
            source: record.source.revision,
            prerelease: record.channel === "preview",
            title: "Goblin " + record.version,
            notes:
                "Installer: goblinctl " +
                record.installer.version +
                "\n\nSource: " +
                record.source.branch +
                " at " +
                record.source.revision +
                "\n\nDeployment checks passed. Azure installation remains manual.\n\n[Install Goblin](" +
                installationUrl +
                "/?version=" +
                record.version +
                ")\n\n[Verification run](https://github.com/" +
                githubRepository +
                "/actions/runs/" +
                record.runId +
                ")",
            names: releaseFiles,
        },
        record.installerOrigin,
    );
}
function publishedRelease(version: string, directory: string) {
    const tag = `goblin-v${validVersion(version)}`;
    const released = api(`releases/tags/${tag}`);
    assert.ok(released && !released.draft, "Choose a published Goblin release");
    download(tag, directory, releaseFiles);
    const record = json(join(directory, "release.json"));
    assert.equal(record.schemaVersion, 2, "Unsupported Goblin release schema");
    authenticateCandidate(directory, record);
    const installerDirectory = join(directory, "installer");
    const installerTag = "goblinctl-v" + record.installer.version;
    assert.match(
        record.installer.version,
        /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/,
    );
    const installerRelease = api("releases/tags/" + installerTag);
    assert.ok(
        installerRelease &&
            !installerRelease.draft &&
            !installerRelease.prerelease,
        "Installer publication is incomplete",
    );
    assertTag(installerTag, record.installer.sourceRevision);
    download(installerTag, installerDirectory, [
        "goblinctl-x86_64-unknown-linux-musl.tar.gz",
        "release.json",
        "SHA256SUMS",
    ]);
    run("cargo", [
        "xtask",
        "release",
        "authenticate-installer",
        "--directory",
        installerDirectory,
    ]);
    verify(directory);
    assert.equal(record.version, version);
    assertTag(tag, record.source.revision);
    return record;
}
async function buildInstallationAssets(
    version: string,
    recommendation: string,
) {
    assert.ok(["recommend", "preserve"].includes(recommendation));
    mkdirSync(installationDirectory, { recursive: true });
    const directory = mkdtempSync(
        join(installationDirectory, "installation-release-"),
    );
    try {
        // Keep the release and installer authentication boundary for explicit
        // publication/recommendation. Snapshot all other releases from their
        // signed manifests and GitHub asset digests.
        publishedRelease(version, directory);
        run("node", [
            "scripts/installation-assets.mts",
            "snapshot",
            join(installationDirectory, "output"),
            "--authenticate",
            "--version",
            version,
            ...(recommendation === "recommend" ? ["--recommend"] : []),
        ]);
        summary(
            "Installation snapshot prepared from GitHub Releases. Approve Website Release to serve it at " +
                installationUrl +
                "/.",
        );
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}
async function checkInstallationAssets(
    version: string,
    recommendation: string,
) {
    mkdirSync(installationDirectory, { recursive: true });
    const directory = mkdtempSync(
        join(installationDirectory, "installation-check-"),
    );
    try {
        publishedRelease(version, directory);
        for (const name of releaseFiles) {
            const response = await fetch(
                `${azureInstallationUrl}/versions/${version}/${name}`,
                { signal: AbortSignal.timeout(30_000), cache: "no-store" },
            );
            assert.equal(
                response.status,
                200,
                `Installation asset unavailable: ${name}`,
            );
            const digest = createHash("sha256")
                .update(Buffer.from(await response.arrayBuffer()))
                .digest("hex");
            assert.equal(
                digest,
                sha(join(directory, name)),
                `Installation asset differs: ${name}`,
            );
        }
        const response = await fetch(`${azureInstallationUrl}/releases.json`, {
            signal: AbortSignal.timeout(30_000),
            cache: "no-store",
        });
        assert.equal(response.status, 200);
        const catalog = (await response.json()) as any;
        assert.ok(
            catalog.releases.some((entry: any) => entry.version === version),
        );
        if (recommendation === "recommend")
            assert.equal(catalog.recommended, version);
        summary(
            `Website installation assets: passed. [Install Goblin ${version}](${installationUrl}/?version=${version}).${recommendation === "recommend" ? " This version is now recommended." : " Recommendation unchanged."}`,
        );
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}
async function main() {
    assert.equal(
        process.env[Env.GITHUB_REPOSITORY.name],
        githubRepository,
        "Release operations run in the Goblin repository",
    );
    const [action, value = "", mode = "preserve"] = process.argv.slice(2);
    switch (action) {
        case "preflight":
            preflight();
            break;
        case "publish":
            publish(resolve(value));
            break;
        case "installation-assets":
            await buildInstallationAssets(validVersion(value), mode);
            break;
        case "check-installation-assets":
            await checkInstallationAssets(validVersion(value), mode);
            break;
        default:
            throw new Error(
                "Expected preflight, publish, installation-assets, or check-installation-assets",
            );
    }
}
if (import.meta.main) await main();
