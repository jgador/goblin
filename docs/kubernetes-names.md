# Kubernetes names

Goblin uses these names. Kubernetes still adds its normal ReplicaSet
hash and unique suffix to Deployment pods; do not remove those identity suffixes.

| Workload | Namespace | Pod name |
| --- | --- | --- |
| Agent Sandbox controller | `sandbox` | `sandbox-<hash>-<suffix>` |
| Goblin application | `goblin` | `app` |
| Repository execution | `agents` | `run-<work-id>-<attempt-number>` |

For Work `123`, its first execution is `run-123-1`; an explicit retry is
`run-123-2`. The final number is the one-based position in that Work's durable
attempt history, not the global attempt ID. A requested revision or continuation
also advances this number. Internal attempt IDs and labels remain unchanged.

An **attempt** is the internal record of one execution, including the first run;
it does not mean a failure has already happened. Each new attempt gets a matching
Sandbox, workspace PVC, Secret, and ConfigMap. Cleanup
removes the pod and temporary inputs, but keeps the suspended Sandbox and PVC.

This changes names, not namespace separation or permissions. PostgreSQL,
Headlamp, Kubernetes system components, Service names, security labels, image
names, and persistent application/database volume names are unchanged. The
application still uses the `goblin-auth` Service and `app=goblin-auth` label.

The installer verifies the upstream Agent Sandbox release checksum before
applying [a Kustomize overlay](../deploy/azure/setup/sandbox-kustomization.yaml).
The overlay shortens the Deployment name and moves its namespaced resources and
ServiceAccount binding to `sandbox`. It does not rewrite the controller image,
CRD/API name, or cluster-role permissions.

Environment references use `k8s/<namespace>/run-<work-id>/<global-attempt-id>`.
The claimed namespace remains part of execution provenance even if the default
configuration changes later. The adapter derives the pod suffix from the
persisted Work history.

## Local test installations

This is an unreleased, breaking naming change. There is no migration or fallback
for earlier resource names or execution references. Recreate local test
installations instead of preserving resources under earlier names:

```bash
npm run install:local -- reset --yes
npm run install:local -- start
```

Reset deletes the local Goblin cluster and its test data, including saved
connections, Work history, and workspaces. Set a new password and reconnect
accounts after installation. Editing this checkout alone does not change a
running installation.
