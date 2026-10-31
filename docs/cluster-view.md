# System and cluster views

## System overview

The **System** shortcut or **Settings → System** shows whole-VM CPU, logical cores,
memory, root disk, uptime, OS, node conditions, and the readiness, usage, and restart
counts of Goblin services and agent sandboxes. Completed pods are hidden; pending
and failed pods are shown. CPU and memory history covers five minutes at 15-second
intervals. On WSL this is the Linux VM, not Windows. Separately mounted disks are
not included in root disk usage.

Memory is the kubelet working set. CPU is recent cores used divided by logical
capacity. Reserved space can make disk used plus available less than total.
Readings are observations, not scheduling guarantees. The view highlights CPU or
memory at 85% or more, less than 10% disk available, node pressure or not-ready
conditions, disabled scheduling, and unready Goblin pods. Missing values show as
unavailable, and readings older than 60 seconds are marked stale. History is kept in
memory, cleared on restart, and not stored in PostgreSQL. Monitoring does not affect
readiness, Work, retries, or admission.

The controller reads `GOBLIN_NODE_NAME` and `GOBLIN_NAMESPACE` from the pod manifest.
It reads its node through the Kubernetes API and that node's kubelet summary over
TLS on port 10250, validating the cluster CA and server name. The
`goblin-system-reader` roles allow get on nodes and nodes/stats and list on pods in
`goblin`; the execution role already allows listing agent pods. The authenticated
`/api/system` returns selected measurements with the normal host and origin checks
and `no-store` caching, not raw kubelet
data, credentials, pod specs, or upstream errors. Browsers cannot choose the
upstream. Only Goblin's own node is shown, not a multi-node total. Without
`GOBLIN_NODE_NAME`, as in standalone development, monitoring reports as unsupported.
Custom clusters must allow kubelet access and trust its serving certificate. Verify
real network and TLS access on each installation, including Azure.

## Cluster view

Open Headlamp from **Settings → Cluster → Open cluster** or at `/headlamp/` on the
Goblin address, for example `http://localhost:8788/headlamp/` on local WSL or
`https://<hostname>/headlamp/` with HTTPS. Use the Goblin password; a locked session
is sent to login and back. No separate token, password, DNS record, or public port
is needed. The Settings link opens a new tab; deep links and refreshes keep the
prefix.

Headlamp runs as the private `goblin-headlamp` Deployment in namespace `goblin`.
Browsers reach it only through Goblin's authenticated proxy, and its NetworkPolicy
admits only `app=platform` pods, not agent sandboxes. Its read-only
`goblin-cluster-viewer` account can inspect workloads, pod logs, events, nodes,
networking, storage, metrics, RBAC objects, and Agent Sandbox resources. It cannot
read Secrets, exec, forward ports, or change resources. Other custom resources need
explicit RBAC. Kubernetes enforces these permissions separately from the proxy.

The proxy strips browser credentials, impersonation, and proxy headers. It allows
only Kubernetes reads, health checks, and access reviews, so Headlamp
administration, plugin installation, cluster import, and the external proxy are
unavailable. Watches and log streams end after five minutes and must authorize
again. Headlamp runs on Goblin's origin; when upgrading it, check the pinned inline
script CSP hashes in `HeadlampProxy` and run the browser check below. A Headlamp
failure does not fail Goblin readiness, but the installer waits for both.

For standalone development, `GOBLIN_HEADLAMP_URL` may point to a trusted private
Headlamp started with `-base-url=/headlamp`; the Kubernetes manifest sets it. Do not
use a third-party origin. `make test` covers the proxy and installer. To test a real
cluster, forward the private service, run the browser check, then stop the forward:

```bash
kubectl port-forward -n goblin service/goblin-headlamp 14466:4466 --address 127.0.0.1
# In another terminal:
GOBLIN_TEST_HEADLAMP_URL=http://127.0.0.1:14466 make headlamp-test
```

The browser check covers authentication, Kubernetes reads, denied Secret and
mutation access, logs, deep links, CSP, and logout. Azure DNS, network rules, and
TLS need separate checks on that VM.
