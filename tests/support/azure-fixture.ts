import { environmentVariables as Env } from "../../config/environment.mjs";
import { databaseHostFixture, goblinctl } from "./goblinctl.js";
import { createHash } from "node:crypto";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
    appendFile,
    cp,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    writeFile,
} from "node:fs/promises";
import { join, resolve } from "node:path";

// Every scenario owns its filesystem and infrastructure log files. No fixed port
// is opened: infrastructure is mocked and the backend test host binds port zero.
export async function fixtureDirectory(prefix: string) {
    const artifacts = resolve(".artifacts/deployment/azure");
    await mkdir(artifacts, { recursive: true });
    return mkdtemp(join(artifacts, prefix));
}

export const publicHostname = "goblin-prod.southeastasia.cloudapp.azure.com";
export const origin = `https://${publicHostname}`;
export const hasherArgs = ["internal", "hash-password"];
export const fakePassword =
    "  Test-only 'quotes' $HOME $(touch PWNED) `touch PWNED` café 🧌  ";
const nativeVersionOutput = execFileSync(goblinctl, ["--version"], {
    encoding: "utf8",
}).trim();
assert.match(nativeVersionOutput, /^goblinctl \d+\.\d+\.\d+$/);
const nativeVersion = nativeVersionOutput.slice("goblinctl ".length);

// Exercise the real rendered bootstrap, replacing only infrastructure commands
// and absolute system paths. Native hashing, shell quoting and secret creation
// all run; no Azure account, Kubernetes installation, or network is needed.
export async function bootstrap(
    root: string,
    password: string,
    nodeState: "ready" | "delayed" | "missing" | "not-ready" = "ready",
    application: {
        publicOrigin?: string;
        certificateEmail?: string;
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
        slowBuild?: number;
        synchronizeRuntimeStart?: boolean;
        holdBuildUntilFailure?: boolean;
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
            | "pull-failed"
            | "k3s-download-failed"
            | "k3s-corrupt"
            | "tls-failed"
            | "tls-untrusted";
    } = {},
) {
    const bin = join(root, "bin");
    await mkdir(bin, { recursive: true });
    const rendezvous = join(root, "synchronize-runtime-start");
    await rm(rendezvous, { force: true });
    if (application.synchronizeRuntimeStart) {
        for (const step of ["image", "k3s"])
            await rm(join(root, "runtime-" + step + "-arrived"), {
                force: true,
            });
        await writeFile(rendezvous, "");
    }
    const failureGate = join(root, "hold-build-until-failure");
    await rm(failureGate, { force: true });
    if (application.holdBuildUntilFailure) await writeFile(failureGate, "");
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
    await cp("deploy/platform", join(source, "deploy/platform"), {
        recursive: true,
    });
    await cp(
        "deploy/install-request.json",
        join(source, "deploy/install-request.json"),
    );
    await cp("deploy/azure/platform", join(source, "deploy/azure/platform"), {
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
const ingressFile = path.join(root, 'ingress.json');
// Both production branches must reach infrastructure before either can finish.
// A serial installer times out here; CPU scheduling cannot fake a failed overlap.
function meetRuntimeStep(step, other) {
  if (!fs.existsSync(path.join(root, 'synchronize-runtime-start'))) return;
  fs.writeFileSync(path.join(root, 'runtime-' + step + '-arrived'), '');
  const deadline = Date.now() + 15000;
  while (!fs.existsSync(path.join(root, 'runtime-' + other + '-arrived'))) {
    if (Date.now() >= deadline) throw new Error('Independent runtime step did not start: ' + other);
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 10);
  }
}
function ingress() {
  if (fs.existsSync(ingressFile)) return JSON.parse(fs.readFileSync(ingressFile, 'utf8'));
  const yaml = fs.readFileSync(path.join(root, 'var/lib/goblin/deploy/azure/platform/ingress.yaml'), 'utf8');
  const line = yaml.split('\\n').find(line => line.trim().startsWith('nginx.org/websocket-services:'));
  const annotations = {'acme.cert-manager.io/http01-edit-in-place': 'true'};
  if (line) annotations['nginx.org/websocket-services'] = JSON.parse(line.split(': ')[1]);
  return {metadata:{resourceVersion:'1',annotations},spec:{tls:[],rules:[{http:{paths:[
    {path:'/.well-known/acme-challenge/fixture',backend:{service:{name:'cm-acme-http-solver',port:{number:8089}}}},
    {path:'/',backend:{service:{name:'platform',port:{number:8787}}}}
  ]}}]}};
}
fs.appendFileSync(path.join(root, 'command-events.jsonl'), JSON.stringify({ name, args }) + '\\n');
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
  } else if (args.some(arg => arg.startsWith('https://raw.githubusercontent.com/k3s-io/k3s/'))) {
    // The pinned upstream script must install the pre-verified binary and images.
    fs.writeFileSync(output, '#!/bin/sh\\n' +
      'test "$INSTALL_K3S_SKIP_DOWNLOAD" = true || exit 98\\n' +
      'test -x "$${Env.GOBLIN_BOOTSTRAP_TEST_DIR.name}/usr/local/bin/k3s" || exit 98\\n' +
      'test -f "$${Env.GOBLIN_BOOTSTRAP_TEST_DIR.name}/var/lib/rancher/k3s/agent/images/k3s-airgap-images-amd64.tar.zst" || exit 98\\n' +
      'printf "%s\\n" "$INSTALL_K3S_VERSION" > "$${Env.GOBLIN_BOOTSTRAP_TEST_DIR.name}/k3s-installed-version"\\n');
  } else if (args.some(arg => arg.includes('/k3s-io/k3s/releases/download/'))) {
    meetRuntimeStep('k3s', 'image');
    fs.appendFileSync(path.join(root, 'k3s-downloads.jsonl'), JSON.stringify(args) + '\\n');
    if (appState === 'k3s-download-failed') process.exit(28);
    fs.writeFileSync(output, appState === 'k3s-corrupt' ? 'corrupt download' : 'k3s release asset\\n');
  } else if (args.some(arg => arg.includes('/nginx/kubernetes-ingress/'))) {
    fs.writeFileSync(output, 'proxy_set_header Host {{else}}$host{{end}};\\n' + 'grpc_set_header Host {{else}}$host{{end}};\\n');
  } else if (args.some(arg => arg.startsWith('https://github.com/') || arg.startsWith('https://helm.nginx.com/'))) {
    fs.writeFileSync(output, 'verified upstream fixture');
  } else if (args.some(arg => /^https?:/.test(arg))) {
    // F5 rejects the whole Ingress if a WebSocket service is absent from its routes.
    const current = ingress();
    const services = current.spec.rules.flatMap(rule => rule.http.paths.map(p => p.backend.service.name));
    const websocket = current.metadata.annotations['nginx.org/websocket-services'];
    if (websocket && websocket.split(',').some(service => !services.includes(service))) process.exit(22);
    if (appState === 'ingress-failed' || (appState === 'public-failed' && !args.some(arg => arg.endsWith(':10.43.0.80')))) process.exit(22);
    if (appState === 'tls-untrusted' && args.some(arg => arg.startsWith('https://'))) process.exit(60);
    if (args.includes('--insecure') || args.includes('-k')) throw new Error('Certificate validation must remain enabled');
    const url = args.find(arg => /^https?:/.test(arg));
    if (url.endsWith('/setup/healthz')) fs.writeFileSync(output, JSON.stringify({ setup: true }));
    else if (url.endsWith('/.well-known/oauth-authorization-server')) fs.writeFileSync(output, JSON.stringify({ issuer: new URL(url).origin + '/' }));
    else if (url.endsWith('/.well-known/oauth-protected-resource/mcp')) fs.writeFileSync(output, JSON.stringify({ resource: new URL(url).origin + '/mcp' }));
    else if (args.some(arg => arg.endsWith('/readyz'))) fs.writeFileSync(output, JSON.stringify({ ready: true }));
    else fs.copyFileSync(path.join(root, 'page.html'), output);
  } else {
    fs.writeFileSync(output, '#!/bin/sh\\nexit 0\\n');
  }
} else if (name === 'sha256sum') {
  if (args.includes('--check')) {
    const text = fs.readFileSync(0, 'utf8');
    fs.appendFileSync(path.join(root, 'checksum-checks.jsonl'), JSON.stringify(text) + '\\n');
    const [expected, file] = text.trim().split(/\\s+/, 2);
    if (appState === 'k3s-corrupt' && file.includes('/var/cache/goblin/downloads/')) process.exit(1);
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
    meetRuntimeStep('image', 'k3s');
    fs.writeFileSync(path.join(root, 'build-pid'), String(process.pid));
    // Keep this independent build running until the real supervisor observes
    // the injected pull failure and starts draining its existing workers.
    if (fs.existsSync(path.join(root, 'hold-build-until-failure'))) {
      const draining = path.join(root, 'var/lib/goblin/install/private/work/pipeline.draining');
      const deadline = Date.now() + 30000;
      while (!fs.existsSync(draining)) {
        if (Date.now() >= deadline) throw new Error('Installer did not drain running workers after the pull failure');
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 10);
      }
    }
    const buildDelay = Number(process.env["${Env.GOBLIN_BOOTSTRAP_SLOW_BUILD.name}"]);
    if (buildDelay) Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, buildDelay);
    if (appState === 'build-failed') process.exit(1);
    fs.accessSync(path.join(args.at(-1), 'Dockerfile'));
    if ((fs.statSync(path.join(args.at(-1), 'frontend/src/connection/index.html')).mode & 0o044) !== 0o044)
      throw new Error('Source assets must remain readable in the non-root image');
    process.stdout.write('#2 [codex 2/3] RUN --mount=type=cache,id=goblin-platform-npm-codex,target=/root/.npm npm ci --omit=dev --no-audit --no-fund\\n#2 1.0 password=do-not-stream\\n#2 DONE 2.0s\\n');
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
  if (args[0] === '--version') process.stdout.write('k3s version v1.36.5+k3s1 (fixture)\\n');
  else if (args[1] === 'get' && args[2] === 'ingress') {
    process.stdout.write(JSON.stringify(ingress()));
  } else if (args[1] === 'patch' && args[2] === 'ingress' && args.includes('--type=json')) {
    const current = ingress();
    for (const operation of JSON.parse(args[args.indexOf('-p') + 1])) {
      const parts = operation.path.slice(1).split('/').map(p => p.replaceAll('~1','/').replaceAll('~0','~'));
      const parent = parts.slice(0,-1).reduce((o,p) => o[p], current);
      const key = parts.at(-1);
      if (operation.op === 'test') {
        if (JSON.stringify(parent[key]) !== JSON.stringify(operation.value)) process.exit(1);
      } else if (operation.op === 'remove') delete parent[key];
      else parent[key] = operation.value;
    }
    current.metadata.resourceVersion = String(Number(current.metadata.resourceVersion) + 1);
    fs.writeFileSync(ingressFile, JSON.stringify(current));
    fs.appendFileSync(path.join(root, 'ingress-routes.jsonl'), JSON.stringify(current) + '\\n');
  } else if (args[1] === 'get' && args[2] === 'service') {
    const modeFile = path.join(root, 'ingress-mode');
    const mode = fs.existsSync(modeFile) ? fs.readFileSync(modeFile, 'utf8') : 'ClusterIP';
    process.stdout.write(args.some(arg => arg.includes('clusterIP')) ? '10.43.0.80' : mode);
  } else if (args[1] === 'patch' && args[2] === 'service') {
    const patch = JSON.parse(args[args.indexOf('-p') + 1]);
    fs.writeFileSync(path.join(root, 'ingress-mode'), patch.spec.type);
  } else if (args[1] === 'wait' && args.includes('certificate/goblin-public-tls') && args.includes('--for=condition=Ready') && appState === 'tls-failed') {
    process.exit(1);
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
  } else if (args[1] === 'wait' && args.includes('sandbox/platform') && appState === 'not-ready') {
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
        "/var/cache/goblin",
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
if (args[0] === 'db' && args[1] === 'host') {
    const result = spawnSync(${JSON.stringify(databaseHostFixture)}, [root], {stdio: 'inherit'});
    process.exit(result.status ?? 1);
}
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
            "__GOBLIN_CERTIFICATE_EMAIL_BASE64__",
            Buffer.from(
                application.certificateEmail ?? "owner@example.com",
            ).toString("base64"),
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
                application.slowBuild ?? 0,
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
