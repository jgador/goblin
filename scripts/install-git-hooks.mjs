import { execFileSync, spawnSync } from "node:child_process";
import { chmodSync, existsSync, readdirSync } from "node:fs";
import { join } from "node:path";

const git = (...args) => execFileSync("git", args, { encoding: "utf8" }).trim();
const root = git("rev-parse", "--show-toplevel");
process.chdir(root);

if (spawnSync("gitleaks", ["version"], { stdio: "ignore" }).status !== 0) {
  console.error("Install Gitleaks 8.30.1 or newer first; see docs/secret-scanning.md.");
  process.exit(1);
}

const configured = spawnSync("git", ["config", "--get", "core.hooksPath"], { encoding: "utf8" }).stdout.trim();
if (configured && configured !== ".githooks") {
  console.error("An existing core.hooksPath is configured. Add the command from .githooks/pre-commit to your existing pre-commit hook instead.");
  process.exit(1);
}

if (!configured) {
  const hooks = git("rev-parse", "--git-path", "hooks");
  if (existsSync(hooks) && readdirSync(hooks).some((name) => !name.endsWith(".sample"))) {
    console.error("Existing Git hooks found. Add the command from .githooks/pre-commit to your existing pre-commit hook instead.");
    process.exit(1);
  }
}

chmodSync(join(root, ".githooks/pre-commit"), 0o755);
git("config", "--local", "core.hooksPath", ".githooks");
console.log("Enabled Gitleaks pre-commit checks for this checkout.");
