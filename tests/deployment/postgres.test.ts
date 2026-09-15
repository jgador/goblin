import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { cp, mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

const initializationPassword = randomBytes(32).toString("hex");

async function fixture(password = initializationPassword) {
  const root = await mkdtemp(join(tmpdir(), "goblin-postgres-setup-"));
  const bin = join(root, "bin");
  await mkdir(bin);
  const deployment = join(root, "deploy/postgres");
  await mkdir(deployment, { recursive: true });
  await cp("deploy/postgres/setup.sh", join(deployment, "setup.sh"));
  await cp("deploy/postgres/write-appsettings.py", join(deployment, "write-appsettings.py"));
  const web = join(root, "backend/src/Goblin.Web");
  await mkdir(web, { recursive: true });
  await writeFile(join(web, "appsettings.json"), JSON.stringify({ ConnectionStrings: {
    Goblin: `Host=goblin-postgres;Port=5432;Database=goblin;Username=goblin_app;Password="${password.replaceAll('"', '""')}"`,
  } }));
  await writeFile(join(bin, "kubectl"), `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const root = process.env.GOBLIN_POSTGRES_SETUP_TEST;
fs.appendFileSync(path.join(root, 'calls.jsonl'), JSON.stringify(args) + '\\n');
if (args[0] === 'get') {
  if (process.env.GOBLIN_POSTGRES_SETUP_DENY === 'true') process.exit(1);
  if (args.includes('json')) {
    const items = args.slice(2, args.indexOf('-n')).map(name => {
      const secret = JSON.parse(fs.readFileSync(path.join(root, name), 'utf8'));
      return { metadata: secret.metadata,
        data: Object.fromEntries(Object.entries(secret.stringData).map(([key, value]) =>
          [key, Buffer.from(value).toString('base64')])) };
    });
    process.stdout.write(JSON.stringify({items}));
  } else if (fs.existsSync(path.join(root, args[2]))) process.stdout.write(args[1] + '/' + args[2]);
} else if (args[0] === 'create' && args[1] === 'namespace') {
  process.stdout.write(JSON.stringify({kind: 'Namespace', metadata: {name: 'goblin'}}));
} else if (args[0] === 'create' && args[1] === '-f') {
  const secret = JSON.parse(fs.readFileSync(0, 'utf8'));
  fs.writeFileSync(path.join(root, secret.metadata.name), JSON.stringify(secret));
  process.stdout.write('secret/' + secret.metadata.name + ' created\\n');
} else if (args[0] === 'apply' && args[1] === '-f') {
  fs.readFileSync(0);
} else if (args[0] === 'apply' && args[1] === '-k') {
  fs.writeFileSync(path.join(root, 'goblin-postgres-data'), 'PVC exists');
}
`, { mode: 0o700 });
  const run = (deny = false) => execFileSync("bash", [join(deployment, "setup.sh")], {
    env: { ...process.env, PATH: `${bin}:${process.env.PATH}`, GOBLIN_POSTGRES_SETUP_TEST: root,
      GOBLIN_POSTGRES_SETUP_DENY: String(deny) },
    encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], timeout: 10_000,
  });
  return { root, run };
}

test("PostgreSQL setup creates separate private credentials and preserves them on rerun", async t => {
  const { root, run } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const first = run();
  const adminText = await readFile(join(root, "goblin-postgres-admin"), "utf8");
  const appText = await readFile(join(root, "goblin-postgres-app"), "utf8");
  const admin = JSON.parse(adminText);
  const app = JSON.parse(appText);
  assert.match(admin.stringData.password, /^[0-9a-f]{64}$/);
  assert.match(app.stringData.password, /^[0-9a-f]{64}$/);
  assert.equal(app.stringData.password, initializationPassword);
  assert.notEqual(admin.stringData.password, app.stringData.password);
  assert.deepEqual(Object.keys(admin.stringData), ["password"]);
  assert.deepEqual(Object.keys(app.stringData), ["password"]);
  const webPath = join(root, "backend/src/Goblin.Web/appsettings.json");
  const toolingPath = join(root, "backend/tools/Goblin.Database/appsettings.json");
  const web = JSON.parse(await readFile(webPath, "utf8"));
  const tooling = JSON.parse(await readFile(toolingPath, "utf8"));
  assert.deepEqual(web.ConnectionStrings, {
    Goblin: `Host=goblin-postgres;Port=5432;Database=goblin;Username=goblin_app;Password="${app.stringData.password}"`,
  });
  assert.deepEqual(tooling.ConnectionStrings, {
    Goblin: `Host=localhost;Port=5432;Database=goblin;Username=goblin_app;Password="${app.stringData.password}"`,
    GoblinAdmin: `Host=localhost;Port=5432;Database=goblin;Username=goblin_admin;Password="${admin.stringData.password}"`,
  });
  assert.equal((await stat(webPath)).mode & 0o777, 0o600);
  assert.equal((await stat(toolingPath)).mode & 0o777, 0o600);
  web.Logging = { LogLevel: { Default: "Warning" } };
  const expected = structuredClone(web);
  web.ConnectionStrings.Goblin = "Host=goblin-postgres;Password=changed-in-checkout";
  await writeFile(webPath, JSON.stringify(web));
  const second = run();
  assert.equal(await readFile(join(root, "goblin-postgres-admin"), "utf8"), adminText);
  assert.equal(await readFile(join(root, "goblin-postgres-app"), "utf8"), appText);
  assert.deepEqual(JSON.parse(await readFile(webPath, "utf8")), expected);
  assert.deepEqual(JSON.parse(await readFile(toolingPath, "utf8")), tooling);
  const calls = await readFile(join(root, "calls.jsonl"), "utf8");
  for (const password of [admin.stringData.password, app.stringData.password]) {
    assert.ok(!(first + second + calls).includes(password));
  }
});

test("PostgreSQL setup preserves quoted password characters when initializing and refreshing configuration", async t => {
  const password = '  quoted "password"; it\'s $literal  ';
  const { root, run } = await fixture(password);
  t.after(() => rm(root, { recursive: true, force: true }));
  const first = run();
  const app = JSON.parse(await readFile(join(root, "goblin-postgres-app"), "utf8"));
  assert.equal(app.stringData.password, password);
  const file = join(root, "backend/src/Goblin.Web/appsettings.json");
  const before = await readFile(file, "utf8");
  const second = run();
  assert.equal(await readFile(file, "utf8"), before);
  assert.ok(!(first + second + await readFile(join(root, "calls.jsonl"), "utf8")).includes(password));
});

test("PostgreSQL setup refuses replacement credentials for existing data or failed API reads", async t => {
  const { root, run } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  assert.throws(() => run(true));
  let calls: string[][] = (await readFile(join(root, "calls.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
  assert.ok(!calls.some(args => args[0] === "create" && args[1] === "-f"));
  run();
  await rm(join(root, "goblin-postgres-app"));
  await writeFile(join(root, "calls.jsonl"), "");
  assert.throws(() => run(), /Restore its credentials/);
  calls = (await readFile(join(root, "calls.jsonl"), "utf8")).trim().split("\n").map(line => JSON.parse(line));
  assert.ok(!calls.some(args => args[0] === "create" && args[1] === "-f"));
  assert.ok(!calls.some(args => args[0] === "apply" && args[1] === "-k"));
});
