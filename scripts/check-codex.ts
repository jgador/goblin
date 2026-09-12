import { mkdtemp, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import assert from "node:assert/strict";
import { createApplication } from "../src/server.js";

// Exercises the installed, pinned binary with an empty private CODEX_HOME.
// A synthetic key tests local credential storage only. This bypasses HTTP key
// verification and never reads personal credentials or submits a model task.
const dataDir = await mkdtemp(join(tmpdir(), "goblin-codex-check-"));
let app: Awaited<ReturnType<typeof createApplication>> | undefined;
try {
  app = await createApplication({ dataDir });
  const status = await app.authentication.status();
  assert.equal(status.runtimeReady, true);
  assert.equal(status.account, null);
  assert.equal(status.login, null);
  await app.codex.request("account/login/start", {
    type: "apiKey", apiKey: "sk-goblin-storage-check-this-is-not-a-real-api-key",
  });
  assert.deepEqual((await app.authentication.status()).account, { type: "apiKey" });
  assert.equal((await stat(join(dataDir, "codex/auth.json"))).mode & 0o777, 0o600);
  await app.close();
  app = await createApplication({ dataDir });
  assert.deepEqual((await app.authentication.status()).account, { type: "apiKey" });
  assert.equal((await app.authentication.logout()).account, null);
  console.log("Pinned Codex passed initialization, isolated key storage, process replacement, and logout checks using a synthetic key.");
} finally {
  await app?.close();
  await rm(dataDir, { recursive: true, force: true });
}
