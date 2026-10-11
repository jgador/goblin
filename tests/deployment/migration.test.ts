import { environmentVariables as Env } from "../../config/environment.mjs";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { goblinctl } from "../support/goblinctl.js";

test("migration retry observes an interrupted job and replaces only a terminal owned failure", async (t) => {
    const root = await mkdtemp(join(tmpdir(), "goblin-migration-recovery-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const bin = join(root, "bin");
    await mkdir(bin);
    await writeFile(join(bin, "package.json"), '{"type":"commonjs"}\n');
    const image = "localhost/goblin-auth:test";
    const job = JSON.parse(
        execFileSync(goblinctl, ["internal", "migration-job", image], {
            encoding: "utf8",
        }),
    );
    await writeFile(
        join(bin, "kubectl"),
        `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const file = name => path.join(process.env["${Env.GOBLIN_MIGRATION_TEST.name}"], name);
fs.appendFileSync(file('calls'), JSON.stringify(args) + '\\n');
if (args[0] === 'get' && fs.existsSync(file('job'))) process.stdout.write(fs.readFileSync(file('job')));
if (args[0] === 'create') { fs.writeFileSync(file('job'), fs.readFileSync(0)); process.stdout.write('job/goblin-schema'); }
if (args[0] === 'wait' && process.env["${Env.GOBLIN_MIGRATION_WAIT.name}"] === 'fail') process.exit(1);
if (args[0] === 'delete') fs.unlinkSync(file('job'));
`,
        { mode: 0o700 },
    );
    const run = (wait = "complete") =>
        execFileSync("bash", ["deploy/postgres/migrate.sh", image], {
            env: {
                ...process.env,
                [Env.PATH.name]: `${bin}:${process.env[Env.PATH.name]}`,
                [Env.GOBLINCTL.name]: goblinctl,
                [Env.GOBLIN_MIGRATION_TEST.name]: root,
                [Env.GOBLIN_MIGRATION_WAIT.name]: wait,
            },
            stdio: ["ignore", "pipe", "pipe"],
        });
    const calls = async () =>
        (await readFile(join(root, "calls"), "utf8"))
            .trim()
            .split("\n")
            .map((line) => JSON.parse(line) as string[]);
    await writeFile(join(root, "job"), JSON.stringify(job));
    assert.throws(() => run("fail"));
    assert.ok(
        !(await calls()).some((args) => ["create", "delete"].includes(args[0])),
    );
    run();
    assert.ok(!(await calls()).some((args) => args[0] === "create"));
    job.status = { conditions: [{ type: "Failed", status: "True" }] };
    await writeFile(join(root, "job"), JSON.stringify(job));
    await writeFile(join(root, "calls"), "");
    run();
    const retried = await calls();
    assert.ok(
        retried.findIndex((args) => args[0] === "delete") <
            retried.findIndex((args) => args[0] === "create"),
    );
    job.spec.template.spec.containers[0].image =
        "localhost/goblin-auth:different";
    await writeFile(join(root, "job"), JSON.stringify(job));
    await writeFile(join(root, "calls"), "");
    assert.throws(() => run(), /ownership or image differs/);
    assert.ok(
        !(await calls()).some((args) => ["create", "delete"].includes(args[0])),
    );
});
