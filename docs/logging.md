# Saved logs

Goblin's Kubernetes installation sends `ILogger` output through Microsoft's JSON
console formatter to Fluent Bit and VictoriaLogs. Open **Settings → Logs → Open logs**
or visit **`/logs/`** on the Goblin address. VictoriaLogs' built-in VMUI opens in a
new tab and uses the Goblin login; there is no separate password or UI pod.

Displayed times use the shared [workspace timezone](timezones.md); Goblin asks for
it first if it is not set. Stored logs and raw timestamps stay UTC. Refresh an open
log view after changing the timezone.

Goblin code does not yet write its own log statements. Existing
`Console.WriteLine` and `Console.Error.WriteLine` output stays plain text.

## What is collected

The main Goblin container writes to stdout/stderr, which k3s stores under
`/var/log/pods` with symlinks in `/var/log/containers`. One Fluent Bit pod per node
tails `*_goblin_platform-*.log`, joins CRI partial lines, parses JSON messages, and
adds pod, container, namespace, and node metadata. Plain-text lines keep their text
and the container timestamp.

Agent worker output is not collected because its progress and result messages can
contain Work content. PostgreSQL, Headlamp, system services, and the logging
services are not collected either. The collector never reads Work PVCs, database
files, or credential mounts.

`Goblin.Web` uses `AddJsonConsole` with UTC timestamps, scopes, and one event per
line. The console queue holds up to 1,024 events and drops new events when full
instead of blocking. `backend/src/Goblin.Web/appsettings.json` sets the default level
to Information, so Debug and Trace are filtered out; ASP.NET Core request logs are
included. Override levels with the standard `Logging__LogLevel__...` variables.
Log statements and scopes must not include credentials or private payloads; JSON
formatting does not redact them.

Fluent Bit uses native filters so large integers keep their exact values. In VictoriaLogs:

| Field                                        | Source                                              |
| -------------------------------------------- | --------------------------------------------------- |
| `_time`                                      | .NET `Timestamp`, falling back to the CRI timestamp |
| `_msg`                                       | .NET `Message`, falling back to the original text   |
| `level`, `category`, `event_id`, `exception` | Matching .NET fields                                |
| `state.*`, `scopes`                          | Structured message properties and scope data        |
| `kubernetes.*`                               | Pod, container, namespace, and node metadata        |
| `service`, `stream`                          | `goblin` and `stdout`/`stderr`                      |

Objects become dotted field names; arrays such as scopes stay JSON. Stream fields
are service, namespace, container name, and stream. Try `service:goblin`,
`level:Warning`, or `category:="Microsoft.Hosting.Lifetime"`.

## Polling and delivery

The official Fluent Bit **5.1.3** image is pinned by digest. `Inotify_Watcher Off`
uses its built-in **250 ms** content polling. `Refresh_Interval 5` finds new files
every five seconds, and `Rotate_Wait 30` gives time to finish reading rotated files.

Offsets and buffered chunks are kept in the node directory
`/var/lib/goblin/fluent-bit` (mounted at `/buffers`) and survive collector restarts.
The HTTP output retries without limit and keeps at most 256 MiB of queued chunks,
dropping the oldest when the limit is reached. These retries are for logs only,
not Work. Delivery can duplicate records after an interrupted acknowledgment.
Lines over 256 KiB are skipped. Long outages, node loss, or log rotation before
collection can lose logs.

## Storage and resources

VictoriaLogs **1.53.0**, pinned by digest, runs as one non-root Deployment with the
**10 GiB PVC** `goblin-victorialogs-data`. It does not use PostgreSQL or Work PVCs.
The Recreate strategy keeps a single process on the log store during upgrades.

Retention is **seven days**, with an **8 GiB** size threshold. VictoriaLogs deletes
whole daily partitions and keeps at least the two most recent days, so the
threshold can be exceeded; it is not a disk quota, and k3s local-path PVCs do not
enforce quotas either. Leave free VM disk space and adjust retention or volume size
for the observed volume. The PVC survives pod replacement but is not a backup.

| Component    | CPU request / limit | Memory request / limit |
| ------------ | ------------------- | ---------------------- |
| Fluent Bit   | 25m / 250m          | 64 MiB / 256 MiB       |
| VictoriaLogs | 100m / 1 CPU        | 256 MiB / 1 GiB        |

VictoriaLogs also uses a 256 MiB cache limit, a 768 MiB Go soft memory limit, two
concurrent searches, and a 30-second query limit.

## Authentication and deployment

The manifests are in `deploy/platform/logging/` and are included by the local and
Azure Kustomize installations. A ConfigMap content hash restarts Fluent Bit when
its configuration changes. The installer waits for both logging workloads. Changes
to the embedded `install-platform.sh` need a new installer build; follow
[the release order](goblinctl.md#releases) before publishing one.

`GOBLIN_VICTORIALOGS_URL` must be a trusted private HTTP origin. Kubernetes sets it
to `http://goblin-victorialogs.goblin.svc:9428`. VictoriaLogs runs with
`-http.pathPrefix=/logs`; `/logs/` redirects to `/logs/select/vmui/`.

Goblin checks the session for every UI, asset, and query request. Its allowlist
includes the read APIs (some use POST) and live tail, and blocks ingestion,
administration, debug, and metrics endpoints. Goblin strips browser cookies,
credentials, tenant selectors, and proxy headers. Live connections end after five
minutes, so reconnects check authorization again. VMUI has its own CSP that allows
its inline styles; other Goblin pages keep their policy. VMUI runs on Goblin's
origin and is trusted code, like Headlamp.

The ClusterIP service has no public ingress. NetworkPolicy allows connections only
from Goblin and Fluent Bit; agent pods cannot reach it. Fluent Bit runs as root to
read node log files, with a read-only root filesystem and log mount, no Linux
capabilities, and a Role that can only get pods. Its metrics port is closed to
other pods. Goblin readiness does not depend on either logging service; the UI
reports when storage is unavailable.

Standalone development starts no collector or storage. Goblin still writes JSON
console logs; to use the UI, set a trusted VictoriaLogs origin that uses the same
path prefix.

## Verification

`make test` covers the JSON console setup, authenticated proxy reads and streams,
credential stripping, blocked writes, logging failures, and installer failures.

To check real collection, buffering, restarts, and the VMUI in a browser:

```bash
make logging-test
```

This needs Docker, kubectl, curl, ripgrep, the repository's .NET and Node tools, and
Chromium installed for Playwright. It creates and removes a dedicated k3s Docker
container with its own temporary kubeconfig, deploys the pinned images, and runs a
**test-only .NET workload**. It does not use an existing Kubernetes context or
saved Goblin credentials. Screenshots are saved under `.artifacts/logs-ui/`.

To check only the UI against a trusted private service, port-forward the
VictoriaLogs service to localhost and run:

```bash
GOBLIN_TEST_VICTORIALOGS_URL=http://127.0.0.1:19428 make logging-test-ui
```

This checks timezone setup, login, search, refresh, the Settings link, CSP, blocked
ingestion, logout, and an Azure-style hostname resolved locally. Real Azure DNS,
ingress, and TLS must be checked on the deployed VM.
