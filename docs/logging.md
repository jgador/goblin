# Monitoring and saved logs

Installed Goblin exposes **Settings → System**, **Tools → Cluster**, and **Tools →
Logs**. Tools open on Goblin's origin with its owner login. Standalone development
reports unsupported monitoring unless the required private services and node
configuration are supplied. Tool failure does not fail readiness or change Work
admission.

## System overview

System shows the controller's Kubernetes node and selected workload health and usage. On
WSL, this is the Linux VM rather than Windows. Root-disk usage excludes separately
mounted disks; memory is the kubelet working set. Short usage history is in memory and
clears on restart.

Custom installations must set `GOBLIN_NODE_NAME` to the observed node and set
`GOBLIN_NAMESPACE` when the application's namespace differs from the default in
[host configuration](../backend/src/Goblin.Web/Hosting/WebRuntimeConfiguration.cs).
[KubernetesSystemSource.cs](../backend/src/Goblin.Web/Monitoring/KubernetesSystemSource.cs)
owns health-warning thresholds;
[SystemMonitor.cs](../backend/src/Goblin.Web/Monitoring/SystemMonitor.cs) owns when
readings become stale.

The controller reads its own node through the Kubernetes API and kubelet summary over
TLS with CA and hostname validation. Public responses contain selected measurements,
never pod specs, raw kubelet data, credentials, or upstream errors. Custom installations
must allow kubelet access and trust its serving certificate.
[System monitoring](../backend/src/Goblin.Web/Monitoring/) and
[execution/RBAC manifests](../deploy/platform/execution.yaml) own limits and
permissions.

## Cluster view

Headlamp opens at `/headlamp/`. Its [read-only role](../deploy/platform/headlamp.yaml)
permits workload and cluster inspection but excludes Secrets, exec, port forwarding, and
mutations. Other custom resources require explicit RBAC.

Goblin's [Headlamp proxy](../backend/src/Goblin.Web/Http/HeadlampProxy.cs) strips
browser credentials, impersonation, and proxy headers and restricts operations.
Administration, plugin installation, cluster import, and external proxying are
unavailable. Watch and log streams end after five minutes and reconnect to recheck
authorization. Headlamp is trusted code
running on Goblin's origin; update its inline-script CSP hashes and run browser checks
when upgrading it.

For a configured private service:

```bash
kubectl port-forward -n goblin service/goblin-headlamp 14466:4466 --address 127.0.0.1
# In another terminal:
GOBLIN_TEST_HEADLAMP_URL=http://127.0.0.1:14466 make headlamp-test
```

Select the intended cluster before forwarding and stop the forward afterward. Standalone
`GOBLIN_HEADLAMP_URL` must name a trusted private service configured with
`-base-url=/headlamp`.

## Saved logs

`/logs/` opens VictoriaLogs VMUI. Display times use the
[workspace timezone](authentication.md#workspace-timezone); stored events remain UTC.
Refresh the view after timezone changes.

The application's JSON console logs pass through Fluent Bit to VictoriaLogs. Only the
platform web container is collected. Agent output may contain Work content and is
excluded, as are database and system services. The collector never reads Work PVCs or
credential mounts. Plain console writes remain plain text. JSON formatting provides no
redaction: keep secrets and private payloads out of statements and scopes.

[fluent-bit.conf](../deploy/platform/logging/fluent-bit.conf) owns collection, field
mapping, offsets, buffering, and transport retry configuration.
[Logging workloads](../deploy/platform/logging/workloads.yaml) own resource limits,
image pins, storage, and retention. Log transport retries never retry Work.

Native field mapping preserves large integers. Search with `service:goblin`,
`level:Warning`, or `category:="Microsoft.Hosting.Lifetime"`. For ChatGPT connection
acceptance, match the returned ping `responseId` to `state.ResponseId` in saved logs, or
`State.ResponseId` in the JSON console event.

Buffered logs and offsets survive collector restarts on the node, but outages, oversized
records, rotation, queue limits, and node loss can lose logs. Interrupted
acknowledgments can duplicate events. This is operational evidence, not Goblin's durable
Work history.

The log PVC survives pod replacement and is not a backup. Retention deletes whole daily
partitions while keeping recent days, so its size threshold is not a disk quota. The k3s
local-path storage class does not enforce PVC size either. Monitor free host space and
tune the manifest to observed volume.

## Authentication and deployment

VictoriaLogs and Headlamp have private Services and network policies admitting only
intended callers. Agents cannot access them. Fluent Bit runs as root to read node logs
but has a read-only root filesystem and limited Kubernetes reads.

The [log proxy](../backend/src/Goblin.Web/Http/VictoriaLogsProxy.cs) checks owner
sessions, strips credentials and tenant/proxy selectors, and allows only UI and read
operations, including allowed POST queries. Ingestion and administration are blocked.
Streams reconnect for authorization. VMUI's CSP differs from the application policy
because it is trusted embedded UI.

`GOBLIN_VICTORIALOGS_URL` must name a trusted private HTTP origin using the `/logs` path
prefix. Standalone development starts no collector or storage. The installed manifests
are in [deploy/platform/logging](../deploy/platform/logging/). Embedded installer
changes need an [installer release](goblinctl.md#source-and-release-boundaries).

## Verification

Proxy and deployment tests cover authentication, blocked writes, credential stripping,
streams, and safe failures. For actual collection and UI behavior:

```bash
make logging-test
```

This uses a dedicated disposable Docker/k3s fixture, not an ambient cluster. Its
dependencies and lifecycle are in [check-logging.sh](../scripts/check-logging.sh). For
an intended private log service forwarded to loopback:

```bash
GOBLIN_TEST_VICTORIALOGS_URL=http://127.0.0.1:19428 make logging-test-ui
```

Run UI checks on Headlamp or VictoriaLogs upgrades, including timezone, CSP, logout, and
blocked mutations. Fixture checks do not prove deployed collection, Azure DNS, ingress,
or TLS. See [verification selection](repository-layout.md#choosing-verification).
