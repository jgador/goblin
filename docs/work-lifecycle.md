# Work lifecycle core

`backend/src/Goblin.Core/Work/` owns the Work product model. `Goblin.Application`
saves Work snapshots, commands, conversations, and attempt rows in PostgreSQL and
dispatches through Wolverine. [Workspace lifecycle](workspace-lifecycle.md) covers
multi-turn attempts, Git checkpoints, and PVCs. The [Work interface](work-centered-ui.md)
covers the UI.

## Product identities

Goblin IDs are positive `long` / `bigint` values. Work HTTP responses encode them
as decimal strings, and commands accept strings, because JavaScript numbers cannot
hold the full 64-bit range. Runtime session references are opaque strings.

Before building a command, the browser posts `{ "kinds": ["Command", "Work"] }`
to `/api/identities` and gets `{ "ids": ["1", "1"] }` in the same order. The
endpoint accepts one to four of `Command`, `Work`, `Conversation`, `Message`, and
`Inspection`. Each kind uses its own sequence, so IDs of different kinds can be
equal. The browser keeps the full command, with its reserved IDs, to resend it
after an unconfirmed response. Sequence gaps are expected.

Attempt IDs are reserved inside the command transaction. Claim owners, decision
IDs, and context message IDs use `work_event_id_seq`. The saved Work snapshot
(schema version 2) is the source of truth; summary columns cannot replace it.

## Attention belongs to Work

Work status is one of `Ready`, `Queued`, `InProgress`, `Cancelling`,
`NeedsAttention`, `Completed`, or `Cancelled`. `NeedsAttention` always has one
attention reason. Every observed failure requires attention, including temporary
failures and failures before execution starts. Successful runtime execution never
completes Work.

| Attention reason | Next step |
| --- | --- |
| `InputRequired` | `Answer` the current decision. A paused (`Waiting`) attempt queues its next runtime turn; otherwise Work returns to `Ready`. An answer that names a repository or changes delivery scope asks for repository setup instead. |
| `GitRepositoryRequired` (wire: `RepositoryRequired`) | `PrepareRepository`, then `AuthorizeRepository` or `DenyRepository` the saved preview. Enabled repositories start directly. |
| `ResultReview` | `Approve` (Work becomes `Completed`) or `RequestChanges` (Work returns to `Ready`). |
| `Failure` | `Retry` (new attempt) or `Cancel`. |
| `UncertainExecution` | `Reconcile`, or `Cancel` and wait for confirmation. Retry is rejected until the stop is confirmed. |
| `CleanupRequired` | `Reconcile` the failed cleanup. The outcome stays saved. |

While cleanup is pending, answer, approve, request changes, cancel, retry, and
new attempts are rejected. Confirmed cleanup restores the earlier `Failure`,
`GitRepositoryRequired`, `InputRequired`, or `ResultReview` attention, or
`Cancelled`. It never approves or retries.

Core transitions never launch processes. Live tool approvals do not exist yet.

## Execution ownership and recovery

Attempt status is one of `Queued`, `Starting`, `Running`, `Waiting`,
`CancellationRequested`, `Succeeded`, `Failed`, `Uncertain`, or `Cancelled`.
The normal path is `Queued` → `Starting` → `Running` → `Succeeded`. Each attempt
keeps its agent and execution target. The requested model is stored separately
from the model and session the runtime reports. Later configuration changes do
not rewrite earlier attempts.

- `TryClaimExecution` records the owner and environment before an external
  launch. Repeated claims return false, even for the same owner. Events from
  another owner or an older attempt are rejected. A late start notification can
  add a session reference but never returns an attempt to `Running`.
- A failure before the claim gives `Failure` attention. After the claim, the
  adapter must separate a confirmed stop (`Failed`) from an unknown outcome
  (`Uncertain`). A timeout or lost connection alone does not prove a stop.
  Confirming the stop gives `Failed` with `Failure` attention. A result recovered
  from an uncertain attempt goes to review.
- When the runtime asks a question, the attempt pauses (`Waiting`). The answer
  queues the next runtime turn of the same attempt in the same transaction.
- Cancelling a queued attempt cancels at once. After a claim, including a paused
  attempt, Work moves to `Cancelling` until the host confirms the stop. If
  execution finished first, its result goes to review. A failed cancellation
  requires attention.
- Finished interactions commit cleanup intent with the outcome. Cleanup stops
  the repository pod and deletes its credential secret. A cleanup failure moves
  Work to `CleanupRequired` and blocks new attempts.

Command transactions take a PostgreSQL advisory lock. A unique index allows one
active, cleanup-pending, or compute-retaining attempt per connection. State,
history, the command receipt, and Wolverine dispatch intent commit together; the
claim commits before Goblin contacts the host. A repeated command ID with the same
payload returns the saved receipt; a different payload is rejected. Stale expected
versions are rejected.

Each store operation creates its own EF Core context from
`IDbContextFactory<GoblinDbContext>` and, for commands, a fresh Wolverine outbox.
Stores keep no tracked entities between calls.

Host journals keep failure evidence while PostgreSQL is unavailable; recovery
applies it first. See [execution hosting](execution-hosting.md) for fencing and
environments.

## Boundaries and verification

`Goblin.Core` depends only on the .NET base library; an architecture test checks
this. Failure transitions take Goblin `FailureKind` values, not upstream error
text. Core rule errors carry no HTTP status codes.

```bash
dotnet test backend/tests/Goblin.Core.Tests
```

PostgreSQL and process tests in `Goblin.Application.Tests` cover deduplication,
dispatch, restart, uncertainty, and cleanup. Core tests do not replace them.

## Repository authority

[Repository intent](git-repository-intent.md) owns repository selection,
authorization, and delivery. The lifecycle rules are:

- A repository named in an objective, context, or answer creates a setup request,
  never access by itself.
- An enabled repository starts with a fresh grant. A disabled repository needs a
  saved preview and explicit approval; the attempt, receipt, enablement, and
  dispatch intent commit together.
- `AuthorizeRepository` accepts only the saved request ID. Added context and
  cancellation invalidate a pending preview.
- A grant fixes the connection generation, account, repository, branches, and
  publication permissions. Work stays bound to one repository once its workspace
  exists.
- Retry creates a fresh grant and attempt. A disabled repository needs approval
  again.

Slack uses the same application commands; see [Slack integration](slack-integration.md).
