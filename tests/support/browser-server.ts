import { goblinctl } from "./goblinctl.js";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { startBackend } from "./backend.js";

// This server is a test fixture. It never runs a real login or contacts OpenAI.
const dataDir = resolve(".goblin-browser-test");
await rm(dataDir, { recursive: true, force: true });
await mkdir(dataDir, { recursive: true, mode: 0o700 });
// Existing journeys start after timezone onboarding. The timezone journeys
// exercise first access and updates explicitly.
await writeFile(
    resolve(dataDir, "workspace-preferences.json"),
    JSON.stringify({ timeZone: "Asia/Manila" }),
);
const passwordHashFile = resolve(dataDir, "owner-password");
await writeFile(
    passwordHashFile,
    execFileSync(goblinctl, ["internal", "hash-password"], {
        input: "a", // Test-only password: confirm there is no minimum length.
    }),
    { mode: 0o600 },
);
const app = await startBackend({
    dataDir,
    passwordHashFile,
    enableWork: !!process.env.GOBLIN_TEST_POSTGRES_APP,
    publicOrigin: "http://127.0.0.1:8798",
    listenUrl: "http://127.0.0.1:8798",
});
for (const signal of ["SIGINT", "SIGTERM"])
    process.on(signal, async () => {
        await app.close();
        await rm(dataDir, { recursive: true, force: true });
    });
