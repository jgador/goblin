# Architecture

Goblin is a modular monolith built around durable Work. Agents are stable Goblin
identities; runtimes execute attempts for those agents. Codex is the only
implemented runtime. PostgreSQL owns product history, Wolverine coordinates
delivery, and the browser shows saved state and submits commands.

The four behavioral boundaries are the first four sections below.

## Work and application coordination

`backend/src/Goblin.Core/Work/` owns lifecycle transitions, decisions, attention,
attempts, and result approval. Core types have no HTTP, persistence, messaging,
process-configuration, runtime SDK, or generated-protocol dependencies.
`Goblin.Application` turns commands into transactional changes and coordinates
connections, execution, recovery, and conversations. `Goblin.Persistence` stores
those changes through SQL-first PostgreSQL mappings.

Work, agent, attempt, and runtime-session identities are separate. Agents outlive
attempts, and accepted attempts outlive HTTP requests. Each attempt records its
agent, runtime, connection, requested and reported model, and execution source.
Runtime sessions are references, not Goblin's transcript. Saved objectives,
context, decisions, outputs, and earlier results supply continuity.

Work state, command receipts, and dispatch intent commit atomically. A repeated
command returns its saved receipt. Dispatch claims need database concurrency
enforcement in addition to core checks. A claim and a durable message do not
guarantee exactly-once external execution.

Every observed dispatch or execution failure requires core-owned attention,
including temporary failures. Message redelivery never authorizes another
execution. Only an explicit retry creates a new attempt, and uncertain execution
must be reconciled first. Confirming that execution stopped resolves uncertainty
but does not retry. Cancellation stays an intent until stopping is confirmed.
A late confirmed result goes to review. A successful runtime turn never approves
or completes Work.

A storage failure must not produce a fake saved transition. Commands report
failure or uncertainty, and durable recovery evidence allows reconciliation when
storage returns. See [Work lifecycle](work-lifecycle.md) and [database ownership](database.md).

## Connections and runtime adapters

`Goblin.Contracts` holds Goblin-owned integration contracts.
`Goblin.Integrations.Codex` owns Codex authentication, transport, generated
protocol translation, model discovery, and session handling. Connection
verification is a restricted operation separate from durable Work. GitHub
repository access, Slack messaging, and read-only ChatGPT sharing have their own
adapters.

Runtime capabilities and connection availability are explicit. Overlapping
verification prompts are rejected. Account changes are rejected while an attempt
holds the connection. Cancellation and timeouts clean up runtime operations. Raw
upstream errors and credentials stay out of public views and Work history.

A second runtime must map to Goblin inputs, events, results, and safe failures
without changing core rules or assuming the Codex App Server protocol. Runtime
switching, context handoff, fallback, and agent collaboration are proposals only.
A possible first experiment is a GitHub Copilot CLI adapter on an isolated test
repository. It must prove its pinned interface, results, fencing, failure
attention, cancellation, credential cleanup, and recovery from the retained
workspace before any capability is advertised. Never add automatic retry or
fallback. See [Codex integration](codex-integration.md), [authentication](authentication.md),
and [model discovery](codex-integration.md#model-selection-and-discovery) for current behavior.

## Execution, isolation, and recovery

`Goblin.Execution` owns workers, process and pod observation, cancellation,
cleanup, and workspace hosting. A text question does not allocate a repository
sandbox. Repository execution requires a GitHub selection, an immutable
Goblin-owned grant, and the configured Kubernetes isolation boundary. It cannot
fall back to the text-only host.

Repository Work has one persistent Sandbox and PVC. Attempts can contain several
runtime turns and survive compute suspension. Files, ignored outputs, and local
Git commits stay on that PVC; PostgreSQL stores checkpoint metadata only.
Suspension does not archive files or authorize volume deletion. There is no
automatic volume reclamation.

Workers receive scoped inputs and credentials. Repository pods have no application
database credentials or Kubernetes service token, run under the restricted pod
security policy, and have public-only egress. Goblin brokers Git operations and
rechecks account generation, repository enablement, branch, and publication
permissions.

Claims commit before external execution starts. Host identity fences, observation,
operation receipts, and explicit reconciliation protect crash recovery and
duplicate delivery. Cleanup failures require attention before continuation.
Closing a browser does not cancel accepted Work. `/readyz` checks only the
application database (when Work is enabled), not Codex, so users can still inspect
Work and repair a failed connection.

See [execution hosting](execution-hosting.md), [workspace lifecycle](workspace-lifecycle.md),
and [repository authorization](git-repository-intent.md).

## Browser commands and persisted views

`Goblin.Web` owns HTTP translation, owner access, request validation, same-origin
security, cookies, and static assets. Endpoints submit application commands and
map saved views and failures into HTTP contracts; they do not own durable
execution. Public requests and responses are classes with explicit
`JsonPropertyName` attributes in `Http/Contracts/`, so C# renames cannot silently
rename JSON fields.

The TypeScript browser displays server-owned Work state. Creation, context,
decisions, approvals, and execution controls use versioned commands. An
unconfirmed submission keeps its original command ID for safe resubmission.
Polling and reconnect read the saved state. Selection, filters, disclosures, and
unsent drafts are presentation state. Sample fixtures never drive the live
application.

Keep the current frontend technology, visual design, branding, accessibility,
escaping, and HTTP security when changing state ownership. See the
[Work interface](work-centered-ui.md) and [frontend maintenance plan](typescript-refactoring.md).

## Verification boundaries

Existing transport, protocol, HTTP, and browser tests protect their boundaries;
use [test selection](repository-layout.md#choosing-verification) to choose checks.
Fixtures and protocol initialization cannot prove PostgreSQL transaction behavior,
authenticated model execution, GitHub publication, or support for a second
runtime. Those claims need actual integration runs. Report opt-in test skips and
live-service gaps.
