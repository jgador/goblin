import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { access, cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { request as httpRequest } from "node:http";
import { join, resolve } from "node:path";
import { startBackend } from "./backend.js";

const publicHostname = "goblin-prod.southeastasia.cloudapp.azure.com";
const origin = `http://${publicHostname}`;
const hasherPath = resolve("deploy/azure/hash-password.py");
const fakePassword = "  Test-only 'quotes' $HOME $(touch PWNED) `touch PWNED` café 🧌  ";

// Exercise the real rendered bootstrap, replacing only infrastructure commands
// and absolute system paths. Python hashing, shell quoting and secret creation
// all run; no Azure account, Kubernetes installation, or network is needed.
async function bootstrap(root: string, password: string, nodeState: "ready" | "delayed" | "missing" | "not-ready" = "ready",
  application: { hostname?: string; sourceRef?: string; dockerInstalled?: boolean;
    state?: "ready" | "docker-failed" | "build-failed" | "not-ready" | "ingress-failed" } = {}) {
  const bin = join(root, "bin");
  await mkdir(bin, { recursive: true });
  const source = join(root, "archive/goblin");
  await mkdir(join(source, "deploy/azure"), { recursive: true });
  await cp("deploy/auth", join(source, "deploy/auth"), { recursive: true });
  await cp("deploy/azure/app", join(source, "deploy/azure/app"), { recursive: true });
  await cp("Dockerfile", join(source, "Dockerfile"));
  await cp("public", join(source, "public"), { recursive: true });
  await cp("public/index.html", join(root, "page.html"));
  execFileSync("tar", ["-czf", join(root, "source.tar.gz"), "-C", join(root, "archive"), "goblin"]);
  for (const name of ["cloud-init", "sha256sum", "curl", "k3s", "sleep", "docker", "dockerd", "apt-get", "systemctl"]) {
    await writeFile(join(bin, name), `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const name = path.basename(process.argv[1]);
const args = process.argv.slice(2);
const root = process.env.GOBLIN_BOOTSTRAP_TEST_DIR;
const nodeState = process.env.GOBLIN_BOOTSTRAP_NODE_STATE;
const appState = process.env.GOBLIN_BOOTSTRAP_APP_STATE;
if (name === 'curl') {
  fs.appendFileSync(path.join(root, 'curl-requests.jsonl'), JSON.stringify(args) + '\\n');
  const output = args[args.indexOf('--output') + 1];
  if (args.some(arg => arg.startsWith('https://codeload.github.com/'))) {
    fs.copyFileSync(path.join(root, 'source.tar.gz'), output);
  } else if (args.includes('--resolve')) {
    if (appState === 'ingress-failed') process.exit(22);
    fs.copyFileSync(path.join(root, 'page.html'), output);
  } else {
    fs.writeFileSync(output, '#!/bin/sh\\nexit 0\\n');
  }
} else if (name === 'sha256sum') {
  if (args.includes('--check')) fs.readFileSync(0);
  else process.stdout.write(require('node:crypto').createHash('sha256').update(fs.readFileSync(args[0])).digest('hex') + '  ' + args[0]);
} else if (name === 'docker' || name === 'dockerd') {
  fs.appendFileSync(path.join(root, 'docker-requests.jsonl'), JSON.stringify([name, ...args]) + '\\n');
  if (process.env.GOBLIN_BOOTSTRAP_DOCKER_INSTALLED === 'false' && !fs.existsSync(path.join(root, 'docker-installed')))
    process.exit(127);
  if (args[0] === 'info' && appState === 'docker-failed') process.exit(1);
  if (args[0] === 'build') {
    if (appState === 'build-failed') process.exit(1);
    fs.accessSync(path.join(args.at(-1), 'Dockerfile'));
    if ((fs.statSync(path.join(args.at(-1), 'public/index.html')).mode & 0o044) !== 0o044)
      throw new Error('Source assets must remain readable in the non-root image');
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
} else if (name === 'k3s') {
  fs.appendFileSync(path.join(root, 'k3s-requests.jsonl'), JSON.stringify(args) + '\\n');
  if (args[1] === 'get' && args[2] === 'nodes') {
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
  } else if (args[1] === 'wait' && args.includes('sandbox/goblin-auth') && appState === 'not-ready') {
    process.exit(1);
  } else if (args[1] === 'create' && args[2] === 'namespace') {
    process.stdout.write(JSON.stringify({ apiVersion: 'v1', kind: 'Namespace', metadata: { name: args[3] } }));
  } else if (args[1] === 'create' && args[2] === 'secret') {
    const file = args.find(arg => arg.startsWith('--from-file=owner-password=')).slice('--from-file=owner-password='.length);
    if ((fs.statSync(file).mode & 0o777) !== 0o600) throw new Error('Verifier file must be private');
    process.stdout.write(JSON.stringify({ apiVersion: 'v1', kind: 'Secret', metadata: { name: args[4], namespace: args[args.indexOf('-n') + 1] }, data: { 'owner-password': fs.readFileSync(file).toString('base64') } }));
  } else if (args[1] === 'apply' && args.at(-1) === '-') {
    const resource = JSON.parse(fs.readFileSync(0, 'utf8'));
    if (resource.kind === 'Secret') fs.writeFileSync(path.join(root, 'secret.json'), JSON.stringify(resource), { mode: 0o600 });
  }
}
`, { mode: 0o700 });
  }
  const hasher = await readFile(hasherPath, "utf8");
  const installer = await readFile("deploy/azure/install-app.sh", "utf8");
  let script = (await readFile("deploy/azure/bootstrap.sh", "utf8"))
    .replace("__GOBLIN_APPLICATION_INSTALLER__", () => installer)
    .replace("__GOBLIN_PASSWORD_HASHER__", () => hasher)
    .replace("__GOBLIN_HOSTNAME_BASE64__", Buffer.from(application.hostname ?? "goblin-prod.southeastasia.cloudapp.azure.com").toString("base64"))
    .replace("__GOBLIN_SOURCE_REF_BASE64__", Buffer.from(application.sourceRef ?? "master").toString("base64"))
    .replace("__GOBLIN_PASSWORD_BASE64__", Buffer.from(password).toString("base64"));
  for (const path of ["/var/lib/goblin", "/var/log/goblin-bootstrap.log", "/etc/rancher/k3s", "/etc/docker"]) {
    script = script.replaceAll(path, join(root, path.slice(1)));
  }
  await mkdir(join(root, "var/log"), { recursive: true });
  const scriptPath = join(root, "bootstrap.sh");
  await writeFile(scriptPath, script, { mode: 0o600 });
  return execFileSync("/bin/sh", [scriptPath], {
    cwd: root,
    env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, GOBLIN_BOOTSTRAP_TEST_DIR: root,
      GOBLIN_BOOTSTRAP_NODE_STATE: nodeState, GOBLIN_BOOTSTRAP_APP_STATE: application.state ?? "ready",
      GOBLIN_BOOTSTRAP_DOCKER_INSTALLED: String(application.dockerInstalled ?? true) },
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
    timeout: 30_000,
  });
}

test("bootstrap waits for node registration after API readiness, including transient list failures", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-node-delayed-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const output = await bootstrap(root, fakePassword, "delayed");
  assert.equal(await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"), "ready\n");
  assert.equal(await readFile(join(root, "node-registration-attempts"), "utf8"), "4");
  const calls: string[][] = (await readFile(join(root, "k3s-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
  const apiReady = calls.findIndex(args => args.includes("--raw=/readyz"));
  const registration = calls.findIndex(args => args[1] === "get" && args[2] === "nodes");
  const nodeReady = calls.findIndex(args => args[1] === "wait" && args.includes("node"));
  const secret = calls.findIndex(args => args[1] === "create" && args[2] === "secret");
  assert.ok(apiReady >= 0 && registration > apiReady && nodeReady > registration && secret > nodeReady);
  assert.match(output, /Waiting for Kubernetes node registration/);
  assert.match(output, /Checking Kubernetes node readiness/);
});

test("bootstrap installs the application and uses Azure's hostname for both routing and origin", async (t) => {
  for (const hostname of ["goblin-prod.southeastasia.cloudapp.azure.com", "custom-name.westeurope.cloudapp.azure.com"]) {
    const root = await mkdtemp(join(tmpdir(), "goblin-hostname-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const output = await bootstrap(root, fakePassword, "ready", { hostname, sourceRef: "refs/heads/master" });
    const config = await readFile(join(root, "var/lib/goblin/deploy/azure/app/kustomization.yaml"), "utf8");
    const ingress = await readFile(join(root, "var/lib/goblin/deploy/azure/app/ingress.yaml"), "utf8");
    assert.ok(config.includes(`value: http://${hostname}`));
    assert.ok(ingress.includes(`host: ${hostname}`));
    assert.ok(!config.includes("__GOBLIN_"));
    assert.ok(!ingress.includes("__GOBLIN_"));
    const digest = (await readFile(join(root, "var/lib/goblin/application-source-sha256"), "utf8")).trim();
    assert.match(digest, /^[0-9a-f]{64}$/);
    assert.ok(config.includes(`localhost/goblin-auth:${digest}`));
    const dockerCalls: string[][] = (await readFile(join(root, "docker-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
    assert.ok(dockerCalls.some(args => args[1] === "build" && args.includes("--load") && args.includes(`localhost/goblin-auth:${digest}`)));
    assert.ok(dockerCalls.some(args => args[1] === "save" && args.includes(`localhost/goblin-auth:${digest}`) && !args.includes("--format")));
    await assert.rejects(access(join(root, "apt-requests.jsonl")), "an existing Docker installation should be reused");
    assert.equal(await readFile(join(root, "var/lib/goblin/public-url"), "utf8"), `http://${hostname}\n`);
    assert.ok(output.includes(`Goblin ready: http://${hostname}`));
    const calls: string[][] = (await readFile(join(root, "k3s-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
    const imported = calls.findIndex(args => args[0] === "ctr" && args.includes("import"));
    const applied = calls.findIndex(args => args[1] === "apply" && args.includes("-k"));
    const restarted = calls.findIndex(args => args[1] === "delete" && args[2] === "pod");
    const ready = calls.findIndex(args => args[1] === "wait" && args.includes("sandbox/goblin-auth"));
    assert.ok(imported >= 0 && applied > imported && restarted > applied && ready > restarted);
    assert.ok(!calls.some(args => args[1] === "delete" && ["pvc", "namespace", "sandbox"].includes(args[2])));
    const downloads: string[][] = (await readFile(join(root, "curl-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
    assert.ok(downloads.some(args => args.includes("https://codeload.github.com/jgador/goblin/tar.gz/refs/heads/master")));
    assert.ok(downloads.some(args => args.includes(`${hostname}:80:10.20.0.4`) && args.includes(`http://${hostname}/`)));
  }
});

test("bootstrap installs Docker without dropping K3s forwarding or replacing existing daemon settings", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-docker-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  await mkdir(join(root, "etc/docker"), { recursive: true });
  await writeFile(join(root, "etc/docker/daemon.json"), JSON.stringify({ "log-driver": "local" }));
  await bootstrap(root, fakePassword, "ready", { dockerInstalled: false });
  assert.deepEqual(JSON.parse(await readFile(join(root, "etc/docker/daemon.json"), "utf8")), {
    "log-driver": "local", "ip-forward-no-drop": true,
  });
  const installs: string[][] = (await readFile(join(root, "apt-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
  assert.ok(installs.some(args => args.includes("install") && args.includes("docker.io") && args.includes("docker-buildx")));
  const services: string[][] = (await readFile(join(root, "systemctl-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
  assert.deepEqual(services, [["enable", "--now", "docker"]]);
});

test("bootstrap cannot report ready when Docker, the application build, pod, or ingress fails", async (t) => {
  for (const state of ["docker-failed", "build-failed", "not-ready", "ingress-failed"] as const) {
    const root = await mkdtemp(join(tmpdir(), "goblin-app-failed-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(bootstrap(root, fakePassword, "ready", { state }), { status: 1 });
    assert.equal(await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"), "failed\n");
    await assert.rejects(access(join(root, "var/lib/goblin/public-url")));
  }
});

test("bootstrap rejects invalid hostname/ref input before invoking the application builder", async (t) => {
  for (const config of [{ hostname: "$(touch PWNED).example.com" }, { sourceRef: "master;touch PWNED" }]) {
    const root = await mkdtemp(join(tmpdir(), "goblin-app-input-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await assert.rejects(bootstrap(root, fakePassword, "ready", config), { status: 1 });
    await assert.rejects(access(join(root, "PWNED")));
    await assert.rejects(access(join(root, "docker-requests.jsonl")));
  }
});

test("bootstrap fails clearly if the API is ready but no node ever registers", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-node-missing-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  await assert.rejects(bootstrap(root, fakePassword, "missing"), { status: 1 });
  assert.equal(await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"), "failed\n");
  const logs = await readFile(join(root, "var/log/goblin-bootstrap.log"), "utf8");
  assert.match(logs, /Kubernetes node did not register/);
  assert.match(logs, /Installation failed during: Waiting for Kubernetes node registration/);
  const calls: string[][] = (await readFile(join(root, "k3s-requests.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
  assert.ok(!calls.some(args => args[1] === "wait" && args.includes("node")));
  assert.ok(!calls.some(args => args[1] === "apply"));
  await assert.rejects(access(join(root, "secret.json")));
});

test("bootstrap does not continue when a registered node fails its readiness wait", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-node-not-ready-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  await assert.rejects(bootstrap(root, fakePassword, "not-ready"), { status: 1 });
  assert.equal(await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"), "failed\n");
  const logs = await readFile(join(root, "var/log/goblin-bootstrap.log"), "utf8");
  assert.match(logs, /timed out waiting for the condition/);
  assert.match(logs, /Installation failed during: Checking Kubernetes node readiness/);
  await assert.rejects(access(join(root, "secret.json")));
});

test("Azure password survives provisioning and unlocks Goblin without an owner token", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-"));
  let close = async () => {};
  t.after(async () => { await close(); await rm(root, { recursive: true, force: true }); });
  const output = await bootstrap(root, fakePassword);
  const logs = await readFile(join(root, "var/log/goblin-bootstrap.log"), "utf8");
  for (const text of [output, logs]) {
    assert.ok(!text.includes(fakePassword));
    assert.ok(!text.includes(Buffer.from(fakePassword).toString("base64")));
    assert.ok(!text.includes("pbkdf2-sha256$"));
  }
  await assert.rejects(access(join(root, "PWNED")));
  assert.equal(await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"), "ready\n");
  const secret = JSON.parse(await readFile(join(root, "secret.json"), "utf8"));
  assert.equal(secret.metadata.name, "goblin-owner-password");
  assert.equal(secret.metadata.namespace, "goblin");
  const verifier = Buffer.from(secret.data["owner-password"], "base64").toString("utf8");
  const passwordHashFile = join(root, "owner-password");
  await writeFile(passwordHashFile, verifier, { mode: 0o600 });
  const dataDir = join(root, "workspace-data");
  let app = await startBackend({ dataDir, passwordHashFile, publicOrigin: origin, allowInsecureHttp: true });
  close = () => app.close();
  const request = (path: string, token?: string, cookie = "") => new Promise<Response>((resolve, reject) => {
    const req = httpRequest(`${app.url}${path}`, {
      method: token === undefined ? "GET" : "POST",
      headers: { Host: publicHostname, Origin: origin, Cookie: cookie, "Content-Type": "application/json" },
    }, (res) => {
      let body = "";
      res.setEncoding("utf8").on("data", (chunk) => { body += chunk; });
      res.on("error", reject);
      res.on("end", () => resolve(new Response(body, { status: res.statusCode!, headers: Object.fromEntries(
        Object.entries(res.headers).filter(([, value]) => value !== undefined)
          .map(([name, value]) => [name, Array.isArray(value) ? value.join(",") : value!]),
      ) })));
    });
    req.on("error", reject);
    req.end(token === undefined ? undefined : JSON.stringify({ token }));
  });
  assert.deepEqual(await (await request("/api/session")).json(), { authenticated: false, usesPassword: true });
  await assert.rejects(access(app.tokenFile));
  assert.equal((await request("/api/status")).status, 401);
  // Whitespace, Unicode, quotes and shell metacharacters must survive exactly.
  assert.equal((await request("/api/session", fakePassword.trim())).status, 401);
  let session = await request("/api/session", fakePassword);
  assert.equal(session.status, 200);
  const cookie = session.headers.get("set-cookie")!.split(";")[0];
  assert.match(session.headers.get("set-cookie")!, /HttpOnly/i);
  assert.doesNotMatch(session.headers.get("set-cookie")!, /;\s*Secure/i);
  assert.equal((await request("/api/status", undefined, cookie)).status, 200);
  assert.equal((await request("/owner-password", undefined, cookie)).status, 404);
  assert.ok(!(await (await request("/api/session")).text()).includes(verifier.trim()));

  await app.close();
  // An old workspace access token may exist on an upgraded persistent volume. It must
  // not be accepted when the Azure password is configured.
  const oldToken = "old-workspace-access-code-must-no-longer-work";
  await writeFile(join(dataDir, "owner-token"), oldToken, { mode: 0o600 });
  app = await startBackend({ dataDir, passwordHashFile, publicOrigin: origin, allowInsecureHttp: true });
  assert.equal((await request("/api/status", undefined, cookie)).status, 401);
  assert.equal((await request("/api/session", oldToken)).status, 401);
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
  const updated = JSON.parse(await readFile(join(root, "secret.json"), "utf8"));
  await writeFile(passwordHashFile, Buffer.from(updated.data["owner-password"], "base64"));
  app = await startBackend({ dataDir, passwordHashFile, publicOrigin: origin, allowInsecureHttp: true });
  assert.equal((await request("/api/session", fakePassword)).status, 401);
  assert.equal((await request("/api/session", newPassword)).status, 200);
});

test("a configured password file must exist and contain a supported verifier", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-invalid-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const passwordHashFile = join(root, "owner-password");
  const dataDir = join(root, "data");
  await mkdir(dataDir);
  await writeFile(join(dataDir, "owner-token"), "old-workspace-access-code-must-no-longer-work");
  const valid = execFileSync("python3", [hasherPath], { input: fakePassword, encoding: "utf8" });
  for (const invalid of [null, "", "not-a-verifier", valid.replace("600000", "1"), valid.replace("600000", "999999999"), "pbkdf2-sha256$600000$bad$bad", "x".repeat(257)]) {
    if (invalid !== null) await writeFile(passwordHashFile, invalid);
    await assert.rejects(startBackend({ dataDir, passwordHashFile }), /hash file is invalid|Could not find file/);
  }
});

test("password provisioning accepts short passwords and never logs rejected passwords", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-policy-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const invalid of ["", "   ", "a".repeat(129), "a\n", "test-password\nline-break", "test-password\u0000control"]) {
    assert.throws(() => execFileSync("python3", [hasherPath], { input: invalid, stdio: ["pipe", "pipe", "pipe"] }), (error: unknown) => {
      const failure = error as { stdout: Buffer; stderr: Buffer };
      assert.equal(failure.stdout.length, 0);
      assert.match(failure.stderr.toString(), /^Goblin password is invalid\./);
      if (invalid) assert.ok(!failure.stderr.toString().includes(invalid));
      return true;
    });
  }
  for (const valid of ["a", "1", "!", "short", "b".repeat(128), fakePassword]) {
    const first = execFileSync("python3", [hasherPath], { input: valid, encoding: "utf8" });
    const second = execFileSync("python3", [hasherPath], { input: valid, encoding: "utf8" });
    assert.match(first, /^pbkdf2-sha256\$600000\$/);
    assert.notEqual(first, second, "each verifier needs a fresh salt");
  }
  await assert.rejects(bootstrap(root, ""));
  assert.equal(await readFile(join(root, "var/lib/goblin/bootstrap-status"), "utf8"), "failed\n");
  await assert.rejects(access(join(root, "secret.json")));
});

test("Azure templates keep the password protected and the portal requires confirmation", async () => {
  const ui = JSON.parse(await readFile("deploy/azure/createUiDefinition.json", "utf8"));
  const field = ui.parameters.basics.find((item: { name: string }) => item.name === "goblinPassword");
  assert.equal(field.type, "Microsoft.Common.PasswordBox");
  assert.equal(field.options.hideConfirmation, false);
  assert.equal(field.constraints.required, true);
  const policy = new RegExp(field.constraints.regex);
  for (const valid of ["a", "1", "!", "short", "b".repeat(128), fakePassword]) assert.ok(policy.test(valid));
  for (const invalid of ["", "   ", "a".repeat(129), "a\n", "test-password\nline-break"]) assert.ok(!policy.test(invalid));
  for (const name of ["azuredeploy.json", "azuredeploy.portal.json"]) {
    const template = JSON.parse(await readFile(`deploy/azure/${name}`, "utf8"));
    assert.equal(template.parameters.goblinPassword.type.toLowerCase(), "securestring");
    assert.ok(!("minLength" in template.parameters.goblinPassword));
    assert.equal(template.parameters.goblinPassword.maxLength, 128);
    assert.ok(!("defaultValue" in template.parameters.goblinPassword));
    const deployment = template.resources.find((item: { type: string }) => item.type === "Microsoft.Resources/deployments");
    const nested = deployment.properties.template;
    assert.equal(nested.parameters.goblinPassword.type.toLowerCase(), "securestring");
    assert.ok(!("minLength" in nested.parameters.goblinPassword));
    assert.equal(deployment.properties.parameters.goblinPassword.value, "[parameters('goblinPassword')]");
    const extension = nested.resources.find((item: { type: string }) => item.type === "Microsoft.Compute/virtualMachines/extensions");
    assert.equal(extension.properties.settings, undefined);
    assert.match(extension.properties.protectedSettings.script, /parameters\('goblinPassword'\)/);
    assert.ok(!JSON.stringify(template.outputs).includes("goblinPassword"));
    assert.ok(!JSON.stringify(nested.outputs).includes("goblinPassword"));
    assert.equal(template.parameters.goblinSourceRef.defaultValue, "master");
    assert.ok(template.outputs.goblinUrl.value.includes("http://"));
    assert.match(extension.properties.protectedSettings.script, /dnsSettings\.fqdn/);
    assert.match(extension.properties.protectedSettings.script, /parameters\('goblinSourceRef'\)/);
    if (name.includes("portal")) assert.deepEqual(Object.keys(ui.parameters.outputs).sort(), Object.keys(template.parameters).sort());
  }
});
