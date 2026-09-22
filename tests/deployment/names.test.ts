import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { cp, mkdtemp, rm, writeFile } from "node:fs/promises";
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
          image: registry.k8s.io/agent-sandbox/agent-sandbox-controller:v1.0.3
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
        /image: registry\.k8s\.io\/agent-sandbox\/agent-sandbox-controller:v1\.0\.3/,
    );
    assert.match(rendered, /serviceAccountName: agent-sandbox-controller/);
    assert.match(
        documents.find((doc) => doc.includes("kind: ClusterRoleBinding"))!,
        /subjects:\n- kind: ServiceAccount\n  name: agent-sandbox-controller\n  namespace: sandbox/,
    );
    assert.doesNotMatch(rendered, /agent-sandbox-system/);
});

test("application overlays keep agents isolated while shortening the Sandbox name", (t) => {
    if (!kubectl)
        return t.skip(
            "k3s or kubectl is required for offline Kustomize validation",
        );
    for (const directory of ["deploy/auth", "deploy/azure/app"]) {
        const rendered = execFileSync(
            kubectl[0]!,
            [...kubectl.slice(1), "kustomize", directory],
            { encoding: "utf8" },
        );
        const documents = rendered.split(/^---$/m);
        const sandbox = documents.find((doc) =>
            doc.includes("kind: Sandbox\n"),
        )!;
        assert.match(sandbox, /metadata:\n  name: app\n  namespace: goblin/);
        assert.match(
            sandbox,
            /name: GOBLIN_EXECUTION_NAMESPACE\n\s+value: agents/,
        );
        assert.match(sandbox, /claimName: goblin-auth-data/);
        assert.match(sandbox, /app: goblin-auth/);
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
                doc.includes("name: goblin-auth\n"),
        )!;
        assert.match(
            brokerPolicy,
            /namespaceSelector:\n\s+matchLabels:\n\s+kubernetes\.io\/metadata\.name: agents/,
        );
        assert.doesNotMatch(brokerPolicy, /matchExpressions/);
    }
});
