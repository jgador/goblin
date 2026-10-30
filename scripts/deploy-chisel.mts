import { spawnSync } from "node:child_process";
import { createPrivateKey } from "node:crypto";
import {
    chmodSync,
    mkdirSync,
    mkdtempSync,
    rmSync,
    writeFileSync,
} from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { environmentVariables as Env } from "../config/environment.mts";

export const reverseAddress = "^R:0\\.0\\.0\\.0:8787$";

export function credentials(environment: NodeJS.ProcessEnv) {
    const required = (name: string) => {
        const value = environment[name];
        if (!value?.trim()) throw new Error(`${name} is missing.`);
        return value;
    };
    const kubeconfig = required(Env.AK3S_KUBECONFIG.name);
    const auth = required(Env.CHISEL_AUTH.name);
    if (!/^[^:\s\x00-\x1f\x7f]+:[^\x00-\x1f\x7f]+$/.test(auth))
        throw new Error(
            "CHISEL_AUTH must contain username:password without control characters.",
        );
    const key = required(Env.CHISEL_SERVER_KEY.name);
    try {
        const parsed = createPrivateKey(key);
        if (
            parsed.asymmetricKeyType !== "ec" ||
            parsed.asymmetricKeyDetails?.namedCurve !== "prime256v1"
        )
            throw new Error("Unsupported key.");
    } catch {
        throw new Error(
            "CHISEL_SERVER_KEY must contain an unencrypted P-256 PEM private key.",
        );
    }
    return {
        kubeconfig,
        key,
        users: JSON.stringify({ [auth]: [reverseAddress] }),
    };
}

type Execute = (args: string[], input?: string) => string;

export function deployChisel(
    environment: NodeJS.ProcessEnv = process.env,
    execute?: Execute,
) {
    const secret = credentials(environment);
    const root = resolve(import.meta.dirname, "..");
    const artifacts = resolve(root, ".artifacts/chisel");
    mkdirSync(artifacts, { recursive: true, mode: 0o700 });
    const directory = mkdtempSync(`${artifacts}/deploy-`);
    chmodSync(directory, 0o700);
    const kubeconfig = resolve(directory, "kubeconfig");
    const run: Execute =
        execute ??
        ((args, input) => {
            const result = spawnSync("kubectl", args, {
                encoding: "utf8",
                input,
                // Credentials stay in private files, never in kubectl's environment.
                env: {
                    [Env.PATH.name]:
                        environment[Env.PATH.name] ?? "/usr/bin:/bin",
                },
            });
            if (result.error || result.status !== 0)
                throw new Error(
                    "kubectl failed; check the explicit cluster credential and Chisel resources.",
                );
            return result.stdout;
        });
    const cluster = (args: string[], input?: string) =>
        run(
            [
                "--kubeconfig",
                kubeconfig,
                "--namespace",
                "goblin",
                "--cache-dir",
                resolve(directory, "cache"),
                "--request-timeout=240s",
                ...args,
            ],
            input,
        );
    try {
        writeFileSync(kubeconfig, secret.kubeconfig, { mode: 0o600 });
        const key = resolve(directory, "key.pem");
        const users = resolve(directory, "users.json");
        writeFileSync(key, secret.key, { mode: 0o600 });
        writeFileSync(users, secret.users, { mode: 0o600 });
        const manifest = run(["kustomize", resolve(root, "deploy/chisel")]);
        const secretManifest = cluster([
            "create",
            "secret",
            "generic",
            "chisel",
            `--from-file=key.pem=${key}`,
            `--from-file=users.json=${users}`,
            "--dry-run=client",
            "-o",
            "json",
        ]);
        const apply = [
            "apply",
            "--server-side",
            "--field-manager=chisel-deploy",
            "-f",
            "-",
        ];
        cluster(apply, secretManifest);
        cluster(apply, manifest);
        // Authfile reloads keep established tunnels. Restart to apply revocation
        // and key changes to every connection, including an existing WSL client.
        cluster(["rollout", "restart", "deployment/chisel"]);
        cluster(["rollout", "status", "deployment/chisel", "--timeout=180s"]);
        return "Chisel server rollout completed. Connect WSL at dev-tunnel.goblinboard.com, then test dev.goblinboard.com.\n";
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    try {
        process.stdout.write(deployChisel());
    } catch (error) {
        process.stderr.write(
            `${error instanceof Error ? error.message : "Chisel deployment failed."}\n`,
        );
        process.exitCode = 1;
    }
}
