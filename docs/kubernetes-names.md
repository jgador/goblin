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

The installer uses `localhost/goblin-platform:<source-sha256>` and installs
manifests under `/var/lib/goblin/deploy/platform/` and
`/var/lib/goblin/deploy/azure/platform/`. The Codex handshake identifies the
platform as `goblin-platform`. Website pod names retain Kubernetes-generated
suffixes; do not rename upstream controllers, CRDs, APIs, or security identities.

The repository Service and PostgreSQL, execution, Headlamp, and logging access
policies select `app=platform`. Website pods must not match these grants, mount a
service-account token, or receive platform credentials. The platform ingress
allows execution repository traffic; the Azure overlay also allows F5 NGINX UI
traffic. Execution RBAC and namespace isolation remain unchanged.

Website deployment manages its Namespace, Deployment, Service, and HPA without
restarting the platform. Removal targets only its Deployment, Service, and HPA,
never the shared namespace. `make website-stop` only stops Docker development.
The HPA owns replicas (1–3, 60% CPU target); deployment leaves `spec.replicas`
unset. Requests are `25m`/`16Mi`, limits `100m`/`32Mi`.

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

Current installations have no compatibility aliases or automatic storage
migrations. Before applying storage manifests or recreating a development install,
inspect PVCs, PVs, reclaim policies, backing directories, and data paths. A new
empty claim does not preserve data; naming changes do not authorize deleting Work
PVCs, credentials, or resetting Kubernetes. Choose deletion scope explicitly.

Published installer inventories in `dependencies.lock.json` describe original
release bytes. Use `cargo xtask dependencies resolve` and package a new installer
to distribute changed embedded scripts; do not rewrite published hashes. Source
edits do not change a running installation.

## Verification

Offline naming/deployment tests check labels, selectors, paths, and installer
contracts. Website and logging runtime checks use explicit local clusters and
isolated fixtures. Inspect actual PVC bindings, network access, routes, and workload
health on the intended installation; a dated local inspection cannot establish
its present storage identity or deployed version. Azure checks remain manual.
