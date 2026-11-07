# Work lifecycle core

`backend/src/Goblin.Core/Work/` owns the Work product model. `Goblin.Application` saves
Work snapshots, commands, conversations, and attempt rows in PostgreSQL and dispatches
through Wolverine. [Workspace lifecycle](workspace-lifecycle.md) covers multi-turn
attempts, Git checkpoints, and PVCs.

## Using Work

In the browser, **Create work** saves a goal and assigns the default agent; **Start**
begins execution separately. An authorized Slack DM or mention creates and starts Work;
see [Slack](slack-integration.md). An untracked browser conversation stores context
without running an agent; **Track this work** creates Work from it.

The current status panel offers the next permitted action. Answer questions, review
results with **Approve** or **Request changes**, and recover failures with an explicit
retry or reconciliation. Repository setup uses a saved authorization preview; see
[repository authority](git-repository-intent.md). Reading or expanding content never
submits a command. Execution details preserve agent, runtime, requested and reported
model, branch, and environment provenance.

If a command response is lost, **Resend command** sends the original command ID and
payload; **Keep saved state** keeps the server view. Refresh never replays a command.
Drafts survive navigation within the page; unsent conversation drafts do not survive a
reload. Browser search covers loaded content, not a server index.

Commands cannot edit a saved goal, and Work has no milestone model. Artifacts expose
metadata and references rather than content retrieval; there is no execution-log view.

**Settings → Connections** manages Codex, GitHub, Slack, and the private ChatGPT plugin.
Knowledge indexing and Teams are unavailable. Workspace inspection is currently an
[API](workspace-lifecycle.md#inspection), without a browser file or terminal view. The
[browser journeys](../tests/e2e/) exercise navigation, draft preservation, command
recovery, focus, and accessibility. Durable journeys need the
[opt-in PostgreSQL test setup](database.md#application-configuration-and-verification);
report skips separately from fixture results.

## Product identities

Goblin IDs are positive `long` / `bigint` values. Work HTTP responses encode them as
decimal strings, and commands accept strings, because JavaScript numbers cannot hold the
full 64-bit range. Runtime session references are opaque strings.

Clients reserve IDs through the
[identity endpoint](../backend/src/Goblin.Web/Http/Endpoints/WorkEndpoints.cs) before
submitting commands and retain them after an unconfirmed response. Different identity
kinds can have equal IDs; sequence gaps are expected. Attempts reserve their IDs in the
command transaction. The saved Work snapshot is the source of truth; summary columns
cannot replace it.

## Attention belongs to Work

The [core types](../backend/src/Goblin.Core/Work/WorkTypes.cs) define statuses and wire
values. `NeedsAttention` always has one attention reason. Every observed failure
requires attention, including temporary failures and failures before execution starts.
Successful runtime execution never completes Work.

| Attention reason | Next step |
| --- | --- |
| `InputRequired` | `Answer` the current decision. A paused (`Waiting`) attempt queues its next runtime turn; otherwise Work returns to `Ready`. Repository choices and delivery changes still follow the authorization rules. |
| `GitRepositoryRequired` (wire: `RepositoryRequired`) | `PrepareRepository`, then `AuthorizeRepository` or `DenyRepository` the saved preview. Enabled repositories start directly. |
| `ResultReview` | `Approve` (Work becomes `Completed`) or `RequestChanges` (Work returns to `Ready`). |
| `Failure` | `Retry` (new attempt) or `Cancel`. |
| `UncertainExecution` | `Reconcile`, or `Cancel` and wait for confirmation. Retry is rejected until the stop is confirmed. |
| `CleanupRequired` | `Reconcile` the failed cleanup. The outcome stays saved. |

While cleanup is pending, answer, approve, request changes, cancel, retry, and new
attempts are rejected. Confirmed cleanup restores the earlier `Failure`,
`GitRepositoryRequired`, `InputRequired`, or `ResultReview` attention, or `Cancelled`.
It never approves or retries.

Core transitions never launch processes. Repository tools run within the authorized
sandbox without live tool approvals.

## Execution ownership and recovery

The normal attempt path is `Queued` → `Starting` → `Running` → `Succeeded`. Each attempt
keeps its agent and execution target. The requested model is stored separately from the
model and session the runtime reports. Later configuration changes do not rewrite
earlier attempts.

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

Database concurrency enforcement allows one active, cleanup-pending, or
compute-retaining attempt per connection. State, history, the command receipt, and
Wolverine dispatch intent commit together; the claim commits before Goblin contacts the
host. A repeated command ID with the same payload returns the saved receipt; a different
payload is rejected. Stale expected versions are rejected.

Host journals keep failure evidence while PostgreSQL is unavailable; recovery applies it
first. See [execution hosting](execution-hosting.md) for fencing and environments.

## Boundaries and verification

`Goblin.Core` depends only on the .NET base library; an architecture test checks this.
Failure transitions take Goblin `FailureKind` values, not upstream error text. Core rule
errors carry no HTTP status codes.

```bash
dotnet test backend/tests/Goblin.Core.Tests
```

PostgreSQL and process tests in `Goblin.Application.Tests` cover deduplication,
dispatch, restart, uncertainty, and cleanup. Core tests do not replace them.

## Repository authority

[Repository intent](git-repository-intent.md) owns repository selection, authorization,
and delivery. The lifecycle rules are:

- Naming a repository in an objective, context, or answer never grants access by
  itself; execution requires standing enablement or approval of a saved preview.
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
