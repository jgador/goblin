import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
    existsSync,
    mkdirSync,
    readFileSync,
    readdirSync,
    rmSync,
    lstatSync,
    writeFileSync,
} from "node:fs";
import { join, resolve } from "node:path";

export const installationUrl = "https://goblinboard.com/install";
export const azureInstallationUrl = `${installationUrl}/azure`;
export const releaseFiles = [
    "azuredeploy.json",
    "azuredeploy.portal.json",
    "createUiDefinition.json",
    "release.json",
    "SHA256SUMS",
];
const versionPattern =
    /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.[1-9]\d*)?$/;
export function validVersion(value: string): string {
    assert.equal(typeof value, "string", "Invalid Goblin version");
    assert.match(value, versionPattern, "Invalid Goblin version");
    return value;
}
const json = (path: string) => JSON.parse(readFileSync(path, "utf8"));
const sha = (path: string) =>
    createHash("sha256").update(readFileSync(path)).digest("hex");

export function validateCatalog(catalog: any) {
    assert.equal(catalog.schemaVersion, 1);
    assert.ok(Array.isArray(catalog.releases));
    const versions = new Set<string>();
    for (const entry of catalog.releases) {
        validVersion(entry.version);
        assert.ok(!versions.has(entry.version), "Duplicate catalog version");
        versions.add(entry.version);
        assert.equal(
            entry.channel,
            entry.version.includes("-preview.") ? "preview" : "stable",
        );
        assert.match(entry.sourceRevision, /^[a-f0-9]{40}$/);
        assert.match(entry.manifestSha256, /^[a-f0-9]{64}$/);
    }
    assert.ok(
        catalog.recommended === null || versions.has(catalog.recommended),
        "Recommendation must be a published version",
    );
    return catalog;
}

// A snapshot has no executable code or Git metadata. Only catalogued release bytes
// enter the website image; signed-release authentication happens before publication.
export function validateAssets(root: string) {
    const directory = join(root, "azure");
    assert.ok(
        lstatSync(join(directory, "releases.json")).isFile(),
        "Catalog must be a regular file",
    );
    const catalog = validateCatalog(json(join(directory, "releases.json")));
    const versionsPath = join(directory, "versions");
    if (existsSync(versionsPath))
        assert.ok(
            lstatSync(versionsPath).isDirectory() &&
                !lstatSync(versionsPath).isSymbolicLink(),
        );
    const versions = existsSync(versionsPath)
        ? readdirSync(versionsPath).sort()
        : [];
    assert.deepEqual(
        versions,
        catalog.releases.map((entry: any) => entry.version).sort(),
        "Version directories differ from the catalog",
    );
    for (const entry of catalog.releases) {
        const path = join(versionsPath, entry.version);
        assert.ok(
            lstatSync(path).isDirectory() && !lstatSync(path).isSymbolicLink(),
        );
        assert.deepEqual(
            readdirSync(path).sort(),
            [...releaseFiles].sort(),
            "Unexpected release files",
        );
        for (const name of releaseFiles)
            assert.ok(
                lstatSync(join(path, name)).isFile(),
                "Release assets must be regular files",
            );
        assert.equal(
            sha(join(path, "release.json")),
            entry.manifestSha256,
            "Release manifest changed",
        );
        const record = json(join(path, "release.json"));
        assert.equal(record.schemaVersion, 2);
        assert.equal(record.version, entry.version);
        assert.equal(record.channel, entry.channel);
        assert.equal(record.source.revision, entry.sourceRevision);
        assert.equal(record.deploymentChecks, "passed");
        for (const name of releaseFiles.slice(0, 3))
            assert.equal(
                sha(join(path, name)),
                record.assets[name],
                `Release asset changed: ${name}`,
            );
        const sums = { ...record.assets, "release.json": entry.manifestSha256 };
        const expected = Object.keys(sums)
            .sort()
            .map((name) => `${sums[name]}  ${name}\n`)
            .join("");
        assert.equal(
            readFileSync(join(path, "SHA256SUMS"), "utf8"),
            expected,
            "Release checksums changed",
        );
    }
    return catalog;
}

export interface InstallationHost {
    releases(): Promise<any[]>;
    asset(release: any, name: string): Promise<Buffer>;
    catalog(): Promise<Buffer | null>;
    authenticate?(directory: string, record: any): Promise<void>;
}
const repository = "jgador/goblin";
async function publicCatalog() {
    const response = await fetch(azureInstallationUrl + "/releases.json", {
        cache: "no-store",
        signal: AbortSignal.timeout(30000),
    });
    if (response.status === 404) return null;
    assert.equal(
        response.status,
        200,
        "Published installation catalog is unavailable",
    );
    return Buffer.from(await response.arrayBuffer());
}
function githubHost(authenticate: boolean): InstallationHost {
    return {
        async releases() {
            if (authenticate)
                return JSON.parse(
                    execFileSync(
                        "gh",
                        [
                            "api",
                            "--paginate",
                            "--slurp",
                            "repos/" + repository + "/releases?per_page=100",
                        ],
                        { encoding: "utf8", maxBuffer: 32 * 1024 * 1024 },
                    ),
                ).flat();
            const releases = [];
            for (let page = 1; ; page++) {
                const response = await fetch(
                    "https://api.github.com/repos/" +
                        repository +
                        "/releases?per_page=100&page=" +
                        page,
                    { signal: AbortSignal.timeout(30000) },
                );
                assert.equal(
                    response.status,
                    200,
                    "GitHub Releases API is unavailable",
                );
                const items = (await response.json()) as any[];
                assert.ok(Array.isArray(items));
                releases.push(...items);
                if (items.length < 100) return releases;
            }
        },
        async asset(release, name) {
            const asset = release.assets.find(
                (item: any) => item.name === name,
            );
            assert.ok(asset, "Published release asset is missing: " + name);
            const url = new URL(asset.browser_download_url);
            assert.equal(
                url.href,
                "https://github.com/" +
                    repository +
                    "/releases/download/" +
                    release.tag_name +
                    "/" +
                    name,
                "Unexpected release asset URL",
            );
            // Public release files contain no credentials; authentication stays in gh.
            const response = await fetch(url, {
                signal: AbortSignal.timeout(30000),
            });
            assert.equal(
                response.status,
                200,
                "Published release asset is unavailable: " + name,
            );
            return Buffer.from(await response.arrayBuffer());
        },
        catalog: publicCatalog,
        ...(authenticate
            ? {
                  async authenticate(directory: string, record: any) {
                      execFileSync(
                          "gh",
                          [
                              "attestation",
                              "verify",
                              join(directory, "release.json"),
                              "--repo",
                              repository,
                              "--signer-workflow",
                              repository +
                                  "/.github/workflows/platform-release.yml",
                              "--signer-digest",
                              record.workflowRevision,
                              "--source-ref",
                              "refs/heads/master",
                              "--deny-self-hosted-runners",
                          ],
                          { stdio: ["ignore", "pipe", "pipe"] },
                      );
                  },
              }
            : {}),
    };
}
function compareVersions(a: any, b: any) {
    const parts = (version: string) =>
        version.split(/\.|-preview\./).map(Number);
    const left = parts(a.version),
        right = parts(b.version);
    for (let i = 0; i < 3; i++)
        if (left[i] !== right[i]) return right[i] - left[i];
    if (left.length !== right.length) return left.length === 3 ? -1 : 1;
    return (right[3] ?? 0) - (left[3] ?? 0);
}
export async function snapshot(
    output: string,
    options: { version?: string; recommend?: boolean } = {},
    host = githubHost(false),
) {
    const directory = resolve(output);
    assert.ok(
        directory.startsWith(resolve(".artifacts") + "/"),
        "Installation snapshots belong under .artifacts/",
    );
    assert.ok(!existsSync(directory), "Installation snapshot already exists");
    if (options.version) validVersion(options.version);
    assert.ok(
        !options.recommend || options.version,
        "Recommending requires a version",
    );
    const previousBytes = await host.catalog();
    const previous = previousBytes
        ? validateCatalog(JSON.parse(previousBytes.toString("utf8")))
        : null;
    const published = await host.releases();
    const catalog = {
        schemaVersion: 1,
        recommended: null as string | null,
        releases: [] as any[],
    };
    const seen = new Set<string>();
    mkdirSync(directory, { recursive: true });
    try {
        for (const release of published) {
            if (release.draft || !/^goblin-v/.test(release.tag_name)) continue;
            const version = release.tag_name.slice("goblin-v".length);
            if (!versionPattern.test(version)) continue;
            assert.ok(!seen.has(version), "Duplicate published Goblin version");
            seen.add(version);
            const manifestBytes = await host.asset(release, "release.json");
            const record = JSON.parse(manifestBytes.toString("utf8"));
            // Historical preproduction formats are not installable by this website.
            if (record.schemaVersion !== 2) continue;
            const path = join(directory, "azure", "versions", version);
            mkdirSync(path, { recursive: true });
            for (const name of releaseFiles) {
                const bytes =
                    name === "release.json"
                        ? manifestBytes
                        : await host.asset(release, name);
                const metadata = release.assets.filter(
                    (asset: any) => asset.name === name,
                );
                assert.equal(
                    metadata.length,
                    1,
                    "Missing or duplicated release asset: " + name,
                );
                assert.equal(
                    metadata[0].digest,
                    "sha256:" +
                        createHash("sha256").update(bytes).digest("hex"),
                    "GitHub release asset digest differs: " + name,
                );
                writeFileSync(join(path, name), bytes);
            }
            assert.equal(record.version, version);
            assert.equal(
                record.channel,
                version.includes("-preview.") ? "preview" : "stable",
            );
            if (host.authenticate) await host.authenticate(path, record);
            catalog.releases.push({
                version,
                channel: record.channel,
                sourceRevision: record.source.revision,
                manifestSha256: createHash("sha256")
                    .update(manifestBytes)
                    .digest("hex"),
            });
        }
        catalog.releases.sort(compareVersions);
        if (options.version)
            assert.ok(
                catalog.releases.some(
                    (entry) => entry.version === options.version,
                ),
                "Requested release is not installable",
            );
        if (previous) {
            for (const entry of previous.releases)
                assert.deepEqual(
                    catalog.releases.find(
                        (item) => item.version === entry.version,
                    ),
                    entry,
                    "Previously hosted release changed or disappeared",
                );
        }
        catalog.recommended = options.recommend
            ? options.version!
            : previous
              ? previous.recommended
              : (catalog.releases.find((entry) => entry.channel === "stable")
                    ?.version ??
                catalog.releases[0]?.version ??
                null);
        mkdirSync(join(directory, "azure"), { recursive: true });
        writeFileSync(
            join(directory, "azure", "releases.json"),
            JSON.stringify(catalog, null, 2) + "\n",
        );
        writeFileSync(
            join(directory, "source.json"),
            JSON.stringify(
                {
                    schemaVersion: 1,
                    baselineSha256: previousBytes
                        ? createHash("sha256")
                              .update(previousBytes)
                              .digest("hex")
                        : null,
                },
                null,
                2,
            ) + "\n",
        );
        validateAssets(directory);
        return catalog;
    } catch (error) {
        rmSync(directory, { recursive: true, force: true });
        throw error;
    }
}
export async function checkBaseline(
    directory: string,
    read: () => Promise<Buffer | null> = publicCatalog,
) {
    const source = json(join(directory, "source.json"));
    assert.equal(source.schemaVersion, 1);
    assert.ok(
        source.baselineSha256 === null ||
            /^[a-f0-9]{64}$/.test(source.baselineSha256),
    );
    const current = await read();
    // A completed rollout can be verified again after a later workflow failure.
    // Only this exact candidate is allowed alongside the unchanged baseline.
    if (
        current &&
        createHash("sha256").update(current).digest("hex") ===
            sha(join(directory, "azure", "releases.json"))
    )
        return;
    assert.equal(
        current ? createHash("sha256").update(current).digest("hex") : null,
        source.baselineSha256,
        "Website installation catalog changed while approval was pending; run Website Release again",
    );
}
if (import.meta.main) {
    const [action, output, ...args] = process.argv.slice(2);
    assert.ok(output, "Pass the snapshot output directory");
    if (action === "check-baseline") await checkBaseline(output);
    else {
        assert.equal(
            action,
            "snapshot",
            "Expected snapshot OUTPUT or check-baseline OUTPUT",
        );
        let version: string | undefined;
        let recommend = false,
            authenticate = false;
        for (let index = 0; index < args.length; index++) {
            if (args[index] === "--version") version = args[++index];
            else if (args[index] === "--recommend") recommend = true;
            else if (args[index] === "--authenticate") authenticate = true;
            else throw new Error("Unknown installation snapshot option");
        }
        const catalog = await snapshot(
            output,
            { version, recommend },
            githubHost(authenticate),
        );
        console.log(
            "Installation snapshot: " +
                catalog.releases.length +
                " Azure releases; recommended: " +
                (catalog.recommended ?? "none"),
        );
    }
}
