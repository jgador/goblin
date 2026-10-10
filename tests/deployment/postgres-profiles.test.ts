import test from "node:test";
import { createServer } from "node:net";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
    mkdir,
    mkdtemp,
    readFile,
    rm,
    stat,
    symlink,
    writeFile,
} from "node:fs/promises";
import { join, resolve } from "node:path";
import { environmentVariables as Env } from "../../config/environment.mjs";
import { goblinctl } from "../support/goblinctl.js";

const artifacts = resolve(".artifacts/pg");
let certificates: string;
test.before(async () => {
    await mkdir(artifacts, { recursive: true });
    certificates = await mkdtemp(join(artifacts, "certs-"));
    const openssl = (...args: string[]) =>
        execFileSync("openssl", args, { cwd: certificates, stdio: "ignore" });
    openssl(
        "req",
        "-x509",
        "-newkey",
        "rsa:2048",
        "-nodes",
        "-keyout",
        "ca.key",
        "-out",
        "ca.crt",
        "-days",
        "2",
        "-subj",
        "/CN=Fictional PostgreSQL test CA",
    );
    await writeFile(
        join(certificates, "client.ext"),
        "extendedKeyUsage=clientAuth\n",
    );
    for (const role of ["app", "admin", "renewed", "expired"]) {
        openssl(
            "req",
            "-new",
            "-newkey",
            "rsa:2048",
            "-nodes",
            "-keyout",
            `${role}.key`,
            "-out",
            `${role}.csr`,
            "-subj",
            `/CN=goblin_${role === "admin" ? "admin" : "app"}`,
        );
        openssl(
            "x509",
            "-req",
            "-in",
            `${role}.csr`,
            "-CA",
            "ca.crt",
            "-CAkey",
            "ca.key",
            "-CAcreateserial",
            "-out",
            `${role}.crt`,
            "-days",
            role === "expired" ? "-1" : "2",
            "-extfile",
            "client.ext",
        );
    }
});
test.after(async () => {
    await rm(certificates, { recursive: true, force: true });
});

async function fixture(t: test.TestContext) {
    const freePort = async () => {
        const server = createServer();
        await new Promise<void>((resolve) =>
            server.listen(0, "127.0.0.1", resolve),
        );
        const address = server.address();
        assert.ok(address && typeof address !== "string");
        await new Promise<void>((resolve, reject) =>
            server.close((error) => (error ? reject(error) : resolve())),
        );
        return address.port;
    };
    const ports = [await freePort(), await freePort(), await freePort()];
    const root = await mkdtemp(join(artifacts, "p-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const bin = join(root, "bin");
    await mkdir(bin);
    await writeFile(join(bin, "package.json"), '{"type":"commonjs"}\n');
    await writeFile(
        join(root, "state.json"),
        JSON.stringify({
            cluster: "local-cluster",
            volume: "retained-volume",
            certificates,
        }),
    );
    const script = `#!${process.execPath}
const fs = require('node:fs');
const path = require('node:path');
const {spawnSync} = require('node:child_process');
const root = process.env[${JSON.stringify(Env.GOBLIN_POSTGRES_SETUP_TEST.name)}];
const state = JSON.parse(fs.readFileSync(path.join(root, 'state.json')));
const name = path.basename(process.argv[1]);
let args = process.argv.slice(2);
const file = n => path.join(root,n);
const load = n => fs.existsSync(file(n)) ? JSON.parse(fs.readFileSync(file(n))) : {};
const save = (n,v) => fs.writeFileSync(file(n),JSON.stringify(v));
fs.appendFileSync(file('calls.jsonl'), JSON.stringify([name,...args])+'\\n');
if (name === 'sudo') { if(args[0]==='-n') args.shift(); const p=spawnSync(args[0],args.slice(1),{stdio:'inherit'}); process.exit(p.status ?? 1); }
if (name === 'k3s') {
    const ca=fs.readFileSync(path.join(state.certificates,'ca.crt'));
    if (state.deny) process.exit(1);
    if (args.includes('namespace')) process.stdout.write(state.cluster);
    else if (args.includes('pvc')) process.stdout.write(state.volume);
    else if (args.includes('goblin-postgres-ca')) process.stdout.write(state.changedCa ? Buffer.from('different CA').toString('base64') : ca.toString('base64'));
    else {
        const role=args.includes('goblin-postgres-admin-tls') ? 'admin' : (state.certificate ?? 'app');
        const data={ 'ca.crt':ca.toString('base64'), 'tls.crt':fs.readFileSync(path.join(state.certificates,role+'.crt')).toString('base64'), 'tls.key':fs.readFileSync(path.join(state.certificates,(state.badKey ? 'admin' : role)+'.key')).toString('base64') };
        if (state.incomplete) delete data['tls.key'];
        process.stdout.write(JSON.stringify({data}));
    }
} else if(name==='psql') {
    const connection=args[args.indexOf('--dbname')+1];
    const port=Number(/port=(\\d+)/.exec(connection)[1]);
    if (state.failLogin || state.failLoginPort===port) process.exit(1);
    const role=/user=(\\w+)/.exec(connection)[1];
    process.stdout.write('goblin|'+role+'|'+(state.noTls?'false':'true')+'\\n');
} else if(name==='systemd-run') {
    if(state.failStart) process.exit(1);
    const units=load('units.json');
    const unit=args.find(a=>a.startsWith('--unit=')).slice(7);
    const listen=args.filter(a=>a.startsWith('--socket-property=ListenStream=')).map(a=>a.split('=').at(-1));
    units[unit+'.socket']={Listen:listen.map(a=>a+' (Stream)').join(' ')};
    units[unit+'.service']={ExecStart:'{ argv[]=/usr/lib/systemd/systemd-socket-proxyd 127.0.0.1:5432 ; }'};
    save('units.json',units);
} else if(name==='systemctl') {
    const units=load('units.json'); const unit=args.at(-1);
    if(args.includes('show')) {
        const prop=args.find(a=>a.startsWith('--property=')).split('=')[1];
        process.stdout.write(prop==='LoadState' ? (units[unit]?'loaded':'not-found') : units[unit]?.[prop] ?? '');
    } else if(args.includes('is-active')) process.exit(units[unit]?0:3);
    else if(args.includes('stop')) { delete units[unit]; save('units.json',units); }
} else if(name==='ssh') {
    if(args.includes('-S')) {
        const control=args[args.indexOf('-S')+1];
        if(args.includes('check')) process.exit(fs.existsSync(control)?0:1);
        if(args.includes('exit')) fs.rmSync(control,{force:true});
        else if(args.includes('-fNT')) {
            if(state.failStart) process.exit(1);
            fs.writeFileSync(control,JSON.stringify(args));
        }
    } else {
        const p=spawnSync('bash',['-c',args.at(-1)],{stdio:'inherit'}); process.exit(p.status ?? 1);
    }
} else if(name==='powershell.exe') {
    if(state.windowsUnavailable) process.exit(1);
    const payload=JSON.parse(fs.readFileSync(0,'utf8'));
    save('windows-payload.json',payload);
    fs.appendFileSync(file('windows-exports'),'1');
    process.stdout.write('C:\\\\Users\\\\Example\\\\AppData\\\\Local\\\\Goblin\\\\postgres\\\\'+payload.profile+'\\\\'+payload.role+'\\\\connection.json');
} else if(name==='capture') {
    const variables=${JSON.stringify([Env.ConnectionStrings__Goblin.name, Env.ConnectionStrings__GoblinAdmin.name, Env.GOBLIN_TEST_POSTGRES_APP.name, Env.GOBLIN_TEST_POSTGRES_ADMIN.name])};
    save('child.json',Object.fromEntries(variables.filter(v=>process.env[v]).map(v=>[v,process.env[v]])));
}
`;
    for (const name of [
        "sudo",
        "k3s",
        "psql",
        "systemctl",
        "systemd-run",
        "ssh",
        "powershell.exe",
        "capture",
    ])
        await writeFile(join(bin, name), script, { mode: 0o700 });
    const run = (...args: string[]) =>
        execFileSync(goblinctl, ["db", ...args], {
            cwd: root,
            env: {
                ...process.env,
                [Env.SUDO_UID.name]: "",
                [Env.SUDO_GID.name]: "",
                [Env.HOME.name]: root,
                [Env.XDG_CONFIG_HOME.name]: join(root, "c"),
                [Env.PATH.name]: `${bin}:${process.env[Env.PATH.name]}`,
                [Env.GOBLIN_POSTGRES_SETUP_TEST.name]: root,
            },
            encoding: "utf8",
            stdio: ["ignore", "pipe", "pipe"],
            timeout: 20_000,
        });
    const change = async (updates: object) => {
        const path = join(root, "state.json");
        await writeFile(
            path,
            JSON.stringify({
                ...JSON.parse(await readFile(path, "utf8")),
                ...updates,
            }),
        );
    };
    const profile = (name = "wsl") => join(root, "c/goblin/postgres", name);
    const calls = async (): Promise<string[][]> =>
        (await readFile(join(root, "calls.jsonl"), "utf8"))
            .trim()
            .split("\n")
            .map((l) => JSON.parse(l));
    return { root, run, change, profile, calls, ports };
}

test("profiles work outside the checkout, verify both local ports, and preserve their port across renewal", async (t) => {
    const f = await fixture(t);
    f.run("connect", "wsl", "--local", "--forward-port", String(f.ports[0]));
    const saved = JSON.parse(
        await readFile(join(f.profile(), "profile.json"), "utf8"),
    );
    assert.equal(saved.server_port, 5432);
    assert.equal(saved.forward_port, f.ports[0]);
    assert.equal((await stat(f.profile())).mode & 0o777, 0o700);
    assert.equal(
        (await stat(join(f.profile(), "app/tls.key"))).mode & 0o777,
        0o600,
    );
    await assert.rejects(stat(join(f.profile(), "admin")));
    const original = await readFile(join(f.profile(), "app/tls.key"));
    await f.change({ certificate: "renewed" });
    f.run("refresh", "wsl");
    assert.notDeepEqual(
        await readFile(join(f.profile(), "app/tls.key")),
        original,
    );
    assert.deepEqual(
        JSON.parse(await readFile(join(f.profile(), "profile.json"), "utf8")),
        saved,
    );
    const calls = await f.calls();
    assert.equal(calls.filter((c) => c[0] === "systemd-run").length, 1);
    const logins = calls.filter((c) => c[0] === "psql").map((c) => c.join(" "));
    assert.ok(logins.some((c) => c.includes("port=5432 ")));
    assert.ok(logins.some((c) => c.includes(`port=${f.ports[0]} `)));
    assert.ok(
        calls
            .filter((c) => c[0] === "k3s")
            .every((c) => c.includes("/etc/rancher/k3s/k3s.yaml")),
    );
    assert.ok(!calls.some((c) => c.includes("goblin-postgres-admin-tls")));
    f.run("disconnect", "wsl");
    f.run("disconnect", "wsl");
    f.run("connect", "wsl");
});

test("identity changes and invalid renewals retain the saved certificates and endpoint", async (t) => {
    const f = await fixture(t);
    f.run("connect", "wsl", "--local");
    const key = await readFile(join(f.profile(), "app/tls.key"));
    const settings = await readFile(join(f.profile(), "profile.json"));
    for (const changed of [
        { cluster: "another-cluster" },
        { volume: "another-volume" },
        { changedCa: true },
        { certificate: "expired" },
        { badKey: true },
        { incomplete: true },
        { deny: true },
        { noTls: true },
    ]) {
        await f.change(changed);
        assert.throws(() => f.run("refresh", "wsl"));
        assert.deepEqual(await readFile(join(f.profile(), "app/tls.key")), key);
        assert.deepEqual(
            await readFile(join(f.profile(), "profile.json")),
            settings,
        );
        await f.change({
            cluster: "local-cluster",
            volume: "retained-volume",
            changedCa: false,
            certificate: "app",
            badKey: false,
            incomplete: false,
            deny: false,
            noTls: false,
        });
    }
});

test("a failed forwarded login cleans up first connections and restores a prior port", async (t) => {
    const f = await fixture(t);
    await f.change({ failLoginPort: f.ports[0] });
    assert.throws(() =>
        f.run(
            "connect",
            "wsl",
            "--local",
            "--forward-port",
            String(f.ports[0]),
        ),
    );
    assert.deepEqual(
        JSON.parse(await readFile(join(f.root, "units.json"), "utf8")),
        {},
    );
    await assert.rejects(stat(join(f.profile(), "profile.json")));
    f.run("disconnect", "wsl");
    await f.change({ failLoginPort: 0 });
    f.run("connect", "wsl", "--local", "--forward-port", String(f.ports[0]));
    await f.change({ failLoginPort: f.ports[2] });
    assert.throws(() =>
        f.run("connect", "wsl", "--forward-port", String(f.ports[2])),
    );
    assert.equal(
        JSON.parse(await readFile(join(f.profile(), "profile.json"), "utf8"))
            .forward_port,
        f.ports[0],
    );
    assert.match(
        await readFile(join(f.root, "units.json"), "utf8"),
        new RegExp(`127\\.0\\.0\\.1:${f.ports[0]}`),
    );
});

test("WSL and Azure profiles coexist, SSH tunnels are reused, and Windows exports stay explicit", async (t) => {
    const f = await fixture(t);
    f.run("connect", "wsl", "--local", "--forward-port", String(f.ports[0]));
    f.run(
        "connect",
        "azure-dev",
        "--ssh",
        "developer@azure.example",
        "--forward-port",
        String(f.ports[1]),
    );
    f.run("connect", "azure-dev");
    assert.equal(
        (await f.calls()).filter((c) => c[0] === "ssh" && c.includes("-fNT"))
            .length,
        1,
    );
    f.run("export", "wsl", "--client", "windows", "--role", "app");
    const payload = JSON.parse(
        await readFile(join(f.root, "windows-payload.json"), "utf8"),
    );
    assert.equal(payload.port, f.ports[0]);
    assert.equal(payload.role, "app");
    assert.equal(payload.profile, "wsl");
    assert.equal(await readFile(join(f.root, "windows-exports"), "utf8"), "1");
    await f.change({ windowsUnavailable: true });
    f.run("refresh", "wsl");
    assert.throws(() => f.run("export", "wsl", "--client", "windows"));
    f.run("disconnect", "azure-dev");
    f.run("disconnect", "azure-dev");
    f.run("connect", "azure-dev");
    assert.equal(
        (await f.calls()).filter((c) => c[0] === "ssh" && c.includes("-fNT"))
            .length,
        2,
    );
});

test("selected roles reach child commands and database tests only opt in explicitly", async (t) => {
    const f = await fixture(t);
    f.run("connect", "wsl", "--local");
    f.run("run", "wsl", "--", "capture");
    let env = JSON.parse(await readFile(join(f.root, "child.json"), "utf8"));
    assert.deepEqual(Object.keys(env), [Env.ConnectionStrings__Goblin.name]);
    assert.match(
        env[Env.ConnectionStrings__Goblin.name],
        /Port=5432;.*Username=goblin_app;/,
    );
    f.run("run", "wsl", "--role", "admin", "--", "capture");
    env = JSON.parse(await readFile(join(f.root, "child.json"), "utf8"));
    assert.deepEqual(Object.keys(env), [
        Env.ConnectionStrings__GoblinAdmin.name,
    ]);
    f.run("run", "wsl", "--role", "both", "--usage", "tests", "--", "capture");
    env = JSON.parse(await readFile(join(f.root, "child.json"), "utf8"));
    assert.equal(
        env[Env.GOBLIN_TEST_POSTGRES_APP.name],
        env[Env.ConnectionStrings__Goblin.name],
    );
    assert.equal(
        env[Env.GOBLIN_TEST_POSTGRES_ADMIN.name],
        env[Env.ConnectionStrings__GoblinAdmin.name],
    );
});

test("profile paths reject traversal and symlinks without writing credentials", async (t) => {
    const f = await fixture(t);
    assert.throws(() => f.run("connect", "../escape", "--local"));
    await mkdir(join(f.root, "c/goblin/postgres"), { recursive: true });
    await mkdir(join(f.root, "unrelated"));
    await symlink(join(f.root, "unrelated"), f.profile());
    assert.throws(() => f.run("connect", "wsl", "--local"));
    await assert.rejects(stat(join(f.root, "unrelated/profile.json")));
});

test("occupied forwarding ports preserve the old listener and incomplete generations are recoverable", async (t) => {
    const f = await fixture(t);
    f.run("connect", "wsl", "--local", "--forward-port", String(f.ports[0]));
    const server = createServer();
    await new Promise<void>((resolve) =>
        server.listen(f.ports[2], "127.0.0.1", resolve),
    );
    try {
        assert.throws(
            () => f.run("connect", "wsl", "--forward-port", String(f.ports[2])),
            /occupied/,
        );
    } finally {
        await new Promise<void>((resolve, reject) =>
            server.close((error) => (error ? reject(error) : resolve())),
        );
    }
    const abandoned = join(f.profile(), ".refresh-interrupted/app");
    await mkdir(abandoned, { recursive: true });
    await writeFile(join(abandoned, "tls.key"), "abandoned test generation");
    f.run("refresh", "wsl");
    await assert.rejects(stat(abandoned));
    assert.equal(
        JSON.parse(await readFile(join(f.profile(), "profile.json"), "utf8"))
            .forward_port,
        f.ports[0],
    );
});

test(
    "Windows filesystem export validates identity, private ACLs and interrupted replacement",
    { skip: process.env[Env.GOBLIN_TEST_WINDOWS_EXPORT.name] !== "true" },
    async () => {
        const script = await readFile(
            "tools/goblinctl/src/postgres/windows-export.ps1",
            "utf8",
        );
        const name = `test-export-${process.pid}-${Date.now()}`;
        const payload = {
            profile: name,
            role: "app",
            port: 55432,
            ca: await readFile(join(certificates, "ca.crt"), "utf8"),
            certificate: await readFile(join(certificates, "app.crt"), "utf8"),
            key: await readFile(join(certificates, "app.key"), "utf8"),
            identity: {
                cluster: "test-cluster",
                volume: "test-volume",
                ca_sha256: "test-ca",
            },
        };
        const ps = (command: string, input?: object) =>
            execFileSync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", command],
                {
                    input: input ? JSON.stringify(input) : undefined,
                    encoding: "utf8",
                    stdio: ["pipe", "pipe", "pipe"],
                    timeout: 20000,
                },
            );
        const run = (value = payload) => ps(script, value);
        const prefix = `$ErrorActionPreference='Stop'; $p=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Goblin\\postgres\\${name}'; `;
        try {
            const destination = run().trim();
            assert.ok(destination.endsWith(`${name}\\app\\connection.json`));
            const result = JSON.parse(
                ps(
                    prefix +
                        `$c=Get-Content -Raw -LiteralPath (Join-Path $p 'app\\connection.json') | ConvertFrom-Json; $a=Get-Acl -LiteralPath (Join-Path $p 'app\\tls.key'); @{port=$c.port; certificate=$c.sslcert; rules=@($a.Access).Count; inherited=@($a.Access | Where-Object IsInherited).Count; ownerMatches=($a.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value); protected=(Get-Acl -LiteralPath (Join-Path $p 'app')).AreAccessRulesProtected} | ConvertTo-Json`,
                ),
            );
            assert.equal(result.port, 55432);
            assert.equal(result.rules, 1);
            assert.equal(result.protected, true);
            assert.equal(result.ownerMatches, true);
            ps(
                prefix +
                    `[IO.File]::Move((Join-Path $p 'identity.json'),(Join-Path $p '.saved-identity'))`,
            );
            assert.throws(() => run({ ...payload, port: 55433 }));
            const unchanged = JSON.parse(
                ps(
                    prefix +
                        `@{port=(Get-Content -Raw -LiteralPath (Join-Path $p 'app\\connection.json') | ConvertFrom-Json).port; identityExists=(Test-Path -LiteralPath (Join-Path $p 'identity.json'))} | ConvertTo-Json`,
                ),
            );
            assert.equal(unchanged.port, 55432);
            assert.equal(unchanged.identityExists, false);
            ps(
                prefix +
                    `[IO.File]::Move((Join-Path $p '.saved-identity'),(Join-Path $p 'identity.json'))`,
            );
            assert.throws(() =>
                run({
                    ...payload,
                    identity: { ...payload.identity, volume: "other-database" },
                }),
            );
            // Simulate a crash between backing up the old role directory and publishing its replacement.
            ps(
                prefix +
                    `[IO.Directory]::Move((Join-Path $p 'app'),(Join-Path $p '.previous-app'))`,
            );
            run({ ...payload, port: 55434 });
            assert.equal(
                ps(
                    prefix +
                        `(Get-Content -Raw -LiteralPath (Join-Path $p 'app\\connection.json') | ConvertFrom-Json).port`,
                ).trim(),
                "55434",
            );
        } finally {
            ps(
                prefix +
                    `if(Test-Path -LiteralPath $p){Remove-Item -LiteralPath $p -Recurse -Force}`,
            );
        }
    },
);
