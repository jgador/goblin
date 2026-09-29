import { environmentVariables as Env } from "../../config/environment.mjs";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
    mkdir,
    mkdtemp,
    readFile,
    rename,
    rm,
    symlink,
    unlink,
    writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { goblinctl } from "../support/goblinctl.js";

test("local source snapshots include edits and exclude ignored credentials and build products", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-local-source-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const repo = join(root, "repo");
    await mkdir(repo);
    execFileSync("git", ["init", "-q", repo]);
    const ignore = await readFile(".gitignore", "utf8");
    await writeFile(join(repo, ".gitignore"), ignore);
    await mkdir(join(repo, ".goblin-secrets"));
    await writeFile(join(repo, ".goblin-secrets/.gitkeep"), "");
    await writeFile(
        join(repo, ".goblin-secrets/owner-password"),
        "test-only ignored verifier",
    );
    await writeFile(join(repo, "tracked.cs"), "old contents");
    await writeFile(join(repo, "deleted.cs"), "removed");
    execFileSync("git", ["-C", repo, "add", "."]);
    await writeFile(join(repo, "tracked.cs"), "current contents");
    await unlink(join(repo, "deleted.cs"));
    await writeFile(join(repo, "new.cs"), "new source");
    await writeFile(join(repo, ".env"), "test-only ignored data");
    for (const dir of [".goblin-local", "target"]) {
        await mkdir(join(repo, dir));
        await writeFile(
            join(repo, dir, "excluded"),
            "private or generated data",
        );
    }
    const archive = join(root, "source.tar.gz");
    execFileSync(goblinctl, ["--repo", repo, "internal", "snapshot", archive]);
    const names = execFileSync("tar", ["-tzf", archive], { encoding: "utf8" })
        .trim()
        .split("\n");
    assert.deepEqual(names, [
        "goblin/.gitignore",
        "goblin/.goblin-secrets/.gitkeep",
        "goblin/new.cs",
        "goblin/tracked.cs",
    ]);
    assert.equal(
        execFileSync("tar", ["-xOzf", archive, "goblin/tracked.cs"], {
            encoding: "utf8",
        }),
        "current contents",
    );
    const bytes = await readFile(archive);
    execFileSync(goblinctl, ["--repo", repo, "internal", "snapshot", archive]);
    assert.deepEqual(await readFile(archive), bytes);
});

test("source snapshots refuse files reached through symlinks", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-local-symlink-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const repo = join(root, "repo");
    await mkdir(join(repo, "tracked"), { recursive: true });
    execFileSync("git", ["init", "-q", repo]);
    await writeFile(join(repo, "tracked/file"), "tracked source");
    execFileSync("git", ["-C", repo, "add", "."]);
    await rename(join(repo, "tracked"), join(root, "external"));
    await symlink(join(root, "external"), join(repo, "tracked"));
    const snapshot = () =>
        execFileSync(
            goblinctl,
            [
                "--repo",
                repo,
                "internal",
                "snapshot",
                join(root, "source.tar.gz"),
            ],
            { stdio: "pipe" },
        );
    assert.throws(snapshot, /symlinks/);
    await unlink(join(repo, "tracked"));
    await symlink(join(root, "external/file"), join(repo, "link"));
    assert.throws(snapshot, /symlinks/);
});

test("native setup assets work outside a checkout and do not overwrite template pins", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-local-assets-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const before = await readFile("dependencies.lock.json");
    execFileSync(goblinctl, ["internal", "unpack", root], {
        cwd: root,
        env: { [Env.PATH.name]: "/nonexistent" },
    });
    for (const name of [
        "installer.sh",
        "install-app.sh",
        "goblin-setup.service",
        "goblin-installer.service",
        "sandbox-kustomization.yaml",
    ]) {
        const text = await readFile(join(root, name), "utf8");
        assert.doesNotMatch(text, /python3/);
        if (name.endsWith(".sh"))
            execFileSync("bash", ["-n", join(root, name)]);
    }
    assert.deepEqual(await readFile("dependencies.lock.json"), before);
});
