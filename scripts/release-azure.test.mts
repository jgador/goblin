import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { expiredGroup, groupName, ownedGroup } from "./release-azure.mts";

test("cleanup requires the release namespace, repository ownership, and matching run", () => {
    const group = {
        name: groupName("123", "2"),
        tags: {
            goblinReleaseRepo: "jgador/goblin",
            goblinReleaseRunId: "123",
            goblinReleaseExpiresAt: "100",
        },
    };
    assert.ok(ownedGroup(group, "123"));
    assert.ok(expiredGroup(group, 101));
    assert.ok(!expiredGroup(group, 99));
    assert.ok(!ownedGroup(group, "124"));
    assert.ok(!ownedGroup({ ...group, name: "rg-goblin-prod" }));
    assert.ok(
        !ownedGroup({
            ...group,
            tags: { ...group.tags, goblinReleaseRepo: "other/repository" },
        }),
    );
    assert.ok(
        !ownedGroup({
            ...group,
            tags: { ...group.tags, goblinReleaseRunId: "12" },
        }),
    );
    for (const expiry of [undefined, "", "NaN", "-1"])
        assert.ok(
            !expiredGroup(
                {
                    ...group,
                    tags: { ...group.tags, goblinReleaseExpiresAt: expiry },
                },
                101,
            ),
        );
    assert.throws(() => groupName("123;command", "1"));
});

test("the live test cannot run from an ordinary local invocation", () => {
    const result = spawnSync(
        process.execPath,
        ["scripts/release-azure.mts", "install"],
        {
            env: {
                ...process.env,
                GITHUB_ACTIONS: "false",
                AZURE_RELEASE_TESTS_ENABLED: "false",
            },
            encoding: "utf8",
        },
    );
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Live Azure checks run only in GitHub Actions/);
});
