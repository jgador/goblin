# Kubernetes names

Goblin uses these names. Kubernetes still adds its normal ReplicaSet
hash and unique suffix to Deployment pods; do not remove those identity suffixes.

| Workload | Namespace | Pod name |
| --- | --- | --- |
| Agent Sandbox controller | `sandbox` | `sandbox-<hash>-<suffix>` |
| Goblin application | `goblin` | `app` |
| Repository execution | `agents` | `work-<work-id>` |

Work `123` retains Sandbox/PVC `work-123` across turns, retries, and revisions.
Each attempt keeps its own identity, repository branch and permissions. Each
compute allocation receives Secret and ConfigMap `input-<work-id>-<attempt-id>-<allocation-number>`.
Suspension removes the pod and temporary inputs while retaining the Sandbox/PVC.

Namespace separation remains unchanged. PostgreSQL,
Headlamp, Kubernetes system components, Service names, security labels, image
names, and persistent application/database volume names are unchanged. The
application still uses the `goblin-auth` Service and `app=goblin-auth` label.

The installer verifies the upstream Agent Sandbox release checksum before
applying [a Kustomize overlay](../deploy/azure/setup/sandbox-kustomization.yaml).
The overlay shortens the Deployment name and moves its namespaced resources and
ServiceAccount binding to `sandbox`. It does not rewrite the controller image,
CRD/API name, or cluster-role permissions.

Environment references use `k8s/<namespace>/work-<work-id>`. The recorded namespace
remains in use even when the configured default changes.

## Local test installations

Goblin is unreleased. Recreate local test installations when the workspace or
database format changes:

```bash
npm run install:local -- reset --yes
npm run install:local -- start
```

Reset deletes the local cluster and its persistent data. Use the repository's
`$clean-slate` skill for a full reset that also removes local test credentials
and the shared password verifier.

The installation includes the application and execution RBAC together; PVC
label updates require `patch` on `persistentvolumeclaims`. Editing the checkout
alone does not change a running installation.
