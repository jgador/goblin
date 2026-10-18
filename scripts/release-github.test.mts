import test from "node:test";
import assert from "node:assert/strict";
import {
    assertApprovalEnvironment,
    deployUrl,
    missingAssets,
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
    assert.ok(link.startsWith("https://portal.azure.com/"));
    assert.equal(
        (
            link.match(
                /https:\/\/goblinboard\.com\/install\/azure\/versions\//g,
            ) ?? []
        ).length,
        2,
    );
    assert.ok(
        link.includes(
            "/azure/versions/0.1.0-preview.2/azuredeploy.portal.json",
        ),
    );
    assert.ok(
        link.includes(
            "/azure/versions/0.1.0-preview.2/createUiDefinition.json",
        ),
    );
    for (const version of [
        "master",
        "goblinctl-v0.1.3",
        "../other",
        "0.1.0?url=bad",
    ])
        assert.throws(() => deployUrl(version));
});
