import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";

export async function startSetup() {
    const root = await mkdtemp(join(tmpdir(), "goblin-setup-ui-"));
    const bundle = join(root, "goblin-setup.pyz");
    const path = join(root, "status.json");
    await writeFile(
        bundle,
        Buffer.from(
            await readFile("deploy/azure/setup-bundle.b64", "utf8"),
            "base64",
        ),
    );
    const transition = (action: string, value = "") =>
        execFileSync("python3", [
            bundle,
            "state",
            action,
            value,
            "--path",
            path,
        ]);
    transition("init");
    const child = spawn(
        "python3",
        [
            bundle,
            "serve",
            "--host",
            "127.0.0.1",
            "--port",
            "0",
            "--state",
            path,
        ],
        { stdio: ["ignore", "pipe", "pipe"] },
    );
    const exited = once(child, "exit");
    let errors = "";
    child.stderr.setEncoding("utf8").on("data", (chunk) => {
        errors += chunk;
    });
    const lines = createInterface({ input: child.stdout });
    const timer = setTimeout(() => child.kill("SIGKILL"), 10000);
    let url = "";
    for await (const line of lines) {
        url = line.replace("Goblin setup listening at ", "");
        break;
    }
    clearTimeout(timer);
    if (!url) {
        await exited;
        await rm(root, { recursive: true, force: true });
        throw new Error(errors || "Setup server did not start");
    }
    return {
        url,
        root,
        path,
        transition,
        async close() {
            child.kill();
            await exited;
            await rm(root, { recursive: true, force: true });
        },
    };
}
