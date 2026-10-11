import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { cp, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

const kubectl =
    spawnSync("k3s", ["--version"]).status === 0
        ? ["k3s", "kubectl"]
        : spawnSync("kubectl", ["version", "--client"]).status === 0
          ? ["kubectl"]
          : null;

test("short controller names preserve upstream images and RBAC references", async (t) => {
    if (!kubectl)
        return t.skip(
            "k3s or kubectl is required for offline Kustomize validation",
        );
    const root = await mkdtemp(join(tmpdir(), "goblin-names-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    await cp(
        "deploy/azure/setup/sandbox-kustomization.yaml",
        join(root, "kustomization.yaml"),
    );
    // A small offline fixture checks the same name/reference transformations as
    // the checksum-pinned release without downloading its full CRD in tests.
    await writeFile(
        join(root, "sandbox.yaml"),
        `
apiVersion: v1
kind: Namespace
metadata:
  name: agent-sandbox-system
---
apiVersion: v1
kind: ServiceAccount
metadata:
  name: agent-sandbox-controller
  namespace: agent-sandbox-system
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: agent-sandbox-controller
subjects:
  - kind: ServiceAccount
    name: agent-sandbox-controller
    namespace: agent-sandbox-system
roleRef:
  kind: ClusterRole
  name: agent-sandbox-controller
  apiGroup: rbac.authorization.k8s.io
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: agent-sandbox-controller
  namespace: agent-sandbox-system
spec:
  selector:
    matchLabels:
      app: agent-sandbox-controller
  template:
    metadata:
      labels:
        app: agent-sandbox-controller
    spec:
      serviceAccountName: agent-sandbox-controller
      containers:
        - name: agent-sandbox-controller
          image: registry.k8s.io/agent-sandbox/agent-sandbox-controller:v1.0.4
`,
    );
    const rendered = execFileSync(
        kubectl[0]!,
        [...kubectl.slice(1), "kustomize", root],
        { encoding: "utf8" },
    );
    const documents = rendered.split(/^---$/m);
    assert.match(
        documents.find((doc) => doc.includes("kind: Namespace"))!,
        /name: sandbox/,
    );
    assert.match(
        documents.find((doc) => doc.includes("kind: Deployment"))!,
        /metadata:\n  name: sandbox\n  namespace: sandbox/,
    );
    assert.match(
        rendered,
        /image: registry\.k8s\.io\/agent-sandbox\/agent-sandbox-controller:v1\.0\.4/,
    );
    assert.match(rendered, /serviceAccountName: agent-sandbox-controller/);
    assert.match(
        documents.find((doc) => doc.includes("kind: ClusterRoleBinding"))!,
        /subjects:\n- kind: ServiceAccount\n  name: agent-sandbox-controller\n  namespace: sandbox/,
    );
    assert.doesNotMatch(rendered, /agent-sandbox-system/);
});

test("platform overlays preserve service routing and execution isolation", (t) => {
    if (!kubectl)
        return t.skip(
            "k3s or kubectl is required for offline Kustomize validation",
        );
    for (const directory of ["deploy/platform", "deploy/azure/platform"]) {
        const rendered = execFileSync(
            kubectl[0]!,
            [...kubectl.slice(1), "kustomize", directory],
            { encoding: "utf8" },
        );
        const documents = rendered.split(/^---$/m);
        const sandbox = documents.find((doc) =>
            doc.includes("kind: Sandbox\n"),
        )!;
        assert.match(
            sandbox,
            /metadata:\n  name: platform\n  namespace: goblin/,
        );
        assert.match(
            sandbox,
            /name: GOBLIN_EXECUTION_NAMESPACE\n\s+value: agents/,
        );
        assert.match(sandbox, /claimName: platform-data/);
        assert.match(sandbox, /app: platform/);
        assert.match(sandbox, /app\.kubernetes\.io\/name: platform/);
        assert.match(sandbox, /\n\s+name: platform\n\s+ports:/);
        const image = sandbox.match(/\n\s+(?:- )?image: ([^\n]+)/)![1]!;
        assert.ok(
            sandbox.includes(`value: ${image}`),
            "execution uses the platform image",
        );
        for (const name of ["platform", "goblin-repository"]) {
            const service = documents.find(
                (doc) =>
                    doc.includes("kind: Service\n") &&
                    doc.includes(`name: ${name}\n`),
            )!;
            assert.match(service, /selector:\n\s+app: platform/);
        }
        if (directory.includes("azure")) {
            const ingress = documents.find((doc) =>
                doc.includes("kind: Ingress\n"),
            )!;
            assert.match(ingress, /service:\n\s+name: platform/);
        }
        const role = documents.find(
            (doc) =>
                doc.includes("kind: RoleBinding") &&
                doc.includes("name: goblin-execution-controller"),
        )!;
        assert.match(role, /namespace: agents/);
        assert.match(
            role,
            /subjects:\n- kind: ServiceAccount\n  name: goblin-execution-controller\n  namespace: goblin/,
        );
        const policy = documents.find((doc) =>
            doc.includes("name: goblin-execution-isolation"),
        )!;
        assert.match(policy, /namespace: agents/);
        assert.match(policy, /ingress: \[\]/);
        assert.match(policy, /kubernetes\.io\/metadata\.name: goblin/);
        const brokerPolicy = documents.find(
            (doc) =>
                doc.includes("kind: NetworkPolicy") &&
                doc.includes("name: platform\n"),
        )!;
        assert.match(
            brokerPolicy,
            /namespaceSelector:\n\s+matchLabels:\n\s+kubernetes\.io\/metadata\.name: agents/,
        );
        assert.doesNotMatch(brokerPolicy, /matchExpressions/);
        for (const name of ["goblin-headlamp", "goblin-victorialogs"]) {
            const policy = documents.find(
                (doc) =>
                    doc.includes("kind: NetworkPolicy\n") &&
                    doc.includes(`name: ${name}\n`),
            )!;
            assert.match(policy, /app: platform/);
            assert.doesNotMatch(
                policy,
                /app: website|app\.kubernetes\.io\/name: website/,
            );
        }
        assert.match(policy, /app: platform/);
    }
});

test("website shares the namespace without platform access or replica ownership", async () => {
    const deployment = await readFile("website/k8s/deployment.yaml", "utf8");
    const service = await readFile("website/k8s/service.yaml", "utf8");
    const hpa = await readFile("website/k8s/hpa.yaml", "utf8");
    for (const manifest of [deployment, service, hpa]) {
        assert.match(manifest, /name: website\n  namespace: goblin/);
        assert.doesNotMatch(
            manifest,
            /app: platform|app\.kubernetes\.io\/name: platform/,
        );
    }
    assert.match(deployment, /app\.kubernetes\.io\/name: website/);
    assert.match(service, /app\.kubernetes\.io\/name: website/);
    assert.match(deployment, /automountServiceAccountToken: false/);
    assert.doesNotMatch(
        deployment,
        /secret:|secretKeyRef:|serviceAccountName:|\n\s+replicas:/,
    );
    assert.match(deployment, /requests:\n\s+cpu: 25m\n\s+memory: 16Mi/);
    assert.match(deployment, /limits:\n\s+cpu: 100m\n\s+memory: 32Mi/);
    assert.match(hpa, /minReplicas: 1\n  maxReplicas: 3/);
    assert.match(hpa, /averageUtilization: 60/);
    const postgres = await readFile("deploy/postgres/postgres.yaml", "utf8");
    const policy = postgres
        .split(/^---$/m)
        .find((doc) => doc.includes("kind: NetworkPolicy"))!;
    assert.match(policy, /app: platform/);
    assert.doesNotMatch(
        policy,
        /app: website|app\.kubernetes\.io\/name: website/,
    );
});
