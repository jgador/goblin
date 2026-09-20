# VM system overview

The **System** shortcut above Settings shows CPU and memory usage at a glance.
Click it, or open **Settings → System**, to inspect the VM behind Goblin:

- VM CPU usage and total logical cores, memory usage and available memory.
- Root filesystem used, total, and available space; VM uptime and operating system.
- CPU and memory over the last five minutes, sampled every 15 seconds.
- Current machine conditions and Goblin service / agent sandbox readiness, CPU,
  memory, and restart counts. Completed pods are excluded; pending and failed
  pods remain visible. Headlamp provides the detailed cluster view.

These are **whole VM** readings, including the OS and non-Kubernetes processes.
They are not the application container's resource limit or a sum of its pods.
On WSL, they describe the WSL Linux VM, not Windows. Windows resource limits and
the physical disk's available space may constrain WSL further. On Azure, the
same collector reads the Linux VM. Separately mounted data disks are not
included in the root filesystem reading.

Memory uses kubelet's working-set estimate, which excludes inactive file cache.
CPU is recent cores consumed divided by logical CPU capacity. Idle CPU and
available memory are observations, not guarantees that another agent can be
scheduled. Disk used plus available can be less than total because some space
is reserved. PVC requested sizes are not treated as physical disk usage.

The overview highlights CPU or memory at 85%, disk availability below 10%,
Kubernetes node pressure / readiness, scheduling disabled, and unready Goblin
pods. These are advisory observations; they do not change Work, retry failed
executions, or enforce new admission limits. Monitoring failures never fail
Goblin's readiness probe. Missing values display as unavailable; old observations
are visibly stale rather than healthy. The small history buffer clears when
Goblin restarts, and stores no telemetry in PostgreSQL.

## Collection and access

`GOBLIN_NODE_NAME` and `GOBLIN_NAMESPACE` are injected from the main pod by the
installation manifest, so local k3s and Azure use the same implementation.
The controller reads its Node record through the Kubernetes API, and then
`https://<node-internal-ip>:10250/stats/summary` directly from that node's kubelet.
Both requests use the mounted service account and validate the cluster CA and
server name. The fixed request is made on the server; browsers cannot choose a
node, path, or upstream address.

The `goblin-system-reader` role grants **get nodes / nodes/stats**, plus **list
pods** in Goblin's namespace. Existing execution permissions supply the agent
pod list. No node proxy or exec permission, host filesystem mount, privileged
collector, public metrics port, external monitoring service, or new credentials
are added. The `/api/system` view requires the same Goblin login and host/origin
checks as other APIs, returns only selected measurements, and is never cached.
Raw kubelet responses, token files, pod specs, and upstream errors are not served.

The current collector observes the node running Goblin, matching the single-VM
installation. This is not an aggregate dashboard for a multi-node installation.
A standalone development server without `GOBLIN_NODE_NAME` clearly reports
monitoring unavailable; it does not mistake its container for the VM. Custom
clusters must permit the controller to reach kubelet's TLS port 10250 and trust
its serving certificate; monitoring fails safely if that is unavailable.

Unit tests cover VM versus pod readings, missing values, pressure, stale data,
bounded history, and recovery. HTTP tests cover login and host checks; browser
tests cover desktop/mobile views, drafts, staleness, pending pods, and locking.
Live WSL k3s validation exercises the direct authenticated kubelet path. Azure
uses the same manifests but requires a separate live deployment check.
