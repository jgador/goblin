import { mkdir, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { startBackend } from "../backend.js";

// This server is a test fixture. It never runs a real login or contacts OpenAI.
const dataDir = resolve(".goblin-browser-test");
await rm(dataDir, { recursive: true, force: true });
await mkdir(dataDir, { recursive: true, mode: 0o700 });
await writeFile(resolve(dataDir, "owner-token"), "browser-test-access-code-never-use-in-production", { mode: 0o600 });
const app = await startBackend({ dataDir, publicOrigin: "http://127.0.0.1:8798", listenUrl: "http://127.0.0.1:8798" });
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, async () => {
  await app.close();
  await rm(dataDir, { recursive: true, force: true });
});
