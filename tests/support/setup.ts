import { environmentVariables as Env } from "../../config/environment.mjs";
import { goblinctl } from "./goblinctl.js";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";

export const setupRetryPassword = "Test-only retry password";

/**
 * Starts the real setup server. With `control`, a local socket stands in for
 * systemd's Accept=yes unit and runs the real root helper for each connection;
 * a fake systemctl records the units it starts.
 */
export async function startSetup(options: { control?: boolean } = {}) {
    const root = await mkdtemp(join(tmpdir(), "goblin-setup-ui-"));
    const path = join(root, "status.json");
    const transition = (action: string, value = "", step?: string) =>
        execFileSync(goblinctl, [
            "internal",
            "state",
            action,
            value,
            "--path",
            path,
            ...(step ? ["--step", step] : []),
        ]);
    transition("init");
    const args = [
        "internal",
        "serve",
        "--host",
        "127.0.0.1",
        "--port",
        "0",
        "--state",
        path,
    ];
    let control: Server | undefined;
    if (options.control) {
        const bin = join(root, "bin");
        await mkdir(bin);
        await writeFile(
            join(bin, "systemctl"),
            `#!/bin/sh\nprintf '%s\\n' "$*" >> '${root}/systemctl.log'\nif [ "$1" = is-active ]; then test -e '${root}/installer-active'; exit $?; fi\n`,
            { mode: 0o700 },
        );
        const verifier = join(root, "owner-password");
        await writeFile(
            verifier,
            execFileSync(goblinctl, ["internal", "hash-password"], {
                input: setupRetryPassword,
            }),
            { mode: 0o600 },
        );
        const socket = join(root, "control.sock");
        control = createServer({ allowHalfOpen: true }, (connection) => {
            const helper = spawn(
                goblinctl,
                [
                    "internal",
                    "setup-control",
                    "--state",
                    path,
                    "--verifier",
                    verifier,
                    "--throttle",
                    join(root, "retry-throttle.json"),
                ],
                {
                    env: {
                        ...process.env,
                        [Env.PATH.name]: `${bin}:${process.env[Env.PATH.name]}`,
                    },
                    stdio: ["pipe", "pipe", "ignore"],
                },
            );
            connection.pipe(helper.stdin);
            helper.stdout.pipe(connection);
        });
        control.listen(socket);
        await once(control, "listening");
        args.push("--control-socket", socket);
    }
    const child = spawn(goblinctl, args, { stdio: ["ignore", "pipe", "pipe"] });
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
        control?.close();
        await rm(root, { recursive: true, force: true });
        throw new Error(errors || "Setup server did not start");
    }
    return {
        url,
        root,
        path,
        transition,
        /** Units the root helper asked systemctl to manage, one call per line. */
        async systemctl() {
            try {
                return (await readFile(join(root, "systemctl.log"), "utf8"))
                    .trim()
                    .split("\n")
                    .filter(Boolean);
            } catch {
                return [];
            }
        },
        async close() {
            child.kill();
            await exited;
            if (control) {
                control.close();
                await once(control, "close");
            }
            await rm(root, { recursive: true, force: true });
        },
    };
}
