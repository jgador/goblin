import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { access, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { request as httpRequest } from "node:http";
import { join, resolve } from "node:path";
import { startBackend } from "./backend.js";

const origin = "http://localhost:8787";
const hasherPath = resolve("deploy/azure/hash-password.py");
const fakePassword = "  Test-only 'quotes' $HOME $(touch PWNED) `touch PWNED` café 🧌  ";

// Exercise the real rendered bootstrap, replacing only infrastructure commands
// and absolute system paths. Python hashing, shell quoting and secret creation
// all run; no Azure account, Kubernetes installation, or network is needed.
async function bootstrap(root: string, password: string) {
  const bin = join(root, "bin");
  await mkdir(bin, { recursive: true });
  for (const name of ["cloud-init", "sha256sum", "curl", "k3s"]) {
    await writeFile(join(bin, name), `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const name = path.basename(process.argv[1]);
const args = process.argv.slice(2);
const root = process.env.GOBLIN_BOOTSTRAP_TEST_DIR;
if (name === 'curl') {
  fs.writeFileSync(args[args.indexOf('--output') + 1], '#!/bin/sh\\nexit 0\\n');
} else if (name === 'sha256sum') {
  fs.readFileSync(0);
} else if (name === 'k3s') {
  if (args[1] === 'create' && args[2] === 'namespace') {
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
  let script = (await readFile("deploy/azure/bootstrap.sh", "utf8"))
    .replace("__GOBLIN_PASSWORD_HASHER__", () => hasher)
    .replace("__GOBLIN_PASSWORD_BASE64__", Buffer.from(password).toString("base64"));
  for (const path of ["/var/lib/goblin", "/var/log/goblin-bootstrap.log", "/etc/rancher/k3s"]) {
    script = script.replaceAll(path, join(root, path.slice(1)));
  }
  await mkdir(join(root, "var/log"), { recursive: true });
  const scriptPath = join(root, "bootstrap.sh");
  await writeFile(scriptPath, script, { mode: 0o600 });
  return execFileSync("/bin/sh", [scriptPath], {
    cwd: root,
    env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, GOBLIN_BOOTSTRAP_TEST_DIR: root },
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
}

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
  assert.equal(secret.metadata.namespace, "goblin-preview");
  const verifier = Buffer.from(secret.data["owner-password"], "base64").toString("utf8");
  const passwordHashFile = join(root, "owner-password");
  await writeFile(passwordHashFile, verifier, { mode: 0o600 });
  const dataDir = join(root, "workspace-data");
  let app = await startBackend({ dataDir, passwordHashFile, publicOrigin: origin });
  close = () => app.close();
  const request = (path: string, token?: string, cookie = "") => new Promise<Response>((resolve, reject) => {
    const req = httpRequest(`${app.url}${path}`, {
      method: token === undefined ? "GET" : "POST",
      headers: { Host: "localhost:8787", Origin: origin, Cookie: cookie, "Content-Type": "application/json" },
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
  assert.equal((await request("/api/status", undefined, cookie)).status, 200);
  assert.equal((await request("/owner-password", undefined, cookie)).status, 404);
  assert.ok(!(await (await request("/api/session")).text()).includes(verifier.trim()));

  await app.close();
  // An old preview token may exist on an upgraded persistent volume. It must
  // not be accepted when the Azure password is configured.
  const oldToken = "old-preview-access-code-must-no-longer-work";
  await writeFile(join(dataDir, "owner-token"), oldToken, { mode: 0o600 });
  app = await startBackend({ dataDir, passwordHashFile, publicOrigin: origin });
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
  app = await startBackend({ dataDir, passwordHashFile, publicOrigin: origin });
  assert.equal((await request("/api/session", fakePassword)).status, 401);
  assert.equal((await request("/api/session", newPassword)).status, 200);
});

test("a configured password file must exist and contain a supported verifier", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "goblin-password-invalid-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const passwordHashFile = join(root, "owner-password");
  const dataDir = join(root, "data");
  await mkdir(dataDir);
  await writeFile(join(dataDir, "owner-token"), "old-preview-access-code-must-no-longer-work");
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
    if (name.includes("portal")) assert.deepEqual(Object.keys(ui.parameters.outputs).sort(), Object.keys(template.parameters).sort());
  }
});
