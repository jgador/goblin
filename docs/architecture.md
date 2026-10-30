# Architecture

Goblin is a modular monolith built around durable Work. Agents are stable Goblin
identities; runtimes execute attempts for those agents. Codex is the only
implemented runtime. PostgreSQL owns product history, Wolverine coordinates
delivery, and the browser observes persisted state and submits commands.

This guide describes the implemented boundaries. It replaces the completed
architecture rewrite plan; it is not an assignment to repeat that rewrite.

## Work and application coordination

`backend/src/Goblin.Core/Work/` owns lifecycle transitions, decisions, attention,
attempts, and result approval. Core types have no HTTP, persistence, messaging,
process-configuration, runtime SDK, or generated-protocol dependencies.
`Goblin.Application` translates commands into transactional changes and coordinates
connections, execution, recovery, and conversation tracking. `Goblin.Persistence`
stores those changes through SQL-first PostgreSQL mappings.

Work, agent, attempt, and runtime-session identities are distinct. Agents outlive
attempts, and accepted attempts outlive HTTP requests. Each attempt records its
agent, runtime, connection, requested/reported model, and execution provenance.
Runtime sessions are references to execution context, not Goblin's transcript.
Persisted objectives, context, decisions, outputs, and prior results supply continuity.

Work state, command receipts, and dispatch intent commit atomically. Duplicate
commands return their saved receipt. Dispatch claims require database concurrency
enforcement in addition to core validation. A claim and a durable message do not
guarantee exactly-once external execution.

Every observed dispatch or execution failure requires core-owned attention,
including temporary failures. Message redelivery does not authorize another
execution. Explicit retry creates a new attempt; uncertain execution must first
be reconciled. Confirming that execution stopped resolves uncertainty without
authorizing retry. Cancellation remains an intent until stopping is confirmed.
A late confirmed result goes to review, and a successful runtime turn never
approves or completes Work automatically.

Storage failure must not produce a fabricated saved transition. Commands report
failure or uncertainty, and durable recovery evidence allows reconciliation when
storage returns. See [Work lifecycle](work-lifecycle.md) and [database ownership](database.md).

## Connections and runtime adapters

`Goblin.Contracts` contains Goblin-owned integration contracts.
`Goblin.Integrations.Codex` owns Codex authentication, transport, generated protocol
translation, model discovery, and session mechanics. Connection verification is
a restricted operation separate from durable Work execution. GitHub repository
access and Slack ingress/delivery have their own adapters.

Runtime capabilities and connection availability are explicit. Overlapping
verification prompts are rejected; account changes wait for the active verification
operation. Active attempts, credential changes, and cleanup use connection
reservation rules. Cancellation and timeouts clean up runtime operations. Raw
upstream errors and credentials stay out of public views and Work history.

Future runtimes must map to Goblin inputs, events, results, and safe failures
without changing core rules or assuming a Codex App Server protocol. Their
supported interface and capabilities require validation against a real integration.
Runtime switching, context handoff, fallback, and collaboration between agents
remain proposals. A possible first experiment is a GitHub Copilot CLI adapter,
using a separately authenticated, isolated test repository and a persisted Work
snapshot. Validate its pinned interface, progress and result handling, fencing,
failure attention, cancellation, credential cleanup, and recovery from the
retained workspace. Advertise only verified capabilities; do not enable native
resume or live approvals without testing them. An explicitly authorized handoff
may compare a completed Codex branch and Goblin context with a new attempt.
Never automatically retry uncertain work or introduce fallback.
See [Codex integration](codex-integration.md), [authentication](authentication.md),
and [model discovery](codex-integration.md#model-selection-and-discovery) for current behavior.

## Execution, isolation, and recovery

`Goblin.Execution` owns workers, process/pod observation, cancellation, cleanup,
and workspace hosting. A text question does not allocate a repository sandbox.
Repository execution requires a GitHub selection, an immutable Goblin-owned
grant, and the configured Kubernetes isolation boundary. It cannot fall back to
the text-only host.

Repository Work has one persistent Sandbox/PVC. Attempts can contain multiple
runtime turns and survive compute suspension. Files, ignored outputs, and local
Git commits stay on that PVC; PostgreSQL stores checkpoint provenance only.
Suspension does not archive files or authorize volume deletion. Automatic volume
reclamation is disabled pending a separate retention policy.

Workers receive scoped inputs and credentials. Repository pods have no application
database credentials or Kubernetes service token and use restricted pod policy
and public-only egress. Goblin brokers Git operations and rechecks account
generation, repository enablement, branch, and publication permissions.

Claims commit before external execution starts. Host identity fences, observation,
operation receipts, and explicit reconciliation protect crash recovery and duplicate
delivery. Cleanup failures require attention before continuation. Closing a browser
does not cancel accepted Work. `/readyz` checks the enabled application database
dependency independently of Codex availability, so users can inspect Work and
repair a failed connection.

See [execution hosting](execution-hosting.md), [workspace lifecycle](workspace-lifecycle.md),
and [repository authorization](git-repository-intent.md).

## Browser commands and persisted views

`Goblin.Web` owns HTTP translation, owner access, request validation, same-origin
security, cookies, and static assets. Endpoints submit application commands and
map saved views and failures into HTTP contracts; they do not own durable execution.

The TypeScript browser displays server-owned Work state. Creation, context,
decisions, approvals, and execution controls use versioned commands. An unconfirmed
submission keeps its original command identity for safe resubmission. Polling and
reconnect retrieve authoritative state. Selection, filters, disclosures, and unsent
drafts are presentation state. Sample fixtures never drive the live application.

Preserve the current frontend technology, visual design, branding, accessibility,
escaping, and HTTP security when changing state ownership. See the
[Work interface](work-centered-ui.md) and [frontend maintenance plan](typescript-refactoring.md).

## Verification boundaries

Core changes require core tests; owning C#/Rust contracts require generated-contract
checks. Existing transport, protocol, HTTP, and browser tests protect their respective
boundaries. Use [test selection](repository-layout.md#choosing-verification) to choose
the relevant checks.

Durability and authenticated execution claims require actual integration runs.
Fixtures and protocol initialization cannot establish PostgreSQL transaction behavior,
authenticated model execution, GitHub publication, or support for a second runtime.
Report opt-in test skips and live-service gaps explicitly.
