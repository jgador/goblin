# System and cluster views

## System overview

The **System** shortcut or **Settings → System** shows whole-VM CPU, logical
cores, memory, root disk, uptime, OS, conditions, and Goblin service and agent
sandbox readiness, usage, and restart counts. It excludes completed pods and
shows pending and failed pods. CPU and memory history covers five minutes at
15-second intervals. On WSL this is the Linux VM, not Windows; separate data
mounts are not included in root disk usage.

Memory uses kubelet working set; CPU is recent cores consumed divided by logical
capacity. Reserved space can make disk used plus available less than total.
Readings are observations, not scheduling guarantees. CPU or memory at 85%, disk
availability below 10%, node pressure or readiness, disabled scheduling, and
unready Goblin pods are highlighted. Missing values are unavailable and old
observations are marked stale. History is cleared on restart and is not stored
in PostgreSQL. Monitoring does not affect readiness, Work, retries, or admission.

The controller uses `GOBLIN_NODE_NAME` and `GOBLIN_NAMESPACE` from the pod
manifest to read its node via Kubernetes and that node's kubelet summary over
TLS on port 10250, validating the cluster CA and server name. The
`goblin-system-reader` role gets nodes and nodes/stats and lists namespace pods;
existing execution permissions supply agent pods. The authenticated `/api/system`
returns selected measurements with normal host/origin checks and no caching,
not raw kubelet data, credentials, pod specs, or upstream errors. Browsers cannot
choose the upstream. This covers Goblin's node, not a multi-node aggregate.
Standalone development without `GOBLIN_NODE_NAME` reports unavailable; custom
clusters must allow kubelet access and trust its serving certificate. Tests cover
reading semantics and UI; actual network/TLS access requires verification on the
installation, including Azure.

## Cluster view

Headlamp is available via **Settings → Cluster → Open cluster** or `/headlamp/`
on the Goblin address: for example `http://localhost:8788/headlamp/` on local
WSL, or `https://<hostname>/headlamp/` for HTTPS. Use the Goblin password; locked
sessions are redirected to login and back. No separate token, password, DNS
record, or public port is needed. The Settings link opens a new tab; deep links
and refreshes retain the prefix.

Headlamp runs as the private `goblin-headlamp` Deployment in the `goblin`
namespace. NGINX routes through Goblin's authenticated proxy; its NetworkPolicy
allows Goblin pods, not agent sandboxes, to reach it. Its read-only
`goblin-cluster-viewer` account can inspect workloads, pod logs, events, nodes,
networking, storage, metrics, RBAC metadata, and Goblin Sandbox resources, but
cannot read Secrets, exec, forward ports, or mutate resources. Other custom
resources require explicit RBAC. Kubernetes enforces these permissions separately
from proxy restrictions.

The proxy strips browser credentials, impersonation and proxy headers, blocks
Headlamp administration, plugins, cluster import and external proxy, and permits
read-only health checks. Watches and log streams require reauthorization after
five minutes. Headlamp is trusted on Goblin's origin; when upgrading, verify its
pinned inline script CSP hashes and run the browser check below. Headlamp failure
does not fail Goblin readiness, but the installer waits for both services.

For standalone development, `GOBLIN_HEADLAMP_URL` may point to a trusted private
Headlamp configured with `-base-url=/headlamp`; Kubernetes sets it automatically.
Do not use an arbitrary third-party origin. `make test` covers the proxy and
installer. To test a real cluster, forward the private service, then run the
browser check and stop the forward afterward:

```bash
kubectl port-forward -n goblin service/goblin-headlamp 14466:4466 --address 127.0.0.1
# In another terminal:
GOBLIN_TEST_HEADLAMP_URL=http://127.0.0.1:14466 make headlamp-test
```

The browser check exercises authentication, Kubernetes reads, denied Secret and
mutation access, logs, deep links, CSP, and logout. Azure DNS, network rules,
and TLS require separate verification on that VM.
