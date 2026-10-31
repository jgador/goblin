# Kubernetes names

Goblin has two components: **platform**, the main application built from
`backend/` and `frontend/`, and **website**, the public preview and marketing site
in `website/`. Both run in namespace **goblin**. Their builds, deployments, and
operational commands are independent.

The [Chisel development gateway](../deploy/chisel/README.md) also uses namespace
`goblin`. Its Deployment, Service, and credential Secret are named `chisel`, its
Ingresses are `chisel-tunnel` and `chisel-dev`, and its deployer ServiceAccount is
`chisel-deployer`. It selects `app.kubernetes.io/name=chisel`. Chisel removal must
target only its own resources, never the shared namespace.

| Identity | Platform | Website |
| --- | --- | --- |
| Namespace | `goblin` | `goblin` |
| Workload | Sandbox `platform` | Deployment `website` |
| Container | `platform` | `website` |
| Main Service | `platform` | `website` |
| NetworkPolicy | `platform` | None |
| HPA | None | `website` |
| Pod labels | `app=platform`, `app.kubernetes.io/name=platform` | `app.kubernetes.io/name=website` |
| Service and access-policy selector | `app=platform` | `app.kubernetes.io/name=website` |
| Source-built Docker image | `goblin-platform:<tag>` | `goblin-website:<tag>` |
| Manifests | `deploy/platform/` | `website/k8s/` |
| Azure overlay | `deploy/azure/platform/` | None |
| Azure installation script | `deploy/azure/install-platform.sh` | None |
| Persistent application PVC | `platform-data` | None |
| Default application data directory | `.goblin-platform`; image `/data/platform` | None |

The installer uses image `localhost/goblin-platform:<source-sha256>` and installs
manifests under `/var/lib/goblin/deploy/platform/` and
`/var/lib/goblin/deploy/azure/platform/`. The Codex client identifies itself as
`goblin-platform`.

The `goblin-repository` Service and the PostgreSQL, execution, Headlamp, and logging
access policies select `app=platform`. Website pods must not match these selectors,
mount a service-account token, or receive platform credentials. The `platform`
NetworkPolicy allows execution pods to reach the repository port 8788; the Azure
overlay also allows NGINX ingress traffic on port 8787.

`make website-deploy` applies the website Namespace, Service, Deployment, and HPA
without restarting the platform. To remove the website, delete only
`deployment/website`, `service/website`, and `hpa/website`, never the shared
namespace. `make website-stop` only stops Docker development. The HPA owns replicas
(1–3, 60% CPU target), so the Deployment leaves `spec.replicas` unset. Requests are
`25m`/`16Mi` and limits `100m`/`32Mi`.

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

Work `123` keeps Sandbox and PVC `work-123` across turns, retries, and revisions.
Execution resources carry label `app=goblin-execution`. Each attempt keeps its own
identity, repository branch, and permissions. Each compute allocation gets a Secret
and ConfigMap named `input-<work-id>-<attempt-id>-<allocation-number>`. Suspension
removes the pod and these inputs but keeps the Sandbox and PVC.

The installer verifies the upstream Agent Sandbox release checksum before applying
[a Kustomize overlay](../deploy/azure/setup/sandbox-kustomization.yaml). The overlay
renames the controller Deployment to `sandbox` and moves its namespaced resources and
ServiceAccount binding to namespace `sandbox`, keeping upstream permissions.
Environment references use `k8s/<namespace>/work-<work-id>`. Goblin keeps using the
recorded namespace even when the configured default changes.

## Storage ownership

There are no compatibility aliases or automatic storage migrations. Before applying
storage manifests or recreating a development install, inspect PVCs, PVs, reclaim
policies, backing directories, and data paths. A new empty claim does not preserve
data. Naming changes do not authorize deleting Work PVCs or credentials, or
resetting Kubernetes. Choose the deletion scope explicitly. Source edits do not
change a running installation.

## Verification

`make node-test` runs the offline deployment tests that check names, labels,
selectors, paths, and installer contracts. On the intended installation, inspect the
actual PVC bindings, network access, routes, and workload health. Azure checks are
manual.
