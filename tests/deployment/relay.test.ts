import { environmentVariables as Env } from "../../config/environment.mjs";
import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
    mkdir,
    mkdtemp,
    readFile,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import { join, resolve, basename } from "node:path";

// Real xtask commands and remote shell scripts, with SSH/Kubernetes/curl replaced
// at the infrastructure boundary. No Azure credentials or live cluster are used.
async function fixture() {
    await mkdir(".artifacts/relay-tests", { recursive: true });
    const root = resolve(await mkdtemp(".artifacts/relay-tests/case-"));
    const session = `relay-${basename(root)}`;
    const sessionPath = resolve(".artifacts", session);
    const bin = join(root, "bin");
    await mkdir(bin);
    await writeFile(join(bin, "package.json"), '{"type":"commonjs"}');
    await writeFile(
        join(root, "saved-key"),
        "fictional SSH fixture; never read by xtask",
    );
    const origin = "https://test.example.com";
    await mkdir(join(root, "remote/var/lib/goblin"), { recursive: true });
    await writeFile(
        join(root, "remote/var/lib/goblin/public-url"),
        origin + "\n",
    );
    const initial = {
        metadata: {
            resourceVersion: "1",
            annotations: {
                "nginx.org/ssl-redirect": "true",
                "nginx.org/websocket-services": "platform",
            },
        },
        spec: {
            ingressClassName: "nginx",
            tls: [
                {
                    hosts: ["test.example.com"],
                    secretName: "goblin-public-tls",
                },
            ],
            rules: [
                {
                    host: "test.example.com",
                    http: {
                        paths: [
                            {
                                path: "/.well-known/acme-challenge/active",
                                backend: {
                                    service: {
                                        name: "cm-acme-http-solver",
                                        port: { number: 8089 },
                                    },
                                },
                            },
                            {
                                path: "/",
                                backend: {
                                    service: {
                                        name: "platform",
                                        port: { number: 8787 },
                                    },
                                },
                            },
                        ],
                    },
                },
            ],
        },
    };
    await writeFile(join(root, "ingress.json"), JSON.stringify(initial));
    await writeFile(join(root, "resources.json"), "{}");
    const executable = `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const cp = require('node:child_process');
const assert = require('node:assert/strict');
const root = process.env[${JSON.stringify(Env.GOBLIN_RELAY_TEST_ROOT.name)}];
const name = path.basename(process.argv[1]);
const args = process.argv.slice(2);
const read = file => JSON.parse(fs.readFileSync(path.join(root, file), 'utf8'));
const write = (file, value) => fs.writeFileSync(path.join(root, file), JSON.stringify(value));
const ingress = () => read('ingress.json');
const upstream = () => ingress().spec.rules[0].http.paths.find(p => p.path === '/').backend.service.name;
fs.appendFileSync(path.join(root, 'calls.jsonl'), JSON.stringify({name,args}) + '\\n');
if (name === 'ssh') {
  if (args.includes('-M')) fs.writeFileSync(path.join(root, 'connected'), 'true');
  else if (args.includes('-O')) {
    const action = args[args.indexOf('-O') + 1];
    if (action === 'check' && !fs.existsSync(path.join(root, 'connected'))) process.exit(1);
    if (action === 'exit') fs.rmSync(path.join(root, 'connected'), {force:true});
  } else {
    const script = args.at(-1).replaceAll('/var/lib/goblin', path.join(root, 'remote/var/lib/goblin'));
    const result = cp.spawnSync('bash', ['-c', script], {stdio:'inherit'});
    process.exit(result.status ?? 1);
  }
} else if (name === 'id') process.stdout.write('1000\\n');
else if (name === 'sudo') {
  if (args[1] === 'install') {
    // Simulate remote ownership; the local test runner cannot chown to the VM's UID.
    assert.deepEqual(args.slice(2, -1), ['-d', '-m', '0700', '-o', '1000', '-g', '1000']);
    const directory = path.resolve(args.at(-1));
    assert.equal(path.dirname(directory), path.join(root, 'remote/var/lib/goblin/dev-relay'));
    assert.match(path.basename(directory), /^[a-f0-9]{32}$/);
    fs.mkdirSync(directory, {recursive:true, mode:0o700});
    fs.chmodSync(directory, 0o700);
  } else {
    const result = cp.spawnSync(args[1], args.slice(2), {stdio:'inherit'});
    process.exit(result.status ?? 1);
  }
} else if (name === 'curl') {
  const local = args.some(arg => arg.startsWith('http://127.0.0.1:'));
  if (!local) {
    // F5 serves no route when its WebSocket annotation names a non-backend service.
    const current = ingress();
    const services = current.spec.rules.flatMap(rule => rule.http.paths.map(p => p.backend.service.name));
    const websocket = current.metadata.annotations['nginx.org/websocket-services'];
    if (websocket && websocket.split(',').some(service => !services.includes(service))) process.exit(22);
  }
  if (!local && upstream() === 'goblin-dev-relay' && !fs.existsSync(path.join(root, 'connected'))) process.exit(22);
  process.stdout.write(JSON.stringify({keys:[{kid:local || upstream() === 'goblin-dev-relay' ? 'local-key' : 'azure-key'}]}));
} else if (name === 'k3s') {
  const resources = read('resources.json');
  if (args[1] === 'get' && args[2] === 'ingress') process.stdout.write(JSON.stringify(ingress()));
  else if (args[1] === 'get') {
    const identifiers = args.slice(2).filter(arg => /^(deployment|service|configmap)\\//.test(arg));
    for (const identifier of identifiers) {
      const item = resources[identifier];
      if (item) process.stdout.write(args.at(-1).startsWith('jsonpath=') ? item.metadata.annotations['goblin.dev/relay-owner'] ?? '' : identifier + '\\n');
    }
  } else if (args[1] === 'apply') {
    const list = JSON.parse(fs.readFileSync(0,'utf8'));
    for (const item of list.items) resources[item.kind.toLowerCase() + '/' + item.metadata.name] = item;
    write('resources.json', resources);
  } else if (args[1] === 'patch') {
    const current = ingress();
    const patch = JSON.parse(args[args.indexOf('-p') + 1]);
    for (const operation of patch) {
      const parts = operation.path.slice(1).split('/').map(p => p.replaceAll('~1','/').replaceAll('~0','~'));
      const parent = parts.slice(0,-1).reduce((o,p) => o[p], current);
      const key = parts.at(-1);
      if (operation.op === 'test') { if (JSON.stringify(parent[key]) !== JSON.stringify(operation.value)) process.exit(1); }
      else if (operation.op === 'remove') delete parent[key];
      else parent[key] = operation.value;
    }
    current.metadata.resourceVersion = String(Number(current.metadata.resourceVersion) + 1);
    write('ingress.json', current);
  } else if (args[1] === 'delete') { delete resources[args[2]]; write('resources.json',resources); }
}
`;
    for (const name of ["ssh", "sudo", "id", "curl", "k3s"])
        await writeFile(join(bin, name), executable, { mode: 0o755 });
    const run = (action: string, ...extra: string[]) =>
        spawnSync(
            resolve("target/debug/xtask"),
            ["relay", "--session", session, action, ...extra],
            {
                encoding: "utf8",
                env: {
                    ...process.env,
                    [Env.PATH.name]: `${bin}:${process.env[Env.PATH.name]}`,
                    [Env.GOBLIN_RELAY_TEST_ROOT.name]: root,
                },
                timeout: 20_000,
            },
        );
    const start = () =>
        run(
            "start",
            "--origin",
            origin,
            "--ssh",
            "goblinadmin@test.example.com",
            "--identity",
            join(root, "saved-key"),
        );
    return {
        root,
        sessionPath,
        initial,
        run,
        start,
        ingress: async () =>
            JSON.parse(await readFile(join(root, "ingress.json"), "utf8")),
        close: async () => {
            await rm(root, { recursive: true, force: true });
            await rm(sessionPath, { recursive: true, force: true });
        },
    };
}

test("relay CLI preserves ACME routing, forwards through a private socket, fails closed, and explicitly restores Azure", async (t) => {
    const f = await fixture();
    t.after(f.close);
    const started = f.start();
    assert.equal(started.status, 0, started.stderr);
    const active = await f.ingress();
    assert.equal(
        active.metadata.annotations["nginx.org/websocket-services"],
        "goblin-dev-relay",
    );
    assert.deepEqual(
        active.spec.rules[0].http.paths[0],
        f.initial.spec.rules[0].http.paths[0],
    );
    assert.equal(
        active.spec.rules[0].http.paths[1].backend.service.name,
        "goblin-dev-relay",
    );
    const resources = JSON.parse(
        await readFile(join(f.root, "resources.json"), "utf8"),
    );
    const prepared = JSON.parse(
        await readFile(join(f.sessionPath, "resources.json"), "utf8"),
    );
    const pod = prepared.items.find(
        (item: { kind: string }) => item.kind === "Deployment",
    ).spec.template.spec;
    assert.equal(pod.securityContext.runAsUser, 1000);
    assert.equal(pod.automountServiceAccountToken, false);
    assert.equal(pod.hostNetwork, undefined);
    assert.equal(
        pod.containers[0].securityContext.readOnlyRootFilesystem,
        true,
    );
    assert.match(
        pod.volumes[0].hostPath.path,
        /^\/var\/lib\/goblin\/dev-relay\/[a-f0-9]{32}$/,
    );
    assert.equal(
        (await stat(join(f.root, "remote", pod.volumes[0].hostPath.path)))
            .mode & 0o777,
        0o700,
    );
    const config = resources["configmap/goblin-dev-relay"].data["nginx.conf"];
    assert.match(config, /proxy_pass http:\/\/unix:\/relay\/upstream.sock:/);
    assert.match(config, /proxy_set_header X-Forwarded-Proto https/);
    assert.match(config, /proxy_set_header Authorization \$http_authorization/);
    assert.match(config, /proxy_buffering off/);
    assert.match(config, /proxy_set_header Upgrade \$http_upgrade/);
    assert.match(config, /proxy_set_header Connection \$connection_upgrade/);
    assert.equal(f.run("status").status, 0);
    await rm(join(f.root, "connected"));
    assert.notEqual(f.run("status").status, 0);
    assert.equal(
        (await f.ingress()).spec.rules[0].http.paths[1].backend.service.name,
        "goblin-dev-relay",
    );
    const stopped = f.run("stop");
    assert.equal(stopped.status, 0, stopped.stderr);
    assert.deepEqual((await f.ingress()).spec, f.initial.spec);
    assert.deepEqual(
        (await f.ingress()).metadata.annotations,
        f.initial.metadata.annotations,
    );
    assert.deepEqual(
        JSON.parse(await readFile(join(f.root, "resources.json"), "utf8")),
        {},
    );
    const calls = (await readFile(join(f.root, "calls.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const reverse = calls.find(
        (call) => call.name === "ssh" && call.args.includes("-R"),
    );
    assert.match(
        reverse.args[reverse.args.indexOf("-R") + 1],
        /^\/var\/lib\/goblin\/dev-relay\/[a-f0-9]{32}\/upstream.sock:127.0.0.1:8787$/,
    );
    assert.ok(
        calls
            .filter((call) => call.name === "ssh")
            .every(
                (call) =>
                    call.args.includes("/dev/null") &&
                    call.args.includes("IdentitiesOnly=yes"),
            ),
    );
});

test("relay cleanup rejects another session owner without changing routing or deleting resources", async (t) => {
    const f = await fixture();
    t.after(f.close);
    const started = f.start();
    assert.equal(started.status, 0, started.stderr);
    const ingress = await f.ingress();
    ingress.metadata.annotations["goblin.dev/relay-owner"] = "another-session";
    await writeFile(join(f.root, "ingress.json"), JSON.stringify(ingress));
    const resources = await readFile(join(f.root, "resources.json"), "utf8");
    assert.notEqual(f.run("stop").status, 0);
    assert.deepEqual(await f.ingress(), ingress);
    assert.equal(
        await readFile(join(f.root, "resources.json"), "utf8"),
        resources,
    );
});

test("relay cleanup preserves routing and resources if a proxy resource loses its owner", async (t) => {
    const f = await fixture();
    t.after(f.close);
    const started = f.start();
    assert.equal(started.status, 0, started.stderr);
    const ingress = await f.ingress();
    const resources = JSON.parse(
        await readFile(join(f.root, "resources.json"), "utf8"),
    );
    delete resources["service/goblin-dev-relay"].metadata.annotations[
        "goblin.dev/relay-owner"
    ];
    await writeFile(join(f.root, "resources.json"), JSON.stringify(resources));
    assert.notEqual(f.run("stop").status, 0);
    assert.deepEqual(await f.ingress(), ingress);
    assert.deepEqual(
        JSON.parse(await readFile(join(f.root, "resources.json"), "utf8")),
        resources,
    );
});
