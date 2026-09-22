# Execution hosting and recovery

Goblin runs one application controller with PostgreSQL and isolated repository
executions. Work is durable whether or not it needs a sandbox. Codex 0.155.1 is
the implemented runtime; the Work core and orchestration do not reference its
protocol. Agent, Work, attempt, connection, and native session IDs stay distinct.

## Choosing an environment

A text question uses an independent worker process with tools and web search
disabled. It does not allocate an Agent Sandbox or receive database credentials.
This first text capability answers from supplied context; live research tools
and a formal question/investigation taxonomy remain deferred.

Repository execution selects a repository enabled under **Settings → GitHub**.
The agent's Git author name/email remain separate from the connected GitHub account.
Each attempt records the GitHub account identity and connection generation,
repository ID, base branch, policy version, and `goblin/<work-id>/<attempt-id>` branch.
It gets a fresh Agent Sandbox and private PVC named
`run-<work-id>-<attempt-number>` in `agents`. The suffix starts at 1 for each Work
and advances with each new execution; the global attempt ID remains unchanged.
The pod uses the Sandbox's name. Environment references record the namespace,
Work ID, and global attempt ID (`k8s/agents/run-<work-id>/<attempt-id>`), with the
pod suffix derived from the durable attempt history. See
[Kubernetes names](kubernetes-names.md) for the deployment layout.

The sandbox receives repository content as a Git bundle and an attempt capability.
It never receives the GitHub token. A trusted repository broker in the Goblin
controller validates operations against the persisted attempt and processes Git
objects in its own bare repository; agent Git configuration and hooks never enter
that repository. `goblin-github publish` publishes commits to the exact assigned
branch. `goblin-github pull-request` opens its draft PR (later branch publications
update the same PR). `goblin-github fetch` refreshes the sandbox's origin references.
The completed interaction also publishes a checkpoint automatically. Merging,
auto-merge, other branches, tags, and repository administration are unavailable.

Publication commands are persisted with Wolverine dispatch intent. Repeated command
IDs cannot publish again. An uncertain response is reconciled by checking the exact
remote branch commit or PR; failed Work is never automatically retried. External
CLI processes have a 120-second watchdog and the operation has a 120-second deadline.
After controller loss, a saved boot identity and monotonic uptime bound child lifetime;
remote absence alone is not proof that a write stopped. A replacement attempt gets
a different branch and can continue from the previous published checkpoint.

Account/repository changes wait for queued, active, uncertain, and cleanup-pending
repository attempts. A different GitHub account disables the existing repository
allowlist until it is explicitly configured again. Connection secrets and CLI
output are excluded from Work history and browser responses.

The worker image has non-root execution, a read-only root filesystem, dropped
capabilities, resource limits, and a one-hour deadline. Repository tools may edit
and execute inside that sandbox without interactive command approval. Repository
workers enable Codex's `shell_tool`, `unified_exec`, and `code_mode_host` features;
the command host is required for model-directed commands even with the optional
`code_mode` feature disabled. Text workers keep all three execution features off.
The pod has no application data volume, database certificate, or service-account token.
Network policy allows DNS, public HTTP(S), and the controller’s internal repository
port (8788). Other private network access and inbound traffic are blocked. The
repository port accepts only attempt capabilities; it cannot serve the UI or
workspace APIs, and public UI ports cannot serve repository operations. This uses Kubernetes container isolation on the installed
runtime; it does not claim VM isolation or protection from a compromised kernel.

## Durable ownership

1. PostgreSQL commits Work, attempt identity, and dispatch intent together through
   Wolverine's outbox. Browser disconnection does not cancel accepted Work.
2. A short database transaction claims a queued attempt and its environment
   before contacting the execution host. Redelivery of a claimed attempt cannot
   launch it again. One active or cleanup-pending attempt can use a connection.
3. Text workers use a persistent reservation, a process start identity, and a
   file gate. Sandboxes use deterministic, create-only identities and a PVC
   execution fence. Reconciliation can create a suspended identity before a
   delayed starter arrives. Replacement pods cannot repeat an uncertain attempt.
4. Recovery observes the existing process/pod. Missing proof produces attention;
   it never launches a replacement. Failed delivery, runtime errors, cancellation
   errors, and storage failures have no automatic Work retry. A local durable
   journal carries failure evidence across a database outage.
5. Cancellation is intent until the host confirms stopping. A confirmed late
   result is retained for review. Retry is a separate explicit command and creates
   a new attempt after uncertainty and cleanup are resolved.
6. Outcomes and cleanup intent commit before credentials or runtime evidence are
   removed. Cleanup suspends the Sandbox, waits for its pods to disappear, and
   deletes its Secret and input ConfigMap. A cleanup failure becomes core-owned
   Needs attention and requires explicit reconciliation.

Linux text-worker journals identify a process by PID, kernel start ticks, boot ID,
and PID namespace. Wall-clock `Process.StartTime` values can differ between the
worker and controller and must not be used to establish that a process stopped.
Older timestamp-only journals remain uncertain unless a saved outcome is available;
they cannot authorize killing a process or launching a replacement. Observation
also rereads the outcome after detecting exit so a just-published response is
preserved. This does not reopen attempts already finalized as failed by an older
version; recovering those saved outcomes requires a separate explicit repair.

Suspended Sandbox identities and workspace PVCs are retained. Do not delete
identity fences while messages or controllers from those attempts could still
arrive. Retention/archival automation is not implemented; an operator can inspect
and archive stopped workspaces. The database remains the source of product
history; runtime logs and sessions are only execution evidence.

Connection verification is separate from durable Work. Overlapping verification
prompts are rejected; account changes wait for verification and cannot change
credentials used by active or uncertain Work. Queued Work waits for a busy
connection. A controller restart invalidates its temporary verification/account
reservation, then refreshes connection availability. Multiple application
controllers sharing the same credential directory are not supported.

## Configuration and deployment

Durable Work is enabled by default and requires a migrated PostgreSQL database.
The [database guide](database.md) describes local connections. For a connection
screen only, set `GOBLIN_WORK_ENABLED=false`; Work APIs are then disabled.
`/readyz` checks PostgreSQL when Work is enabled and does not depend on Codex.
An unavailable runtime remains visible through connection state and Work
attention while the application and history stay accessible.

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Goblin` | Application database connection; never forwarded to workers. |
| `GOBLIN_EXECUTION_NAMESPACE` | Enables repository hosting in the installed execution namespace. Without it, only text capability is advertised. |
| `GOBLIN_EXECUTION_IMAGE` | Worker image, normally the same version as Goblin. |
| `GOBLIN_KUBERNETES_URL` | Optional API address; defaults to the in-cluster API. |
| `GOBLIN_KUBERNETES_TOKEN_FILE`, `GOBLIN_KUBERNETES_CA_FILE` | Controller credentials/trust paths, mounted only in the app. |

GitHub sign-in uses the bundled official `gh` CLI (2.101.0, checksum-pinned for
amd64 and arm64). Open **Settings → GitHub → Connect GitHub**, copy the device
code, and authorize **GitHub CLI** on GitHub. No custom OAuth app, client ID,
callback URL, or manually copied access token is needed. `gh` requests its standard
OAuth scopes; Goblin's repository allowlist is enforced by the broker, not by a
branch-scoped OAuth token.

The private CLI profile lives under the persisted data directory's `github-cli/`.
Headless containers use a private file with mode 0600 rather than a system keyring.
No host `GH_TOKEN`, `GITHUB_TOKEN`, browser command, or CLI configuration is inherited.
The existing custom OAuth connection needs one new CLI sign-in; its legacy token is
never imported into the new profile. Disconnect removes Goblin's saved profile;
remote revocation is available in GitHub's authorized application settings.

GitHub branch rules provide a second safeguard; account bypass privileges matter.
Branch publication can trigger existing GitHub Actions under their configured
permissions. The broker's branch restriction does not reconfigure those workflows.

The Azure/local installer provisions PostgreSQL, verifies certificate login,
runs `deploy/postgres/migrate.sh IMAGE`, then deploys the application and execution
RBAC/network policy. The migration job alone receives the schema-admin client
certificate. Neither Wolverine nor EF creates schema at application startup.

## Verification limits

Tests exercise real PostgreSQL transactions and Wolverine delivery, process
fencing/cancellation, contract dependencies, connection reservations, cleanup
failure recovery, and browser decisions/approvals/repeated commands. The broker
is additionally tested with real local Git bundles and a bare remote. The packaged
non-root image obtained a real GitHub device code and cancelled sign-in without
installing credentials. The pinned Codex initialization, isolated credential
storage, process replacement, and logout checks also passed.

The local PostgreSQL run used password authentication; six certificate-specific
tests were skipped, so it does not establish certificate authentication coverage.
Live Kubernetes validation of the updated repository network route is still
required; it was not performed in this session.

The command-host regression runs the pinned Codex binary against a local model
fixture. It verifies that a model-issued nested command writes a file for repository
Work and remains blocked for text Work. This catches a disabled `code_mode_host`,
which a direct app-server `command/exec` check does not detect.

A subsequent WSL check used the connected ChatGPT account and the corrected
production adapter in a Kubernetes container with the execution security settings.
Codex successfully ran commands, wrote a file, and made a local Git commit. The
temporary pod and credential Secret were removed afterward. This check did not
publish to GitHub or open a PR; those workflows still need an authenticated test
of the complete repository path. No cross-runtime continuation or second production
integration is claimed; see [the bounded experiment](second-runtime-experiment.md).
