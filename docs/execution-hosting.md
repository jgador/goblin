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

Repository execution must name `owner/repository` explicitly. The UI also asks
for the Goblin agent's Git author name and email; they are saved with the attempt.
The host allocates an Agent Sandbox and a private PVC in `goblin-executions`.
The worker clones that repository, creates a unique
`goblin/<work-id>/<attempt-id>` branch, and uses the GitHub credential connected
inside Goblin. A completed interaction commits changes and pushes that branch.
It never targets the default branch automatically. Follow-ups start a fresh
sandbox from the last saved branch for that repository. Failed or uncertain
workspaces remain on their PVC for investigation.

The worker image has non-root execution, a read-only root filesystem, dropped
capabilities, resource limits, and a one-hour deadline. Repository tools may edit
and execute inside that sandbox without interactive command approval. The pod
has no application data volume, database certificate, or service-account token.
Network policy allows DNS and public HTTP(S), and blocks private networks and
inbound traffic. This uses Kubernetes container isolation on the installed
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
| `GOBLIN_GITHUB_CLIENT_ID` | GitHub OAuth application's public client ID; enable device flow for that application. |

GitHub sign-in uses device authorization with `repo` and `read:user` scopes. Set
its client ID in the Goblin container configuration, then use **Connect GitHub**
in Work. The access token is stored privately in Goblin's data volume and copied
only into the selected repository execution. Tokens never enter Work JSON, git
remote URLs, command arguments, or browser views. **Disconnect GitHub** removes
Goblin's local credential; remote token revocation remains available in GitHub's
authorized-app settings. Codex/OpenAI sign-in remains separate.

The Azure/local installer provisions PostgreSQL, verifies certificate login,
runs `deploy/postgres/migrate.sh IMAGE`, then deploys the application and execution
RBAC/network policy. The migration job alone receives the schema-admin client
certificate. Neither Wolverine nor EF creates schema at application startup.

## Verification limits

Tests exercise real PostgreSQL transactions and Wolverine delivery, process
fencing/cancellation, contract dependencies, connection reservations, cleanup
failure recovery, and browser decisions/approvals/repeated commands. A disposable
Kubernetes namespace exercises the generated isolation policy and real Codex
initialization, result observation, suspension, and credential cleanup.

A successful authenticated model task and GitHub commit/push require configured
service credentials. Those operations were not exercised in this implementation
session. No cross-runtime continuation or second production integration is
claimed; see [the bounded experiment](second-runtime-experiment.md).
