import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
    mkdirSync,
    mkdtempSync,
    readFileSync,
    rmSync,
    writeFileSync,
    existsSync,
} from "node:fs";
import { join, resolve } from "node:path";
import {
    snapshot,
    checkBaseline,
    type InstallationHost,
    validateAssets,
    validateCatalog,
    releaseFiles,
} from "./installation-assets.mts";

function fixture(t: test.TestContext) {
    const scratch = resolve(".artifacts/installation-assets-tests");
    mkdirSync(scratch, { recursive: true });
    const directory = mkdtempSync(join(scratch, "case-"));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    const input = join(directory, "input");
    const version = "0.1.0-preview.1";
    const path = join(input, "azure", "versions", version);
    mkdirSync(path, { recursive: true });
    const hash = (name: string) =>
        createHash("sha256")
            .update(readFileSync(join(path, name)))
            .digest("hex");
    const assets: Record<string, string> = {};
    for (const name of releaseFiles.slice(0, 3)) {
        writeFileSync(join(path, name), JSON.stringify({ fixture: name }));
        assets[name] = hash(name);
    }
    const sourceRevision = "a".repeat(40);
    writeFileSync(
        join(path, "release.json"),
        JSON.stringify({
            schemaVersion: 2,
            version,
            channel: "preview",
            source: { revision: sourceRevision },
            deploymentChecks: "passed",
            assets,
        }),
    );
    const manifestSha256 = hash("release.json");
    const sums: Record<string, string> = {
        ...assets,
        "release.json": manifestSha256,
    };
    writeFileSync(
        join(path, "SHA256SUMS"),
        Object.keys(sums)
            .sort()
            .map((name) => `${sums[name]}  ${name}\n`)
            .join(""),
    );
    const entry = {
        version,
        channel: "preview",
        sourceRevision,
        manifestSha256,
    };
    const catalog = {
        schemaVersion: 1,
        recommended: version,
        releases: [entry],
    };
    writeFileSync(
        join(input, "azure", "releases.json"),
        JSON.stringify(catalog),
    );
    const release = {
        tag_name: "goblin-v" + version,
        draft: false,
        prerelease: true,
        assets: releaseFiles.map((name) => ({
            name,
            digest: "sha256:" + hash(name),
        })),
    };
    const host: InstallationHost = {
        async releases() {
            return [release];
        },
        async asset(_release, name) {
            return readFileSync(join(path, name));
        },
        async catalog() {
            return Buffer.from(JSON.stringify(catalog));
        },
    };
    return { directory, input, path, catalog, entry, release, host };
}

test("installation catalogs reject unsafe, duplicated, mismatched, or unknown recommended versions", (t) => {
    const { catalog, entry } = fixture(t);
    assert.equal(validateCatalog(catalog), catalog);
    for (const invalid of [
        { ...catalog, schemaVersion: 2 },
        { ...catalog, recommended: "0.9.0" },
        { ...catalog, releases: [entry, entry] },
        { ...catalog, releases: [{ ...entry, version: "../outside" }] },
        { ...catalog, releases: [{ ...entry, channel: "stable" }] },
        { ...catalog, releases: [{ ...entry, sourceRevision: "unknown" }] },
    ])
        assert.throws(() => validateCatalog(invalid));
});

test("installation snapshots verify every template and the original manifest and checksums", (t) => {
    const { input, path, catalog } = fixture(t);
    assert.deepEqual(validateAssets(input), catalog);
    writeFileSync(join(path, "azuredeploy.portal.json"), "changed template");
    assert.throws(() => validateAssets(input), /Release asset changed/);
});

test("installation snapshots reject missing files, unexpected scripts, and altered metadata", (t) => {
    const { input, path } = fixture(t);
    writeFileSync(join(path, "app.js"), "throw new Error('unexpected')");
    assert.throws(() => validateAssets(input), /Unexpected release files/);
    rmSync(join(path, "app.js"));
    writeFileSync(join(path, "SHA256SUMS"), "changed");
    assert.throws(() => validateAssets(input), /Release checksums changed/);
    rmSync(join(path, "SHA256SUMS"));
    assert.throws(() => validateAssets(input), /Unexpected release files/);
});

test("GitHub Releases snapshots include current formats only and preserve deployed recommendations", async (t) => {
    const { directory, catalog, release, host } = fixture(t);
    const historical = { ...release, tag_name: "goblin-v0.0.1-preview.1" };
    const original = host.asset;
    host.releases = async () => [
        release,
        historical,
        { tag_name: "goblinctl-v0.1.0" },
        { ...release, draft: true },
    ];
    host.asset = async (item, name) =>
        item === historical
            ? Buffer.from(JSON.stringify({ schemaVersion: 1 }))
            : original(item, name);
    const output = join(directory, "output");
    assert.deepEqual(await snapshot(output, {}, host), catalog);
    assert.deepEqual(validateAssets(output), catalog);
    assert.ok(!existsSync(join(output, ".git")));
    assert.ok(
        !existsSync(join(output, "azure", "versions", "0.0.1-preview.1")),
    );
    await assert.rejects(snapshot(output, {}, host), /already exists/);
    await assert.rejects(
        snapshot("outside-artifacts", {}, host),
        /under .artifacts/,
    );
});

test("new installations choose the newest supported stable release before previews", async (t) => {
    const { directory, path, release, host } = fixture(t);
    const manifests = new Map<string, Buffer>();
    const items = [
        "0.2.0-preview.1",
        "0.1.1",
        "0.1.0",
        "0.1.0-preview.10",
        "0.1.0-preview.2",
    ].map((version) => {
        const record = JSON.parse(
            readFileSync(join(path, "release.json"), "utf8"),
        );
        record.version = version;
        record.channel = version.includes("-preview.") ? "preview" : "stable";
        const bytes = Buffer.from(JSON.stringify(record));
        manifests.set(version, bytes);
        return {
            ...release,
            tag_name: "goblin-v" + version,
            assets: release.assets.map((asset) => ({
                ...asset,
                digest:
                    asset.name === "release.json"
                        ? "sha256:" +
                          createHash("sha256").update(bytes).digest("hex")
                        : asset.digest,
            })),
        };
    });
    host.catalog = async () => null;
    host.releases = async () => items;
    host.asset = async (item, name) => {
        const manifest = manifests.get(item.tag_name.slice("goblin-v".length))!;
        if (name === "release.json") return manifest;
        if (name === "SHA256SUMS") {
            const sums = {
                ...JSON.parse(manifest.toString()).assets,
                "release.json": createHash("sha256")
                    .update(manifest)
                    .digest("hex"),
            };
            return Buffer.from(
                Object.keys(sums)
                    .sort()
                    .map((key) => sums[key] + "  " + key + "\n")
                    .join(""),
            );
        }
        return readFileSync(join(path, name));
    };
    // Every fixture asset retains the GitHub API digest of its final bytes.
    for (const item of items)
        for (const asset of item.assets)
            asset.digest =
                "sha256:" +
                createHash("sha256")
                    .update(await host.asset(item, asset.name))
                    .digest("hex");
    const catalog = await snapshot(join(directory, "fresh"), {}, host);
    assert.equal(catalog.recommended, "0.1.1");
    assert.deepEqual(
        catalog.releases.map((entry) => entry.version),
        [
            "0.2.0-preview.1",
            "0.1.1",
            "0.1.0",
            "0.1.0-preview.10",
            "0.1.0-preview.2",
        ],
    );
    const rollback = await snapshot(
        join(directory, "rollback"),
        { version: "0.1.0", recommend: true },
        host,
    );
    assert.equal(rollback.recommended, "0.1.0");
});

test("snapshots reject altered published assets, missing hosted releases, and unavailable requested versions", async (t) => {
    const { directory, host } = fixture(t);
    const original = host.asset;
    host.asset = async (item, name) =>
        name === "azuredeploy.json"
            ? Buffer.from("changed")
            : original(item, name);
    await assert.rejects(
        snapshot(join(directory, "corrupt"), {}, host),
        /digest differs/,
    );
    assert.ok(!existsSync(join(directory, "corrupt")));
    host.asset = original;
    await assert.rejects(
        snapshot(join(directory, "missing"), { version: "0.9.0" }, host),
        /not installable/,
    );
    host.releases = async () => [];
    await assert.rejects(
        snapshot(join(directory, "deleted"), {}, host),
        /changed or disappeared/,
    );
});

test("approval baseline rejects a deployed recommendation change and initial publication races", async (t) => {
    const { directory, host } = fixture(t);
    const output = join(directory, "approved");
    await snapshot(output, {}, host);
    await checkBaseline(output, host.catalog);
    await checkBaseline(output, async () =>
        readFileSync(join(output, "azure", "releases.json")),
    );
    await assert.rejects(
        checkBaseline(output, async () => null),
        /changed while approval/,
    );
    const first = join(directory, "first");
    host.catalog = async () => null;
    await snapshot(first, {}, host);
    await checkBaseline(first, host.catalog);
    await assert.rejects(
        checkBaseline(first, async () => Buffer.from("another catalog")),
        /changed while approval/,
    );
});

test("snapshot authentication failures stop packaging and empty repositories produce an empty Azure catalog", async (t) => {
    const { directory, host } = fixture(t);
    host.authenticate = async () => {
        throw new Error("Attestation rejected");
    };
    await assert.rejects(
        snapshot(join(directory, "untrusted"), {}, host),
        /Attestation rejected/,
    );
    assert.ok(!existsSync(join(directory, "untrusted")));
    host.releases = async () => [];
    host.catalog = async () => null;
    const catalog = await snapshot(join(directory, "empty"), {}, host);
    assert.deepEqual(catalog, {
        schemaVersion: 1,
        recommended: null,
        releases: [],
    });
});

test("website packaging includes only provider catalogs and validated release files", async (t) => {
    const { directory, input, catalog } = fixture(t);
    const artifacts = join(directory, "artifacts");
    const snapshotPath = join(artifacts, "installation-assets", "output");
    mkdirSync(snapshotPath, { recursive: true });
    const { cpSync, readdirSync } = await import("node:fs");
    cpSync(join(input, "azure"), join(snapshotPath, "azure"), {
        recursive: true,
    });
    writeFileSync(
        join(snapshotPath, "source.json"),
        "private build provenance",
    );
    const output = join(directory, "packaged");
    const packageAssets = () =>
        execFileSync(
            "node",
            ["website/package-installation.mts", artifacts, output],
            { stdio: ["ignore", "pipe", "pipe"] },
        );
    packageAssets();
    assert.deepEqual(validateAssets(output), catalog);
    assert.deepEqual(readdirSync(output), ["azure"]);
    assert.ok(!existsSync(join(output, "source.json")));
    writeFileSync(
        join(
            snapshotPath,
            "azure",
            "versions",
            catalog.releases[0].version,
            "azuredeploy.json",
        ),
        "changed",
    );
    assert.throws(packageAssets, /Release asset changed/);
});
