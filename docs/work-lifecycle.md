# Work lifecycle core

`backend/src/Goblin.Core/Work/` owns the Work product model. `Goblin.Application` saves
Work snapshots, commands, conversations, and attempt rows in PostgreSQL and dispatches
through Wolverine. [Workspace lifecycle](workspace-lifecycle.md) covers multi-turn
attempts, Git checkpoints, and PVCs.

## Using Work

**Work inbox** opens in **Board** view, grouping items into Ready, In progress,
Needs attention, and Closed. All four lanes stay visible when empty. Closed includes
completed and cancelled Work. Choose **List** to see items ordered by most recently
updated. **Status** in List includes the Board groups and individual Completed and
Cancelled filters. **Filter inbox** narrows either view. The **Needs attention**
shortcut shows that group in List, with labels that describe the next action. The
sidebar count shows all Work that needs attention. Returning to the inbox restores the
last layout, filter text, and List status. **Search** or **Ctrl+K** (**⌘K** on Mac)
opens a dialog for loaded Work and conversations from any workspace page. Use the
arrow keys and **Enter** to open a result, or **Escape** to return to the current page.

The clock in the upper-right toolbar sets the workspace auto-refresh interval to
3, 5, 10, 15, 30, or 60 seconds. It defaults to 3 seconds. Choose **Off** to stop
automatic updates, including when returning to the tab or reconnecting. Your choice
is saved in browser local storage and reused on reload. Select **Refresh now** in
the same menu to update the workspace immediately.

In **New work**, **Create work** uses the connected Codex account to generate a title
of up to 90 characters, then saves your request as the description and assigns the
default agent. The description can be a change request or a question.
A failed title request keeps the draft available to retry.
The **Codex** button shows the current status and links to connection settings.
When Codex is disconnected, **Connect Codex** below the composer opens its setup page.
**Start work** begins execution from the Ready item. Use **Add repository** to
select a repository from the Ready item. In **New chat**, **Save message** saves a
message in an untracked conversation. **Track this work** creates Work from it.
**Track this work**, the ChatGPT plugin, and authorized Slack requests also use Codex
to title new Work. An authorized Slack DM or mention starts Work after it is saved.
See [Slack](slack-integration.md).

Choose **Rename work** beside the title to edit it. **Enter** saves and **Escape**
cancels. Renaming works with Codex disconnected. Open **Description** to read the
original request. Goblin posts questions and proposed results in the Work conversation.
The review area offers **Approve & complete** and **Request changes**. The composer
keeps separate drafts for context and review feedback. **Saved outputs** contains
results and artifacts, and **Execution details** contains progress and attempt
records. **Add note** saves local context to closed Work.

Approving or cancelling repository Work schedules deletion of its local workspace
files. Open Work keeps its workspace for retries and revisions.

The recovery panel shows the last failure and current Codex status, with access
to the **Activity timeline**. Use **Retry work** or **Reconcile execution** when
available. Repository setup uses a
[saved authorization preview](git-repository-intent.md). Reading or expanding
content never submits a command. Execution details preserve agent, runtime, requested and reported
model, branch, and environment provenance.

For repository-backed Work, **Review changes** opens the saved patch when one is
available. During result review, find it alongside the approval and feedback
actions. In other active states, the toolbar’s **Changes** button toggles the
preview. The diff opens beside the conversation on wide screens or across the Work area
on narrow screens. When the panes are side by side, drag their divider or focus it and
use the arrow keys to resize Changes. Your browser remembers the width across Work
items. Choose **Reset width** in the Changes header to restore the default.
Close the full diff or press **Escape** while reviewing to return to the summary.
**All files** shows a continuous review, **By file** lets you select a path, and
**Wrap lines** fits long lines within the diff.
Expand a file or load more lines to read longer diffs. The
[saved diff](workspace-lifecycle.md#files-and-git-checkpoints)
is available while the execution pod is stopped and is cleared when the Work closes.

If a command response is lost, **Resend command** sends the original command ID and
payload; **Keep saved state** keeps the server view. Refresh never replays a command.
Drafts survive navigation within the page; unsent conversation drafts do not survive a
reload. Browser search covers loaded content, not a server index.

PostgreSQL stores the title and description in the `public.work_item` columns.
Goblin combines them with the lifecycle snapshot in `state` to display Work.
Artifacts expose metadata and references. The Work UI has no execution-log view.

Open the workspace menu at the bottom of the sidebar to find **Settings**, **System**,
and **Lock workspace**. With the sidebar collapsed, the menu opens from the left side
of the header. **Settings → Connections** manages the Codex runtime, GitHub, Slack, and
the private ChatGPT plugin. The Settings back button names the page it returns to.
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
command transaction. The persisted lifecycle snapshot is authoritative for Work
transitions, and the title and description come from their columns.

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

- Naming a repository in a description, context, or answer never grants access by
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
