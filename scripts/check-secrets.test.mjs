import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const scanner = fileURLToPath(new URL("./check-secrets.mjs", import.meta.url));
const installer = fileURLToPath(new URL("./install-git-hooks.mjs", import.meta.url));
const config = fileURLToPath(new URL("../.gitleaks.toml", import.meta.url));
const hook = fileURLToPath(new URL("../.githooks/pre-commit", import.meta.url));

// Generated locally, never authenticated, and deliberately not a literal token
// in the test source. The variable length exercises our additional OpenAI rule.
const syntheticKey = () => ["sk", "proj", randomBytes(32).toString("hex")].join("-");

function fixture(t) {
  const directory = mkdtempSync(join(tmpdir(), "goblin-secrets-test-"));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const git = (...args) => execFileSync("git", args, { cwd: directory, encoding: "utf8", stdio: ["pipe", "pipe", "pipe"] });
  const write = (path, content) => {
    mkdirSync(dirname(join(directory, path)), { recursive: true });
    writeFileSync(join(directory, path), content);
  };
  const scan = (mode = "scan") => {
    const result = spawnSync(process.execPath, [scanner, mode], { cwd: directory, encoding: "utf8" });
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

function detected(result, secret, label, rule = "goblin-openai-key") {
  assert.equal(result.status, 1);
  assert.ok(result.output.includes(`[${label}`));
  assert.ok(result.output.includes(rule));
  assert.equal(result.output.includes(secret), false, "Secret must never appear in output");
}

test("clean scans do not read ignored runtime credentials", (t) => {
  const repo = fixture(t);
  repo.write(".env", syntheticKey());
  repo.write(".goblin-auth/auth.json", JSON.stringify({ token: syntheticKey() }));
  repo.write(".visible.txt", "Safe untracked file.\n");
  assert.equal(repo.scan().status, 0);
  assert.equal(repo.scan("staged").status, 0);
});

test("untracked hidden files and unusual paths are scanned and redacted", (t) => {
  const repo = fixture(t);
  const secret = syntheticKey();
  repo.write(".hidden/path with spaces\nand newline.txt", secret);
  const result = repo.scan();
  detected(result, secret, "worktree");
  assert.ok(result.output.includes(".hidden/path with spaces\\nand newline.txt"));
});

test("a working-tree fix cannot hide a key still staged in the index", (t) => {
  const repo = fixture(t);
  const secret = syntheticKey();
  repo.write("notes.txt", secret);
  repo.git("add", "notes.txt");
  repo.write("notes.txt", "Fixed working-tree contents.\n");
  detected(repo.scan("staged"), secret, "staged");
  const result = repo.scan();
  detected(result, secret, "index");
  assert.ok(result.output.includes("worktree: 0 finding(s)"));
});

test("staged scan uses index contents, not unstaged working-tree contents", (t) => {
  const repo = fixture(t);
  const secret = syntheticKey();
  repo.write("notes.txt", "Safe staged contents.\n");
  repo.git("add", "notes.txt");
  repo.write("notes.txt", secret);
  assert.equal(repo.scan("staged").status, 0);
  detected(repo.scan(), secret, "worktree");
});

test("staged checks inspect unchanged lines in the affected file", (t) => {
  const repo = fixture(t);
  const secret = syntheticKey();
  repo.write("notes.txt", secret + "\n" + "safe line\n".repeat(20));
  repo.git("add", "notes.txt");
  repo.git("commit", "--quiet", "-m", "Synthetic historical candidate");
  repo.write("notes.txt", secret + "\n" + "safe line\n".repeat(20) + "New unrelated line.\n");
  repo.git("add", "notes.txt");
  detected(repo.scan("staged"), secret, "staged");
});

test("force-added ignored files remain in scope", (t) => {
  const repo = fixture(t);
  const secret = syntheticKey();
  repo.write(".env", secret);
  repo.git("add", "--force", ".env");
  detected(repo.scan("staged"), secret, "staged");
  detected(repo.scan(), secret, "worktree");
});

test("private auth filenames block even unfamiliar credential formats", (t) => {
  const repo = fixture(t);
  repo.write(".goblin-auth/owner-token", "synthetic-short-value");
  repo.git("add", "--force", ".goblin-auth/owner-token");
  detected(repo.scan("staged"), "synthetic-short-value", "staged", "goblin-private-auth-file");
});

test("upstream provider rules remain enabled and allow comments cannot bypass them", (t) => {
  const repo = fixture(t);
  const secret = "ghp_" + randomBytes(18).toString("hex");
  repo.write("provider.txt", secret + " # gitleaks:allow\n");
  detected(repo.scan(), secret, "worktree", "github-pat");
});

test("deleting a key permits cleanup, while history still reports its earlier commit", (t) => {
  const repo = fixture(t);
  const secret = syntheticKey();
  repo.write("notes.txt", secret);
  repo.git("add", "notes.txt");
  repo.git("commit", "--quiet", "-m", "Synthetic historical candidate");
  repo.git("rm", "--quiet", "notes.txt");
  assert.equal(repo.scan("staged").status, 0);
  repo.git("commit", "--quiet", "-m", "Remove candidate");
  assert.equal(repo.scan().status, 0);
  detected(repo.scan("history"), secret, "history");
});

test("symlinks are scanned as link text without reading external files", (t) => {
  const repo = fixture(t);
  const external = mkdtempSync(join(tmpdir(), "goblin-secrets-external-"));
  t.after(() => rmSync(external, { recursive: true, force: true }));
  writeFileSync(join(external, "private.txt"), syntheticKey());
  symlinkSync(join(external, "private.txt"), join(repo.directory, "link.txt"));
  repo.git("add", "link.txt");
  assert.equal(repo.scan().status, 0);
});

test("a directory replaced by a symlink cannot expose external contents", (t) => {
  const repo = fixture(t);
  repo.write("nested/file.txt", "Safe tracked contents.\n");
  repo.git("add", "nested/file.txt");
  rmSync(join(repo.directory, "nested"), { recursive: true });
  const external = mkdtempSync(join(tmpdir(), "goblin-secrets-external-"));
  t.after(() => rmSync(external, { recursive: true, force: true }));
  writeFileSync(join(external, "file.txt"), syntheticKey());
  symlinkSync(external, join(repo.directory, "nested"), "dir");
  const result = repo.scan();
  assert.equal(result.status, 2);
  assert.ok(result.output.includes("symlink directory"));
});

test("submodules fail explicitly instead of being silently skipped", (t) => {
  const repo = fixture(t);
  const commit = repo.git("rev-parse", "HEAD").trim();
  repo.git("update-index", "--add", "--cacheinfo", `160000,${commit},nested-repo`);
  const result = repo.scan();
  assert.equal(result.status, 2);
  assert.ok(result.output.includes("scan submodules separately"));
});

test("installed Git hook rejects a secret and allows a corrected commit", (t) => {
  const repo = fixture(t);
  repo.git("config", "--unset", "core.hooksPath");
  mkdirSync(join(repo.directory, "scripts"));
  mkdirSync(join(repo.directory, ".githooks"));
  copyFileSync(scanner, join(repo.directory, "scripts/check-secrets.mjs"));
  copyFileSync(hook, join(repo.directory, ".githooks/pre-commit"));
  const setup = spawnSync(process.execPath, [installer], { cwd: repo.directory, encoding: "utf8" });
  assert.equal(setup.status, 0);
  const secret = syntheticKey();
  const original = repo.git("rev-parse", "HEAD");
  repo.write("notes.txt", secret);
  repo.git("add", "notes.txt");
  const commit = spawnSync("git", ["commit", "--quiet", "-m", "Must be blocked"], { cwd: repo.directory, encoding: "utf8" });
  assert.notEqual(commit.status, 0);
  assert.equal((commit.stdout + commit.stderr).includes(secret), false);
  assert.equal(repo.git("rev-parse", "HEAD"), original);
  repo.write("notes.txt", "Corrected contents.\n");
  repo.git("add", "notes.txt");
  repo.git("commit", "--quiet", "-m", "Safe commit");
  assert.notEqual(repo.git("rev-parse", "HEAD"), original);
});
