import { execFileSync, spawnSync } from "node:child_process";
import {
  lstatSync, mkdirSync, mkdtempSync, readFileSync, readlinkSync, realpathSync,
  rmSync, writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, relative } from "node:path";

// Scan Git's inventory, rather than recursively reading ignored auth stores,
// node_modules, or build output. Never print raw scanner output or file contents.
const mode = process.argv[2] ?? "scan";
if (!["scan", "staged", "history"].includes(mode) || process.argv.length > 3) {
  console.error("Usage: node scripts/check-secrets.mjs [scan|staged|history]");
  process.exit(2);
}

let temporary;
let root;

function git(args, options = {}) {
  return execFileSync("git", args, {
    cwd: root, maxBuffer: 256 * 1024 * 1024, stdio: ["pipe", "pipe", "pipe"], ...options,
  });
}

function paths(args) {
  return git(args).toString("utf8").split("\0").filter(Boolean);
}

function writeSnapshot(directory, path, content) {
  const destination = join(directory, path);
  mkdirSync(dirname(destination), { recursive: true });
  writeFileSync(destination, content, { mode: 0o600 });
}

function indexEntries() {
  return paths(["ls-files", "--stage", "-z"]).map((entry) => {
    const separator = entry.indexOf("\t");
    const [fileMode, object, stage] = entry.slice(0, separator).split(" ");
    const path = entry.slice(separator + 1);
    if (stage !== "0") throw new Error("Resolve merge conflicts before scanning.");
    if (!["100644", "100755", "120000"].includes(fileMode)) {
      throw new Error(`Cannot scan Git entry ${JSON.stringify(path)} (mode ${fileMode}); scan submodules separately.`);
    }
    return { object, path };
  });
}

function snapshotIndex(entries, directory) {
  if (entries.length === 0) return;
  // Read exact index blobs, including partial staging, without changing the
  // index or running checkout filters. NUL-delimited inventory preserves paths.
  const blobs = git(["cat-file", "--batch"], {
    input: entries.map(({ object }) => object).join("\n") + "\n",
  });
  let offset = 0;
  for (const { object, path } of entries) {
    const end = blobs.indexOf(10, offset);
    const [actual, type, length] = blobs.subarray(offset, end).toString("ascii").split(" ");
    const size = Number(length);
    if (actual !== object || type !== "blob" || !Number.isSafeInteger(size) || size < 0) {
      throw new Error("Unable to read the complete Git index; scan incomplete.");
    }
    offset = end + 1;
    if (offset + size >= blobs.length) throw new Error("Truncated index snapshot; scan incomplete.");
    writeSnapshot(directory, path, blobs.subarray(offset, offset + size));
    offset += size + 1;
  }
}

function snapshotWorktree(directory) {
  const inventory = new Set(paths(["ls-files", "--cached", "--others", "--exclude-standard", "-z"]));
  let count = 0;
  for (const path of inventory) {
    const source = join(root, path);
    let stat;
    try {
      stat = lstatSync(source);
    } catch (error) {
      if (error.code === "ENOENT") continue; // Tracked working-tree deletion.
      throw error;
    }
    if (realpathSync(dirname(source)) !== dirname(source)) {
      throw new Error(`Cannot scan ${JSON.stringify(path)} through a symlink directory; review it separately.`);
    }
    if (stat.isSymbolicLink()) {
      // Git publishes the link text, not the external file it points to.
      writeSnapshot(directory, path, readlinkSync(source));
    } else if (stat.isFile()) {
      writeSnapshot(directory, path, readFileSync(source));
    } else {
      throw new Error(`Cannot scan ${JSON.stringify(path)}; expected a file or symlink.`);
    }
    count++;
  }
  return count;
}

function scan(label, directory, count) {
  const report = join(temporary, `${label}.json`);
  const args = mode === "history"
    ? ["git", root, "--log-opts=--all --full-history"]
    : ["dir", directory];
  const result = spawnSync("gitleaks", [
    ...args, "--config", join(root, ".gitleaks.toml"),
    "--redact=100", "--no-banner", "--no-color", "--log-level=error",
    "--ignore-gitleaks-allow", "--gitleaks-ignore-path", temporary,
    "--max-archive-depth=2", "--report-format=json", "--report-path", report,
  ], { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  if (result.error || result.signal || ![0, 1].includes(result.status)) {
    throw new Error("Gitleaks failed; scan incomplete. Check the installed version and .gitleaks.toml. Raw output is withheld to protect secrets.");
  }
  const findings = JSON.parse(readFileSync(report, "utf8"));
  for (const finding of findings) {
    const path = mode === "history" ? finding.File : relative(directory, finding.File);
    const commit = finding.Commit ? ` @ ${finding.Commit.slice(0, 12)}` : "";
    console.error(`[${label}${commit}] ${JSON.stringify(path)}:${finding.StartLine} (${finding.RuleID})`);
  }
  if (result.status === 1 && findings.length === 0) {
    throw new Error("Gitleaks failed without a finding report; scan incomplete.");
  }
  console.log(`${label}: ${findings.length} finding(s)${count === undefined ? "" : ` in ${count} file(s)`}.`);
  return findings.length > 0;
}

try {
  root = realpathSync(git(["rev-parse", "--show-toplevel"]).toString("utf8").trim());
  const version = spawnSync("gitleaks", ["version"], { encoding: "utf8" });
  const numbers = version.stdout?.trim().replace(/^v/, "").split(".").map(Number);
  if (version.status !== 0 || !numbers || numbers.length !== 3 || !numbers.every(Number.isInteger)
    || numbers[0] !== 8 || numbers[1] < 30 || (numbers[1] === 30 && numbers[2] < 1)) {
    throw new Error("Install Gitleaks 8.30.1 or a newer 8.x release on PATH; see docs/secret-scanning.md.");
  }
  temporary = mkdtempSync(join(tmpdir(), "goblin-secrets-"));
  let found = false;
  if (mode === "history") {
    found = scan("history", root);
  } else {
    let entries = indexEntries();
    if (mode === "staged") {
      const changed = new Set(paths(["diff", "--cached", "--name-only", "--no-renames", "--diff-filter=ACMT", "-z"]));
      entries = entries.filter(({ path }) => changed.has(path));
    }
    const index = join(temporary, "index");
    mkdirSync(index);
    snapshotIndex(entries, index);
    found = scan(mode === "staged" ? "staged" : "index", index, entries.length);
    if (mode === "scan") {
      const worktree = join(temporary, "worktree");
      mkdirSync(worktree);
      const count = snapshotWorktree(worktree);
      found = scan("worktree", worktree, count) || found;
    }
  }
  if (found) {
    console.error("Possible secrets found. Review the locations above, remove sensitive values, and restage any corrected files before committing.");
    process.exitCode = 1;
  }
} catch (error) {
  // execFileSync errors can contain Git output and thus credential values.
  console.error(error instanceof Error && !("stdout" in error) && !("stderr" in error)
    ? error.message : "Git operation failed; secret scan incomplete.");
  process.exitCode = 2;
} finally {
  if (temporary) rmSync(temporary, { recursive: true, force: true });
}
