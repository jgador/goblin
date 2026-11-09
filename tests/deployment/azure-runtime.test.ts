import { environmentVariables as Env } from "../../config/environment.mjs";
import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
    access,
    cp,
    mkdir,
    readFile,
    readdir,
    rm,
    stat,
    writeFile,
} from "node:fs/promises";
import { join } from "node:path";
import {
    bootstrap,
    fixtureDirectory,
    fakePassword,
} from "../support/azure-fixture.js";

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
    const root = await fixtureDirectory("goblin-concurrency-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword, "ready", {
        pullDelay: 400,
        synchronizeRuntimeStart: true,
    });
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
    // Image downloads start once k3s is running; dependent waits then cover startup only.
    for (const id of ["prefetch", "cert-manager", "sandbox"])
        assert.ok(start(id) >= end("k3s"));
    assert.ok(
        start("database") >= end("cert-manager") &&
            start("database") >= end("sandbox") &&
            start("database") >= end("prefetch"),
    );
    assert.ok(
        start("migrate") >= end("import") &&
            start("migrate") >= end("database"),
    );
    const pulls = (await readFile(join(root, "pull-events.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const addOn = (image: string) =>
        image.includes("/jetstack/") ||
        image.includes("/agent-sandbox/") ||
        image.includes("nginx/nginx-ingress:");
    let active = 0,
        maximum = 0;
    for (const event of pulls.filter((event) => !addOn(event.image))) {
        active += event.event === "start" ? 1 : -1;
        maximum = Math.max(maximum, active);
    }
    assert.equal(active, 0);
    assert.equal(pulls.length, 20);
    assert.ok(maximum > 1 && maximum <= 4);
    // Add-on images are present before their manifests are applied.
    const calls: string[][] = (
        await readFile(join(root, "k3s-requests.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const lastPull = (fragment: string) =>
        calls.findLastIndex(
            (args) =>
                args[0] === "crictl" &&
                args[1] === "pull" &&
                args[2].includes(fragment),
        );
    const rollout = (name: string) =>
        calls.findIndex((args) => args[1] === "rollout" && args.includes(name));
    assert.ok(lastPull("/jetstack/") < rollout("deployment/cert-manager"));
    assert.ok(lastPull("/agent-sandbox/") < rollout("deployment/sandbox"));
    assert.ok(!JSON.stringify(progress).includes("do-not-stream"));
    assert.match(JSON.stringify(progress), /installing runtime dependencies/);
});

test("k3s and its system images are verified, cached outside the cluster, and installed without another download", async (t) => {
    const root = await fixtureDirectory("goblin-k3s-cache-");
    t.after(() => rm(root, { recursive: true, force: true }));
    await bootstrap(root, fakePassword);
    const downloads: string[][] = (
        await readFile(join(root, "k3s-downloads.jsonl"), "utf8")
    )
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line));
    const urls = downloads.map((args) =>
        args.find((arg) => arg.startsWith("https://")),
    );
    assert.deepEqual(urls.sort(), [
        "https://github.com/k3s-io/k3s/releases/download/v1.36.5%2Bk3s1/k3s",
        "https://github.com/k3s-io/k3s/releases/download/v1.36.5%2Bk3s1/k3s-airgap-images-amd64.tar.zst",
    ]);
    // Transfers resume and fail only on stalls, never on a fixed total duration.
    for (const args of downloads) {
        assert.ok(args.includes("--continue-at"));
        assert.ok(args.includes("--speed-time"));
        assert.ok(!args.includes("--max-time"));
    }
    const cache = join(root, "var/cache/goblin/downloads");
    assert.deepEqual((await readdir(cache)).sort(), [
        "k3s-airgap-images-amd64-v1.36.5+k3s1.tar.zst",
        "k3s-v1.36.5+k3s1",
        "nginx-ingress-2.8.0.tgz",
        "nginx-ingress-template-5.7.0.tmpl",
    ]);
    assert.equal(
        await readFile(join(root, "k3s-installed-version"), "utf8"),
        "v1.36.5+k3s1\n",
    );
    const chart = await readFile(
        join(root, "var/lib/rancher/k3s/server/manifests/goblin-nginx.yaml"),
        "utf8",
    );
    assert.match(chart, /ingress-template: \|-/);
    assert.match(chart, /type: LoadBalancer/);
    assert.match(chart, /proxy_set_header Host .*\$http_host/);
    assert.match(chart, /grpc_set_header Host .*\$http_host/);
    assert.doesNotMatch(chart, /use-forwarded-headers:.*true/i);
    const ingress = await readFile(
        join(root, "var/lib/goblin/deploy/azure/platform/ingress.yaml"),
        "utf8",
    );
    assert.match(ingress, /nginx\.org\/websocket-services: "platform"/);
    assert.match(ingress, /nginx\.org\/proxy-http-version: "1\.1"/);
    const checks = (await readFile(join(root, "checksum-checks.jsonl"), "utf8"))
        .trim()
        .split("\n")
        .map((line) => JSON.parse(line) as string);
    for (const [checksum, name] of [
        [
            "d73847bcd3c5fccef0115b372e2f9a91f3032dc84bbf71518a4617565294d313",
            "k3s-v1.36.5+k3s1",
        ],
        [
            "7d939471907df2d4e537a9c308bf94173e453fff4a2e9e77e9ae92a64be3007f",
            "k3s-airgap-images-amd64-v1.36.5+k3s1.tar.zst",
        ],
    ])
        assert.ok(
            checks.some(
                (text) =>
                    text.startsWith(checksum) && text.trim().endsWith(name),
            ),
        );

    // A reset removes k3s and Goblin state but keeps the verified download cache.
    const second = await fixtureDirectory("goblin-k3s-cached-");
    t.after(() => rm(second, { recursive: true, force: true }));
    await mkdir(join(second, "var/cache/goblin"), { recursive: true });
    await cp(cache, join(second, "var/cache/goblin/downloads"), {
        recursive: true,
    });
    await writeFile(
        join(second, "var/cache/goblin/downloads/k3s-v1.36.0+k3s1"),
        "superseded release",
    );
    await bootstrap(second, fakePassword);
    await assert.rejects(access(join(second, "k3s-downloads.jsonl")));
    await assert.rejects(
        access(join(second, "var/cache/goblin/downloads/k3s-v1.36.0+k3s1")),
    );
    assert.equal(
        await readFile(join(second, "k3s-installed-version"), "utf8"),
        "v1.36.5+k3s1\n",
    );
});

test("installer waits for node registration after API readiness, including transient list failures", async (t) => {
    const root = await fixtureDirectory("goblin-node-delayed-");
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
    const root = await fixtureDirectory("goblin-sandbox-version-");
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
            args.includes(join(root, "var/lib/goblin/deploy/azure/platform")),
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
        const root = await fixtureDirectory("goblin-hostname-");
        t.after(() => rm(root, { recursive: true, force: true }));
        const output = await bootstrap(root, fakePassword, "ready", {
            hostname,
            sourceRef: "refs/heads/master",
        });
        const config = await readFile(
            join(
                root,
                "var/lib/goblin/deploy/azure/platform/kustomization.yaml",
            ),
            "utf8",
        );
        const ingress = await readFile(
            join(root, "var/lib/goblin/deploy/azure/platform/ingress.yaml"),
            "utf8",
        );
        assert.ok(config.includes(`value: https://${hostname}`));
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
        assert.ok(config.includes(`localhost/goblin-platform:${digest}`));
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
                    args.includes(`localhost/goblin-platform:${digest}`),
            ),
        );
        assert.ok(
            dockerCalls.some(
                (args) =>
                    args[1] === "save" &&
                    args.includes(`localhost/goblin-platform:${digest}`) &&
                    !args.includes("--format"),
            ),
        );
        await assert.rejects(
            access(join(root, "apt-requests.jsonl")),
            "an existing Docker installation should be reused",
        );
        assert.equal(
            await readFile(join(root, "var/lib/goblin/public-url"), "utf8"),
            `https://${hostname}\n`,
        );
        assert.ok(output.includes(`Goblin ready: https://${hostname}`));
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
                args.includes(
                    join(root, "var/lib/goblin/deploy/azure/platform"),
                ),
        );
        const restarted = calls.findIndex(
            (args) => args[1] === "delete" && args[2] === "pod",
        );
        const ready = calls.findIndex(
            (args) => args[1] === "wait" && args.includes("sandbox/platform"),
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
            downloads.some((args) =>
                args.includes(`${hostname}:443:10.20.0.4`),
            ),
            "the public probe selects a single IPv4 node address",
        );
    }
});

test("installer isolates Docker configuration from Desktop credentials, contexts and builders across retries", async (t) => {
    const root = await fixtureDirectory("goblin-docker-config-");
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
    const root = await fixtureDirectory("goblin-docker-");
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
