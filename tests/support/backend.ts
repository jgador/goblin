import { spawn } from "node:child_process";
import { once } from "node:events";
import { pbkdf2Sync, randomBytes } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { createInterface } from "node:readline";
import { resolve } from "node:path";

export interface BackendOptions {
    dataDir: string;
    passwordHashFile?: string;
    publicOrigin?: string;
    allowInsecureHttp?: boolean;
    listenUrl?: string;
    scenario?: string;
    timeoutMs?: number;
    promptTimeoutMs?: number;
    verification?: "accepted" | "unverified" | "invalid";
    enableWork?: boolean;
}

export async function writePasswordHash(path: string, password: string) {
    const salt = randomBytes(16);
    const digest = pbkdf2Sync(password, salt, 600_000, 32, "sha256");
    await writeFile(
        path,
        `pbkdf2-sha256$600000$${salt.toString("base64")}$${digest.toString("base64")}\n`,
        { mode: 0o600 },
    );
}

// This adapter only launches the C# test host. All HTTP and Codex behavior runs in .NET.
export async function startBackend(options: BackendOptions) {
    const child = spawn(
        "dotnet",
        [
            resolve(
                "backend/tests/Goblin.TestHost/bin/Debug/net10.0/Goblin.TestHost.dll",
            ),
            JSON.stringify({
                root: process.cwd(),
                node: process.execPath,
                ...options,
            }),
        ],
        { stdio: ["pipe", "pipe", "pipe"] },
    );
    const exited = once(child, "exit");
    let errors = "";
    child.stderr.setEncoding("utf8").on("data", (chunk) => {
        errors += chunk;
    });
    const lines = createInterface({ input: child.stdout });
    let info: { url: string } | undefined;
    try {
        for await (const line of lines) {
            info = JSON.parse(line) as typeof info;
            break;
        }
    } catch {
        child.kill();
    }
    if (!info) {
        await exited;
        throw new Error(errors || "The C# test host did not start.");
    }
    let closed = false;
    return {
        ...info,
        async close() {
            if (closed) return;
            closed = true;
            child.stdin.end("\n");
            const timer = setTimeout(() => child.kill("SIGKILL"), 10000);
            try {
                await exited;
            } finally {
                clearTimeout(timer);
            }
        },
    };
}
