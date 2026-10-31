# Execution hosting and recovery

Goblin runs one application controller with PostgreSQL and isolated repository
executions. Work is durable whether or not it needs a sandbox. Codex 0.161.0 is
the implemented runtime; the Work core and orchestration do not reference its
protocol. Agent, Work, attempt, connection, and native session IDs stay separate.
Multi-turn attempts and the Work PVC are described in
[workspace lifecycle](workspace-lifecycle.md).

## Choosing an environment

A text question runs in a separate local worker process with tools disabled. It
does not allocate an Agent Sandbox or receive database credentials, and it answers
from the supplied Work context.

Repository execution needs a repository enabled in Settings, or one explicitly
enabled through a saved authorization preview in the Work conversation. The
agent's Git author name and email are separate from the connected GitHub account.
Each attempt records the GitHub account identity and connection generation,
repository ID, base branch, policy version, and `goblin/<work-id>/<attempt-id>`
branch. The first repository allocation for a Work creates a Sandbox and private
PVC named `work-<work-id>` in the `agents` namespace. Explicit retries and
revisions reuse them with new attempt permissions. Suspended compute resumes as a
new pod on the same PVC; retained compute picks up later turns by polling Goblin
for the next claimed turn. Environment references are `k8s/<namespace>/work-<work-id>`.
See [Kubernetes names](kubernetes-names.md).

The sandbox receives repository content as a Git bundle plus an attempt
capability. It never receives the GitHub token. A trusted repository broker in the
Goblin controller checks each operation against the saved attempt and handles Git
objects in its own bare repository; agent Git configuration and hooks never enter
that repository. `goblin-github publish` pushes commits to the exact assigned
branch. `goblin-github pull-request` opens a draft PR (later pushes update the same
PR). `goblin-github fetch` refreshes the sandbox's origin branches. At the end of
a turn the worker publishes only when push is approved; otherwise the broker
verifies a local Git checkpoint without publishing. PostgreSQL stores only commit
metadata; workspace files stay on the PVC. Merging, auto-merge, other branches,
tags, and repository administration are not available.

Publication commands are saved with Wolverine dispatch intent. A repeated command
ID cannot publish again. An uncertain response is reconciled by checking the
exact remote branch commit or PR; failed Work is never retried automatically.
Each external CLI process has a 120-second watchdog, and the whole operation has
a 120-second deadline. After controller loss, a saved boot ID and system uptime
bound how long a child could still run; remote absence alone does not prove a
write stopped. A replacement attempt gets a new branch and continues from the
surviving local Git HEAD on the Work PVC, including unpublished commits. A missing
volume requires attention.

GitHub account and repository changes are rejected while a repository attempt is
queued, active, uncertain, cleanup-pending, or holding compute. Signing in with a
different GitHub account disables all enabled repositories until they are enabled
again. Connection secrets and CLI output are kept out of Work history and browser
responses.

The sandbox pod runs as non-root with a read-only root filesystem, dropped
capabilities, resource limits, and a one-hour deadline. Repository tools may edit
and run commands inside the sandbox without interactive approval. Repository
workers enable Codex's `shell_tool`, `unified_exec`, and `code_mode_host`
features; model-directed commands need the command host even with `code_mode`
disabled. Text workers keep all three off. Both worker kinds disable web search,
browser, and computer-use tools. The pod has no application data volume, database
certificate, or service-account token. Network policy allows DNS, public HTTP(S),
and the controller's internal repository port (8788); other private network
access and all inbound traffic are blocked. The repository port accepts only
attempt capabilities and cannot serve the UI or workspace APIs, and public UI
ports cannot serve repository operations. This is Kubernetes container isolation
on the installed runtime, not VM isolation or protection from a compromised
kernel.

## Durable ownership

Claims, cancellation, retry, and attention follow [Work lifecycle](work-lifecycle.md).
The host adds these rules:

1. A short database transaction claims a queued attempt and its environment
   before Goblin contacts the host. Redelivery of a claimed attempt cannot launch
   it again. Browser disconnection does not cancel accepted Work.
2. Text workers use a persistent reservation, a process start identity, and a
   file gate. Each Work has a stable Sandbox identity. Versioned ownership changes,
   separate input mounts, and a claim file per attempt and turn stop delayed
   operations from affecting a newer allocation. Reconciliation can create a
   suspended Sandbox before a delayed starter arrives. Replacement pods cannot
   repeat a claimed turn.
3. Recovery observes the existing process or pod. Missing proof produces
   attention; it never launches a replacement. A local durable journal keeps
   failure evidence across a database outage.
4. Outcomes and cleanup intent commit before credentials or runtime evidence are
   removed. Cleanup suspends the Sandbox, waits for its pod to disappear, and
   deletes its Secret and input ConfigMap. A cleanup failure requires attention
   and explicit reconciliation.

Linux text-worker journals identify a process by PID, kernel start ticks, boot ID,
and PID namespace. Do not use wall-clock `Process.StartTime` to decide that a
process stopped; it can differ between the worker and controller. Observation
rereads the outcome after detecting exit so a just-written result is kept.

Suspended Sandbox identities and Work PVCs are kept. Identity fences must stay
while delayed messages or controllers could still arrive. There is no automatic
volume reclamation or expiry; export and deletion need a separate retention
policy. Runtime logs and sessions are execution evidence; PostgreSQL owns product
history.

Connection verification is separate from durable Work. Overlapping verification
prompts are rejected, and Codex account changes are rejected while an attempt
holds the connection. Queued Work waits while its connection is busy, changing,
or being verified. A controller restart clears its temporary verification or
account-change state, then refreshes connection availability. Running several
application controllers on the same credential directory is not supported.

## Configuration and deployment

Durable Work is on by default and needs a migrated PostgreSQL database; see the
[database guide](database.md). To run only the connection screen, set
`GOBLIN_WORK_ENABLED=false`; Work APIs are then disabled. `/readyz` checks
PostgreSQL when Work is enabled and does not depend on Codex. An unavailable
runtime shows in connection state and Work attention while the application and
history stay available.

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Goblin` | Application database connection; never forwarded to workers. |
| `GOBLIN_EXECUTION_NAMESPACE` | Enables repository hosting in this namespace. Without it, only text execution is offered. |
| `GOBLIN_EXECUTION_IMAGE` | Worker image, normally the same version as Goblin. |
| `GOBLIN_KUBERNETES_URL` | Optional API address; defaults to the in-cluster API. |
| `GOBLIN_KUBERNETES_TOKEN_FILE`, `GOBLIN_KUBERNETES_CA_FILE` | Controller credential and CA paths, mounted only in the app. |

GitHub sign-in uses the bundled official `gh` CLI (2.101.0, checksum-pinned for
amd64 and arm64). Open **Settings → GitHub → Connect GitHub**, copy the device
code, and authorize **GitHub CLI** on GitHub. No custom OAuth app, client ID,
callback URL, or copied access token is needed. `gh` requests its standard OAuth
scopes; the broker, not the token, enforces Goblin's repository allowlist.

The CLI profile lives in `github-cli/` under the data directory, in a private file
with mode 0600 instead of a system keyring. No host `GH_TOKEN`, `GITHUB_TOKEN`,
browser command, or CLI configuration is inherited. Disconnect removes Goblin's
saved profile; revoke remote access in GitHub's authorized application settings.

GitHub branch rules are a second safeguard; account bypass privileges matter.
Branch pushes can trigger existing GitHub Actions with their configured
permissions. The broker's branch restriction does not change those workflows.

Installation provisions PostgreSQL, verifies certificate login, runs
`goblinctl db migrate IMAGE`, then deploys the application and the execution
RBAC and network policy. Only the migration job receives the schema-admin client
certificate. Neither Wolverine nor EF creates schema at startup.

## Verification limits

Available tests cover core contracts, process fencing and cancellation,
connection reservations, cleanup recovery, browser commands, and local Git bundles
and bare remotes. PostgreSQL transaction and Wolverine delivery coverage needs the
opt-in database suites; certificate coverage needs their TLS fixture.

`make codex-test` checks pinned Codex initialization, storage, process replacement,
and logout with a synthetic credential. It does not prove authenticated model
execution. Kubernetes network and isolation checks and full GitHub publication
are separate integration checks. Use [test selection](repository-layout.md#choosing-verification).

The command-host regression in `Goblin.Tests` runs the pinned Codex binary against
a local model fixture. It checks that a model-issued nested command writes a file
for repository Work and stays blocked for text Work. This catches a disabled
`code_mode_host`, which a direct app-server `command/exec` check misses.

Authenticated execution and GitHub branch and PR delivery need configured service
credentials and explicit live checks. Cross-runtime continuation is a
[proposal](architecture.md#connections-and-runtime-adapters).
