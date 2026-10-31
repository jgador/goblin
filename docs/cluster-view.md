# System and cluster views

## System overview

**Settings → System** shows the VM's CPU, logical cores, memory, root disk, uptime,
OS, and node conditions, plus readiness, usage, and restart counts of Goblin
services and agent sandboxes. Completed pods are hidden. On WSL this is the Linux
VM, not Windows. Separately mounted disks are not in root disk usage.

Memory is the kubelet working set; CPU is cores used divided by logical capacity.
CPU and memory history covers five minutes at 15-second intervals, is kept in
memory, and clears on restart. The view warns at 85% CPU or memory, less than 10%
disk available, node pressure or not-ready conditions, disabled scheduling, and
unready Goblin pods. Readings older than 60 seconds are marked stale. Monitoring
does not affect readiness, Work, or admission.

The controller reads `GOBLIN_NODE_NAME` and `GOBLIN_NAMESPACE` from the pod
manifest. It reads its node from the Kubernetes API and the kubelet summary over TLS
on port 10250, checking the cluster CA and server name. The `goblin-system-reader`
roles allow get on nodes and nodes/stats and list on pods in `goblin`. The
authenticated `/api/system` returns selected measurements only, never raw kubelet
data, credentials, pod specs, or upstream errors. Only Goblin's own node is shown.
Without `GOBLIN_NODE_NAME`, as in standalone development, monitoring reports as
unsupported. Custom clusters must allow kubelet access and trust its serving
certificate; check network and TLS access on each installation.

## Cluster view

Open Headlamp from **Settings → Cluster → Open cluster** or at `/headlamp/` on the
Goblin address, such as `http://localhost:8788/headlamp/` on local WSL. Sign in
with the Goblin password; no separate token, DNS record, or public port is needed.

Headlamp runs as the private `goblin-headlamp` Deployment in namespace `goblin`.
Browsers reach it only through Goblin's authenticated proxy, and its NetworkPolicy
admits only `app: platform` pods. Its `goblin-cluster-viewer` role can read
workloads, pod logs, events, nodes, networking, storage, metrics, RBAC objects, and
Agent Sandbox resources. It cannot read Secrets, exec, forward ports, or change
resources. Other custom resources need explicit RBAC.

The proxy (`HeadlampProxy`) strips browser credentials, impersonation, and proxy
headers. It allows only Kubernetes reads, health checks, and access reviews, so
Headlamp administration, plugin installation, cluster import, and its external
proxy are unavailable. Watches and log streams end after five minutes and must
authorize again. When upgrading Headlamp, update the inline script CSP hashes in
`HeadlampProxy` and run the browser check below. A Headlamp failure does not fail
Goblin readiness, but the installer waits for both.

For standalone development, `GOBLIN_HEADLAMP_URL` may point to a trusted private
Headlamp started with `-base-url=/headlamp`. Do not use a third-party origin. To
test a real cluster, forward the private service and run the browser check:

```bash
kubectl port-forward -n goblin service/goblin-headlamp 14466:4466 --address 127.0.0.1
# In another terminal:
GOBLIN_TEST_HEADLAMP_URL=http://127.0.0.1:14466 make headlamp-test
```

Stop the forward when done. On Azure, check DNS, network rules, and TLS separately.
