import { environmentVariables as Env } from "../../config/environment.mjs";
import { goblinctl } from "../support/goblinctl.js";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

async function fixture() {
    const root = await mkdtemp(join(tmpdir(), 'goblin-postgres-"quoted;-'));
    const bin = join(root, "bin");
    await mkdir(bin);
    await writeFile(join(bin, "package.json"), '{"type":"commonjs"}\n');
    await cp("deploy/postgres", join(root, "deploy/postgres"), {
        recursive: true,
    });
    const web = join(root, "backend/src/Goblin.Web");
    await mkdir(web, { recursive: true });
    await writeFile(
        join(web, "appsettings.json"),
        JSON.stringify({
            ConnectionStrings: {
                Goblin: "Host=unused;Username=goblin_app;SSL Mode=VerifyFull",
            },
            Logging: { LogLevel: { Default: "Warning" } },
        }),
    );
    await writeFile(
        join(bin, "kubectl"),
        `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const root = process.env["${Env.GOBLIN_POSTGRES_SETUP_TEST.name}"];
const file = name => path.join(root, name);
fs.appendFileSync(file('calls.jsonl'), JSON.stringify(args) + '\\n');
const secret = (name, values) => {
  if (!fs.existsSync(file(name))) fs.writeFileSync(file(name), JSON.stringify({
    metadata: { name }, stringData: values,
  }));
};
if (args[0] === 'get') {
  if (process.env["${Env.GOBLIN_POSTGRES_SETUP_DENY.name}"] === 'true') process.exit(1);
  const names = args.slice(2, args.indexOf('-n'));
  for (const name of names) {
    if (fs.existsSync(file(name))) process.stdout.write(args[1] + '/' + name + '\\n');
  }
} else if (args[0] === 'create' && args[1] === 'namespace') {
  process.stdout.write(JSON.stringify({kind: 'Namespace', metadata: {name: 'goblin'}}));
} else if (args[0] === 'create' && args[2] === 'deploy/postgres/verify.yaml') {
  process.stdout.write('job.batch/goblin-postgres-verify-test');
} else if (args[0] === 'create' && args[1] === '-f') {
  const value = JSON.parse(fs.readFileSync(0, 'utf8'));
  fs.writeFileSync(file(value.metadata.name), JSON.stringify(value));
} else if (args[0] === 'apply' && args[1] === '-f') {
  if (args[2] === '-') fs.readFileSync(0);
  else if (args[2].endsWith('/ca.yaml')) secret('goblin-postgres-ca', {'tls.key': 'CA-private-key-stays-in-cluster'});
  else if (args[2].endsWith('/certificates.yaml')) {
    for (const role of ['app', 'admin']) secret('goblin-postgres-' + role + '-tls', {
      'tls.crt': role + '-certificate', 'tls.key': role + '-private-key', 'ca.crt': 'public-CA',
    });
    secret('goblin-postgres-tls', {'tls.crt': 'server-certificate'});
  }
} else if (args[0] === 'wait' && process.env["${Env.GOBLIN_POSTGRES_CERT_FAILED.name}"] === 'true') process.exit(1);
else if (args[0] === 'apply' && args[1] === '-k') fs.writeFileSync(file('goblin-postgres-data'), 'PVC exists');
`,
        { mode: 0o700 },
    );
    const run = (environment: Record<string, string | undefined> = {}) =>
        execFileSync("bash", [join(root, "deploy/postgres/setup.sh")], {
            env: {
                ...process.env,
                [Env.PATH.name]: `${bin}:${process.env[Env.PATH.name]}`,
                [Env.GOBLIN_POSTGRES_SETUP_TEST.name]: root,
                [Env.GOBLINCTL.name]: goblinctl,
                ...environment,
            },
            encoding: "utf8",
            stdio: ["ignore", "pipe", "pipe"],
            timeout: 15_000,
        });
    const calls = async (): Promise<string[][]> =>
        (await readFile(join(root, "calls.jsonl"), "utf8"))
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
    return { root, run, calls };
}

test("database setup does not patch or restart the application", async (t) => {
    const { root, run, calls } = await fixture();
    t.after(() => rm(root, { recursive: true, force: true }));
    run();
    const requests = await calls();
    assert.ok(
        requests.some((args) => args.includes("statefulset/goblin-postgres")),
    );
    assert.ok(
        !requests.some(
            (args) =>
                args.includes("sandbox") ||
                args.includes("sandbox/app") ||
                args.includes("app=goblin-auth"),
        ),
    );
});

test("certificate setup retains cluster identities without exporting checkout credentials", async (t) => {
    const { root, run, calls } = await fixture();
    t.after(() => rm(root, { recursive: true, force: true }));
    const webPath = join(root, "backend/src/Goblin.Web/appsettings.json");
    const before = await readFile(webPath, "utf8");
    const first = run();
    const adminSecret = await readFile(
        join(root, "goblin-postgres-admin"),
        "utf8",
    );
    const ca = await readFile(join(root, "goblin-postgres-ca"), "utf8");
    const second = run();
    assert.equal(await readFile(webPath, "utf8"), before);
    await assert.rejects(
        readFile(join(root, "backend/tools/Goblin.Database/appsettings.json")),
    );
    assert.equal(
        await readFile(join(root, "goblin-postgres-admin"), "utf8"),
        adminSecret,
    );
    assert.equal(await readFile(join(root, "goblin-postgres-ca"), "utf8"), ca);
    assert.doesNotMatch(
        first + second + JSON.stringify(await calls()),
        /app-private-key|admin-private-key|CA-private-key/,
    );
    assert.ok(
        !(first + second).includes(JSON.parse(adminSecret).stringData.password),
    );
});

test("setup fails before changing PostgreSQL when certificate issuance or API access fails", async (t) => {
    for (const environment of [
        { [Env.GOBLIN_POSTGRES_SETUP_DENY.name]: "true" },
        { [Env.GOBLIN_POSTGRES_CERT_FAILED.name]: "true" },
    ]) {
        const { root, run, calls } = await fixture();
        t.after(() => rm(root, { recursive: true, force: true }));
        const before = await readFile(
            join(root, "backend/src/Goblin.Web/appsettings.json"),
            "utf8",
        );
        assert.throws(() => run(environment));
        assert.ok(
            !(await calls()).some(
                (args) => args[0] === "apply" && args[1] === "-k",
            ),
        );
        assert.equal(
            await readFile(
                join(root, "backend/src/Goblin.Web/appsettings.json"),
                "utf8",
            ),
            before,
        );
    }
});

test("setup refuses to replace a lost CA or initialization Secret for existing PostgreSQL data", async (t) => {
    for (const missing of ["goblin-postgres-ca", "goblin-postgres-admin"]) {
        const { root, run, calls } = await fixture();
        t.after(() => rm(root, { recursive: true, force: true }));
        run();
        await rm(join(root, missing));
        await writeFile(join(root, "calls.jsonl"), "");
        assert.throws(() => run(), /Restore/);
        assert.ok(
            !(await calls()).some(
                (args) => args[0] === "apply" && args[2] !== "-",
            ),
        );
        assert.ok(
            !(await calls()).some(
                (args) => args[0] === "create" && args[1] === "-f",
            ),
        );
    }
});
