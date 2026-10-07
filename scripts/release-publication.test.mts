import test from "node:test";
import assert from "node:assert/strict";
import {
    mkdirSync,
    mkdtempSync,
    readFileSync,
    rmSync,
    writeFileSync,
} from "node:fs";
import { basename, join, resolve } from "node:path";
import {
    inspectPublication,
    publishPair,
    type Publication,
    type PublicationHost,
} from "./release-github.mts";

function fixture(t: test.TestContext) {
    const scratch = resolve(".artifacts/publication-tests");
    mkdirSync(scratch, { recursive: true });
    const root = mkdtempSync(join(scratch, "case-"));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    function spec(tag: string, names: string[]): Publication {
        const directory = join(root, tag);
        mkdirSync(directory);
        for (const name of names)
            writeFileSync(join(directory, name), tag + "/" + name);
        return {
            directory,
            tag,
            source: "a".repeat(40),
            prerelease: tag.includes("preview"),
            title: tag,
            notes: "Candidate fixture",
            names,
        };
    }
    return {
        installer: spec("goblinctl-v0.1.5", [
            "release.json",
            "SHA256SUMS",
            "goblinctl.tar.gz",
        ]),
        goblin: spec("goblin-v0.1.1-preview.1", [
            "release.json",
            "SHA256SUMS",
            "azuredeploy.json",
        ]),
    };
}

function githubFixture() {
    const refs = new Map<string, string>();
    const releases = new Map<
        string,
        {
            id: number;
            tag_name: string;
            draft: boolean;
            prerelease: boolean;
            body: string;
            files: Map<string, Buffer>;
        }
    >();
    const mutations: string[] = [];
    let interruptAt = 0;
    let corruptUpload = false;
    const changed = (operation: string) => {
        mutations.push(operation);
        if (mutations.length === interruptAt)
            throw new Error("Simulated connection loss after " + operation);
    };
    const host: PublicationHost = {
        api(route, method = "GET", body?: any) {
            if (method === "GET" && route.startsWith("git/ref/tags/")) {
                const sha = refs.get(route.slice("git/ref/tags/".length));
                return sha ? { object: { type: "commit", sha } } : null;
            }
            if (method === "GET" && route.startsWith("releases/tags/")) {
                const released = releases.get(
                    route.slice("releases/tags/".length),
                );
                // GitHub's tag endpoint only returns published releases.
                return released && !released.draft
                    ? {
                          ...released,
                          assets: [...released.files.keys()].map((name) => ({
                              name,
                          })),
                      }
                    : null;
            }
            const page = /^releases\?per_page=100&page=(\d+)$/.exec(route);
            if (method === "GET" && page) {
                const start = (Number(page[1]) - 1) * 100;
                return [...releases.values()]
                    .sort((left, right) => right.id - left.id)
                    .slice(start, start + 100)
                    .map((released) => ({
                        ...released,
                        assets: [...released.files.keys()].map((name) => ({
                            name,
                        })),
                    }));
            }
            if (method === "POST" && route === "git/refs") {
                const tag = body.ref.slice("refs/tags/".length);
                assert.ok(!refs.has(tag), "Never replace a tag");
                refs.set(tag, body.sha);
                changed("tag:" + tag);
                return {};
            }
            if (method === "POST" && route === "releases") {
                assert.ok(
                    !releases.has(body.tag_name),
                    "Never replace a release",
                );
                const released = {
                    ...body,
                    id: releases.size + 1,
                    files: new Map(),
                };
                releases.set(body.tag_name, released);
                changed("draft:" + body.tag_name);
                return released;
            }
            if (method === "PATCH" && route.startsWith("releases/")) {
                const released = [...releases.values()].find(
                    (entry) => entry.id === Number(route.slice(9)),
                )!;
                assert.ok(released.draft, "Never edit a published release");
                assert.deepEqual(body, { draft: false, make_latest: "false" });
                released.draft = false;
                changed("publish:" + released.tag_name);
                return released;
            }
            throw new Error(
                "Unexpected GitHub request: " + method + " " + route,
            );
        },
        run(command, args) {
            assert.equal(command, "gh");
            assert.deepEqual(args.slice(0, 2), ["release", "upload"]);
            assert.ok(!args.includes("--clobber"));
            const released = releases.get(args[2])!;
            assert.ok(released.draft);
            for (const path of args.slice(5)) {
                const name = basename(path);
                assert.ok(
                    !released.files.has(name),
                    "Never overwrite uploaded bytes",
                );
                released.files.set(
                    name,
                    corruptUpload
                        ? Buffer.from("corrupt upload")
                        : readFileSync(path),
                );
                changed("upload:" + released.tag_name + "/" + name);
            }
            return "";
        },
        download(tag, directory, names) {
            mkdirSync(directory, { recursive: true });
            for (const name of names)
                writeFileSync(
                    join(directory, name),
                    releases.get(tag)!.files.get(name)!,
                );
        },
        summary() {},
    };
    return {
        host,
        refs,
        releases,
        mutations,
        interrupt: (at: number) => {
            interruptAt = at;
        },
        corrupt: () => {
            corruptUpload = true;
        },
    };
}

test("every interrupted publication resumes the same pair without overwriting bytes", (t) => {
    const { installer, goblin } = fixture(t);
    const completed = githubFixture();
    publishPair(installer, goblin, "built", completed.host);
    const writes = completed.mutations.length;
    for (let cut = 1; cut <= writes; cut++) {
        const github = githubFixture();
        github.interrupt(cut);
        assert.throws(
            () => publishPair(installer, goblin, "built", github.host),
            /Simulated connection loss/,
        );
        github.interrupt(0);
        publishPair(installer, goblin, "built", github.host);
        assert.deepEqual(
            github.mutations,
            completed.mutations,
            "Recovery must perform only the remaining writes",
        );
        assert.ok(
            github.mutations.indexOf("publish:" + installer.tag) <
                github.mutations.indexOf("publish:" + goblin.tag),
        );
        const before = github.mutations.length;
        publishPair(installer, goblin, "built", github.host);
        assert.equal(
            github.mutations.length,
            before,
            "A completed retry is read-only",
        );
    }
});

test("a conflict in the Goblin version stops before publishing an installer", (t) => {
    const { installer, goblin } = fixture(t);
    const github = githubFixture();
    github.refs.set(goblin.tag, "b".repeat(40));
    assert.throws(
        () => publishPair(installer, goblin, "built", github.host),
        /another source/,
    );
    assert.equal(github.mutations.length, 0);
});

test("draft recovery discovers retained candidates beyond the first page of releases", (t) => {
    const { installer, goblin } = fixture(t);
    const github = githubFixture();
    github.interrupt(2 + installer.names.length);
    assert.throws(
        () => publishPair(installer, goblin, "built", github.host),
        /Simulated connection loss/,
    );
    const retained = github.releases.get(installer.tag)!;
    for (let index = 0; index < 100; index++) {
        const tag = "goblinctl-v9.0." + index;
        github.releases.set(tag, {
            ...retained,
            id: retained.id + index + 1,
            tag_name: tag,
            files: new Map(),
        });
    }
    assert.equal(github.host.api("releases/tags/" + installer.tag), null);
    const before = github.mutations.length;
    const inspected = inspectPublication(installer, github.host);
    assert.ok(inspected);
    assert.equal(inspected.released.id, retained.id);
    assert.equal(inspected.released.draft, true);
    assert.deepEqual(inspected.missing, []);
    assert.equal(github.mutations.length, before);
});

test("changed candidate bytes cannot take over an incomplete publication", (t) => {
    const { installer, goblin } = fixture(t);
    const github = githubFixture();
    github.interrupt(2); // Tag and draft exist; no asset has been uploaded yet.
    assert.throws(() => publishPair(installer, goblin, "built", github.host));
    github.interrupt(0);
    writeFileSync(
        join(installer.directory, "release.json"),
        "another candidate",
    );
    assert.throws(
        () => publishPair(installer, goblin, "built", github.host),
        /another candidate/,
    );
    assert.equal(github.mutations.length, 2);
});

test("corrupt uploads remain drafts and stop the dependent Goblin publication", (t) => {
    const { installer, goblin } = fixture(t);
    const github = githubFixture();
    github.corrupt();
    assert.throws(
        () => publishPair(installer, goblin, "built", github.host),
        /Existing asset differs/,
    );
    assert.equal(github.releases.get(installer.tag)!.draft, true);
    assert.ok(!github.refs.has(goblin.tag));
});

test("installer reuse requires a complete immutable publication", (t) => {
    const { installer, goblin } = fixture(t);
    const github = githubFixture();
    assert.throws(
        () => publishPair(installer, goblin, "published", github.host),
        /fully published/,
    );
    assert.equal(github.mutations.length, 0);
    publishPair(installer, goblin, "built", github.host);
    const before = github.mutations.length;
    publishPair(installer, goblin, "published", github.host);
    assert.equal(github.mutations.length, before);
    github.releases.get(installer.tag)!.files.delete("SHA256SUMS");
    assert.throws(
        () => publishPair(installer, goblin, "published", github.host),
        /incomplete/,
    );
    assert.equal(github.mutations.length, before);
});
