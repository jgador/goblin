# Kubernetes names

Goblin has two components: **platform**, the main application built from
`backend/` and `frontend/`, and **website**, the public preview and marketing
site in `website/`. Both run in Kubernetes namespace **goblin**. Their builds,
deployments, and operational commands remain independent.

The [Chisel development gateway](../deploy/chisel/README.md) also uses namespace
`goblin`. Its Deployment, Service, and credential Secret are named `chisel`, its
Ingresses are `chisel-tunnel` and `chisel-dev`, and its deployer ServiceAccount is
`chisel-deployer`. Its selector uses `app.kubernetes.io/name=chisel`, separate from
the platform and website selectors. Chisel bootstrap preserves namespace policy;
removal must target its owned resources and never the shared namespace.

| Identity | Platform | Website |
| --- | --- | --- |
| Namespace | `goblin` | `goblin` |
| Workload | Sandbox `platform` | Deployment `website` |
| Container | `platform` | `website` |
| Main Service | `platform` | `website` |
| NetworkPolicy | `platform` | No website-specific policy |
| HPA | None | `website` |
| Pod component label | `app=platform`, `app.kubernetes.io/name=platform` | `app.kubernetes.io/name=website` |
| Service and access-policy selector | `app=platform` | `app.kubernetes.io/name=website` |
| Source-built Docker image | `goblin-platform:<tag>` | `goblin-website:<tag>` |
| Manifests | `deploy/platform/` | `website/k8s/` |
| Azure overlay | `deploy/azure/platform/` | Independent local preview workflow |
| Azure installation script | `deploy/azure/install-platform.sh` | None |
| Persistent application PVC | `platform-data` | None |
| Default application data directory | `.goblin-platform`; image `/data/platform` | None |

The installer uses `localhost/goblin-platform:<source-sha256>` for its locally
built image and execution-image setting. Installed manifests live under
`/var/lib/goblin/deploy/platform/` and `/var/lib/goblin/deploy/azure/platform/`.
Docker image names keep product scoping because Kubernetes namespaces do not
scope Docker repositories.

The Codex client handshake identifies the platform as `goblin-platform`, retaining
the Goblin display title and the upstream protocol's existing fields.

Kubernetes retains its normal generated identities. Website pods are
`website-<replicaset-hash>-<suffix>`; the current Sandbox controller names the
platform pod `platform`. Do not shorten generated suffixes or rewrite upstream
controller images, CRD/API names, or security identities.

PostgreSQL, Headlamp, logging, and the repository endpoint retain their functional
names. The repository Service selects platform pods. PostgreSQL, execution,
Headlamp, and logging access policies select `app=platform`; website labels must
never match these grants. Website pods disable service-account token mounting
and receive no platform credentials. The platform ingress policy continues to
allow execution's repository traffic; the Azure overlay additionally allows
F5 NGINX UI traffic. Execution RBAC and namespace isolation remain unchanged.

Website deployment applies only its Namespace, Deployment, Service, and HPA;
it never restarts the platform. Website removal must target only its Deployment,
Service, and HPA, never namespace `goblin`. `make website-stop` stops only its
Docker development workflow. Redeployment leaves `spec.replicas` unset so the
HPA owns scaling (1–3 replicas, CPU utilization target 60%). Website resource
requests remain `25m`/`16Mi`, with limits `100m`/`32Mi`.

```bash
sudo k3s kubectl wait --for=condition=Ready sandbox/platform -n goblin --timeout=300s
sudo k3s kubectl port-forward -n goblin svc/platform 8787:8787
sudo k3s kubectl logs -n goblin -l app=platform -c platform
sudo k3s kubectl top pod -n goblin -l app.kubernetes.io/name=website --containers
sudo k3s kubectl get hpa website -n goblin
make website-preview # localhost:5174; Docker live reload uses localhost:5173
```

## Execution identities

| Workload | Namespace | Pod name |
| --- | --- | --- |
| Agent Sandbox controller | `sandbox` | `sandbox-<hash>-<suffix>` |
| Repository execution | `agents` | `work-<work-id>` |

Work `123` retains Sandbox/PVC `work-123` across turns, retries, and revisions.
Each attempt keeps its own identity, repository branch and permissions. Each
compute allocation receives Secret and ConfigMap
`input-<work-id>-<attempt-id>-<allocation-number>`. Suspension removes the pod
and temporary inputs while retaining the Sandbox/PVC.

The installer verifies the upstream Agent Sandbox release checksum before
applying [a Kustomize overlay](../deploy/azure/setup/sandbox-kustomization.yaml).
The overlay shortens the Deployment name and moves its namespaced resources and
ServiceAccount binding to `sandbox`, preserving upstream permissions.
Environment references use `k8s/<namespace>/work-<work-id>`. The recorded namespace
remains in use even when the configured default changes.

## Storage ownership

Current installations use the names and formats above. There are no compatibility
aliases or automatic storage migrations. Source edits do not change a running
installation. Development installations with superseded formats can be recreated
after confirming ownership and explicitly choosing their deletion scope.

Before applying storage manifests, inspect bound PVCs, PVs, reclaim policies,
backing directories, and configured data paths. A new empty claim does not preserve
an installation. A naming change alone never authorizes deleting Work PVCs,
credentials, or resetting Kubernetes. Website removal targets only its owned
resources and never the shared namespace.

Published installer input inventories in `dependencies.lock.json` record the
original release bytes. They are historical provenance, not current paths;
regenerate deployment checksums with `cargo xtask dependencies resolve` and
package a new installer to distribute changed embedded scripts. Never rewrite
published hashes to describe unpublished source.

## Verification

Offline naming/deployment tests check labels, selectors, paths, and installer
contracts. Website and logging runtime checks use explicit local clusters and
isolated fixtures. Inspect actual PVC bindings, network access, routes, and workload
health on the intended installation; a dated local inspection cannot establish
its present storage identity or deployed version. Azure checks remain manual.
