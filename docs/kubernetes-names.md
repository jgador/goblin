# Kubernetes names

Goblin has two components: **platform**, the main application built from
`backend/` and `frontend/`, and **website**, the public preview and marketing
site in `website/`. Both run in Kubernetes namespace **goblin**. Their builds,
deployments, and operational commands remain independent.

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
Traefik UI traffic. Execution RBAC and namespace isolation remain unchanged.

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

## Existing local storage

PVC names cannot be renamed in place. Inventory the currently bound application
PVC, its PV, reclaim policy, backing directory, and current data-directory setting
before applying these templates to an existing installation. Never initialize
an empty replacement and describe it as preserving the installation.

A safe stateless transition can use a task-local overlay referencing the existing
PVC, while moving the application data directory on that same filesystem after
stopping its writers. Verify replacement health before retiring superseded
stateless resources. This leaves the PVC name and backing path to transition
separately. A full claim transition requires retaining the backing volume and
controlled claim rebinding or a verified copy; a `Delete` reclaim policy must
never be allowed to delete application data. Do not delete Work PVCs, credentials,
or reset k3s to accomplish a naming change.

Goblin is unreleased; new installations use only the current names and formats.
There are no compatibility aliases or automatic storage migrations. Editing the
checkout alone does not change a running installation. The website's former
namespace may be removed only after checking every namespaced resource and
confirming no unrelated resources remain.

Published installer input inventories in `dependencies.lock.json` record the
original release bytes. They are historical provenance, not current paths;
regenerate deployment checksums with `cargo xtask dependencies resolve` and
package a new installer to distribute changed embedded scripts. Never rewrite
published hashes to describe unpublished source.

## Local verification — 2026-10-12

The local installation runs healthy `platform` and `website` workloads in
`goblin`. Platform routes through the existing browser port 8788; website Docker
development and Kubernetes preview retain ports 5173 and 5174. Website redeployment
left the platform pod identity and restart count unchanged. HPA CPU metrics are
active. The website-only one-visit smoke loaded all twelve HTTP assets; it does
not establish new capacity measurements. The existing Headlamp and logging UI
integration checks exercise local cluster resources with isolated test credentials.
The disposable k3s logging smoke verified platform-container collection, buffering,
collector restart, and recovery. The local collector also ingests records tagged
with container `platform`.

The application directory moved on the same bound filesystem to `/data/platform`,
retaining its inode and permissions. The original application claim and PV backing
path remain. The installed overlay intentionally references that existing claim
and omits the new `platform-data` PVC declaration. Claim replacement/rebinding is
still pending; applying freshly generated manifests without addressing this local
exception would create an empty replacement. Database, logging, and Work PVCs and
credentials were retained. Superseded stateless platform resources and the old
website namespace were retired after ownership and replacement-health checks.

The full offline suite passed, with PostgreSQL-dependent checks left opt-in.
Azure assets were generated and tested offline; no Azure installation or
authenticated model execution was performed. The current installer was rebuilt,
integrity-checked, and activated locally; the published dependency pin is unchanged.
