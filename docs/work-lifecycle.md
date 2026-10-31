# Work lifecycle core

`backend/src/Goblin.Core/Work/` owns the product model used by the live
application. `Goblin.Application` saves its versioned snapshots, commands,
conversations, and attempt projections in PostgreSQL and dispatches through
Wolverine. Runtime adapters and the UI use Goblin-owned contracts.
[Workspace lifecycle](workspace-lifecycle.md) covers multi-turn attempts, Git
checkpoints, and PVCs. The [Work interface](work-centered-ui.md) covers the UI.

## Product identities

Goblin IDs are positive C# `long` / PostgreSQL `bigint` values. Work HTTP
responses encode them as decimal strings and commands accept decimal strings,
because JavaScript numbers cannot hold the full 64-bit range. Runtime session
references are opaque strings.

Before building a new command, the browser posts `{ "kinds": ["Command", "Work"] }`
to `/api/identities` and receives `{ "ids": ["1", "1"] }` in the requested order.
The authenticated endpoint accepts one to four entries from `Command`, `Work`,
`Conversation`, `Message`, and `Inspection`. Each kind uses its table's identity
sequence, so values from different kinds can be equal. The browser keeps the
complete command, including its reserved IDs, for resubmission after an
unconfirmed response. An allocation failure creates no command or Work; sequence
gaps are expected.

Attempt IDs are reserved before state and dispatch intent commit. Claim owners,
decision IDs, and context message IDs use `work_event_id_seq`, which is separate
from table identities. Core snapshots use schema version 2. Every saved Work item
needs its aggregate snapshot; summary columns cannot replace it.

## Attention belongs to Work

Work status is one of `Ready`, `Queued`, `InProgress`, `Cancelling`,
`NeedsAttention`, `Completed`, or `Cancelled`. `NeedsAttention` always carries
one attention reason. Every observed failure requires attention, including
temporary failures and failures before execution starts. Successful runtime
execution never completes Work by itself.

| Attention reason | What must happen next |
| --- | --- |
| `InputRequired` | `Answer` the current decision. A paused (`Waiting`) attempt queues its next runtime turn; an answer to a completed interaction returns Work to `Ready`. An answer that names a repository or changes delivery scope asks for repository setup instead. |
| `GitRepositoryRequired` (wire: `RepositoryRequired`) | Select a GitHub repository with `PrepareRepository`, then `AuthorizeRepository` or `DenyRepository` a saved preview. Enabled repositories start directly with a fresh attempt grant. |
| `ResultReview` | `Approve` the current result, which completes Work, or `RequestChanges`, which returns Work to `Ready`. |
| `Failure` | `Retry`, which records a new attempt, or `Cancel`. |
| `UncertainExecution` | `Reconcile` the existing execution, or `Cancel` and wait for confirmation. Retry is rejected until reconciliation confirms the stop. |
| `CleanupRequired` | `Reconcile` the failed cleanup. The outcome stays saved. |

While cleanup is pending, answering, approving, requesting changes, cancelling,
retrying, and starting another attempt are rejected. After a cleanup failure,
confirmed cleanup restores the earlier state: `Failure`, `GitRepositoryRequired`,
`InputRequired`, or `ResultReview` attention, or `Cancelled`. It never approves a
result or retries.

The browser shows the attention reason and its allowed actions. It must not infer
success from local clicks or treat uncertain execution as an ordinary failure.
Core transitions never launch processes. Live tool approvals are a separate
runtime capability that does not exist yet.

## Execution ownership and recovery

Attempt status is one of `Queued`, `Starting`, `Running`, `Waiting`,
`CancellationRequested`, `Succeeded`, `Failed`, `Uncertain`, or `Cancelled`.
The normal path is `Queued` → `Starting` → `Running` → `Succeeded`. Every attempt
captures its agent and execution target. The requested model is stored separately
from the model and session references the runtime reports. Later assignment or
configuration changes do not rewrite earlier attempts.

`TryClaimExecution` records the owner and environment reference before an
external launch. Repeated claims return false, even for the original owner.
Events from another owner or an older attempt cannot change current Work. A
delayed start notification can add a missing session reference but never turns a
result, cancellation, or failure back into `Running`.

A failure before the claim moves Work to `Failure` attention. After the claim,
the adapter must separate a **confirmed stopped failure** (`Failed`) from an
**uncertain outcome** (`Uncertain`). A timeout or lost connection alone does not
prove the execution stopped. An uncertain attempt cannot be replaced. Confirming
the stop moves it to `Failed` with `Failure` attention; only an explicit retry
creates the next attempt. A result recovered from an uncertain attempt goes to
review, and history keeps the earlier failure.

An attempt pauses (`Waiting`) when the runtime asks a question. Answering records
the decision and atomically queues the next runtime turn of the same attempt.
The runtime's keep or release choice decides whether compute stays running for
that turn. Changing repository or delivery scope needs a new attempt instead.

Cancellation before a claim cancels at once. After a claim, including a paused
attempt, cancellation moves Work to `Cancelling` and waits for host confirmation.
A delayed start notification cannot remove the cancellation. If execution finished
before cancellation took effect, its actual result goes to review. A failed
cancellation requires attention; it does not claim that the process stopped.

The database serializes short command transactions with an advisory lock and
allows one active, cleanup-pending, or compute-retaining attempt per connection.
State, history, command receipts, and Wolverine dispatch intent commit together.
Claims commit before Goblin contacts the host. Command IDs plus payload
fingerprints stop repeated submissions from repeating transitions; expected
versions reject stale actions.

Each store operation creates and disposes its own EF Core context through
`IDbContextFactory<GoblinDbContext>`. Transactional reads, the advisory lock, and
writes use that context. Each Work command enrolls it in a fresh Wolverine outbox,
so buffered messages never carry over from another operation, including one that
rolled back. Stores keep no tracked entities between calls.

Completed interactions commit cleanup intent with the outcome. Cleanup confirms
that repository pods stopped and removes their credential mounts. A cleanup
failure moves Work to `CleanupRequired`, keeps the outcome, and blocks replacement
until explicit reconciliation.

Host journals keep failure evidence while PostgreSQL is unavailable. Recovery
applies it before scheduling or observing attempts. A lost browser response stays
an unconfirmed command until its receipt can be read. Host and runtime
observations never authorize another execution of a claimed attempt. See
[execution hosting](execution-hosting.md) for fencing and environments.

## Boundaries and verification

`Goblin.Core` depends only on the .NET base library. Product types must not
reference ASP.NET, database entities, Wolverine, or runtime SDKs or protocols;
an architecture test checks compiled and project references. Tests also use an
invented runtime ID to prove that lifecycle rules do not depend on Codex. This
does not mean a second runtime is supported.

Failure transitions accept Goblin-owned `FailureKind` categories, not upstream
exception text. Adapters keep credentials, raw errors, and transport details out
of Work history. Objective, decision, feedback, and result text are product
content; HTTP adapters check input sizes and the UI renders it as text. Core rule
errors carry no HTTP status codes. Repository Git output overflow, cancellation,
and timeout stop the process tree but never authorize another attempt or remove
workspace files.

Run the core scenarios and boundary check with:

```bash
dotnet test backend/tests/Goblin.Core.Tests
```

The solution and `make test` include this project. Real PostgreSQL and process
tests in `Goblin.Application.Tests` cover command deduplication, dispatch,
restart, reservations, uncertainty, and cleanup. Aggregate tests do not replace
these integration checks or authenticated runtime checks.

## Repository authority

[Repository intent](git-repository-intent.md) owns repository selection,
authorization, delivery wording, and commands. The lifecycle rules are:

- Repository references in objectives, context, and answers create a setup or
  authorization request, never authority by themselves.
- Enabled repositories start directly with a fresh attempt grant. A disabled
  repository needs a saved preview and explicit approval; the attempt, receipt,
  repository enablement, and dispatch intent commit atomically.
- `AuthorizeRepository` accepts only the saved request ID and rechecks connection
  identity and permission. Added context and cancellation invalidate a pending
  preview; `DenyRepository` declines it.
- A grant captures connection generation, account identity, repository ID, base
  and work branches, and publication permissions. Work stays bound to one
  repository once its workspace exists.
- Retry creates a fresh grant and attempt and needs reconciliation and confirmed
  cleanup first. A disabled repository again needs explicit approval.

Slack uses the same application commands through its own conversation adapter;
see [Slack integration](slack-integration.md). Repository approvals and retries
stay in Goblin.
