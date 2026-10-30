import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { generateKeyPairSync } from "node:crypto";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { environmentVariables as Env } from "../config/environment.mts";
import { credentials, deployChisel, reverseAddress } from "./deploy-chisel.mts";

const key = generateKeyPairSync("ec", { namedCurve: "prime256v1" })
    .privateKey.export({ type: "pkcs8", format: "pem" })
    .toString();
const fixture = {
    [Env.AK3S_KUBECONFIG.name]: "fictional isolated kubeconfig",
    [Env.CHISEL_AUTH.name]: "operator:fictional-test-password",
    [Env.CHISEL_SERVER_KEY.name]: key,
};
const root = resolve(import.meta.dirname, "..");

test("generated tunnel ACL authorizes only the intended reverse listener", () => {
    const users = JSON.parse(credentials(fixture).users);
    assert.equal(Object.keys(users).length, 1);
    const allowed = new RegExp(users[fixture[Env.CHISEL_AUTH.name]][0]);
    assert.ok(allowed.test("R:0.0.0.0:8787"));
    for (const address of [
        "R:0.0.0.0:22",
        "R:127.0.0.1:8787",
        "127.0.0.1:8787",
        "socks",
        "R:10.0.0.0:8787",
        "R:0x0x0x0:8787",
        "R:0.0.0.0:87870",
    ])
        assert.equal(allowed.test(address), false, address);
    assert.equal(users[fixture[Env.CHISEL_AUTH.name]][0], reverseAddress);
});

test("invalid deployment inputs fail before invoking Kubernetes and never expose their values", () => {
    for (const name of [
        Env.AK3S_KUBECONFIG.name,
        Env.CHISEL_AUTH.name,
        Env.CHISEL_SERVER_KEY.name,
    ]) {
        assert.throws(
            () =>
                deployChisel({ ...fixture, [name]: "" }, () => {
                    assert.fail(
                        "Kubernetes must not run with missing credentials.",
                    );
                }),
            new RegExp(`${name} is missing`),
        );
    }
    for (const auth of [
        "missing-separator",
        "operator:",
        "operator:secret\nextra",
        " user:password",
    ])
        assert.throws(
            () => credentials({ ...fixture, [Env.CHISEL_AUTH.name]: auth }),
            /CHISEL_AUTH must/,
        );
    const rsa = generateKeyPairSync("rsa", { modulusLength: 2048 })
        .privateKey.export({ type: "pkcs8", format: "pem" })
        .toString();
    for (const invalid of ["fictional invalid key", rsa]) {
        assert.throws(
            () =>
                credentials({
                    ...fixture,
                    [Env.CHISEL_SERVER_KEY.name]: invalid,
                }),
            (error: unknown) => {
                assert.ok(error instanceof Error);
                assert.match(error.message, /P-256 PEM private key/);
                assert.ok(!error.message.includes(invalid));
                return true;
            },
        );
    }
});

test("offline Kustomize output deploys only Chisel app resources, excluding bootstrap and credentials", () => {
    const manifest = execFileSync(
        "kubectl",
        ["kustomize", resolve(root, "deploy/chisel")],
        { encoding: "utf8" },
    );
    assert.equal((manifest.match(/^kind: /gm) ?? []).length, 4);
    assert.equal(
        (manifest.match(/^  namespace: goblin-chisel$/gm) ?? []).length,
        4,
    );
    assert.doesNotMatch(
        manifest,
        /^kind: (?:Namespace|Role|RoleBinding|ServiceAccount|Secret)$/m,
    );
    assert.doesNotMatch(
        manifest,
        /goblinboard\.com\/|host: goblinboard\.com$|NodePort|LoadBalancer|hostNetwork: true/m,
    );
    const ingresses = manifest
        .split(/^---$/m)
        .filter((part) => part.includes("kind: Ingress"));
    assert.equal(ingresses.length, 2);
    assert.ok(
        ingresses.some(
            (part) =>
                part.includes("host: dev-tunnel.goblinboard.com") &&
                part.includes("number: 8080"),
        ),
    );
    assert.ok(
        ingresses.some(
            (part) =>
                part.includes("host: dev.goblinboard.com") &&
                part.includes("number: 8787"),
        ),
    );
});

test("deployment keeps secrets out of arguments, uses an explicit namespace, and cleans files on success and failure", () => {
    // Fail each invocation, including after Secrets were applied or rollout began.
    for (const failAt of [-1, 0, 1, 2, 3, 4, 5]) {
        const artifacts = resolve(root, ".artifacts/chisel");
        const before = existsSync(artifacts)
            ? readdirSync(artifacts).sort()
            : [];
        let calls = 0;
        let directory = "";
        const inputs: string[] = [];
        const execute = (args: string[], input?: string) => {
            for (const value of Object.values(fixture))
                assert.ok(!args.join(" ").includes(value));
            if (args[0] !== "kustomize") {
                assert.equal(args[0], "--kubeconfig");
                directory = dirname(args[1]!);
                assert.ok(
                    directory.startsWith(
                        resolve(root, ".artifacts/chisel/deploy-"),
                    ),
                );
                assert.equal(statSync(directory).mode & 0o777, 0o700);
                assert.equal(
                    readFileSync(args[1]!, "utf8"),
                    fixture[Env.AK3S_KUBECONFIG.name],
                );
                assert.equal(statSync(args[1]!).mode & 0o777, 0o600);
                assert.equal(args[2], "--namespace");
                assert.equal(args[3], "goblin-chisel");
                for (const name of ["key.pem", "users.json"])
                    assert.equal(
                        statSync(resolve(directory, name)).mode & 0o777,
                        0o600,
                    );
            }
            if (calls++ === failAt)
                throw new Error("Fictional command failure.");
            if (input) inputs.push(input);
            if (args.includes("--dry-run=client"))
                return "fictional private Secret manifest";
            return "fictional public application manifest";
        };
        if (failAt === -1) {
            const output = deployChisel(fixture, execute);
            assert.equal(calls, 6);
            assert.deepEqual(inputs, [
                "fictional private Secret manifest",
                "fictional public application manifest",
            ]);
            for (const value of Object.values(fixture))
                assert.ok(!output.includes(value));
        } else {
            assert.throws(
                () => deployChisel(fixture, execute),
                /Fictional command failure/,
            );
            assert.equal(calls, failAt + 1);
        }
        if (directory) assert.equal(existsSync(directory), false);
        assert.deepEqual(readdirSync(artifacts).sort(), before);
    }
});
