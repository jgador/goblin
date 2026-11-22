import { request as httpRequest } from "node:http";
import { environmentVariables as Env } from "../../config/environment.mjs";
import { goblinctl } from "../support/goblinctl.js";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
    access,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import { join, resolve } from "node:path";
import { startBackend } from "../support/backend.js";
import {
    bootstrap,
    fixtureDirectory,
    fakePassword,
    hasherArgs,
    origin,
    publicHostname,
} from "../support/azure-fixture.js";

test("installer targets its k3s cluster when standalone kubectl has no context or an unrelated kubeconfig", async (t) => {
    for (const context of ["missing", "other-cluster"]) {
        const root = await fixtureDirectory("goblin-kubeconfig-");
        t.after(() => rm(root, { recursive: true, force: true }));
        const kubeconfig =
            context === "missing" ? undefined : join(root, "other-kubeconfig");
        const otherConfig = "current-context: other-cluster\n";
        if (kubeconfig) await writeFile(kubeconfig, otherConfig);

        await bootstrap(root, fakePassword, "ready", { kubeconfig });

        const status = JSON.parse(
            await readFile(
                join(root, "var/lib/goblin/install/status.json"),
                "utf8",
            ),
        );
        assert.equal(status.status, "ready");
        const calls: {
            command: string;
            args: string[];
            kubeconfig: string | null;
        }[] = (await readFile(join(root, "kubernetes-contexts.jsonl"), "utf8"))
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        assert.ok(
            calls.some(
                ({ command, args }) =>
                    command === "kubectl" &&
                    args.includes("statefulset/goblin-postgres"),
            ),
            "database setup must run through the standalone kubectl",
        );
        assert.ok(
            calls.some(
                ({ command, args }) =>
                    command === "kubectl" &&
                    args.includes("job/goblin-test-migration"),
            ),
            "schema migration must run through the standalone kubectl",
        );
        for (const call of calls)
            assert.equal(
                call.kubeconfig,
                join(root, "etc/rancher/k3s/k3s.yaml"),
            );
        if (kubeconfig)
            assert.equal(await readFile(kubeconfig, "utf8"), otherConfig);
    }
});

test("bootstrap rejects invalid hostname/ref input before invoking the application builder", async (t) => {
    for (const config of [
        { hostname: "$(touch PWNED).example.com" },
        { sourceRef: "master;touch PWNED" },
    ]) {
        const root = await fixtureDirectory("goblin-platform-input-");
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(bootstrap(root, fakePassword, "ready", config), {
            status: 1,
        });
        await assert.rejects(access(join(root, "PWNED")));
        await assert.rejects(access(join(root, "docker-requests.jsonl")));
    }
});

test("Azure password survives provisioning and unlocks Goblin", async (t) => {
    const root = await fixtureDirectory("goblin-password-");
    let close = async () => {};
    t.after(async () => {
        await close();
        await rm(root, { recursive: true, force: true });
    });
    const output = await bootstrap(root, fakePassword);
    const logs = await readFile(
        join(root, "var/log/goblin-bootstrap.log"),
        "utf8",
    );
    for (const text of [output, logs]) {
        assert.ok(!text.includes(fakePassword));
        assert.ok(!text.includes(Buffer.from(fakePassword).toString("base64")));
        assert.ok(!text.includes("pbkdf2-sha256$"));
    }
    await assert.rejects(access(join(root, "PWNED")));
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const secret = JSON.parse(
        await readFile(join(root, "secret.json"), "utf8"),
    );
    assert.equal(secret.metadata.name, "goblin-owner-password");
    assert.equal(secret.metadata.namespace, "goblin");
    const verifier = Buffer.from(
        secret.data["owner-password"],
        "base64",
    ).toString("utf8");
    const passwordHashFile = join(root, "owner-password");
    await writeFile(passwordHashFile, verifier, { mode: 0o600 });
    const dataDir = join(root, "workspace-data");
    let app = await startBackend({
        dataDir,
        passwordHashFile,
        publicOrigin: origin,
        allowInsecureHttp: true,
    });
    close = () => app.close();
    const request = (path: string, password?: string, cookie = "") =>
        new Promise<Response>((resolve, reject) => {
            const req = httpRequest(
                `${app.url}${path}`,
                {
                    method: password === undefined ? "GET" : "POST",
                    headers: {
                        Host: publicHostname,
                        Origin: origin,
                        Cookie: cookie,
                        "Content-Type": "application/json",
                    },
                },
                (res) => {
                    let body = "";
                    res.setEncoding("utf8").on("data", (chunk) => {
                        body += chunk;
                    });
                    res.on("error", reject);
                    res.on("end", () =>
                        resolve(
                            new Response(body, {
                                status: res.statusCode!,
                                headers: Object.fromEntries(
                                    Object.entries(res.headers)
                                        .filter(
                                            ([, value]) => value !== undefined,
                                        )
                                        .map(([name, value]) => [
                                            name,
                                            Array.isArray(value)
                                                ? value.join(",")
                                                : value!,
                                        ]),
                                ),
                            }),
                        ),
                    );
                },
            );
            req.on("error", reject);
            req.end(
                password === undefined
                    ? undefined
                    : JSON.stringify({ password }),
            );
        });
    assert.deepEqual(await (await request("/api/session")).json(), {
        authenticated: false,
    });
    assert.equal((await request("/api/status")).status, 401);
    // Whitespace, Unicode, quotes and shell metacharacters must survive exactly.
    assert.equal(
        (await request("/api/session", fakePassword.trim())).status,
        401,
    );
    let session = await request("/api/session", fakePassword);
    assert.equal(session.status, 200);
    const cookie = session.headers.get("set-cookie")!.split(";")[0];
    assert.match(session.headers.get("set-cookie")!, /HttpOnly/i);
    assert.match(session.headers.get("set-cookie")!, /;\s*Secure/i);
    assert.equal((await request("/api/status", undefined, cookie)).status, 200);
    assert.equal(
        (await request("/owner-password", undefined, cookie)).status,
        404,
    );
    assert.ok(
        !(await (await request("/api/session")).text()).includes(
            verifier.trim(),
        ),
    );

    await app.close();
    // Restarting keeps the configured password and invalidates browser sessions.
    app = await startBackend({
        dataDir,
        passwordHashFile,
        publicOrigin: origin,
        allowInsecureHttp: true,
    });
    assert.equal((await request("/api/status", undefined, cookie)).status, 401);
    assert.equal((await request("/api/session", "goblin")).status, 401);
    session = await request("/api/session", fakePassword);
    assert.equal(session.status, 200);
    for (let attempt = 0; attempt < 5; attempt++) {
        const response = await request("/api/session", "incorrect-password");
        assert.equal(response.status, 401);
        assert.match(await response.text(), /Goblin password is incorrect/);
    }
    assert.equal((await request("/api/session", fakePassword)).status, 429);

    // A new bootstrap password takes effect after the app restarts and invalidates
    // the previous password. Reapplying the same password is also safe.
    await app.close();
    const newPassword = "a"; // A single character must work through provisioning and login.
    await bootstrap(root, newPassword);
    const updated = JSON.parse(
        await readFile(join(root, "secret.json"), "utf8"),
    );
    await writeFile(
        passwordHashFile,
        Buffer.from(updated.data["owner-password"], "base64"),
    );
    app = await startBackend({
        dataDir,
        passwordHashFile,
        publicOrigin: origin,
        allowInsecureHttp: true,
    });
    assert.equal((await request("/api/session", fakePassword)).status, 401);
    assert.equal((await request("/api/session", newPassword)).status, 200);
});

test("the local repository verifier passes unchanged through Azure's bootstrap and Kubernetes Secret creation", async (t) => {
    const root = await fixtureDirectory("goblin-local-bootstrap-password-");
    t.after(() => rm(root, { recursive: true, force: true }));
    const passwordHashFile = join(root, ".goblin-secrets/owner-password");
    execFileSync(goblinctl, ["password", "set", "--path", passwordHashFile], {
        env: { ...process.env, [Env.GOBLIN_LOCAL_PASSWORD.name]: fakePassword },
    });
    const verifier = await readFile(passwordHashFile, "utf8");
    const output = await bootstrap(root, "", "ready", {
        passwordHashFile,
        hostname: "localhost",
    });
    const secret = JSON.parse(
        await readFile(join(root, "secret.json"), "utf8"),
    );
    assert.equal(
        Buffer.from(secret.data["owner-password"], "base64").toString(),
        verifier,
    );
    assert.equal(
        await readFile(
            join(root, "var/lib/goblin/install/private/owner-password"),
            "utf8",
        ),
        verifier,
    );
    for (const text of [
        output,
        await readFile(join(root, "bootstrap.sh"), "utf8"),
        await readFile(join(root, "var/log/goblin-bootstrap.log"), "utf8"),
    ]) {
        assert.ok(!text.includes(fakePassword));
        assert.ok(!text.includes(Buffer.from(fakePassword).toString("base64")));
        assert.ok(!text.includes(verifier.trim()));
    }
});

test("a configured password file must exist and contain a supported verifier", async (t) => {
    const root = await fixtureDirectory("goblin-password-invalid-");
    t.after(() => rm(root, { recursive: true, force: true }));
    const passwordHashFile = join(root, "owner-password");
    const dataDir = join(root, "data");
    await mkdir(dataDir);
    const valid = execFileSync(goblinctl, hasherArgs, {
        input: fakePassword,
        encoding: "utf8",
    });
    for (const invalid of [
        null,
        "",
        "not-a-verifier",
        valid.replace("600000", "1"),
        valid.replace("600000", "999999999"),
        "pbkdf2-sha256$600000$bad$bad",
        "x".repeat(257),
    ]) {
        if (invalid !== null) await writeFile(passwordHashFile, invalid);
        await assert.rejects(
            startBackend({ dataDir, passwordHashFile }),
            /hash file is invalid|Could not find file/,
        );
    }
});

test("password provisioning accepts short passwords and never logs rejected passwords", async (t) => {
    const root = await fixtureDirectory("goblin-password-policy-");
    t.after(() => rm(root, { recursive: true, force: true }));
    for (const invalid of [
        "",
        "   ",
        "a".repeat(129),
        "a\n",
        "test-password\nline-break",
        "test-password\u0000control",
    ]) {
        assert.throws(
            () =>
                execFileSync(goblinctl, hasherArgs, {
                    input: invalid,
                    stdio: ["pipe", "pipe", "pipe"],
                }),
            (error: unknown) => {
                const failure = error as { stdout: Buffer; stderr: Buffer };
                assert.equal(failure.stdout.length, 0);
                assert.match(
                    failure.stderr.toString(),
                    /^goblinctl: Enter a non-blank Goblin password/,
                );
                if (invalid)
                    assert.ok(!failure.stderr.toString().includes(invalid));
                return true;
            },
        );
    }
    for (const valid of [
        "a",
        "1",
        "!",
        "short",
        "b".repeat(128),
        fakePassword,
    ]) {
        const first = execFileSync(goblinctl, hasherArgs, {
            input: valid,
            encoding: "utf8",
        });
        const second = execFileSync(goblinctl, hasherArgs, {
            input: valid,
            encoding: "utf8",
        });
        assert.match(first, /^pbkdf2-sha256\$600000\$/);
        assert.notEqual(first, second, "each verifier needs a fresh salt");
    }
    await assert.rejects(bootstrap(root, ""));
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "failed\n",
    );
    await assert.rejects(access(join(root, "secret.json")));
});

test("Azure templates keep the password protected and the portal requires confirmation", async () => {
    const ui = JSON.parse(
        await readFile(".artifacts/azure/createUiDefinition.json", "utf8"),
    );
    const field = ui.parameters.basics.find(
        (item: { name: string }) => item.name === "goblinPassword",
    );
    assert.equal(field.type, "Microsoft.Common.PasswordBox");
    assert.equal(field.options.hideConfirmation, false);
    assert.equal(field.constraints.required, true);
    const policy = new RegExp(field.constraints.regex);
    for (const valid of ["a", "1", "!", "short", "b".repeat(128), fakePassword])
        assert.ok(policy.test(valid));
    for (const invalid of [
        "",
        "   ",
        "a".repeat(129),
        "a\n",
        "test-password\nline-break",
    ])
        assert.ok(!policy.test(invalid));
    for (const name of ["azuredeploy.json", "azuredeploy.portal.json"]) {
        const template = JSON.parse(
            await readFile(`.artifacts/azure/${name}`, "utf8"),
        );
        const pin = JSON.parse(await readFile("dependencies.lock.json", "utf8"))
            .goblinctl.release;
        assert.ok(
            JSON.stringify(template).includes(pin.sha256),
            "ARM must pin the native archive checksum",
        );
        assert.ok(
            JSON.stringify(template).includes("goblinctl-v"),
            "ARM must download a versioned native release",
        );
        assert.ok(!JSON.stringify(template).includes("python3"));
        assert.equal(
            template.parameters.goblinPassword.type.toLowerCase(),
            "securestring",
        );
        assert.ok(!("minLength" in template.parameters.goblinPassword));
        assert.equal(template.parameters.goblinPassword.maxLength, 128);
        assert.ok(!("defaultValue" in template.parameters.goblinPassword));
        const deployment = template.resources.find(
            (item: { type: string }) =>
                item.type === "Microsoft.Resources/deployments",
        );
        const nested = deployment.properties.template;
        assert.equal(
            nested.parameters.goblinPassword.type.toLowerCase(),
            "securestring",
        );
        assert.ok(!("minLength" in nested.parameters.goblinPassword));
        assert.equal(
            deployment.properties.parameters.goblinPassword.value,
            "[parameters('goblinPassword')]",
        );
        const extension = nested.resources.find(
            (item: { type: string }) =>
                item.type === "Microsoft.Compute/virtualMachines/extensions",
        );
        assert.equal(extension.properties.settings, undefined);
        assert.match(
            extension.properties.protectedSettings.script,
            /parameters\('goblinPassword'\)/,
        );
        assert.ok(!JSON.stringify(template.outputs).includes("goblinPassword"));
        assert.ok(!JSON.stringify(nested.outputs).includes("goblinPassword"));
        assert.match(
            template.parameters.goblinSourceRef.defaultValue,
            /^[0-9a-f]{40}$/,
        );
        assert.deepEqual(template.parameters.goblinSourceRef.allowedValues, [
            template.parameters.goblinSourceRef.defaultValue,
        ]);
        assert.equal(template.parameters.goblinSourceRef.minLength, 40);
        assert.equal(template.parameters.goblinSourceRef.maxLength, 40);
        assert.ok(template.outputs.goblinUrl.value.includes("https://"));
        assert.match(
            extension.properties.protectedSettings.script,
            /dnsSettings\.fqdn/,
        );
        assert.match(
            extension.properties.protectedSettings.script,
            /parameters\('goblinSourceRef'\)/,
        );
        if (name.includes("portal"))
            assert.deepEqual(
                Object.keys(ui.parameters.outputs).sort(),
                Object.keys(template.parameters).sort(),
            );
    }
});

test("Azure selects a Goblin version bound to the matching source", async () => {
    const ui = JSON.parse(
        await readFile(".artifacts/azure/createUiDefinition.json", "utf8"),
    );
    const template = JSON.parse(
        await readFile(".artifacts/azure/azuredeploy.portal.json", "utf8"),
    );
    const field = ui.parameters.basics.find(
        (item: { name: string }) => item.name === "goblinSourceRef",
    );
    assert.equal(field.type, "Microsoft.Common.DropDown");
    assert.equal(field.label, "Goblin version");
    assert.equal(field.defaultValue, template.metadata.goblin.version);
    assert.deepEqual(field.constraints.allowedValues, [
        {
            label: template.metadata.goblin.version,
            value: template.parameters.goblinSourceRef.defaultValue,
        },
    ]);
    assert.equal(
        ui.parameters.outputs.goblinSourceRef,
        "[basics('goblinSourceRef')]",
    );
});

test("Azure defaults to Dsv6 and separates HTTPS Goblin from read-only setup", async () => {
    for (const name of ["azuredeploy.json", "azuredeploy.portal.json"]) {
        const template = JSON.parse(
            await readFile(`.artifacts/azure/${name}`, "utf8"),
        );
        assert.equal(
            template.parameters.vmSize.defaultValue,
            "Standard_D4s_v6",
        );
        assert.match(template.outputs.goblinUrl.value, /https:\/\//);
        assert.match(template.outputs.setupUrl.value, /http:\/\//);
        assert.equal(template.parameters.certificateEmail.maxLength, 254);
    }
});

test("invalid certificate contact cannot enter shell or issuer configuration", async (t) => {
    const root = await fixtureDirectory("goblin-contact-invalid-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(
        bootstrap(root, fakePassword, "ready", {
            certificateEmail: "owner@example.com\nmalicious: value",
        }),
    );
    await assert.rejects(access(join(root, "k3s-requests.jsonl")));
});

test("a forwarded public origin is used by the application and readiness probes", async (t) => {
    for (const hostname of ["localhost"]) {
        const root = await fixtureDirectory("goblin-forwarded-origin-");
        t.after(() => rm(root, { recursive: true, force: true }));
        const publicOrigin = `http://${hostname}:8788`;
        await bootstrap(root, fakePassword, "ready", {
            hostname,
            publicOrigin,
        });
        assert.equal(
            await readFile(join(root, "var/lib/goblin/public-url"), "utf8"),
            publicOrigin + "\n",
        );
        assert.equal(
            JSON.parse(
                await readFile(
                    join(root, "var/lib/goblin/install/status.json"),
                    "utf8",
                ),
            ).publicUrl,
            publicOrigin,
        );
        assert.ok(
            (
                await readFile(
                    join(
                        root,
                        "var/lib/goblin/deploy/azure/platform/kustomization.yaml",
                    ),
                    "utf8",
                )
            ).includes(`value: ${publicOrigin}`),
        );
        const requests: string[][] = (
            await readFile(join(root, "curl-requests.jsonl"), "utf8")
        )
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        assert.ok(
            requests.some(
                (args) =>
                    args.includes(`Host: ${hostname}:8788`) &&
                    args.includes(`http://${hostname}/readyz`),
            ),
        );
        assert.ok(
            requests.some(
                (args) =>
                    args.includes(`Host: ${hostname}:8788`) &&
                    args.includes(`${publicOrigin}/readyz`),
            ),
            "public readiness uses the actual browser port",
        );
        assert.ok(
            (
                await readFile(
                    join(
                        root,
                        "var/lib/goblin/deploy/azure/platform/ingress.yaml",
                    ),
                    "utf8",
                )
            ).includes(`host: ${hostname}`),
        );
    }
});

test("invalid forwarded origins fail before installing cluster components", async (t) => {
    for (const publicOrigin of [
        "http://other.example:8788",
        "http://goblin.local.test:0",
        "http://goblin.local.test:65536",
        "http://goblin.local.test/path",
        "http://user@goblin.local.test",
        "http://goblin.local.test\n",
    ]) {
        const root = await fixtureDirectory("goblin-invalid-origin-");
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(
            bootstrap(root, fakePassword, "ready", {
                hostname: "goblin.local.test",
                publicOrigin,
            }),
            { status: 1 },
        );
        await assert.rejects(access(join(root, "k3s-requests.jsonl")));
    }
});

test("native archives install license documents", async (t) => {
    const artifacts = resolve(".artifacts/deployment");
    await mkdir(artifacts, { recursive: true });
    const root = await mkdtemp(join(artifacts, "goblin-licensed-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "ready", {
        nativeArchive: "licensed",
        bootstrapOnly: true,
    });
    for (const path of [
        "share",
        "share/licenses",
        "share/licenses/goblinctl",
    ]) {
        assert.equal(
            (await stat(join(root, "opt/goblin", path))).mode & 0o777,
            0o755,
        );
    }
    for (const name of ["LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"]) {
        assert.deepEqual(
            await readFile(
                join(root, "opt/goblin/share/licenses/goblinctl", name),
            ),
            await readFile(name),
        );
        assert.equal(
            (
                await stat(
                    join(root, "opt/goblin/share/licenses/goblinctl", name),
                )
            ).mode & 0o777,
            0o644,
        );
    }
});

test("native archives with missing or unexpected entries fail before activating the installer", async (t) => {
    const artifacts = resolve(".artifacts/deployment");
    await mkdir(artifacts, { recursive: true });
    for (const nativeArchive of [
        "missing-licenses",
        "unexpected-entry",
    ] as const) {
        const root = await mkdtemp(join(artifacts, "goblin-archive-rejected-"));
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(
            bootstrap(root, fakePassword, "ready", {
                nativeArchive,
                bootstrapOnly: true,
            }),
        );
        await assert.rejects(access(join(root, "opt/goblin/bin/goblinctl")));
        await assert.rejects(access(join(root, "systemctl-requests.jsonl")));
        assert.match(
            await readFile(join(root, "var/log/goblin-bootstrap.log"), "utf8"),
            /Unsupported native archive contents/,
        );
    }
});

test("native download failure, corruption, or wrong version leaves the previous installer untouched", async (t) => {
    for (const nativeDownload of [
        "failed",
        "corrupt",
        "wrong-version",
    ] as const) {
        const root = await fixtureDirectory("goblin-native-failed-");
        t.after(() => rm(root, { recursive: true, force: true }));
        const previous = join(
            root,
            "etc/systemd/system/goblin-installer.service",
        );
        await mkdir(join(root, "etc/systemd/system"), { recursive: true });
        await writeFile(previous, "previous installer");
        await assert.rejects(
            bootstrap(root, fakePassword, "ready", {
                nativeDownload,
                bootstrapOnly: true,
            }),
        );
        assert.equal(await readFile(previous, "utf8"), "previous installer");
        await assert.rejects(
            access(join(root, "var/lib/goblin/install/private/owner-password")),
        );
        await assert.rejects(access(join(root, "systemctl-requests.jsonl")));
        assert.equal(
            (
                await readFile(
                    join(root, "var/lib/goblin/bootstrap-status"),
                    "utf8",
                )
            ).trim(),
            "failed",
        );
        assert.ok(
            !(
                await readFile(
                    join(root, "var/log/goblin-bootstrap.log"),
                    "utf8",
                )
            ).includes(fakePassword),
        );
    }
});
