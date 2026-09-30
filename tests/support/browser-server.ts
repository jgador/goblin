import { environmentVariables as Env } from "../../config/environment.mjs";
import { goblinctl } from "./goblinctl.js";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { startBackend } from "./backend.js";

// This server is a test fixture. It never runs a real login or contacts OpenAI.
const dataDir = resolve(".artifacts/playwright/browser-server");
await rm(dataDir, { recursive: true, force: true });
await mkdir(dataDir, { recursive: true, mode: 0o700 });
// Existing journeys start after timezone onboarding. The timezone journeys
// exercise first access and updates explicitly.
await writeFile(
    resolve(dataDir, "workspace-preferences.json"),
    JSON.stringify({ timeZone: "Asia/Manila" }),
);
const passwordHashFile = resolve(dataDir, "owner-password");
// A private, offline GitHub catalog for the durable repository journeys.
const githubProfile = resolve(dataDir, "github-cli/active");
await mkdir(githubProfile, { recursive: true, mode: 0o700 });
await writeFile(
    resolve(githubProfile, "account.json"),
    JSON.stringify({
        generation: "browser-fixture",
        accountId: "42",
        login: "owner",
    }),
    { mode: 0o600 },
);
const gitHubCommand = resolve(dataDir, "fake-gh");
await writeFile(
    gitHubCommand,
    `#!${process.execPath}
if (process.argv[2] !== "api" || process.argv[3] !== "repos/owner/repo") process.exit(1);
console.log(JSON.stringify({ id: 22, full_name: "owner/repo", default_branch: "main", permissions: { push: true } }));
`,
    { mode: 0o700 },
);
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
    gitHubCommand,
    enableWork: !!process.env[Env.GOBLIN_TEST_POSTGRES_APP.name],
    publicOrigin: "http://127.0.0.1:8798",
    listenUrl: "http://127.0.0.1:8798",
});
for (const signal of ["SIGINT", "SIGTERM"])
    process.on(signal, async () => {
        await app.close();
        await rm(dataDir, { recursive: true, force: true });
    });
