import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
    cp,
    mkdir,
    mkdtemp,
    readFile,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

async function fixture() {
    const root = await mkdtemp(join(tmpdir(), 'goblin-postgres-"quoted;-'));
    const bin = join(root, "bin");
    await mkdir(bin);
    await cp("deploy/postgres", join(root, "deploy/postgres"), {
        recursive: true,
    });
    const web = join(root, "backend/src/Goblin.Web");
    await mkdir(web, { recursive: true });
    await writeFile(
        join(web, "appsettings.json"),
        JSON.stringify({
            ConnectionStrings: {
                Goblin: "Host=old-database;Username=goblin_app;Password=old-test-value",
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
const root = process.env.GOBLIN_POSTGRES_SETUP_TEST;
const file = name => path.join(root, name);
fs.appendFileSync(file('calls.jsonl'), JSON.stringify(args) + '\\n');
const secret = (name, values) => {
  if (!fs.existsSync(file(name))) fs.writeFileSync(file(name), JSON.stringify({
    metadata: { name }, stringData: values,
  }));
};
if (args[0] === 'get') {
  if (process.env.GOBLIN_POSTGRES_SETUP_DENY === 'true') process.exit(1);
  const names = args.slice(2, args.indexOf('-n'));
  if (args.includes('json')) {
    const items = names.map(name => {
      const value = JSON.parse(fs.readFileSync(file(name), 'utf8'));
      return { metadata: value.metadata, data: Object.fromEntries(Object.entries(value.stringData).map(
        ([key, data]) => [key, Buffer.from(data).toString('base64')])) };
    });
    process.stdout.write(JSON.stringify({items}));
  } else for (const name of names) {
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
} else if (args[0] === 'wait' && process.env.GOBLIN_POSTGRES_CERT_FAILED === 'true') process.exit(1);
else if (args[0] === 'apply' && args[1] === '-k') fs.writeFileSync(file('goblin-postgres-data'), 'PVC exists');
`,
        { mode: 0o700 },
    );
    const run = (environment: Record<string, string | undefined> = {}) =>
        execFileSync(
            "bash",
            [join(root, "deploy/postgres/setup.sh"), "--port", "55432"],
            {
                env: {
                    ...process.env,
                    PATH: `${bin}:${process.env.PATH}`,
                    GOBLIN_POSTGRES_SETUP_TEST: root,
                    ...environment,
                },
                encoding: "utf8",
                stdio: ["ignore", "pipe", "pipe"],
                timeout: 15_000,
            },
        );
    const calls = async (): Promise<string[][]> =>
        (await readFile(join(root, "calls.jsonl"), "utf8"))
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line));
    return { root, run, calls };
}

test("certificate setup removes passwords, protects exported keys, and preserves identities/data on rerun", async (t) => {
    const { root, run, calls } = await fixture();
    t.after(() => rm(root, { recursive: true, force: true }));
    const first = run();
    const webPath = join(root, "backend/src/Goblin.Web/appsettings.json");
    const toolPath = join(
        root,
        "backend/tools/Goblin.Database/appsettings.json",
    );
    const webText = await readFile(webPath, "utf8");
    const toolText = await readFile(toolPath, "utf8");
    const web = JSON.parse(webText);
    const tooling = JSON.parse(toolText);
    assert.deepEqual(web.Logging, { LogLevel: { Default: "Warning" } });
    assert.match(
        web.ConnectionStrings.Goblin,
        /Host=goblin-postgres;Port=5432;.*Username=goblin_app;SSL Mode=VerifyFull;/,
    );
    assert.match(
        web.ConnectionStrings.Goblin,
        /SSL Key="\/etc\/goblin-postgres\/tls.key"/,
    );
    assert.match(
        tooling.ConnectionStrings.GoblinAdmin,
        /Host=localhost;Port=55432;.*Username=goblin_admin;SSL Mode=VerifyFull;/,
    );
    assert.ok(
        tooling.ConnectionStrings.Goblin.includes(root.replaceAll('"', '""')),
    );
    assert.doesNotMatch(
        webText + toolText,
        /Password=|Trust Server Certificate|private-key/,
    );
    for (const role of ["app", "admin"]) {
        const directory = join(root, ".goblin-postgres", role);
        assert.equal((await stat(directory)).mode & 0o777, 0o700);
        assert.equal(
            (await stat(join(directory, "tls.key"))).mode & 0o777,
            0o600,
        );
        assert.equal(
            await readFile(join(directory, "tls.key"), "utf8"),
            role + "-private-key",
        );
    }
    assert.equal((await stat(toolPath)).mode & 0o777, 0o600);
    const adminSecret = await readFile(
        join(root, "goblin-postgres-admin"),
        "utf8",
    );
    const ca = await readFile(join(root, "goblin-postgres-ca"), "utf8");
    const second = run();
    assert.equal(
        await readFile(join(root, "goblin-postgres-admin"), "utf8"),
        adminSecret,
    );
    assert.equal(await readFile(join(root, "goblin-postgres-ca"), "utf8"), ca);
    assert.equal(await readFile(webPath, "utf8"), webText);
    assert.equal(await readFile(toolPath, "utf8"), toolText);
    const output = first + second + JSON.stringify(await calls());
    assert.ok(!output.includes(JSON.parse(adminSecret).stringData.password));
    assert.doesNotMatch(
        output,
        /app-private-key|admin-private-key|CA-private-key/,
    );
    assert.equal(
        (await calls()).filter(
            (args) => args[0] === "create" && args[2] === "-",
        ).length,
        1,
    );
});

test("setup fails before changing PostgreSQL when certificate issuance or API access fails", async (t) => {
    for (const environment of [
        { GOBLIN_POSTGRES_SETUP_DENY: "true" },
        { GOBLIN_POSTGRES_CERT_FAILED: "true" },
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

test("incomplete exported client credentials do not overwrite settings or keys", async (t) => {
    const { root, run } = await fixture();
    t.after(() => rm(root, { recursive: true, force: true }));
    run();
    const file = join(root, "backend/tools/Goblin.Database/appsettings.json");
    const before = await readFile(file, "utf8");
    assert.throws(() =>
        execFileSync(
            "python3",
            [join(root, "deploy/postgres/write-appsettings.py")],
            {
                input: JSON.stringify({ items: [] }),
                stdio: ["pipe", "pipe", "pipe"],
            },
        ),
    );
    assert.equal(await readFile(file, "utf8"), before);
    assert.equal(
        await readFile(join(root, ".goblin-postgres/app/tls.key"), "utf8"),
        "app-private-key",
    );
});

test("connecting an existing Sandbox preserves its image, origin, storage and other containers", async (t) => {
    const { root, run } = await fixture();
    t.after(() => rm(root, { recursive: true, force: true }));
    run();
    const original = {
        containers: [
            { name: "sidecar", image: "sidecar-image" },
            {
                name: "auth",
                image: "existing-image",
                env: [
                    {
                        name: "GOBLIN_PUBLIC_ORIGIN",
                        value: "http://existing.test",
                    },
                ],
                volumeMounts: [{ name: "data", mountPath: "/data" }],
            },
        ],
        volumes: [
            { name: "data", persistentVolumeClaim: { claimName: "keep-me" } },
        ],
    };
    const patch = (spec: object) =>
        execFileSync(
            "python3",
            [join(root, "deploy/postgres/configure-app.py")],
            {
                input: JSON.stringify({ spec: { podTemplate: { spec } } }),
                encoding: "utf8",
            },
        );
    const updated = JSON.parse(patch(original)).spec.podTemplate.spec;
    assert.deepEqual(updated.containers[0], original.containers[0]);
    assert.equal(updated.containers[1].image, "existing-image");
    assert.deepEqual(
        updated.containers[1].env[0],
        original.containers[1]!.env![0],
    );
    assert.deepEqual(updated.volumes[0], original.volumes[0]);
    assert.equal(
        updated.volumes[1].secret.secretName,
        "goblin-postgres-app-tls",
    );
    assert.doesNotMatch(JSON.stringify(updated), /admin-tls|Password=/);
    assert.equal(patch(updated), "");
});
