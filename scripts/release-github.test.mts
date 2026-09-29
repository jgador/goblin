import test from "node:test";
import assert from "node:assert/strict";
import {
    assertApprovalEnvironment,
    deployUrl,
    missingAssets,
    updateCatalog,
} from "./release-github.mts";

test("publication recovery only adds missing assets with the same identity", () => {
    const candidate = {
        "release.json": "manifest",
        "azuredeploy.json": "template",
    };
    assert.deepEqual(missingAssets({ "release.json": "manifest" }, candidate), [
        "azuredeploy.json",
    ]);
    assert.deepEqual(missingAssets(candidate, candidate), []);
    assert.throws(() =>
        missingAssets({ "release.json": "another-candidate" }, candidate),
    );
    assert.throws(() =>
        missingAssets({ "unrecognized-file": "anything" }, candidate),
    );
});

test("publishing previews preserves the recommendation and explicit rollback restores an older release", () => {
    const stable = {
        version: "0.1.0",
        channel: "stable",
        manifestSha256: "stable",
    };
    const preview = {
        version: "0.2.0-preview.1",
        channel: "preview",
        manifestSha256: "preview",
    };
    const catalog = { schemaVersion: 1, recommended: null, releases: [] };
    updateCatalog(catalog, stable, true);
    updateCatalog(catalog, preview, false);
    assert.equal(catalog.recommended, stable.version);
    updateCatalog(catalog, preview, true);
    assert.equal(catalog.recommended, preview.version);
    updateCatalog(catalog, stable, true);
    assert.equal(catalog.recommended, stable.version);
    assert.equal(catalog.releases.length, 2);
    assert.throws(() =>
        updateCatalog(
            catalog,
            { ...stable, manifestSha256: "replacement" },
            true,
        ),
    );
});

test("a named environment without reviewers cannot silently publish", () => {
    assert.throws(() => assertApprovalEnvironment(null));
    assert.throws(() => assertApprovalEnvironment({ protection_rules: [] }));
    assert.throws(() =>
        assertApprovalEnvironment({
            protection_rules: [{ type: "required_reviewers", reviewers: [] }],
        }),
    );
    assertApprovalEnvironment({
        protection_rules: [
            { type: "required_reviewers", reviewers: [{ type: "User" }] },
        ],
    });
});

test("installation links carry both assets from one version", () => {
    const link = decodeURIComponent(deployUrl("0.1.0-preview.2"));
    assert.ok(
        link.includes("/versions/0.1.0-preview.2/azuredeploy.portal.json"),
    );
    assert.ok(
        link.includes("/versions/0.1.0-preview.2/createUiDefinition.json"),
    );
    for (const version of [
        "master",
        "goblinctl-v0.1.3",
        "../other",
        "0.1.0?url=bad",
    ])
        assert.throws(() => deployUrl(version));
});
