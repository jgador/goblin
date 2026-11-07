# Execution hosting and recovery

Goblin runs one application controller with PostgreSQL and isolated executions. Codex is
the current runtime; the Work core and orchestration do not reference its protocol.
Agent, Work, attempt, connection, and native session IDs stay separate. Claims,
cancellation, retry, and attention follow [Work lifecycle](work-lifecycle.md). The Work
PVC, turns, and Git checkpoints are in [workspace lifecycle](workspace-lifecycle.md).

## Choosing an environment

A text question runs in a separate local worker process with tools disabled. It gets no
Sandbox and no database credentials, and answers from the supplied Work context.

Repository execution needs a repository enabled in Settings, or one enabled through a
saved authorization preview in the Work conversation. Each attempt records the GitHub
account and connection generation, repository ID, base branch, policy version, and
branch `goblin/<work-id>/<attempt-id>`. The agent's Git author name and email are
separate from the connected GitHub account. The first repository allocation for a Work
creates a Sandbox and private PVC named `work-<work-id>` in the execution namespace
(`agents` in the deployed manifests). Recorded references preserve the namespace and
Work identity; see [storage ownership](architecture.md#resource-and-storage-ownership).

The sandbox receives repository content as a Git bundle plus an attempt capability. It
never receives the GitHub token. The repository broker in the controller checks each
operation against the saved attempt and handles Git objects in its own bare repository;
agent Git configuration and hooks never enter it. `goblin-github publish` pushes to the
assigned branch only. `goblin-github pull-request` opens a draft PR; later pushes update
it. `goblin-github fetch` refreshes the sandbox's origin branches. At the end of a turn
the worker publishes only when push is approved; otherwise the broker verifies a local
Git checkpoint without publishing. Merging, auto-merge, other branches, tags, and
repository administration are not available.

Publication commands are saved with Wolverine dispatch intent, and a repeated command ID
cannot publish again. External processes and complete operations have enforced time
bounds. An uncertain response is reconciled by checking the exact remote branch commit
or PR. After controller loss, a saved boot ID and system uptime bound how long a child
process could still run; remote absence alone does not prove a write stopped. A
replacement attempt gets a new branch and continues from the local Git HEAD on the Work
PVC, including unpublished commits. A missing volume requires attention.

GitHub account and repository changes are rejected while a repository attempt is queued,
running, uncertain, cleanup-pending, or holding compute. Signing in with a different
GitHub account disables all enabled repositories. Connection secrets and CLI output stay
out of Work history and browser responses.

Repository tools run without interactive approvals inside the restricted pod
configuration defined by the
[manifest builder](../backend/src/Goblin.Execution/SandboxManifestBuilder.cs) and
[execution policy](../deploy/platform/execution.yaml). Pods run non-root with read-only
roots, dropped capabilities, resource limits, and a deadline. They have no database
credentials, application volume, or Kubernetes service token.

Network policy allows DNS, public HTTP/HTTPS, and Goblin's capability-authenticated
repository broker only. Private networks and inbound access are blocked. The broker port
serves no browser/API traffic; public ports serve no repository operations. Text workers
have tools disabled; repository workers enable only the intended Codex execution tools.
Neither exposes browser or computer-use tools. Codex's model-directed command execution
requires `code_mode_host` even when `code_mode` is disabled; preserve that dependency in
repository worker options. This is container isolation, not VM isolation or protection
against a compromised kernel.

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

Linux text-worker journals identify a process by PID, kernel start ticks, boot ID, and
PID namespace. Do not use `Process.StartTime` to decide that a process stopped; the
clock can differ between worker and controller.

Suspended Sandboxes and Work PVCs are kept, and there is no automatic volume expiry or
deletion. Keep a Sandbox identity while delayed messages or controllers could still
arrive for it. Runtime logs and sessions are execution evidence; PostgreSQL owns product
history.

The Codex connection check is separate from Work. Overlapping checks are rejected, and
Codex account changes are rejected while an attempt holds the connection. Queued Work
waits while the connection is busy, changing, or being checked. A controller restart
clears a pending check or account change. Do not run several application controllers on
the same credential directory.

## Configuration and deployment

Durable Work is on by default and needs a migrated PostgreSQL database; see the
[database guide](database.md). Set `GOBLIN_WORK_ENABLED=false` to run only the
connection screen; Work APIs are then disabled. `/readyz` checks PostgreSQL when Work is
enabled and does not depend on Codex.

[Environment catalogs](environment-variables.md) describe supported settings.
[WebRuntimeConfiguration](../backend/src/Goblin.Web/Hosting/WebRuntimeConfiguration.cs)
owns controller defaults; [platform manifests](../deploy/platform/) own installed
credentials, networking, and overrides. Without an execution namespace only text
execution is offered. Never forward the controller's database or Kubernetes credentials
to workers.

Capacity settings are in
[workspace lifecycle](workspace-lifecycle.md#capacity-and-retention).

GitHub sign-in uses the bundled, checksum-pinned `gh` CLI. Open **Settings → GitHub →
Connect GitHub**, copy the device code, and authorize **GitHub CLI** on GitHub. No
custom OAuth app or copied token is needed. `gh` requests its standard scopes; the
broker, not the token, limits Goblin to enabled repositories. The CLI profile is a mode
0600 file in `github-cli/` under the data directory. Host `GH_TOKEN`, `GITHUB_TOKEN`,
and CLI configuration are not inherited. Disconnect removes the saved profile; revoke
remote access in GitHub's authorized application settings.

GitHub branch rules are a second safeguard; check account bypass privileges. Branch
pushes can trigger existing GitHub Actions with their configured permissions.

Installation provisions PostgreSQL, runs `goblinctl db migrate IMAGE`, then deploys the
application with the execution RBAC and network policy. Only the migration job receives
the schema-admin certificate. Neither Wolverine nor EF creates schema at startup.

## Verification limits

`make codex-test` checks the pinned Codex with a synthetic credential; it does not prove
authenticated model execution. PostgreSQL and Wolverine delivery need the opt-in
database suites. Kubernetes network isolation, authenticated execution, and GitHub
branch and PR delivery need live checks with configured credentials. See
[test selection](repository-layout.md#choosing-verification).
