# Work lifecycle core

`backend/src/Goblin.Core/Work/` owns the product model used by the live
application. `Goblin.Application` persists its versioned snapshots, commands,
conversations, and attempt projections in PostgreSQL and dispatches through
Wolverine. Runtime adapters and the UI consume Goblin-owned contracts.

The [workspace lifecycle](workspace-lifecycle.md) describes multi-turn attempts,
Git checkpoint metadata, persistent PVCs, and read-only inspection.

The [Work-centered workspace](work-centered-ui.md) documents the UI hierarchy,
its projections of these contracts, and deferred backend/schema capabilities.

## Product identities

Goblin identities are positive C# `long` / PostgreSQL `bigint` values. The browser
keeps IDs and version numbers as decimal strings because JavaScript numbers cannot
represent the full 64-bit range. Work HTTP responses encode `long` values as strings
and accept decimal strings in commands. Runtime session references remain opaque
strings; Wolverine retains its own storage types.

Before constructing a new command, the browser posts `{ "kinds": ["Command", "Work"] }`
to `/api/identities` and receives `{ "ids": ["1", "1"] }` in the requested order.
The authenticated endpoint accepts one to four entries from `Command`, `Work`,
`Conversation`, and `Message`. Each uses its table's identity sequence, so values
can coincide across tables. The concrete command, including its reserved IDs,
is retained for resubmission after an unconfirmed response. An allocation failure
creates no pending command or Work; gaps in sequences are expected.

Attempts reserve their ID before state and dispatch intent commit. Claim owners,
decisions, and core context messages use `work_event_ids`, independent of table
identities. A context entry is not identified by its source command/message ID:
those IDs come from separate sequences and may coincide. Core snapshots use
schema version 2 for the bigint model. Earlier GUID snapshots and host journals
are incompatible with this unreleased schema change.

## Attention belongs to Work

The user selected **"Surface every failure for attention"** on 2026-09-19.
There is no exception for temporary failures or failures before execution starts.
An execution attempt can fail while the Work item remains open and needs
attention. Successful runtime execution also does not complete Work by itself.

| Attention reason | What must happen next |
| --- | --- |
| `InputRequired` | Answer the recorded decision; a paused attempt queues its next runtime turn. Older completed interactions become Ready. |
| `RepositoryRequired` | Select an enabled repository and Git author identity, then explicitly authorize repository access for this Work. No repository execution is dispatched before authorization. |
| `ResultReview` | Approve the specific result or request changes. Approval completes Work. |
| `Failure` | Explicitly request a retry, or cancel Work. A retry records a new attempt. |
| `UncertainExecution` | Reconcile the existing execution before allowing a retry, or request cancellation and await confirmation. |
| `CleanupRequired` | Reconcile failed execution cleanup before approving, answering, or starting another attempt. The outcome remains saved. |

The attention reason belongs to the aggregate. The browser should present it and
its permitted actions. It must not infer successful actions from local clicks or
collapse uncertain execution into an ordinary retryable failure.

Answering a paused input request records intent and atomically queues the next
runtime turn of the same attempt. An AI keep/release decision controls workspace
continuity through verified checkpoints. Answering older completed interactions
and requesting changes after a result return Work to Ready for an explicit new
execution. Core transitions never launch processes. Live tool approvals remain a
separate runtime capability.

## Execution ownership and recovery

Work, assigned agent, attempt, connection, and runtime session are separate
identities. Every attempt captures its agent and execution target. Requested model
selection is separate from the model and opaque session references reported by
the runtime. Later assignments/configuration must not rewrite earlier attempts.

The normal execution path is `Queued` → `Starting` → `Running` → `Succeeded`.
`TryClaimExecution` records the owner and environment reference before an external
launch. Repeated claims return false, even for the original owner. Notifications
for another owner or an older attempt cannot change current Work.
A delayed start acknowledgement can add missing provenance without reverting a
result, cancellation, or failure attention to running.

Failure before a claim moves Work to attention. Once an execution may have
started, the adapter must distinguish a **confirmed stopped failure** from an
**uncertain outcome**. A timeout or lost connection alone is not proof that the
execution stopped. An uncertain attempt cannot be replaced. Confirmation that
it stopped leaves failure attention pending; only an explicit retry creates the
next attempt. A recovered result goes to review and retains the earlier failure
in history.

Cancellation before a claim prevents dispatch immediately. After a claim,
cancellation records intent and waits for host confirmation. A delayed start
notification cannot erase cancellation. If execution completed before cancellation
took effect, its actual result goes to review. Failed cancellation requires
attention; it does not assert that the process has stopped.

The database serializes short command transactions and enforces one active or
cleanup-pending attempt per connection. State, history, command receipts, and
Wolverine dispatch intent commit together. Claims commit before contacting the
host. Command IDs plus payload fingerprints prevent repeated submissions from
repeating transitions; expected versions reject stale actions.

Each store operation creates and disposes its own EF Core context through
`IDbContextFactory<GoblinDbContext>`. Transactional reads, the advisory lock, and
writes use that same context. Work commands enroll it in a fresh Wolverine
outbox so buffered messages cannot carry over from another operation, including
one that rolled back. Factory-created contexts do not share transactions.

Completed interactions commit cleanup intent with the outcome. Cleanup confirms
that repository pods stopped and removes their credential mounts. Failure keeps
Work in attention, retains the outcome, and blocks replacement until explicit
reconciliation. Confirmed cleanup restores the appropriate review/input/failure
state; it never approves the result or retries execution.

Host journals preserve failure evidence when PostgreSQL is unavailable. Recovery
surfaces it before scheduling or observing attempts; a lost browser response
remains an unconfirmed command until its receipt can be retrieved. Host and
runtime observations never authorize another execution of a claimed attempt.
See [execution hosting](execution-hosting.md) for fencing and environment details.

## Boundaries and verification

The Web entry point, `GoblinApplication.CreateAsync`, composes the host and maps
feature endpoints. HTTP routes and named handlers live in
`backend/src/Goblin.Web/Http/Endpoints/`; `Hosting/WebServices.cs` registers
dependencies and `Hosting/CodexRecovery.cs` owns the existing runtime startup loop.
`Http/WorkspaceMiddleware.cs` centralizes session, host/origin, repository-listener,
proxy, and public-error handling. `Http/ApiRequest.cs` retains the JSON request
limits, and `Http/WorkResponse.cs` retains string serialization of Work IDs.
This first Web refactor keeps Minimal APIs, the public contracts, and application
service ownership intact. Connection handlers still coordinate integration calls
with request-scoped stores; Work lifecycle rules remain in Core.

Public HTTP requests and responses use classes with explicit `JsonPropertyName`
attributes. `Http/Contracts/` maps application and core types into those classes,
including every nested Work snapshot, so C# property renames cannot silently rename
JSON fields. Request classes map back to application commands before dispatch.
Core types, persistence encodings, and internal repository transport remain
independent of these public HTTP contracts. Contract tests pin the existing JSON
names and verify mapping, account discriminators, request defaults, and bigint IDs.
`WorkSnapshot` and `AttemptSnapshot` are transfer classes for persistence and worker
inputs; the live aggregate and its domain history remain distinct. The
[C# record audit](csharp-record-audit.md) explains the retained value/state records.

`Goblin.Core` has only .NET base-library dependencies. Product types must not
reference ASP.NET, database entities, Wolverine, or runtime SDKs/protocols. The
architecture test checks compiled dependencies and explicit project references.
The initial adapter uses Codex; tests also use an invented runtime identifier
to verify that lifecycle rules do not require Codex semantics. This does not
advertise support for a second integration.

Failure transitions accept Goblin-owned categories, not upstream exception text.
Adapters must keep credentials, raw errors, and transport details outside Work
history. Objective, decision, feedback, and result text are product content;
HTTP adapters validate input sizes and the UI renders that content as text. Core rule errors carry no HTTP status codes.

Run the core scenarios and boundary check with:

```bash
dotnet test backend/tests/Goblin.Core.Tests
```

The solution and `make test` include this project. The tests cover attention for
every failure category, explicit retries, execution ownership, stale events,
uncertainty, cancellation races, decisions, revisions, and approval. Real PostgreSQL and process tests in `Goblin.Application.Tests` cover command
deduplication, dispatch, restart, reservations, uncertainty, and cleanup. Browser
tests exercise persisted decisions and approvals. Aggregate tests do not replace
these integration checks or authenticated runtime validation.

## Repository authority

Repository references in objectives, user context, and decision answers resolve
into durable setup or authorization requests. Users can enable a repository in
Settings or explicitly enable it when approving a Work request in the conversation.
The saved preview identifies the GitHub account, repository, base/work branches,
Git author, and push/PR permissions. Repository metadata lookup precedes the preview;
checkout and execution wait for approval. See [repository intent](git-repository-intent.md).

`PrepareRepository` records a preview without creating an attempt. `AuthorizeRepository`
accepts only its saved request ID and checks current enablement and connection identity.
If the preview includes enablement, that setting, the authorized attempt, the command
receipt, and dispatch intent commit atomically. Decline, cancellation, and added context
prevent reuse of a pending approval. Explicit retries require a fresh preview.

Repository handoff preserves the same Work and earlier attempts' immutable runtime,
model, session, and decision history. Cleanup must be confirmed before replacement;
failures and uncertain execution retain their retry and reconciliation requirements.
Ordinary answers within an authorized repository attempt continue it. A changed Git
scope requires another preview and authorization before a replacement attempt.

Repository grants capture connection generation, account identity, repository ID,
base/work branches, and publication permissions. The broker checks the exact branch
and operation without sharing upstream credentials. Publication and local Git checkpoint
receipts are durable. Local checkpoints never require publication; files stay on the
Work PVC. Settings changes are blocked while affected execution retains capacity or
requires cleanup. The shared application commands also serve the Slack conversation
adapter. PostgreSQL records approved external identities, message receipts, thread
associations, and source provenance. Receipt processing commits conversation and
Work changes with dispatch intent through the same application boundary. Slack
users need an explicit local owner grant; repository approvals and retries remain
in Goblin. Teams and management MCP adapters remain unimplemented.

The [Slack integration plan](slack-integration-plan.md) describes customer-owned
apps, automated local setup, Socket Mode, and the required actor-mapping boundary.
