import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { spawn } from "node:child_process";
import { once } from "node:events";

// Exercises the installed, pinned binary with an empty private CODEX_HOME.
// A synthetic key tests local credential storage only. This bypasses HTTP key
// verification and never reads personal credentials or submits a model task.
const dataDir = await mkdtemp(join(tmpdir(), "goblin-codex-check-"));
try {
  for (const scenario of ["storage", "restore"]) {
    const child = spawn("dotnet", [resolve("tests/Goblin.TestHost/bin/Debug/net10.0/Goblin.TestHost.dll"),
      JSON.stringify({ root: process.cwd(), dataDir, scenario, realCodex: true, timeoutMs: 20000 })], { stdio: "inherit" });
    const [code] = await once(child, "exit");
    if (code !== 0) throw new Error("The pinned Codex storage check failed.");
  }
  console.log("Pinned Rust Codex passed initialization, isolated key storage, process replacement, and logout through the C# client.");
} finally {
  await rm(dataDir, { recursive: true, force: true });
}
