# Execution hosting and recovery

Goblin runs one application controller with PostgreSQL and isolated executions.
Codex is the current runtime; the Work core and orchestration do not reference its
protocol. Agent, Work, attempt, connection, and native session IDs stay separate.
Claims, cancellation, retry, and attention follow [Work lifecycle](work-lifecycle.md).
The Work PVC, turns, and Git checkpoints are in
[workspace lifecycle](workspace-lifecycle.md).

## Choosing an environment

A text question runs in a separate local worker process with tools disabled. It
gets no Sandbox and no database credentials, and answers from the supplied Work
context.

Repository execution needs a repository enabled in Settings, or one enabled
through a saved authorization preview in the Work conversation. Each attempt
records the GitHub account and connection generation, repository ID, base branch,
policy version, and branch `goblin/<work-id>/<attempt-id>`. The agent's Git author
name and email are separate from the connected GitHub account. The first
repository allocation for a Work creates a Sandbox and private PVC named
`work-<work-id>` in the execution namespace (`agents` in the deployed manifests).
Environment references are `k8s/<namespace>/work-<work-id>`. See
[Kubernetes names](kubernetes-names.md).

The sandbox receives repository content as a Git bundle plus an attempt
capability. It never receives the GitHub token. The repository broker in the
controller checks each operation against the saved attempt and handles Git objects
in its own bare repository; agent Git configuration and hooks never enter it.
`goblin-github publish` pushes to the assigned branch only. `goblin-github
pull-request` opens a draft PR; later pushes update it. `goblin-github fetch`
refreshes the sandbox's origin branches. At the end of a turn the worker publishes
only when push is approved; otherwise the broker verifies a local Git checkpoint
without publishing. Merging, auto-merge, other branches, tags, and repository
administration are not available.

Publication commands are saved with Wolverine dispatch intent, and a repeated
command ID cannot publish again. Each external CLI process and each whole
operation has a 120-second limit. An uncertain response is reconciled by checking
the exact remote branch commit or PR. After controller loss, a saved boot ID and
system uptime bound how long a child process could still run; remote absence
alone does not prove a write stopped. A replacement attempt gets a new branch and
continues from the local Git HEAD on the Work PVC, including unpublished commits.
A missing volume requires attention.

GitHub account and repository changes are rejected while a repository attempt is
queued, running, uncertain, cleanup-pending, or holding compute. Signing in with a
different GitHub account disables all enabled repositories. Connection secrets
and CLI output stay out of Work history and browser responses.

The sandbox pod runs as non-root with a read-only root filesystem, dropped
capabilities, CPU and memory limits, and a one-hour deadline. Repository tools run
commands without interactive approval. Repository workers enable Codex's
`shell_tool`, `unified_exec`, and `code_mode_host` features; model-directed
commands need `code_mode_host` even with `code_mode` disabled. Text workers keep
all three off. Both disable web search, browser, and computer-use tools. The pod
has no service-account token, database certificate, or application data volume.
Network policy allows DNS, public HTTP and HTTPS, and the controller's repository
port 8788; other private addresses and all inbound traffic are blocked. Port 8788
accepts only attempt capabilities and serves no UI or workspace API; the public
ports serve no repository operations. This is container isolation, not VM
isolation or protection from a compromised kernel.

## Durable ownership

The host adds these rules to the [Work lifecycle](work-lifecycle.md) claims:

1. Redelivery of a claimed attempt cannot launch it again. Browser disconnection
   does not cancel accepted Work.
2. Text workers use a persistent reservation, a process start identity, and a
   file gate. Each Work has one Sandbox identity. Kubernetes resource versions,
   per-allocation input mounts, and a create-only claim file per attempt and turn
   stop delayed starts and cleanup from taking over a newer allocation or
   repeating a claimed turn.
3. Recovery observes the existing process or pod. Missing proof produces
   attention; it never launches a replacement.
4. Cleanup suspends the Sandbox, waits for its pod to disappear, then deletes the
   attempt's Secret and input ConfigMap. A cleanup failure requires attention and
   explicit reconciliation.

Linux text-worker journals identify a process by PID, kernel start ticks, boot ID,
and PID namespace. Do not use `Process.StartTime` to decide that a process
stopped; the clock can differ between worker and controller.

Suspended Sandboxes and Work PVCs are kept, and there is no automatic volume
expiry or deletion. Keep a Sandbox identity while delayed messages or
controllers could still arrive for it. Runtime logs and sessions are execution evidence; PostgreSQL
owns product history.

The Codex connection check is separate from Work. Overlapping checks are rejected,
and Codex account changes are rejected while an attempt holds the connection.
Queued Work waits while the connection is busy, changing, or being checked. A
controller restart clears a pending check or account change. Do not run several
application controllers on the same credential directory.

## Configuration and deployment

Durable Work is on by default and needs a migrated PostgreSQL database; see the
[database guide](database.md). Set `GOBLIN_WORK_ENABLED=false` to run only the
connection screen; Work APIs are then disabled. `/readyz` checks PostgreSQL when
Work is enabled and does not depend on Codex.

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Goblin` | Application database connection; never forwarded to workers. |
| `GOBLIN_EXECUTION_NAMESPACE` | Enables repository execution and inspection in this namespace. Without it, only text execution is offered. |
| `GOBLIN_EXECUTION_IMAGE` | Worker image, normally the same version as Goblin. |
| `GOBLIN_KUBERNETES_URL` | Optional API address; defaults to the in-cluster API. |
| `GOBLIN_KUBERNETES_TOKEN_FILE`, `GOBLIN_KUBERNETES_CA_FILE` | Controller credential and CA paths; default to the service-account mount. |

Capacity settings are in [workspace lifecycle](workspace-lifecycle.md#capacity-and-retention).

GitHub sign-in uses the bundled, checksum-pinned `gh` CLI. Open **Settings →
GitHub → Connect GitHub**, copy the device code, and authorize **GitHub CLI** on
GitHub. No custom OAuth app or copied token is needed. `gh` requests its standard
scopes; the broker, not the token, limits Goblin to enabled repositories. The CLI
profile is a mode 0600 file in `github-cli/` under the data directory. Host
`GH_TOKEN`, `GITHUB_TOKEN`, and CLI configuration are not inherited. Disconnect
removes the saved profile; revoke remote access in GitHub's authorized application
settings.

GitHub branch rules are a second safeguard; check account bypass privileges.
Branch pushes can trigger existing GitHub Actions with their configured
permissions.

Installation provisions PostgreSQL, runs `goblinctl db migrate IMAGE`, then
deploys the application with the execution RBAC and network policy. Only the
migration job receives the schema-admin certificate. Neither Wolverine nor EF
creates schema at startup.

## Verification limits

`make codex-test` checks the pinned Codex with a synthetic credential; it does
not prove authenticated model execution. PostgreSQL and Wolverine delivery need
the opt-in database suites. Kubernetes network isolation, authenticated
execution, and GitHub branch and PR delivery need live checks with configured
credentials. See [test selection](repository-layout.md#choosing-verification).
