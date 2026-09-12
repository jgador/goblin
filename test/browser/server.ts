import { mkdir, rm, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createApplication } from "../../src/server.js";
import { PublicError } from "../../src/errors.js";

// This server is a test fixture. It never runs a real login or contacts OpenAI.
const dataDir = resolve(".goblin-browser-test");
await rm(dataDir, { recursive: true, force: true });
await mkdir(dataDir, { recursive: true, mode: 0o700 });
await writeFile(resolve(dataDir, "owner-token"), "browser-test-access-code-never-use-in-production", { mode: 0o600 });
const app = await createApplication({ dataDir, publicOrigin: "http://127.0.0.1:8798",
  codexOptions: { args: [fileURLToPath(new URL("../fixtures/fake-codex.js", import.meta.url)), "manual"] },
  verifyApiKey: async (key) => {
    if (key.includes("invalid")) throw new PublicError("invalid_api_key", "OpenAI rejected this API key. Check the key and try again.");
    return "accepted";
  },
});
app.server.listen(8798, "127.0.0.1");
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, async () => {
  await app.close();
  await rm(dataDir, { recursive: true, force: true });
});
