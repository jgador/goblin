# Saved logs

Goblin's Kubernetes installation wires `ILogger` to Microsoft's JSON console
formatter, Fluent Bit, and VictoriaLogs. Open **Settings → Logs → Open logs** or
visit **`/logs/`** on the Goblin address. VictoriaLogs' built-in VMUI uses the Goblin
login and opens in a new tab. No CLI, separate UI pod, or second password is needed.

Displayed log times use the shared [workspace timezone](timezones.md), confirmed
on first access and editable under **Settings → Time & date**. IP location
suggests the initial timezone, with browser detection and UTC as fallbacks. Reopen
or refresh log views after changing the preference. Stored logs and raw timestamps
remain UTC.

This change configures providers and delivery only. Application log statements
and Work/Attempt instrumentation are deferred. Existing `Console.WriteLine` and
`Console.Error.WriteLine` messages remain plain text.

## What is collected

The main Goblin container writes to stdout/stderr. k3s/containerd wraps each line
in CRI format in `/var/log/pods`; `/var/log/containers` provides symlinks. One
Fluent Bit DaemonSet pod per node tails `*_goblin_auth-*.log`, reconstructs CRI
partial lines, parses JSON messages, and adds pod/container/namespace/node metadata.
Plain messages use the container timestamp and retain their text.

Agent worker output is excluded: its progress/result protocols can contain Work
content. PostgreSQL, Headlamp, system services, and the logging services themselves
are also outside the initial collection scope. The collector never reads Work
PVCs, database files, or credential mounts. No lifecycle or worker protocol changes
are needed to add application log statements later.

`Goblin.Web` uses `AddJsonConsole` with UTC timestamps, scopes, and one event per
line. The console queue holds up to 1,024 events and drops incoming events when
full instead of blocking application work. The default log level in
`backend/src/Goblin.Web/appsettings.json` is Information, including `Goblin`,
hosting lifecycle events, and framework diagnostics. ASP.NET Core emits request
and response logs without adding log statements to API handlers. Debug and Trace
events are filtered out by default.
Environment overrides use the usual .NET `Logging__LogLevel__...` convention.
Future log statements and scopes must omit credentials and private payloads;
JSON formatting does not redact them.

The collector uses native filters to preserve large integer values. In VictoriaLogs:

| Field                                        | Source                                              |
| -------------------------------------------- | --------------------------------------------------- |
| `_time`                                      | .NET `Timestamp`, falling back to the CRI timestamp |
| `_msg`                                       | .NET `Message`, falling back to the original text   |
| `level`, `category`, `event_id`, `exception` | Corresponding .NET fields                           |
| `state.*`, `scopes`                          | Structured message properties and scope data        |
| `kubernetes.*`                               | Pod, container, namespace, and node metadata        |
| `service`, `stream`                          | `goblin` and `stdout`/`stderr`                      |

VictoriaLogs flattens objects into dotted field names and retains arrays such as
scopes as JSON. Stream fields are service, namespace, container name, and stdout/
stderr. Pod, Work, Attempt, and trace identifiers remain ordinary searchable fields.
Try `service:goblin`, `level:Warning`, or `category:="Microsoft.Hosting.Lifetime"`.

## Polling and delivery

The official Fluent Bit **5.1.2** image is pinned by digest. `Inotify_Watcher Off`
selects its native **250 ms** content polling; no custom build is used.
`Refresh_Interval 5` discovers new files every five seconds. This is separate from
content polling. `Rotate_Wait 30` allows time to drain rotated files.

Offsets and filesystem chunks live in the node's dedicated
`/var/lib/goblin/fluent-bit` directory, mounted at `/buffers`. A collector restart
retains both. The HTTP output retries delivery and limits its queued chunks to
256 MiB; oldest queued chunks can be dropped when that limit is exceeded. These
are telemetry retries, independent of Work execution. Log delivery can duplicate
records after interrupted acknowledgments; it is not exactly once. Lines over
256 KiB are skipped. Long outages, node loss, or container log rotation before
collection can lose logs.

## Storage and resources

VictoriaLogs **1.52.0**, also pinned by digest, runs as one non-root Deployment
with a separate **10 GiB PVC** named `goblin-victorialogs-data`. It uses its native
log store; PostgreSQL and Work PVCs are not involved. A Recreate deployment
strategy ensures one process owns the log store during upgrades.

Retention is **seven days**, with an **8 GiB** stored-data retention threshold.
VictoriaLogs deletes old daily partitions, retaining at least the most recent two
days for size-based retention. The threshold can therefore be exceeded; it is not
a hard disk quota. k3s local-path PVC requests also do not impose filesystem quotas.
Leave VM disk headroom and adjust retention/volume sizing for observed volume.
This local PVC survives pod replacement, but is not a backup against VM/disk loss.

| Component    | CPU request / limit | Memory request / limit |
| ------------ | ------------------- | ---------------------- |
| Fluent Bit   | 25m / 250m          | 64 MiB / 256 MiB       |
| VictoriaLogs | 100m / 1 CPU        | 256 MiB / 1 GiB        |

VictoriaLogs has a 256 MiB cache budget, a 768 MiB Go soft memory limit, two
concurrent search requests, and a 30-second query limit. These are initial budgets
for the single-VM installation, not measured capacity guarantees. VMUI is served
by VictoriaLogs itself, so the stack adds **two pods** on one node.

## Authentication and deployment

The manifests are in `deploy/auth/logging/`, included by the existing local and
Azure Kustomize installations. ConfigMap content hashes roll Fluent Bit when its
configuration changes. The installer copies the manifests and waits for both
logging workloads. Rebuild the native installer for changes to its embedded
`install-app.sh`; follow [the release order](goblinctl.md#release-order) before
publishing a production installer.

`GOBLIN_VICTORIALOGS_URL` is a trusted private HTTP origin. Kubernetes sets it to
`http://goblin-victorialogs.goblin.svc:9428`. VictoriaLogs runs with
`-http.pathPrefix=/logs`; `/logs/` redirects to `/logs/select/vmui/`.

Goblin checks the session for every UI, asset, and query request. Its explicit
allowlist includes POST-based read APIs and live tail, and excludes ingestion,
administration, debug, and metrics endpoints. Browser cookies, credentials,
tenant selectors, and proxy headers are stripped. Live connections are limited
to five minutes before reconnecting and checking authorization again. VMUI has a
route-specific CSP allowing its layout styles; Goblin's pages keep their existing
policy. VMUI shares Goblin's origin and is trusted code, like Headlamp.

The ClusterIP service has no public ingress. NetworkPolicy permits connections
from Goblin and Fluent Bit only; agent pods cannot access it. Fluent Bit runs as
root to read node-owned logs, with a read-only root filesystem/log mount, no Linux
capabilities, and a namespace Role that only gets pod metadata. Its own metrics
port is not exposed to other pods. Goblin readiness does not depend on either
logging service; the UI reports unavailable when storage is down.

Standalone development does not start a collector or storage service. JSON
console logs are still emitted locally; configure a trusted VictoriaLogs origin
with the same path prefix to enable the UI.

## Verification

`npm test` covers JSON formatting, scopes and integer serialization, category
filters, authenticated proxy reads/streams, credential stripping, rejected
mutations, backend failure, existing Headlamp behavior, and installer failures.
Database tests are separate and remain opt-in.

For actual collection, buffering, restarts, and the real VMUI browser check:

```bash
npm run test:logging
```

This requires Docker, kubectl, curl, ripgrep, the repository's .NET/Node tools, and Chromium
installed for Playwright. It creates and deletes a dedicated k3s Docker container,
uses an explicit temporary kubeconfig, deploys the pinned images, and runs a
**test-only .NET workload**. It does not use an existing Kubernetes context or
saved Goblin credentials. Screenshots are saved under `test-results/logs-*.png`.

To check just the real UI against a trusted private service, port-forward the
VictoriaLogs service to localhost and run:

```bash
GOBLIN_TEST_VICTORIALOGS_URL=http://127.0.0.1:19428 npm run test:logs-ui
```

This checks login, search, refresh, Settings access, CSP, logout, and an Azure-shaped
hostname resolved locally. Actual Azure DNS, ingress, and TLS require deployment
verification on that VM.

### Verified on 2026-09-27

- `npm test`: seven Rust tests, protocol/Kubernetes generation checks, 246 .NET
  tests, and 90 HTTP/deployment tests passed. Thirty-nine .NET PostgreSQL tests
  and one Work HTTP persistence test were skipped because a live database was
  not configured.
- The disposable k3s check delivered actual .NET pod logs with structured fields,
  scopes, integer precision, CRI metadata, multiline messages, and text fallback.
  Data survived VictoriaLogs replacement on its PVC; queued records survived a
  collector restart while VictoriaLogs was unavailable and arrived after recovery.
- The real VMUI passed login, search/display, refresh, Settings link, CSP,
  ingestion rejection, logout, and return-to-query checks on localhost and a
  locally resolved Azure-shaped hostname.
- Typechecking, Clippy, formatting, and Azure template drift checks passed. The
  rebuilt musl installer review archive passed the hostname/install test.
- After changing the default to Information, all five focused logging tests
  passed. The running local installation received the matching
  `Logging__LogLevel__Default=Information` override and a pod restart; request
  start/finish events were confirmed in VictoriaLogs with no collector drops.

Test clusters were removed. No release was published. The updated installer
checksum remains a local review artifact;
follow the release procedure before production rollout.
