# Work lifecycle core

`backend/src/Goblin.Core/Work/` owns the product model used by the live
application. `Goblin.Application` persists its versioned snapshots, commands,
conversations, and attempt projections in PostgreSQL and dispatches through
Wolverine. Runtime adapters and the UI consume Goblin-owned contracts.

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
| `InputRequired` | Answer the recorded decision; the Work becomes ready to continue. |
| `ResultReview` | Approve the specific result or request changes. Approval completes Work. |
| `Failure` | Explicitly request a retry, or cancel Work. A retry records a new attempt. |
| `UncertainExecution` | Reconcile the existing execution before allowing a retry, or request cancellation and await confirmation. |
| `CleanupRequired` | Reconcile failed execution cleanup before approving, answering, or starting another attempt. The outcome remains saved. |

The attention reason belongs to the aggregate. The browser should present it and
its permitted actions. It must not infer successful actions from local clicks or
collapse uncertain execution into an ordinary retryable failure.

Answering input and requesting changes record user intent and return Work to
`Ready`. The user can then issue Start work to queue the next attempt. These core transitions do not launch processes. Input requests in
this increment represent completed runtime interactions that returned a question;
live tool approvals need a separately validated runtime capability.

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

The solution and `npm test` include this project. The tests cover attention for
every failure category, explicit retries, execution ownership, stale events,
uncertainty, cancellation races, decisions, revisions, and approval. Real PostgreSQL and process tests in `Goblin.Application.Tests` cover command
deduplication, dispatch, restart, reservations, uncertainty, and cleanup. Browser
tests exercise persisted decisions and approvals. Aggregate tests do not replace
these integration checks or authenticated runtime validation.

## Repository authority

Repository attempts capture their GitHub connection generation, account identity,
repository ID, branch and policy in `RepositoryGrant`. Work inherits enabled
repository settings when queued; the request cannot choose its own grant. The
core validates the exact Work/attempt branch and allowed operation. The trusted
repository adapter enforces it without sharing the upstream token with the agent.
Publication receipts and dispatch are durable; uncertain remote effects must be
reconciled before replacement. Settings cannot change an account or repository
policy while affected Work is queued, active, uncertain, or awaiting cleanup.
