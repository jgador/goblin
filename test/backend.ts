import { spawn } from "node:child_process";
import { once } from "node:events";
import { createInterface } from "node:readline";
import { resolve } from "node:path";

export interface BackendOptions {
  dataDir: string;
  publicOrigin?: string;
  listenUrl?: string;
  scenario?: string;
  timeoutMs?: number;
  promptTimeoutMs?: number;
  verification?: "accepted" | "unverified" | "invalid";
}

// This adapter only launches the C# test host. All HTTP and Codex behavior runs in .NET.
export async function startBackend(options: BackendOptions) {
  const child = spawn("dotnet", [resolve("tests/Goblin.TestHost/bin/Debug/net10.0/Goblin.TestHost.dll"),
    JSON.stringify({ root: process.cwd(), node: process.execPath, ...options })], { stdio: ["pipe", "pipe", "pipe"] });
  const exited = once(child, "exit");
  let errors = "";
  child.stderr.setEncoding("utf8").on("data", (chunk) => { errors += chunk; });
  const lines = createInterface({ input: child.stdout });
  let info: { url: string; tokenFile: string } | undefined;
  try {
    for await (const line of lines) {
      info = JSON.parse(line) as typeof info;
      break;
    }
  } catch { child.kill(); }
  if (!info) {
    await exited;
    throw new Error(errors || "The C# test host did not start.");
  }
  let closed = false;
  return { ...info, async close() {
    if (closed) return;
    closed = true;
    child.stdin.end("\n");
    const timer = setTimeout(() => child.kill("SIGKILL"), 10000);
    try { await exited; } finally { clearTimeout(timer); }
  } };
}
