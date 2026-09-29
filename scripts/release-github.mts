// GitHub orchestration; Rust owns candidate selection and release verification.
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
    appendFileSync,
    cpSync,
    existsSync,
    mkdirSync,
    mkdtempSync,
    readFileSync,
    rmSync,
    writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const repo = "jgador/goblin";
export const siteUrl = "https://jgador.github.io/goblin";
export const releaseFiles = [
    "azuredeploy.json",
    "azuredeploy.portal.json",
    "createUiDefinition.json",
    "azure-result.json",
    "release.json",
    "SHA256SUMS",
];
const versionPattern =
    /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.[1-9]\d*)?$/;
export function validVersion(value: string): string {
    assert.match(value, versionPattern, "Invalid Goblin version");
    return value;
}
export function deployUrl(version: string): string {
    const base = `${siteUrl}/versions/${validVersion(version)}`;
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
            `repos/${repo}/${route}`,
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
    if (process.env.GITHUB_STEP_SUMMARY)
        appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`);
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
            repo,
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
    assertApprovalEnvironment(api("environments/goblin-release"));
    const pages = api("pages");
    assert.equal(
        pages?.build_type,
        "workflow",
        "Configure GitHub Pages with GitHub Actions as its source",
    );
    summary("Release approval and installation hosting are configured.");
}
function pinPr(version: string) {
    assert.match(version, /^\d+\.\d+\.\d+$/);
    const allowed = ["dependencies.lock.json", "dependencies.toml"];
    const changed = run("git", ["diff", "--name-only"])
        .split("\n")
        .filter(Boolean);
    assert.ok(
        changed.every((path) => allowed.includes(path)),
        "Pin PR contains unrelated changes",
    );
    assert.equal(
        json("dependencies.lock.json").goblinctl.release.version,
        version,
    );
    const branch = `automation/goblinctl-v${version}`;
    const existing = api(`git/ref/heads/${branch}`);
    if (existing) {
        for (const path of allowed) {
            const remote = api(
                `contents/${path}?ref=${encodeURIComponent(branch)}`,
            );
            assert.equal(
                Buffer.from(remote.content, "base64").toString(),
                readFileSync(path, "utf8"),
                "Existing pin branch differs; review it before creating another update",
            );
        }
    } else {
        assert.ok(changed.length, "Installer is already pinned");
        run("git", ["checkout", "-b", branch]);
        run("git", ["add", "--", ...allowed]);
        run("git", [
            "-c",
            "user.name=github-actions[bot]",
            "-c",
            "user.email=41898282+github-actions[bot]@users.noreply.github.com",
            "commit",
            "-m",
            `Pin goblinctl ${version}`,
        ]);
        run("git", ["push", "origin", `HEAD:refs/heads/${branch}`]);
    }
    let pull = api(
        `pulls?state=open&head=${encodeURIComponent(`jgador:${branch}`)}`,
    )?.[0];
    if (!pull)
        pull = api("pulls", "POST", {
            title: `Pin goblinctl ${version}`,
            head: branch,
            base: "master",
            body: `Select the authenticated published goblinctl ${version}. Image dependencies are unchanged.\n\nAfter merging, run **Prepare Goblin release** again.`,
        });
    // GITHUB_TOKEN-created PRs do not emit a new pull_request workflow run.
    run("gh", [
        "workflow",
        "run",
        "checks.yml",
        "--repo",
        repo,
        "--ref",
        branch,
    ]);
    summary(
        `Installer dependency update: ${pull.html_url}\n\nMerge this PR, then run **Prepare Goblin release**.`,
    );
}
function publish(directory: string) {
    verify(directory);
    const record = json(join(directory, "release.json"));
    assert.equal(record.sourceRevision, process.env.RELEASE_SOURCE);
    assert.equal(record.runId, process.env.GITHUB_RUN_ID);
    // Publish may be retried; the expected preparation attempt is a job output.
    assert.equal(record.runAttempt, process.env.PREPARED_ATTEMPT);
    const tag = `goblin-v${validVersion(record.version)}`;
    publishAssets(
        directory,
        tag,
        record.sourceRevision,
        record.channel === "preview",
        `Goblin ${record.version}`,
        `Installer: goblinctl ${record.installer.version}\n\nSource: ${record.sourceRevision}\n\nDeployment checks and Azure installation passed.\n\n[Install Goblin ${record.version}](${siteUrl}/?version=${record.version})\n\n[Verification run](https://github.com/${repo}/actions/runs/${record.runId})`,
        releaseFiles,
    );
}
function publishInstaller(directory: string) {
    const record = json(join(directory, "release.json"));
    const archive = "goblinctl-x86_64-unknown-linux-musl.tar.gz";
    assert.equal(record.schemaVersion, 1);
    assert.match(record.version, /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/);
    assert.equal(record.sourceRevision, process.env.RELEASE_SOURCE);
    assert.equal(record.sourceDirty, false);
    assert.equal(record.target, "x86_64-unknown-linux-musl");
    assert.equal(record.sha256, sha(join(directory, archive)));
    assert.equal(
        readFileSync(join(directory, "SHA256SUMS"), "utf8"),
        `${record.sha256}  ${archive}\n`,
    );
    publishAssets(
        directory,
        `goblinctl-v${record.version}`,
        record.sourceRevision,
        false,
        `goblinctl ${record.version}`,
        `Native installer built and tested from ${record.sourceRevision}.`,
        [archive, "release.json", "SHA256SUMS"],
    );
}
function publishAssets(
    directory: string,
    tag: string,
    source: string,
    prerelease: boolean,
    title: string,
    notes: string,
    namesToPublish: string[],
) {
    const hashes = Object.fromEntries(
        namesToPublish.map((name) => [name, sha(join(directory, name))]),
    );
    const reference = api(`git/ref/tags/${tag}`);
    if (!reference)
        api("git/refs", "POST", { ref: `refs/tags/${tag}`, sha: source });
    assertTag(tag, source);
    let published = api(`releases/tags/${tag}`);
    if (!published)
        published = api("releases", "POST", {
            tag_name: tag,
            name: title,
            draft: true,
            prerelease: prerelease,
            make_latest: "false",
            body: notes,
        });
    assert.equal(published.prerelease, prerelease);
    const staging = mkdtempSync(join(tmpdir(), "goblin-publication-"));
    try {
        const names = published.assets.map(
            (asset: any) => asset.name,
        ) as string[];
        assert.ok(
            names.every((name) => namesToPublish.includes(name)),
            "Release contains unexpected assets",
        );
        download(tag, staging, names);
        const missing = missingAssets(
            Object.fromEntries(
                names.map((name) => [name, sha(join(staging, name))]),
            ),
            hashes,
        );
        if (!published.draft)
            assert.equal(
                missing.length,
                0,
                "Published release is incomplete; never overwrite it",
            );
        if (missing.length)
            run("gh", [
                "release",
                "upload",
                tag,
                "--repo",
                repo,
                ...missing.map((name) => join(directory, name)),
            ]);
        if (published.draft)
            api(`releases/${published.id}`, "PATCH", {
                draft: false,
                make_latest: "false",
            });
        rmSync(staging, { recursive: true, force: true });
        download(tag, staging, namesToPublish);
        assert.deepEqual(
            Object.fromEntries(
                namesToPublish.map((name) => [name, sha(join(staging, name))]),
            ),
            hashes,
        );
    } finally {
        rmSync(staging, { recursive: true, force: true });
    }
    summary(
        `Published [${title}](https://github.com/${repo}/releases/tag/${tag}). Installation-site delivery and dependency updates are reported separately.`,
    );
}
function publishedRelease(version: string, directory: string) {
    const tag = `goblin-v${validVersion(version)}`;
    const released = api(`releases/tags/${tag}`);
    assert.ok(released && !released.draft, "Choose a published Goblin release");
    download(tag, directory, releaseFiles);
    run("gh", [
        "attestation",
        "verify",
        join(directory, "release.json"),
        "--repo",
        repo,
        "--signer-workflow",
        `${repo}/.github/workflows/goblin-release.yml`,
        "--deny-self-hosted-runners",
    ]);
    verify(directory);
    const record = json(join(directory, "release.json"));
    assert.equal(record.version, version);
    assert.equal(released.prerelease, record.channel === "preview");
    assertTag(tag, record.sourceRevision);
    return record;
}
export function updateCatalog(catalog: any, entry: any, recommend: boolean) {
    assert.equal(catalog.schemaVersion, 1);
    assert.ok(Array.isArray(catalog.releases));
    if (catalog.recommended !== null) validVersion(catalog.recommended);
    for (const release of catalog.releases) validVersion(release.version);
    const previous = catalog.releases.find(
        (item: any) => item.version === entry.version,
    );
    if (previous)
        assert.deepEqual(
            previous,
            entry,
            "Published catalog entry is immutable",
        );
    else catalog.releases.unshift(entry);
    if (recommend) catalog.recommended = entry.version;
    assert.ok(
        catalog.recommended === null ||
            catalog.releases.some(
                (item: any) => item.version === catalog.recommended,
            ),
    );
    return catalog;
}
function site(version: string, recommendation: string) {
    assert.ok(["recommend", "preserve"].includes(recommendation));
    const downloadDir = mkdtempSync(join(tmpdir(), "goblin-site-release-"));
    try {
        const record = publishedRelease(version, downloadDir);
        const path = resolve(".artifacts/site");
        assert.ok(!existsSync(path), "Site worktree already exists");
        const branch = api("git/ref/heads/gh-pages");
        if (branch) {
            run("git", ["fetch", "origin", "gh-pages"]);
            run("git", ["worktree", "add", "--detach", path, "FETCH_HEAD"]);
        } else {
            run("git", ["worktree", "add", "--detach", path, "HEAD"]);
            run(
                "git",
                ["checkout", "--orphan", `pages-${process.env.GITHUB_RUN_ID}`],
                path,
            );
            run("git", ["rm", "-rf", "."], path);
        }
        const destination = join(path, "versions", version);
        if (existsSync(destination)) {
            for (const name of releaseFiles)
                assert.equal(
                    sha(join(destination, name)),
                    sha(join(downloadDir, name)),
                    "Versioned site assets changed",
                );
        } else cpSync(downloadDir, destination, { recursive: true });
        cpSync("deploy/install", path, { recursive: true });
        mkdirSync(join(path, "assets"), { recursive: true });
        cpSync(
            "assets/branding/svg/icon-light.svg",
            join(path, "assets/icon.svg"),
        );
        const catalogPath = join(path, "releases.json");
        const catalog = existsSync(catalogPath)
            ? json(catalogPath)
            : { schemaVersion: 1, recommended: null, releases: [] };
        const entry = {
            version,
            channel: record.channel,
            sourceRevision: record.sourceRevision,
            manifestSha256: sha(join(downloadDir, "release.json")),
        };
        writeFileSync(
            catalogPath,
            `${JSON.stringify(updateCatalog(catalog, entry, recommendation === "recommend"), null, 2)}\n`,
        );
        writeFileSync(join(path, ".nojekyll"), "");
        run("git", ["add", "--all"], path);
        if (run("git", ["diff", "--cached", "--name-only"], path)) {
            run(
                "git",
                [
                    "-c",
                    "user.name=github-actions[bot]",
                    "-c",
                    "user.email=41898282+github-actions[bot]@users.noreply.github.com",
                    "commit",
                    "-m",
                    `Install catalog: Goblin ${version} (${recommendation})`,
                ],
                path,
            );
            run("git", ["push", "origin", "HEAD:refs/heads/gh-pages"], path);
        }
        // Upload only the generated site, never worktree Git metadata.
        cpSync(path, ".artifacts/pages", {
            recursive: true,
            filter: (source) => source !== join(path, ".git"),
        });
        summary(
            `Installation site prepared for Goblin ${version}. Recommendation: ${catalog.recommended ?? "none"}. The Pages deployment job must complete before this is live.`,
        );
    } finally {
        rmSync(downloadDir, { recursive: true, force: true });
    }
}
async function checkSite(version: string, recommendation: string) {
    const directory = mkdtempSync(join(tmpdir(), "goblin-site-check-"));
    try {
        publishedRelease(version, directory);
        for (const name of releaseFiles) {
            const response = await fetch(
                `${siteUrl}/versions/${version}/${name}`,
                { signal: AbortSignal.timeout(30_000), cache: "no-store" },
            );
            assert.equal(
                response.status,
                200,
                `Site asset unavailable: ${name}`,
            );
            const digest = createHash("sha256")
                .update(Buffer.from(await response.arrayBuffer()))
                .digest("hex");
            assert.equal(
                digest,
                sha(join(directory, name)),
                `Site asset differs: ${name}`,
            );
        }
        const response = await fetch(`${siteUrl}/releases.json`, {
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
            `Installation site: passed. [Install Goblin ${version}](${siteUrl}/?version=${version}).${recommendation === "recommend" ? " This version is now recommended." : " Recommendation unchanged."}`,
        );
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}
async function main() {
    assert.equal(
        process.env.GITHUB_REPOSITORY,
        repo,
        "Release operations run in the Goblin repository",
    );
    const [action, value = "", mode = "preserve"] = process.argv.slice(2);
    switch (action) {
        case "preflight":
            preflight();
            break;
        case "pin-pr":
            pinPr(value);
            break;
        case "publish":
            publish(resolve(value));
            break;
        case "publish-installer":
            publishInstaller(resolve(value));
            break;
        case "site":
            site(validVersion(value), mode);
            break;
        case "check-site":
            await checkSite(validVersion(value), mode);
            break;
        default:
            throw new Error(
                "Expected preflight, pin-pr, publish, site, or check-site",
            );
    }
}
if (import.meta.main) await main();
