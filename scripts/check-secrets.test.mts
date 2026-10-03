import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import {
    copyFileSync,
    mkdirSync,
    mkdtempSync,
    rmSync,
    symlinkSync,
    writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import type { TestContext } from "node:test";

const scanner = fileURLToPath(new URL("./check-secrets.mts", import.meta.url));
const installer = fileURLToPath(
    new URL("./install-git-hooks.mts", import.meta.url),
);
const config = fileURLToPath(new URL("../.gitleaks.toml", import.meta.url));
const hook = fileURLToPath(new URL("../.githooks/pre-commit", import.meta.url));

// Generated locally, never authenticated, and deliberately not a literal token
// in the test source. The variable length exercises our additional OpenAI rule.
const syntheticKey = () =>
    ["sk", "proj", randomBytes(32).toString("hex")].join("-");

function fixture(t: TestContext) {
    const directory = mkdtempSync(join(tmpdir(), "goblin-secrets-test-"));
    t.after(() => rmSync(directory, { recursive: true, force: true }));
    const git = (...args: string[]) =>
        execFileSync("git", args, {
            cwd: directory,
            encoding: "utf8",
            stdio: ["pipe", "pipe", "pipe"],
        });
    const write = (path: string, content: string) => {
        mkdirSync(dirname(join(directory, path)), { recursive: true });
        writeFileSync(join(directory, path), content);
    };
    const scan = (mode = "scan") => {
        const result = spawnSync(process.execPath, [scanner, mode], {
            cwd: directory,
            encoding: "utf8",
        });
        return { status: result.status, output: result.stdout + result.stderr };
    };
    git("init", "--quiet");
    git("config", "user.name", "Secret scanner test");
    git("config", "user.email", "scanner@example.invalid");
    git("config", "commit.gpgsign", "false");
    // Isolate tests from any global hooks on the developer's machine.
    git("config", "core.hooksPath", join(directory, "no-hooks"));
    copyFileSync(config, join(directory, ".gitleaks.toml"));
    write(".gitignore", ".env\n.goblin-auth/\n");
    write("notes.txt", "Safe content.\n");
    git("add", ".");
    git("commit", "--quiet", "-m", "Initial test fixture");
    return { directory, git, write, scan };
}

function detected(
    result: { status: number | null; output: string },
    secret: string,
    label: string,
    rule = "goblin-openai-key",
) {
    assert.equal(result.status, 1);
    assert.ok(result.output.includes(`[${label}`));
    assert.ok(result.output.includes(rule));
    assert.equal(
        result.output.includes(secret),
        false,
        "Secret must never appear in output",
    );
}

const webSettingsPath = "backend/src/Goblin.Web/appsettings.json";
const databaseConnection = (password: string) =>
    `Host=goblin-postgres;Port=5432;Database=goblin;Username=goblin_app;Password=${password}`;

const releaseMetadataPaths = [
    "dependencies.lock.json",
    "deploy/azure/azuredeploy.json",
    "deploy/azure/azuredeploy.portal.json",
];
const credentialsSourcePath = "tools/goblinctl/src/credentials.rs";

test("source-file checksums in release metadata pass worktree, index, staged, and history scans", (t) => {
    const gitRepository = fixture(t);
    const checksum = createHash("sha256")
        .update("pub fn synthetic_release_input() {}\n")
        .digest("hex");
    for (const path of releaseMetadataPaths) {
        gitRepository.write(
            path,
            JSON.stringify(
                { installer: { files: { [credentialsSourcePath]: checksum } } },
                null,
                2,
            ) + "\n",
        );
    }
    assert.equal(gitRepository.scan().status, 0);
    gitRepository.git("add", ...releaseMetadataPaths);
    assert.equal(gitRepository.scan("staged").status, 0);
    assert.equal(gitRepository.scan().status, 0);
    gitRepository.git("commit", "--quiet", "-m", "Release source checksums");
    assert.equal(gitRepository.scan("history").status, 0);
});

test("release checksum exceptions do not hide other credentials in the same files or lines", (t) => {
    for (const path of releaseMetadataPaths) {
        const gitRepository = fixture(t);
        const apiKey = randomBytes(32).toString("hex");
        const openaiKey = syntheticKey();
        for (const indentation of [2, undefined]) {
            gitRepository.write(
                path,
                JSON.stringify(
                    {
                        [credentialsSourcePath]:
                            randomBytes(32).toString("hex"),
                        ApiKey: apiKey,
                        OpenAI: { ApiKey: openaiKey },
                    },
                    null,
                    indentation,
                ) + "\n",
            );
            const result = gitRepository.scan();
            detected(result, apiKey, "worktree", "generic-api-key");
            detected(result, openaiKey, "worktree");
            gitRepository.git("add", path);
            const staged = gitRepository.scan("staged");
            detected(staged, apiKey, "staged", "generic-api-key");
            detected(staged, openaiKey, "staged");
        }
    }
});

test("release checksum exceptions require the exact file, field, and hash shape", (t) => {
    for (const [path, field, value, rule] of [
        [
            "deploy/azure/another.json",
            credentialsSourcePath,
            randomBytes(32).toString("hex"),
            "generic-api-key",
        ],
        [
            releaseMetadataPaths[0],
            "credentials",
            randomBytes(32).toString("hex"),
            "generic-api-key",
        ],
        [
            releaseMetadataPaths[0],
            credentialsSourcePath,
            randomBytes(31).toString("hex"),
            "generic-api-key",
        ],
        [
            releaseMetadataPaths[0],
            credentialsSourcePath,
            syntheticKey(),
            "goblin-openai-key",
        ],
    ]) {
        const gitRepository = fixture(t);
        gitRepository.write(
            path,
            JSON.stringify({ [field]: value }, null, 2) + "\n",
        );
        detected(gitRepository.scan(), value, "worktree", rule);
    }
});

test("the certificate database connection passes worktree, staged, and history scans", (t) => {
    const gitRepository = fixture(t);
    gitRepository.write(
        webSettingsPath,
        JSON.stringify(
            {
                ConnectionStrings: {
                    Goblin: "Host=goblin-postgres;Port=5432;Database=goblin;Username=goblin_app;SSL Mode=VerifyFull;Root Certificate=/etc/goblin-postgres/ca.crt;SSL Certificate=/etc/goblin-postgres/tls.crt;SSL Key=/etc/goblin-postgres/tls.key",
                },
            },
            null,
            2,
        ) + "\n",
    );
    assert.equal(gitRepository.scan().status, 0);
    gitRepository.git("add", webSettingsPath);
    assert.equal(gitRepository.scan("staged").status, 0);
    assert.equal(gitRepository.scan().status, 0);
    gitRepository.git(
        "commit",
        "--quiet",
        "-m",
        "Certificate database configuration",
    );
    assert.equal(gitRepository.scan("history").status, 0);
});

test("database passwords are rejected, including the former web configuration exception", (t) => {
    const password = randomBytes(32).toString("hex");
    const connection = databaseConnection(password);
    for (const [path, values] of [
        [webSettingsPath, { Goblin: connection }],
        [
            "backend/tools/Goblin.Database/appsettings.json",
            { Goblin: connection },
        ],
        ["backend/src/AnotherApp/appsettings.json", { Goblin: connection }],
        [
            webSettingsPath,
            {
                GoblinAdmin: connection.replace(
                    "Username=goblin_app",
                    "Username=goblin_admin",
                ),
            },
        ],
        [
            webSettingsPath,
            {
                Goblin: connection.replace(
                    "Host=goblin-postgres",
                    "Host=another-database",
                ),
            },
        ],
    ] as const) {
        const gitRepository = fixture(t);
        gitRepository.write(
            path,
            JSON.stringify({ ConnectionStrings: values }, null, 2) + "\n",
        );
        detected(gitRepository.scan(), password, "worktree", "generic-api-key");
        gitRepository.git("add", path);
        detected(
            gitRepository.scan("staged"),
            password,
            "staged",
            "generic-api-key",
        );
    }
});

test("other generic credentials and OpenAI keys in the web configuration remain blocked", (t) => {
    const gitRepository = fixture(t);
    const password = randomBytes(32).toString("hex");
    const apiKey = randomBytes(32).toString("hex");
    const openaiKey = syntheticKey();
    for (const indentation of [2, undefined]) {
        gitRepository.write(
            webSettingsPath,
            JSON.stringify(
                {
                    ConnectionStrings: { Goblin: databaseConnection(password) },
                    ApiKey: apiKey,
                    OpenAI: { ApiKey: openaiKey },
                },
                null,
                indentation,
            ) + "\n",
        );
        const result = gitRepository.scan();
        detected(result, apiKey, "worktree", "generic-api-key");
        detected(result, openaiKey, "worktree");
        gitRepository.git("add", webSettingsPath);
        detected(gitRepository.scan("staged"), openaiKey, "staged");
    }
});

test("clean scans do not read ignored runtime credentials", (t) => {
    const gitRepository = fixture(t);
    gitRepository.write(".env", syntheticKey());
    gitRepository.write(
        ".goblin-auth/auth.json",
        JSON.stringify({ token: syntheticKey() }),
    );
    gitRepository.write(".visible.txt", "Safe untracked file.\n");
    assert.equal(gitRepository.scan().status, 0);
    assert.equal(gitRepository.scan("staged").status, 0);
});

test("untracked hidden files and unusual paths are scanned and redacted", (t) => {
    const gitRepository = fixture(t);
    const secret = syntheticKey();
    gitRepository.write(".hidden/path with spaces\nand newline.txt", secret);
    const result = gitRepository.scan();
    detected(result, secret, "worktree");
    assert.ok(
        result.output.includes(".hidden/path with spaces\\nand newline.txt"),
    );
});

test("a working-tree fix cannot hide a key still staged in the index", (t) => {
    const gitRepository = fixture(t);
    const secret = syntheticKey();
    gitRepository.write("notes.txt", secret);
    gitRepository.git("add", "notes.txt");
    gitRepository.write("notes.txt", "Fixed working-tree contents.\n");
    detected(gitRepository.scan("staged"), secret, "staged");
    const result = gitRepository.scan();
    detected(result, secret, "index");
    assert.ok(result.output.includes("worktree: 0 finding(s)"));
});

test("staged scan uses index contents, not unstaged working-tree contents", (t) => {
    const gitRepository = fixture(t);
    const secret = syntheticKey();
    gitRepository.write("notes.txt", "Safe staged contents.\n");
    gitRepository.git("add", "notes.txt");
    gitRepository.write("notes.txt", secret);
    assert.equal(gitRepository.scan("staged").status, 0);
    detected(gitRepository.scan(), secret, "worktree");
});

test("staged checks inspect unchanged lines in the affected file", (t) => {
    const gitRepository = fixture(t);
    const secret = syntheticKey();
    gitRepository.write("notes.txt", secret + "\n" + "safe line\n".repeat(20));
    gitRepository.git("add", "notes.txt");
    gitRepository.git(
        "commit",
        "--quiet",
        "-m",
        "Synthetic historical candidate",
    );
    gitRepository.write(
        "notes.txt",
        secret + "\n" + "safe line\n".repeat(20) + "New unrelated line.\n",
    );
    gitRepository.git("add", "notes.txt");
    detected(gitRepository.scan("staged"), secret, "staged");
});

test("force-added ignored files remain in scope", (t) => {
    const gitRepository = fixture(t);
    const secret = syntheticKey();
    gitRepository.write(".env", secret);
    gitRepository.git("add", "--force", ".env");
    detected(gitRepository.scan("staged"), secret, "staged");
    detected(gitRepository.scan(), secret, "worktree");
});

test("private auth filenames block even unfamiliar credential formats", (t) => {
    const gitRepository = fixture(t);
    gitRepository.write(".goblin-auth/owner-password", "synthetic-short-value");
    gitRepository.git("add", "--force", ".goblin-auth/owner-password");
    detected(
        gitRepository.scan("staged"),
        "synthetic-short-value",
        "staged",
        "goblin-private-auth-file",
    );
});

test("the local secret placeholder is allowed but force-added credentials are blocked", (t) => {
    const gitRepository = fixture(t);
    gitRepository.write(
        ".gitignore",
        ".goblin-secrets/*\n!.goblin-secrets/.gitkeep\n",
    );
    gitRepository.write(".goblin-secrets/.gitkeep", "");
    gitRepository.write(
        ".goblin-secrets/owner-password",
        "synthetic-local-verifier",
    );
    gitRepository.git("add", ".");
    assert.equal(gitRepository.scan("staged").status, 0);
    gitRepository.git("add", "--force", ".goblin-secrets/owner-password");
    detected(
        gitRepository.scan("staged"),
        "synthetic-local-verifier",
        "staged",
        "goblin-private-auth-file",
    );
});

test("upstream provider rules remain enabled and allow comments cannot bypass them", (t) => {
    const gitRepository = fixture(t);
    const secret = "ghp_" + randomBytes(18).toString("hex");
    gitRepository.write("provider.txt", secret + " # gitleaks:allow\n");
    detected(gitRepository.scan(), secret, "worktree", "github-pat");
});

test("deleting a key permits cleanup, while history still reports its earlier commit", (t) => {
    const gitRepository = fixture(t);
    const secret = syntheticKey();
    gitRepository.write("notes.txt", secret);
    gitRepository.git("add", "notes.txt");
    gitRepository.git(
        "commit",
        "--quiet",
        "-m",
        "Synthetic historical candidate",
    );
    gitRepository.git("rm", "--quiet", "notes.txt");
    assert.equal(gitRepository.scan("staged").status, 0);
    gitRepository.git("commit", "--quiet", "-m", "Remove candidate");
    assert.equal(gitRepository.scan().status, 0);
    detected(gitRepository.scan("history"), secret, "history");
});

test("symlinks are scanned as link text without reading external files", (t) => {
    const gitRepository = fixture(t);
    const external = mkdtempSync(join(tmpdir(), "goblin-secrets-external-"));
    t.after(() => rmSync(external, { recursive: true, force: true }));
    writeFileSync(join(external, "private.txt"), syntheticKey());
    symlinkSync(
        join(external, "private.txt"),
        join(gitRepository.directory, "link.txt"),
    );
    gitRepository.git("add", "link.txt");
    assert.equal(gitRepository.scan().status, 0);
});

test("a directory replaced by a symlink cannot expose external contents", (t) => {
    const gitRepository = fixture(t);
    gitRepository.write("nested/file.txt", "Safe tracked contents.\n");
    gitRepository.git("add", "nested/file.txt");
    rmSync(join(gitRepository.directory, "nested"), { recursive: true });
    const external = mkdtempSync(join(tmpdir(), "goblin-secrets-external-"));
    t.after(() => rmSync(external, { recursive: true, force: true }));
    writeFileSync(join(external, "file.txt"), syntheticKey());
    symlinkSync(external, join(gitRepository.directory, "nested"), "dir");
    const result = gitRepository.scan();
    assert.equal(result.status, 2);
    assert.ok(result.output.includes("symlink directory"));
});

test("submodules fail explicitly instead of being silently skipped", (t) => {
    const gitRepository = fixture(t);
    const commit = gitRepository.git("rev-parse", "HEAD").trim();
    gitRepository.git(
        "update-index",
        "--add",
        "--cacheinfo",
        `160000,${commit},nested-repo`,
    );
    const result = gitRepository.scan();
    assert.equal(result.status, 2);
    assert.ok(result.output.includes("scan submodules separately"));
});

test("installed Git hook rejects a secret and allows a corrected commit", (t) => {
    const gitRepository = fixture(t);
    gitRepository.git("config", "--unset", "core.hooksPath");
    mkdirSync(join(gitRepository.directory, "scripts"));
    mkdirSync(join(gitRepository.directory, ".githooks"));
    copyFileSync(
        scanner,
        join(gitRepository.directory, "scripts/check-secrets.mts"),
    );
    copyFileSync(hook, join(gitRepository.directory, ".githooks/pre-commit"));
    const setup = spawnSync(process.execPath, [installer], {
        cwd: gitRepository.directory,
        encoding: "utf8",
    });
    assert.equal(setup.status, 0);
    const secret = syntheticKey();
    const original = gitRepository.git("rev-parse", "HEAD");
    gitRepository.write("notes.txt", secret);
    gitRepository.git("add", "notes.txt");
    const commit = spawnSync(
        "git",
        ["commit", "--quiet", "-m", "Must be blocked"],
        { cwd: gitRepository.directory, encoding: "utf8" },
    );
    assert.notEqual(commit.status, 0);
    assert.equal((commit.stdout + commit.stderr).includes(secret), false);
    assert.equal(gitRepository.git("rev-parse", "HEAD"), original);
    gitRepository.write("notes.txt", "Corrected contents.\n");
    gitRepository.git("add", "notes.txt");
    gitRepository.git("commit", "--quiet", "-m", "Safe commit");
    assert.notEqual(gitRepository.git("rev-parse", "HEAD"), original);
});
