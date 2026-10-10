import { environmentVariables as Env } from "../../config/environment.mjs";
import { goblinctl } from "../support/goblinctl.js";
import { createHash } from "node:crypto";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import {
    access,
    appendFile,
    cp,
    mkdir,
    mkdtemp,
    readFile,
    readdir,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { request as httpRequest } from "node:http";
import { join, resolve } from "node:path";
import { startBackend } from "../support/backend.js";

const publicHostname = "goblin-prod.southeastasia.cloudapp.azure.com";
const origin = `http://${publicHostname}`;
const hasherArgs = ["internal", "hash-password"];
const fakePassword =
    "  Test-only 'quotes' $HOME $(touch PWNED) `touch PWNED` café 🧌  ";
const nativeVersionOutput = execFileSync(goblinctl, ["--version"], {
    encoding: "utf8",
}).trim();
assert.match(nativeVersionOutput, /^goblinctl \d+\.\d+\.\d+$/);
const nativeVersion = nativeVersionOutput.slice("goblinctl ".length);

// Exercise the real rendered bootstrap, replacing only infrastructure commands
// and absolute system paths. Native hashing, shell quoting and secret creation
// all run; no Azure account, Kubernetes installation, or network is needed.
async function bootstrap(
    root: string,
    password: string,
    nodeState: "ready" | "delayed" | "missing" | "not-ready" = "ready",
    application: {
        publicOrigin?: string;
        hostname?: string;
        sourceRef?: string;
        passwordHashFile?: string;
        dockerInstalled?: boolean;
        dockerEnvironment?: NodeJS.ProcessEnv;
        kubeconfig?: string;
        bootstrapOnly?: boolean;
        nativeDownload?: "failed" | "corrupt" | "wrong-version";
        nativeArchive?: "licensed" | "missing-licenses" | "unexpected-entry";
        resume?: boolean;
        recovery?: boolean;
        pullDelay?: number;
        slowBuild?: boolean;
        state?:
            | "ready"
            | "docker-failed"
            | "build-failed"
            | "not-ready"
            | "headlamp-failed"
            | "logs-failed"
            | "collector-failed"
            | "ingress-failed"
            | "public-failed"
            | "sandbox-failed"
            | "cert-failed"
            | "webhook-failed"
            | "pull-failed";
    } = {},
) {
    const bin = join(root, "bin");
    await mkdir(bin, { recursive: true });
    await writeFile(join(bin, "package.json"), '{"type":"commonjs"}\n');
    const source = join(root, "archive/goblin");
    await mkdir(join(source, "deploy/azure"), { recursive: true });
    await cp("deploy/postgres", join(source, "deploy/postgres"), {
        recursive: true,
    });
    // A retained application archive may contain obsolete provisioning helpers.
    // Operational code must come from the installed CLI, independent of that source.
    for (const name of ["setup.sh", "migrate.sh"])
        await writeFile(
            join(source, "deploy/postgres", name),
            "#!/bin/sh\nprintf 'Obsolete source provisioning helper was executed.\\n' >&2\nexit 99\n",
        );
    await mkdir(join(source, "backend/src/Goblin.Web"), { recursive: true });
    await mkdir(join(source, "backend/tools/Goblin.Database"), {
        recursive: true,
    });
    await cp(
        "backend/src/Goblin.Web/appsettings.json",
        join(source, "backend/src/Goblin.Web/appsettings.json"),
    );
    await cp("deploy/auth", join(source, "deploy/auth"), { recursive: true });
    await cp(
        "deploy/install-request.json",
        join(source, "deploy/install-request.json"),
    );
    await cp("deploy/azure/app", join(source, "deploy/azure/app"), {
        recursive: true,
    });
    await cp("Dockerfile", join(source, "Dockerfile"));
    await cp("frontend/src", join(source, "frontend/src"), { recursive: true });
    await cp("frontend/src/connection/index.html", join(root, "page.html"));
    execFileSync("tar", [
        "-czf",
        join(root, "source.tar.gz"),
        "-C",
        join(root, "archive"),
        "goblin",
    ]);
    for (const name of [
        "cloud-init",
        "sha256sum",
        "curl",
        "k3s",
        "kubectl",
        "sleep",
        "docker",
        "dockerd",
        "apt-get",
        "systemctl",
    ]) {
        await writeFile(
            join(bin, name),
            `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const name = path.basename(process.argv[1]);
const args = process.argv.slice(2);
if (name === 'kubectl') args.unshift('kubectl');
const root = process.env["${Env.GOBLIN_BOOTSTRAP_TEST_DIR.name}"];
const nodeState = process.env["${Env.GOBLIN_BOOTSTRAP_NODE_STATE.name}"];
const appState = process.env["${Env.GOBLIN_BOOTSTRAP_APP_STATE.name}"];
if (name === 'curl') {
  fs.appendFileSync(path.join(root, 'curl-requests.jsonl'), JSON.stringify(args) + '\\n');
  const output = args[args.indexOf('--output') + 1];
  if (args.some(arg => arg.startsWith('https://codeload.github.com/'))) {
    fs.copyFileSync(path.join(root, 'source.tar.gz'), output);
  } else if (args.some(arg => arg.includes('/releases/download/goblinctl-v'))) {
    if (process.env["${Env.GOBLIN_BOOTSTRAP_NATIVE_DOWNLOAD.name}"] === 'failed') process.exit(22);
    fs.copyFileSync(path.join(root, 'goblinctl.tar.gz'), output);
    if (process.env["${Env.GOBLIN_BOOTSTRAP_NATIVE_DOWNLOAD.name}"] === 'corrupt') fs.appendFileSync(output, 'corruption');
  } else if (args.includes('http://127.0.0.1/setup/healthz')) {
    fs.writeFileSync(output, JSON.stringify({ setup: true }));
  } else if (args.some(arg => arg.startsWith('http://'))) {
    if (appState === 'ingress-failed' || (appState === 'public-failed' && !args.some(arg => arg.endsWith(':10.43.0.80')))) process.exit(22);
    if (args.some(arg => arg.endsWith('/readyz'))) fs.writeFileSync(output, JSON.stringify({ ready: true }));
    else fs.copyFileSync(path.join(root, 'page.html'), output);
  } else {
    fs.writeFileSync(output, '#!/bin/sh\\nexit 0\\n');
  }
} else if (name === 'sha256sum') {
  if (args.includes('--check')) {
    const text = fs.readFileSync(0, 'utf8');
    fs.appendFileSync(path.join(root, 'checksum-checks.jsonl'), JSON.stringify(text) + '\\n');
    const [expected, file] = text.trim().split(/\\s+/, 2);
    if (file.endsWith('/goblinctl.tar.gz')) {
      const actual = require('node:crypto').createHash('sha256').update(fs.readFileSync(file)).digest('hex');
      if (actual !== expected) { process.stderr.write('Native archive checksum mismatch'); process.exit(1); }
    }
  }
  else process.stdout.write(require('node:crypto').createHash('sha256').update(fs.readFileSync(args[0])).digest('hex') + '  ' + args[0]);
} else if (name === 'docker' || name === 'dockerd') {
  fs.appendFileSync(path.join(root, 'docker-requests.jsonl'), JSON.stringify([name, ...args]) + '\\n');
  if (name === 'docker') {
    const configDir = path.join(root, 'var/lib/goblin/install/private/work/docker-config');
    if (process.env["${Env.DOCKER_CONFIG.name}"] !== configDir || process.env["${Env.DOCKER_HOST.name}"] !== 'unix:///var/run/docker.sock')
      throw new Error('Installer must use its own Docker configuration and the native daemon');
    for (const key of ["${Env.DOCKER_CONTEXT.name}", "${Env.DOCKER_TLS.name}", "${Env.DOCKER_TLS_VERIFY.name}", "${Env.DOCKER_CERT_PATH.name}", "${Env.BUILDX_CONFIG.name}", "${Env.BUILDX_BUILDER.name}"])
      if (key in process.env) throw new Error('Installer inherited ' + key);
    const config = JSON.parse(fs.readFileSync(path.join(configDir, 'config.json'), 'utf8'));
    if (JSON.stringify(config) !== JSON.stringify({ auths: { 'https://index.docker.io/v1/': {} } }))
      throw new Error('Installer must pull anonymously without a credential helper');
  }
  if (process.env["${Env.GOBLIN_BOOTSTRAP_DOCKER_INSTALLED.name}"] === 'false' && !fs.existsSync(path.join(root, 'docker-installed')))
    process.exit(127);
  if (args[0] === 'info' && appState === 'docker-failed') process.exit(1);
  if (args[0] === 'image' && args[1] === 'inspect' && !fs.existsSync(path.join(root, 'built-image'))) process.exit(1);
  if (args[0] === 'build') {
    fs.writeFileSync(path.join(root, 'build-pid'), String(process.pid));
    if (process.env["${Env.GOBLIN_BOOTSTRAP_SLOW_BUILD.name}"] === 'true') Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 60000);
    if (appState === 'build-failed') process.exit(1);
    fs.accessSync(path.join(args.at(-1), 'Dockerfile'));
    if ((fs.statSync(path.join(args.at(-1), 'frontend/src/connection/index.html')).mode & 0o044) !== 0o044)
      throw new Error('Source assets must remain readable in the non-root image');
    process.stdout.write('#2 [assets 3/8] RUN npm ci --omit=dev\\n#2 1.0 password=do-not-stream\\n#2 DONE 2.0s\\n');
    fs.writeFileSync(path.join(root, 'built-image'), 'true');
  } else if (args[0] === 'save') fs.writeFileSync(args[args.indexOf('--output') + 1], 'test image archive');
} else if (name === 'apt-get') {
  fs.appendFileSync(path.join(root, 'apt-requests.jsonl'), JSON.stringify(args) + '\\n');
  if (args.includes('install') && args.includes('docker.io')) {
    const config = JSON.parse(fs.readFileSync(path.join(root, 'etc/docker/daemon.json'), 'utf8'));
    if (config['ip-forward-no-drop'] !== true) throw new Error('Docker must preserve forwarding before its service starts');
    fs.writeFileSync(path.join(root, 'docker-installed'), 'true');
  }
} else if (name === 'systemctl') {
  fs.appendFileSync(path.join(root, 'systemctl-requests.jsonl'), JSON.stringify(args) + '\\n');
  if (args[0] === 'cat' && !fs.existsSync(path.join(root, 'etc/systemd/system', args[1]))) process.exit(1);
} else if (name === 'k3s' || name === 'kubectl') {
  if (args[1] === '--kubeconfig') args.splice(1, 2);
  fs.appendFileSync(path.join(root, 'k3s-requests.jsonl'), JSON.stringify(args) + '\\n');
  if (args[0] === 'crictl' && args[1] === 'inspecti') process.exit(1);
  if (args[0] === 'crictl' && args[1] === 'pull') {
    fs.appendFileSync(path.join(root, 'pull-events.jsonl'), JSON.stringify({ event: 'start', image: args[2] }) + '\\n');
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, Number(process.env["${Env.GOBLIN_BOOTSTRAP_PULL_DELAY.name}"]));
    if (appState === 'pull-failed') process.exit(1);
    fs.appendFileSync(path.join(root, 'pull-events.jsonl'), JSON.stringify({ event: 'end', image: args[2] }) + '\\n');
  }
  if (args[0] === 'kubectl') {
    const kubeconfig = process.env["${Env.KUBECONFIG.name}"] ?? null;
    fs.appendFileSync(path.join(root, 'kubernetes-contexts.jsonl'), JSON.stringify({ command: name, args, kubeconfig }) + '\\n');
    if (name === 'kubectl' && kubeconfig !== path.join(root, 'etc/rancher/k3s/k3s.yaml')) {
      process.stderr.write('Standalone kubectl is not configured for the installed k3s cluster\\n');
      process.exit(1);
    }
  }
  if (args[1] === 'get' && args[2] === 'secret' && args.includes('json')) {
    process.stdout.write(JSON.stringify({ items: ['app', 'admin'].map(role => ({ metadata: { name: 'goblin-postgres-' + role + '-tls' }, data: Object.fromEntries(['tls.crt', 'tls.key', 'ca.crt'].map(key => [key, Buffer.from('test-certificate-' + role).toString('base64')])) })) }));
    process.exit(0);
  }
  if (args[1] === 'create' && args[2] === '-f') {
    if (args[3] === '-') {
      const resource = JSON.parse(fs.readFileSync(0, 'utf8'));
      if (resource.kind === 'Job') process.stdout.write('job/goblin-test-migration');
    } else process.stdout.write('job/goblin-test-verification');
    process.exit(0);
  }
  if (args[1] === 'get' && args[2] === 'service') {
    // Model K3s reconciling its watched HelmChartConfig, including chart v40's
    // service.spec.type setting. An ignored value retains LoadBalancer.
    const yaml = fs.readFileSync(path.join(root, 'var/lib/rancher/k3s/server/manifests/goblin-traefik-config.yaml'), 'utf8');
    fs.writeFileSync(path.join(root, 'ingress-mode'), yaml.includes('service:\\n      spec:\\n        type: ClusterIP') ? 'ClusterIP' : 'LoadBalancer');
    process.stdout.write(args.some(arg => arg.includes('clusterIP')) ? '10.43.0.80' : fs.readFileSync(path.join(root, 'ingress-mode'), 'utf8'));
  } else if (args[1] === 'rollout' && args.includes('deployment/cert-manager-webhook') && appState === 'cert-failed') {
    process.exit(1);
  } else if (args[1] === 'rollout' && args.includes('deployment/sandbox') && appState === 'cert-failed') {
    // Finish a parallel worker while the certificate worker is recording its failure.
    const deadline = Date.now() + 10000;
    while (!fs.existsSync(path.join(root, 'cert-failure-reporting'))) {
      if (Date.now() >= deadline) throw new Error('Certificate failure was not reported');
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 10);
    }
  } else if (args[1] === 'rollout' && args.includes('deployment/sandbox') && appState === 'sandbox-failed') {
    process.exit(1);
  } else if (args[1] === 'create' && args.includes('--dry-run=server') && appState === 'webhook-failed') {
    process.exit(1);
  } else if (args[1] === 'get' && args[2] === 'nodes') {
    if (args.includes('json')) {
      process.stdout.write(JSON.stringify({ items: [{ status: { addresses: [
        { type: 'Hostname', address: 'goblin' },
        { type: 'InternalIP', address: 'fd00::4' },
        { type: 'InternalIP', address: '10.20.0.4' }
      ] } }] }));
      process.exit(0);
    }
    if (args.some(arg => arg.startsWith('jsonpath='))) {
      process.stdout.write('10.20.0.4');
      process.exit(0);
    }
    const attemptsFile = path.join(root, 'node-registration-attempts');
    const attempt = (fs.existsSync(attemptsFile) ? Number(fs.readFileSync(attemptsFile, 'utf8')) : 0) + 1;
    fs.writeFileSync(attemptsFile, String(attempt));
    if (nodeState === 'delayed' && attempt === 1) {
      process.stderr.write('Temporary API connection failure\\n');
      process.exit(1);
    }
    if (nodeState === 'missing' || (nodeState === 'delayed' && attempt < 4)) process.exit(0);
    fs.writeFileSync(path.join(root, 'node-registered'), 'true');
    process.stdout.write('node/goblin\\n');
  } else if (args[1] === 'wait' && args.includes('node')) {
    if (nodeState === 'missing' || (nodeState === 'delayed' && !fs.existsSync(path.join(root, 'node-registered')))) {
      process.stderr.write('error: no matching resources found\\n');
      process.exit(1);
    }
    if (nodeState === 'not-ready') {
      process.stderr.write('error: timed out waiting for the condition on nodes/goblin\\n');
      process.exit(1);
    }
  } else if (args[1] === 'wait' && args.includes('sandbox/app') && appState === 'not-ready') {
    process.exit(1);
  } else if (args[1] === 'rollout' && args.includes('deployment/goblin-headlamp') && appState === 'headlamp-failed') {
    process.exit(1);
  } else if (args[1] === 'rollout' && args.includes('deployment/goblin-victorialogs') && appState === 'logs-failed') {
    process.exit(1);
  } else if (args[1] === 'rollout' && args.includes('daemonset/goblin-fluent-bit') && appState === 'collector-failed') {
    process.exit(1);
  } else if (args[1] === 'create' && args[2] === 'namespace') {
    process.stdout.write(JSON.stringify({ apiVersion: 'v1', kind: 'Namespace', metadata: { name: args[3] } }));
  } else if (args[1] === 'create' && args[2] === 'secret') {
    const file = args.find(arg => arg.startsWith('--from-file=owner-password=')).slice('--from-file=owner-password='.length);
    if ((fs.statSync(file).mode & 0o777) !== 0o600) throw new Error('Verifier file must be private');
    process.stdout.write(JSON.stringify({ apiVersion: 'v1', kind: 'Secret', metadata: { name: args[4], namespace: args[args.indexOf('-n') + 1] }, data: { 'owner-password': fs.readFileSync(file).toString('base64') } }));
  } else if (args[1] === 'apply' && args.at(-1) === '-') {
    const input = fs.readFileSync(0, 'utf8');
    if (input.includes('kind: Service')) {
      if (!input.includes('name: goblin-postgres-local') || !input.includes('port: 5432')) throw new Error('Unexpected host PostgreSQL service');
      fs.writeFileSync(path.join(root, 'postgres-host-service.yaml'), input);
      process.exit(0);
    }
    const resource = JSON.parse(input);
    if (resource.kind === 'Secret') fs.writeFileSync(path.join(root, 'secret.json'), JSON.stringify(resource), { mode: 0o600 });
  }
}
`,
            { mode: 0o700 },
        );
    }
    const paths = [
        "/var/lib/goblin",
        "/var/log/goblin-bootstrap.log",
        "/var/log/goblin-installer.log",
        "/etc/rancher/k3s",
        "/etc/docker",
        "/opt/goblin",
        "/usr/local/bin",
        "/run/goblin-setup",
        "/etc/systemd/system",
        "/var/lib/rancher/k3s",
    ];
    const remap = (script: string) =>
        paths
            .reduce(
                (text, path) =>
                    text.replaceAll(path, join(root, path.slice(1))),
                script,
            )
            .replaceAll(
                "export GOBLINCTL=" + join(root, "opt/goblin/bin/goblinctl"),
                "export GOBLINCTL=" + join(bin, "goblinctl"),
            )
            .replace(
                '"$bootstrap_dir/goblinctl" internal activate',
                '"' + join(bin, "goblinctl") + '" internal activate',
            );
    // Redirect only host paths; every operation still executes native Rust code.
    await writeFile(
        join(bin, "goblinctl"),
        `#!${process.execPath}
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const args = process.argv.slice(2);
const root = process.env["${Env.GOBLIN_BOOTSTRAP_TEST_DIR.name}"];
if (args[0] === 'internal' && args[1] === 'activate') args.push('--system-root', root);
if (args[0] === 'db' && args[1] === 'host') args.push('--system-root', root);
if (args[0] === 'internal' && ['state', 'build-progress'].includes(args[1]) && !args.includes('--path')) args.push('--path', path.join(root, 'var/lib/goblin/install/status.json'));
if (args[0] === 'internal' && args[1] === 'docker-config') args.push(path.join(root, 'etc/docker/daemon.json'));
if (args[0] === 'internal' && args[1] === 'state' && args[2] === 'fail-step' && args[3] === 'cert-manager' && process.env["${Env.GOBLIN_BOOTSTRAP_APP_STATE.name}"] === 'cert-failed') {
    fs.writeFileSync(path.join(root, 'cert-failure-reporting'), 'true');
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1000);
}
const result = spawnSync(${JSON.stringify(goblinctl)}, args, {stdio: 'inherit'});
if (result.status !== 0) process.exit(result.status ?? 1);
if (args[0] === 'internal' && ['unpack','activate'].includes(args[1])) {
    const directory = args[1] === 'unpack' ? args[2] : path.join(root, 'opt/goblin/setup');
    for (const name of fs.readdirSync(directory)) {
        const file = path.join(directory, name);
        let text = fs.readFileSync(file, 'utf8');
        for (const prefix of ${JSON.stringify(paths)}) text = text.replaceAll(prefix, root + prefix);
        text = text.replaceAll('export GOBLINCTL=' + root + '/opt/goblin/bin/goblinctl', 'export GOBLINCTL=' + root + '/bin/goblinctl');
        fs.writeFileSync(file, text);
    }
}
`,
        { mode: 0o700 },
    );
    for (const name of ["setup.sh", "migrate.sh"]) {
        const path = join(source, "deploy/postgres", name);
        await writeFile(path, remap(await readFile(path, "utf8")));
    }
    // Repack the modified source, and stage a real native download archive.
    execFileSync("tar", [
        "-czf",
        join(root, "source.tar.gz"),
        "-C",
        join(root, "archive"),
        "goblin",
    ]);
    await mkdir(join(root, "native"), { recursive: true });
    await cp(goblinctl, join(root, "native/goblinctl"));
    const nativeEntries = ["goblinctl"];
    if (application.nativeArchive !== "missing-licenses") {
        for (const name of ["LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"]) {
            await cp(name, join(root, "native", name));
            nativeEntries.push(name);
        }
    }
    if (application.nativeArchive === "unexpected-entry") {
        await writeFile(
            join(root, "native/unexpected"),
            "unexpected archive entry",
        );
        nativeEntries.push("unexpected");
    }
    execFileSync("tar", [
        "-czf",
        join(root, "goblinctl.tar.gz"),
        "-C",
        join(root, "native"),
        ...nativeEntries,
    ]);
    const nativeChecksum = createHash("sha256")
        .update(await readFile(join(root, "goblinctl.tar.gz")))
        .digest("hex");
    let script = (await readFile("deploy/azure/bootstrap.sh", "utf8"))
        .replace(
            "__GOBLIN_HOSTNAME_BASE64__",
            Buffer.from(
                application.hostname ??
                    "goblin-prod.southeastasia.cloudapp.azure.com",
            ).toString("base64"),
        )
        .replace(
            "__GOBLIN_SOURCE_REF_BASE64__",
            Buffer.from(application.sourceRef ?? "master").toString("base64"),
        )
        .replace(
            "__GOBLIN_PASSWORD_BASE64__",
            Buffer.from(password).toString("base64"),
        )
        .replace(
            "__GOBLINCTL_VERSION__",
            application.nativeDownload === "wrong-version"
                ? nativeVersion === "0.0.0"
                    ? "0.0.1"
                    : "0.0.0"
                : nativeVersion,
        )
        .replace("__GOBLINCTL_SHA256__", nativeChecksum);
    script = remap(script);
    await mkdir(join(root, "var/log"), { recursive: true });
    await mkdir(join(root, "etc/systemd/system"), { recursive: true });
    const scriptPath = join(root, "bootstrap.sh");
    await writeFile(scriptPath, script, { mode: 0o600 });
    const options = {
        cwd: root,
        env: {
            ...process.env,
            ...application.dockerEnvironment,
            [Env.PATH.name]:
                `${bin}:${application.dockerEnvironment?.[Env.PATH.name] ?? process.env[Env.PATH.name]}`,
            [Env.KUBECONFIG.name]: application.kubeconfig,
            [Env.GOBLIN_BOOTSTRAP_TEST_DIR.name]: root,
            [Env.GOBLIN_BOOTSTRAP_NATIVE_DOWNLOAD.name]:
                application.nativeDownload ?? "",
            [Env.SERVICE_RESULT.name]: application.recovery
                ? "signal"
                : "success",
            [Env.GOBLIN_PUBLIC_ORIGIN.name]: application.publicOrigin ?? "",
            [Env.GOBLIN_PASSWORD_HASH_FILE.name]:
                application.passwordHashFile ?? "",
            [Env.GOBLIN_BOOTSTRAP_NODE_STATE.name]: nodeState,
            [Env.GOBLIN_BOOTSTRAP_APP_STATE.name]: application.state ?? "ready",
            [Env.GOBLIN_BOOTSTRAP_PULL_DELAY.name]: String(
                application.pullDelay ?? 0,
            ),
            [Env.GOBLIN_BOOTSTRAP_SLOW_BUILD.name]: String(
                application.slowBuild ?? false,
            ),
            [Env.GOBLIN_BOOTSTRAP_DOCKER_INSTALLED.name]: String(
                application.dockerInstalled ?? true,
            ),
        },
        encoding: "utf8" as const,
        stdio: ["ignore", "pipe", "pipe"] as ["ignore", "pipe", "pipe"],
        timeout: 60_000,
    };
    let output = application.resume
        ? ""
        : execFileSync("/bin/sh", [scriptPath], options);
    if (!application.bootstrapOnly) {
        // systemctl launches independently in production. Exercise the worker here
        // separately, after verifying that the Azure extension has already returned.
        try {
            const workerOutput = execFileSync(
                "bash",
                [
                    join(root, "opt/goblin/setup/installer.sh"),
                    ...(application.recovery ? ["recover"] : []),
                ],
                options,
            );
            await appendFile(
                join(root, "var/log/goblin-installer.log"),
                workerOutput,
            );
            output += workerOutput;
        } catch (error) {
            const failure = error as { stdout: string; stderr: string };
            await appendFile(
                join(root, "var/log/goblin-installer.log"),
                failure.stdout + failure.stderr,
            );
            throw error;
        }
    }
    return output;
}

test("worker collection preserves exit statuses when children finish before the wait starts", async () => {
    const installer = await readFile("deploy/azure/setup/installer.sh", "utf8");
    const start = installer.indexOf("wait_for_worker() {");
    assert.ok(start >= 0);
    const helper = installer.slice(
        start,
        installer.indexOf("\n}\n", start) + 3,
    );
    execFileSync("bash", [
        "-c",
        helper +
            `
set -eu
(exit 0) & first=$!
(exit 7) & second=$!
sleep 0.1
result=0
wait_for_worker "$first" "$second" || result=$?
[[ "$finished" == "$first" && "$result" == 0 ]]
result=0
wait_for_worker "$second" || result=$?
[[ "$finished" == "$second" && "$result" == 7 ]]
`,
    ]);
});

test("parallel installation bounds image pulls, overlaps independent steps, and joins before migration", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-concurrency-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "ready", { pullDelay: 400 });
    const progress = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    const steps = Object.fromEntries(
        progress.steps.map((step: { id: string }) => [step.id, step]),
    );
    const start = (id: string) => Date.parse(steps[id].startedAt);
    const end = (id: string) => Date.parse(steps[id].finishedAt);
    assert.ok(start("image") < end("k3s") && start("k3s") < end("image"));
    assert.ok(
        start("database") >= end("cert-manager") &&
            start("database") >= end("sandbox"),
    );
    assert.ok(
        start("migrate") >= end("import") &&
            start("migrate") >= end("database"),
    );
    const pulls = (await readFile(join(root, "pull-events.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    let active = 0,
        maximum = 0;
    for (const event of pulls) {
        active += event.event === "start" ? 1 : -1;
        maximum = Math.max(maximum, active);
    }
    assert.equal(active, 0);
    assert.equal(pulls.length, 16);
    assert.ok(maximum > 1 && maximum <= 4);
    assert.ok(!JSON.stringify(progress).includes("do-not-stream"));
    assert.match(JSON.stringify(progress), /installing runtime dependencies/);
});

test("a pull failure stops the owned build process before retry can deploy", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-pull-failure-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(
        bootstrap(root, fakePassword, "ready", {
            state: "pull-failed",
            slowBuild: true,
            pullDelay: 1000,
        }),
        { status: 1 },
    );
    const pid = Number(await readFile(join(root, "build-pid"), "utf8"));
    assert.throws(() => process.kill(pid, 0), { code: "ESRCH" });
    const progress = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    assert.equal(progress.status, "failed");
    assert.equal(
        progress.steps.find((step: { id: string }) => step.id === "deploy")
            .status,
        "waiting",
    );
    assert.equal(
        progress.steps.find((step: { id: string }) => step.id === "prefetch")
            .status,
        "failed",
    );
    execFileSync("flock", [
        "-n",
        join(root, "var/lib/goblin/install/installer.lock"),
        "true",
    ]);
    await access(
        join(root, "var/lib/goblin/install/private/work/goblin-source.tar.gz"),
    );
});

test("installer waits for node registration after API readiness, including transient list failures", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-node-delayed-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const output = await bootstrap(root, fakePassword, "delayed");
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    assert.equal(
        await readFile(join(root, "node-registration-attempts"), "utf8"),
        "4",
    );
    const calls: string[][] = (
        await readFile(join(root, "k3s-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const apiReady = calls.findIndex((args) => args.includes("--raw=/readyz"));
    const registration = calls.findIndex(
        (args) => args[1] === "get" && args[2] === "nodes",
    );
    const nodeReady = calls.findIndex(
        (args) => args[1] === "wait" && args.includes("node"),
    );
    const secret = calls.findIndex(
        (args) => args[1] === "create" && args[2] === "secret",
    );
    assert.ok(
        apiReady >= 0 &&
            registration > apiReady &&
            nodeReady > registration &&
            secret > nodeReady,
    );
    assert.match(output, /Waiting for Kubernetes node registration/);
    assert.match(output, /Checking Kubernetes node readiness/);
});

test("installer verifies the pinned Agent Sandbox core release and waits for its controller", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-sandbox-version-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword);
    const downloads: string[][] = (
        await readFile(join(root, "curl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const sandboxDownloads = downloads
        .flat()
        .filter((arg) =>
            arg.startsWith("https://github.com/kubernetes-sigs/agent-sandbox/"),
        );
    assert.deepEqual(sandboxDownloads, [
        "https://github.com/kubernetes-sigs/agent-sandbox/releases/download/v1.0.4/sandbox.yaml",
    ]);
    const manifest = join(
        root,
        "var/lib/goblin/install/private/work/sandbox.yaml",
    );
    const checks: string[] = (
        await readFile(join(root, "checksum-checks.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        checks.includes(
            `c4f6344b6f04e450b61c433ce6275f2a45bc30f746b55ce47ade454ffa2a1aa7  ${manifest}\n`,
        ),
    );
    const calls: string[][] = (
        await readFile(join(root, "k3s-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const applied = calls.findIndex(
        (args) =>
            args[1] === "apply" &&
            args.includes("-k") &&
            args.includes(join(root, "var/lib/goblin/install/private/work")) &&
            args.includes("--server-side") &&
            args.includes("--field-manager=goblin-bootstrap"),
    );
    const established = calls.findIndex(
        (args) =>
            args[1] === "wait" &&
            args.includes("crd/sandboxes.agents.x-k8s.io") &&
            args.includes("--for=condition=Established"),
    );
    const ready = calls.findIndex(
        (args) =>
            args[1] === "rollout" &&
            args[2] === "status" &&
            args.includes("deployment/sandbox") &&
            args.includes("sandbox"),
    );
    const application = calls.findIndex(
        (args) =>
            args[1] === "apply" &&
            args.includes(join(root, "var/lib/goblin/deploy/azure/app")),
    );
    assert.ok(
        applied >= 0 &&
            established > applied &&
            ready > established &&
            application > ready,
    );
});

test("installer installs the application and uses Azure's hostname for both routing and origin", async (t) => {
    for (const hostname of [
        "goblin-prod.southeastasia.cloudapp.azure.com",
        "custom-name.westeurope.cloudapp.azure.com",
    ]) {
        const root = await mkdtemp(join(tmpdir(), "goblin-hostname-"));
        t.after(() => rm(root, { recursive: true, force: true }));
        const output = await bootstrap(root, fakePassword, "ready", {
            hostname,
            sourceRef: "refs/heads/master",
        });
        const config = await readFile(
            join(root, "var/lib/goblin/deploy/azure/app/kustomization.yaml"),
            "utf8",
        );
        const ingress = await readFile(
            join(root, "var/lib/goblin/deploy/azure/app/ingress.yaml"),
            "utf8",
        );
        assert.ok(config.includes(`value: http://${hostname}`));
        assert.ok(ingress.includes(`host: ${hostname}`));
        assert.ok(!config.includes("__GOBLIN_"));
        assert.ok(!ingress.includes("__GOBLIN_"));
        const digest = (
            await readFile(
                join(root, "var/lib/goblin/application-source-sha256"),
                "utf8",
            )
        ).trim();
        assert.match(digest, /^[0-9a-f]{64}$/);
        assert.ok(config.includes(`localhost/goblin-auth:${digest}`));
        const dockerCalls: string[][] = (
            await readFile(join(root, "docker-requests.jsonl"), "utf8")
        )
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        assert.ok(
            dockerCalls.some(
                (args) =>
                    args[1] === "build" &&
                    args.includes("--load") &&
                    args.includes(`localhost/goblin-auth:${digest}`),
            ),
        );
        assert.ok(
            dockerCalls.some(
                (args) =>
                    args[1] === "save" &&
                    args.includes(`localhost/goblin-auth:${digest}`) &&
                    !args.includes("--format"),
            ),
        );
        await assert.rejects(
            access(join(root, "apt-requests.jsonl")),
            "an existing Docker installation should be reused",
        );
        assert.equal(
            await readFile(join(root, "var/lib/goblin/public-url"), "utf8"),
            `http://${hostname}\n`,
        );
        assert.ok(output.includes(`Goblin ready: http://${hostname}`));
        const calls: string[][] = (
            await readFile(join(root, "k3s-requests.jsonl"), "utf8")
        )
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        const imported = calls.findIndex(
            (args) => args[0] === "ctr" && args.includes("import"),
        );
        const applied = calls.findIndex(
            (args) =>
                args[1] === "apply" &&
                args.includes(join(root, "var/lib/goblin/deploy/azure/app")),
        );
        const restarted = calls.findIndex(
            (args) => args[1] === "delete" && args[2] === "pod",
        );
        const ready = calls.findIndex(
            (args) => args[1] === "wait" && args.includes("sandbox/app"),
        );
        assert.ok(
            imported >= 0 &&
                applied > imported &&
                restarted > applied &&
                ready > restarted,
        );
        assert.ok(
            !calls.some(
                (args) =>
                    args[1] === "delete" &&
                    ["pvc", "namespace", "sandbox"].includes(args[2]),
            ),
        );
        const downloads: string[][] = (
            await readFile(join(root, "curl-requests.jsonl"), "utf8")
        )
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        assert.ok(
            downloads.some((args) =>
                args.includes(
                    "https://codeload.github.com/jgador/goblin/tar.gz/refs/heads/master",
                ),
            ),
        );
        assert.ok(
            downloads.some(
                (args) =>
                    args.includes(`${hostname}:80:10.43.0.80`) &&
                    args.includes(`http://${hostname}/`),
            ),
        );
        assert.ok(
            downloads.some((args) => args.includes(`${hostname}:80:10.20.0.4`)),
            "the public probe selects a single IPv4 node address",
        );
    }
});

test("installer targets its k3s cluster when standalone kubectl has no context or an unrelated kubeconfig", async (t) => {
    for (const context of ["missing", "other-cluster"]) {
        const root = await mkdtemp(join(tmpdir(), "goblin-kubeconfig-"));
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

test("installer isolates Docker configuration from Desktop credentials, contexts and builders across retries", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-docker-config-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const personal = join(root, "personal-docker");
    await mkdir(personal);
    const personalConfig = JSON.stringify({
        credsStore: "desktop.exe",
        currentContext: "desktop-linux",
        auths: { "https://index.docker.io/v1/": { auth: "test-only" } },
    });
    await writeFile(join(personal, "config.json"), personalConfig);
    const dockerEnvironment = {
        [Env.PATH.name]:
            "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        [Env.DOCKER_CONFIG.name]: personal,
        [Env.DOCKER_HOST.name]: "tcp://desktop.invalid:2376",
        [Env.DOCKER_CONTEXT.name]: "desktop-linux",
        [Env.DOCKER_TLS.name]: "1",
        [Env.DOCKER_TLS_VERIFY.name]: "1",
        [Env.DOCKER_CERT_PATH.name]: join(personal, "certs"),
        [Env.BUILDX_CONFIG.name]: join(personal, "buildx"),
        [Env.BUILDX_BUILDER.name]: "personal-builder",
    };
    await assert.rejects(
        bootstrap(root, fakePassword, "ready", {
            dockerEnvironment,
            state: "build-failed",
        }),
    );
    const configDir = join(
        root,
        "var/lib/goblin/install/private/work/docker-config",
    );
    assert.equal((await stat(configDir)).mode & 0o777, 0o700);
    assert.equal(
        (await stat(join(configDir, "config.json"))).mode & 0o777,
        0o600,
    );
    await bootstrap(root, fakePassword, "ready", {
        dockerEnvironment,
        resume: true,
    });
    assert.equal(
        await readFile(join(personal, "config.json"), "utf8"),
        personalConfig,
    );
    assert.deepEqual(await readdir(personal), ["config.json"]);
    await assert.rejects(access(configDir), { code: "ENOENT" });
});

test("installer installs Docker without dropping K3s forwarding or replacing existing daemon settings", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-docker-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await mkdir(join(root, "etc/docker"), { recursive: true });
    await writeFile(
        join(root, "etc/docker/daemon.json"),
        JSON.stringify({ "log-driver": "local" }),
    );
    await bootstrap(root, fakePassword, "ready", { dockerInstalled: false });
    assert.deepEqual(
        JSON.parse(
            await readFile(join(root, "etc/docker/daemon.json"), "utf8"),
        ),
        {
            "log-driver": "local",
            "ip-forward-no-drop": true,
        },
    );
    const installs: string[][] = (
        await readFile(join(root, "apt-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        installs.some(
            (args) =>
                args.includes("install") &&
                args.includes("docker.io") &&
                args.includes("docker-buildx"),
        ),
    );
    const services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        services.some((args) => args.join(" ") === "enable --now docker"),
    );
});

test("installer cannot report ready when Agent Sandbox, Docker, the application build, pod, Headlamp, logging, or ingress fails", async (t) => {
    for (const state of [
        "sandbox-failed",
        "docker-failed",
        "build-failed",
        "not-ready",
        "headlamp-failed",
        "logs-failed",
        "collector-failed",
        "ingress-failed",
    ] as const) {
        const root = await mkdtemp(join(tmpdir(), "goblin-app-failed-"));
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(
            bootstrap(root, fakePassword, "ready", { state }),
            { status: 1 },
        );
        assert.equal(
            await readFile(
                join(root, "var/lib/goblin/bootstrap-status"),
                "utf8",
            ),
            "setup-ready\n",
        );
        assert.equal(
            JSON.parse(
                await readFile(
                    join(root, "var/lib/goblin/install/status.json"),
                    "utf8",
                ),
            ).status,
            "failed",
        );
    }
});

test("bootstrap rejects invalid hostname/ref input before invoking the application builder", async (t) => {
    for (const config of [
        { hostname: "$(touch PWNED).example.com" },
        { sourceRef: "master;touch PWNED" },
    ]) {
        const root = await mkdtemp(join(tmpdir(), "goblin-app-input-"));
        t.after(() => rm(root, { recursive: true, force: true }));
        await assert.rejects(bootstrap(root, fakePassword, "ready", config), {
            status: 1,
        });
        await assert.rejects(access(join(root, "PWNED")));
        await assert.rejects(access(join(root, "docker-requests.jsonl")));
    }
});

test("installer fails clearly if the API is ready but no node ever registers", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-node-missing-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(bootstrap(root, fakePassword, "missing"), {
        status: 1,
    });
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const logs = await readFile(
        join(root, "var/log/goblin-installer.log"),
        "utf8",
    );
    assert.match(logs, /Kubernetes node did not register/);
    assert.match(logs, /Installation failed/);
    const calls: string[][] = (
        await readFile(join(root, "k3s-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        !calls.some((args) => args[1] === "wait" && args.includes("node")),
    );
    assert.ok(!calls.some((args) => args[1] === "apply"));
    await assert.rejects(access(join(root, "secret.json")));
});

test("installer does not continue when a registered node fails its readiness wait", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-node-not-ready-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(bootstrap(root, fakePassword, "not-ready"), {
        status: 1,
    });
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const logs = await readFile(
        join(root, "var/log/goblin-installer.log"),
        "utf8",
    );
    assert.match(logs, /timed out waiting for the condition/);
    assert.match(logs, /Installation failed/);
    await assert.rejects(access(join(root, "secret.json")));
});

test("Azure password survives provisioning and unlocks Goblin", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-password-"));
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
    assert.doesNotMatch(session.headers.get("set-cookie")!, /;\s*Secure/i);
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
    const root = await mkdtemp(
        join(tmpdir(), "goblin-local-bootstrap-password-"),
    );
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
    const root = await mkdtemp(join(tmpdir(), "goblin-password-invalid-"));
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
    const root = await mkdtemp(join(tmpdir(), "goblin-password-policy-"));
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
        assert.ok(template.outputs.goblinUrl.value.includes("http://"));
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

test("Azure provisioning returns with a status page before any cluster or application work", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-early-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "missing", { bootstrapOnly: true });
    assert.equal(
        await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"),
        "setup-ready\n",
    );
    const status = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    assert.equal(status.status, "waiting");
    assert.ok(
        status.steps.every(
            (step: { status: string }) => step.status === "waiting",
        ),
    );
    for (const file of [
        "k3s-requests.jsonl",
        "docker-requests.jsonl",
        "apt-requests.jsonl",
    ])
        await assert.rejects(access(join(root, file)));
    const services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.ok(
        services.some(
            (args) => args.join(" ") === "enable --now goblin-setup.service",
        ),
    );
    assert.ok(
        services.some(
            (args) =>
                args.join(" ") === "enable --now goblin-installer.service",
        ),
    );
});

test("cert-manager readiness gates database setup and migrations before application startup", async (t) => {
    const databaseFiles = [
        "backend/src/Goblin.Web/appsettings.json",
        "deploy/postgres/postgres.yaml",
        "deploy/postgres/setup.sh",
    ];
    const before = await Promise.all(
        databaseFiles.map((path) => readFile(path)),
    );
    for (const state of ["ready", "cert-failed", "webhook-failed"] as const) {
        const root = await mkdtemp(join(tmpdir(), "goblin-certs-"));
        t.after(() => rm(root, { recursive: true, force: true }));
        if (state === "ready") await bootstrap(root, fakePassword);
        else
            await assert.rejects(
                bootstrap(root, fakePassword, "ready", { state }),
                { status: 1 },
            );
        const calls: string[][] = (
            await readFile(join(root, "k3s-requests.jsonl"), "utf8")
        )
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
        assert.ok(
            calls.some((args) =>
                args.includes("deployment/cert-manager-webhook"),
            ),
        );
        if (state !== "cert-failed")
            assert.ok(calls.some((args) => args.includes("--dry-run=server")));
        assert.equal(
            calls.some(
                (args) =>
                    args[0] === "kubectl" &&
                    args.includes("statefulset/goblin-postgres"),
            ),
            state === "ready",
        );
        if (state === "ready") {
            const migration = calls.findIndex((args) =>
                args.some((value) => value.includes("goblin-test-migration")),
            );
            const application = calls.findIndex(
                (args) =>
                    args.includes("-k") &&
                    args.some((value) => value.endsWith("/app")),
            );
            assert.ok(migration >= 0, "the schema migration job ran");
            assert.ok(
                application > migration,
                "migrations finish before the application is deployed",
            );
        }
        const status = JSON.parse(
            await readFile(
                join(root, "var/lib/goblin/install/status.json"),
                "utf8",
            ),
        );
        assert.equal(status.status, state === "ready" ? "ready" : "failed");
        if (state !== "ready") {
            assert.equal(status.failedStep, "cert-manager");
            assert.equal(status.currentStep, "cert-manager");
            assert.ok(
                !calls.some(
                    (args) =>
                        args[1] === "create" &&
                        args[2] === "secret" &&
                        args.includes("goblin-postgres-admin"),
                ),
            );
        }
    }
    for (let i = 0; i < databaseFiles.length; i++)
        assert.ok(
            before[i].equals(await readFile(databaseFiles[i])),
            "database configuration is unchanged",
        );
});

test("a failed handoff restores private ingress and UI, then retry preserves credentials and completes", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-retry-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(
        bootstrap(root, fakePassword, "ready", { state: "public-failed" }),
        { status: 1 },
    );
    const statePath = join(root, "var/lib/goblin/install/status.json");
    let state = JSON.parse(await readFile(statePath, "utf8"));
    assert.equal(state.status, "failed");
    assert.equal(state.currentStep, "activate");
    assert.equal(
        await readFile(join(root, "ingress-mode"), "utf8"),
        "ClusterIP",
    );
    const before = await readFile(join(root, "secret.json"), "utf8");
    let services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(services.at(-1), [
        "enable",
        "--now",
        "goblin-setup.service",
    ]);
    assert.ok(
        !services.some(
            (args) =>
                args[0] === "disable" && args.includes("goblin-setup.service"),
        ),
    );
    await bootstrap(root, fakePassword, "ready", { resume: true });
    state = JSON.parse(await readFile(statePath, "utf8"));
    assert.equal(state.status, "ready");
    assert.equal(state.attempt, 2);
    assert.ok(
        state.steps.every(
            (step: { status: string; attempt: number }) =>
                step.status === "complete" && step.attempt === 2,
        ),
    );
    assert.equal(await readFile(join(root, "secret.json"), "utf8"), before);
    assert.equal(
        await readFile(join(root, "ingress-mode"), "utf8"),
        "LoadBalancer",
    );
    services = (await readFile(join(root, "systemctl-requests.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(services.at(-1), [
        "disable",
        "goblin-setup.service",
        "goblin-installer.service",
    ]);
    await assert.rejects(
        access(join(root, "var/lib/goblin/install/private/work")),
    );
    await access(join(root, "opt/goblin/bin/goblinctl"));
    await access(join(root, "var/log/goblin-installer.log"));
    const requests: string[][] = (
        await readFile(join(root, "curl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.equal(
        requests.filter((args) =>
            args.some((arg) => arg.includes("codeload.github.com")),
        ).length,
        1,
    );
});

test("the native downloader fits Azure Custom Script limits", async () => {
    const script = await readFile("deploy/azure/bootstrap.sh", "utf8");
    assert.ok(Buffer.byteLength(script) < 64 * 1024);
    assert.doesNotMatch(script, /python3|cargo |rustc /);
    assert.match(script, /sha256sum --check/);
    execFileSync("bash", ["-n"], { input: script });
});

test("a killed worker's recovery restores the UI from its persisted handoff marker", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-killed-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "ready", { bootstrapOnly: true });
    const statePath = join(root, "var/lib/goblin/install/status.json");
    for (const transition of [["begin"], ["start", "activate"], ["handoff"]])
        execFileSync(goblinctl, [
            "internal",
            "state",
            ...transition,
            "--path",
            statePath,
        ]);
    await writeFile(join(root, "var/lib/goblin/install/private/handoff"), "");
    await writeFile(join(root, "ingress-mode"), "LoadBalancer");
    await bootstrap(root, fakePassword, "ready", {
        resume: true,
        recovery: true,
    });
    const status = JSON.parse(
        await readFile(
            join(root, "var/lib/goblin/install/status.json"),
            "utf8",
        ),
    );
    assert.equal(status.status, "failed");
    assert.equal(status.currentStep, "activate");
    assert.equal(
        await readFile(join(root, "ingress-mode"), "utf8"),
        "ClusterIP",
    );
    await assert.rejects(
        access(join(root, "var/lib/goblin/install/private/handoff")),
    );
    const services: string[][] = (
        await readFile(join(root, "systemctl-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    assert.deepEqual(services.at(-1), [
        "enable",
        "--now",
        "goblin-setup.service",
    ]);
});

test("a concurrent installer cannot mutate state while the installation lock is held", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-locked-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "ready", { bootstrapOnly: true });
    const statePath = join(root, "var/lib/goblin/install/status.json");
    const before = await readFile(statePath, "utf8");
    const locker = spawn(
        "flock",
        [
            join(root, "var/lib/goblin/install/installer.lock"),
            "sh",
            "-c",
            "printf locked; cat",
        ],
        { stdio: ["pipe", "pipe", "pipe"] },
    );
    const exited = once(locker, "exit");
    try {
        await once(locker.stdout, "data");
        await bootstrap(root, fakePassword, "ready", { resume: true });
        assert.equal(await readFile(statePath, "utf8"), before);
        await assert.rejects(access(join(root, "k3s-requests.jsonl")));
    } finally {
        locker.stdin.end();
        await exited;
    }
});

test("a forwarded public origin is used by the application and readiness probes", async (t) => {
    for (const hostname of ["goblin.local.test", "localhost"]) {
        const root = await mkdtemp(join(tmpdir(), "goblin-forwarded-origin-"));
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
                        "var/lib/goblin/deploy/azure/app/kustomization.yaml",
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
                    join(root, "var/lib/goblin/deploy/azure/app/ingress.yaml"),
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
        const root = await mkdtemp(join(tmpdir(), "goblin-invalid-origin-"));
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
        const root = await mkdtemp(join(tmpdir(), "goblin-native-failed-"));
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
