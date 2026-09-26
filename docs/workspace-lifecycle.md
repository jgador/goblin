# Workspaces, conversation turns, and Git checkpoints

Work owns one persistent Sandbox and PVC. PostgreSQL stores Work conversations,
lifecycle history, execution provenance, inspection sessions, and small Git
checkpoint records. **Workspace files and filesystem archives are never stored
in PostgreSQL.** Goblin does not create a full workspace archive after a turn.

## One workspace per Work

The first repository execution provisions a Sandbox and private PVC named
`work-<work-id>`. Both persist across turns, explicit retries, and requested
revisions. The Work snapshot records their opaque address and the last attempt,
allocation, and turn that could have changed files. Attempts keep separate
claims, runtime sessions, and repository grants. The current adapter binds the
workspace to one repository; changing repositories requires separate Work.

Suspension terminates the pod and releases CPU and memory while retaining the
Sandbox object and PVC. It does not require a filesystem backup. A runtime
failure requires attention and confirmed cleanup before an explicit retry.
A proposed result leaves Work awaiting review with its files retained.

New compute receives fresh attempt-specific credentials and inputs. Kubernetes
resource versions fence ownership changes; delayed starts and cleanup cannot
take over newer allocations. Each attempt/turn has a create-only claim file.
A duplicate start cannot reactivate a suspended allocation.

The next authorized branch starts at the surviving local Git HEAD, preserving
unpublished commits, staged changes, dirty files, untracked files, installed tools,
and ignored outputs. A fresh Codex thread receives saved Work context and checks
Git status/diffs. Native sessions remain in ephemeral runtime storage. Missing
retained storage requires attention; Goblin does not silently replace lost files
with a checkout or an older checkpoint.

## Conversation and execution

An agent question pauses a logical attempt. Answering saves the decision and
atomically queues the next runtime turn of that attempt. The runtime may request
keeping compute for interactive investigation or suspending it while waiting.
There is no warm timer or idle inference. After suspension, an answer first runs
with tools disabled using saved Work context. A request for file access resumes
the same Work workspace for a repository turn. This is not a retry.

Runtime failure, cleanup failure, uncertain execution, cancellation, and explicit
retry retain the core's attention and ownership rules. No failed Work is retried
automatically. Runtime session references never replace Goblin's durable history.

## Files and Git checkpoints

The worker retires the runtime and verifies that other processes have stopped.
It commits intended repository changes and publishes the authorized branch
through Goblin's GitHub broker. The controller verifies the published commit.
`workspace_checkpoints` records the Work, attempt, turn, allocation, repository,
branch, commit SHA, and timestamp. This is Git provenance only: the table has no
archive payload or archive digest. The internal checkpoint endpoint accepts a
bounded JSON request with the turn number and commit SHA.

Files remain on the PVC. `.goblin/changes.patch` contains the most recent turn's
Git diff for inspection. The Git checkpoint also anchors verified repository
setup memory. It does not back up untracked/ignored files, tools, or build outputs,
and never authorizes deleting a volume. The worker has no archive upload or
filesystem restoration step. Published Git commits can be retrieved from GitHub;
files outside Git depend on the retained volume and any operator-managed backups.

## Inspection

Open workspace shows the latest published Git commit and recorded inspection
sessions. **Start inspection** creates a separate pod mounting the Work PVC
read-only. Execution must have stopped and cleanup must be confirmed. Inspection
and repository execution reserve capacity and exclude concurrent access to the
same Work volume. No agent credentials, database credentials, Kubernetes token,
or archive download capability are mounted into the inspection pod.

File/diff browsing and the browser terminal use this recorded inspection pod.
File previews are bounded and reject traversal, symbolic links, and private Git
or worker files. The file list is limited to 1,000 entries and previews to 1 MiB;
use the terminal to inspect larger trees. Files are rendered as text. Historical
Git checkpoints are metadata, not historical filesystem snapshots. Full archive
downloads and archive restoration are not available.

Closing the panel disconnects the terminal; **Stop workspace** explicitly releases
inspection compute. Sessions remain visible after reopening and count against
capacity. Terminal connections require authentication and same-origin checks,
with availability rechecked while connected. File requests also require an
available inspection session belonging to the Work. Browsers cannot choose
arbitrary Kubernetes targets. The terminal is a streaming command shell rather
than a full-screen terminal emulator.

## Capacity and retention

The database serializes admission with execution claims and inspection
reservations. Extra sessions wait for capacity. Failed inspection allocations
retain their reservation until explicitly stopped. Execution deadlines and
resource failures require attention.

| Configuration | Default |
| --- | --- |
| `GOBLIN_MAX_SANDBOXES` | 2 |
| `GOBLIN_SANDBOX_CPU_LIMIT` | 2 |
| `GOBLIN_SANDBOX_MEMORY_LIMIT` | 2Gi |
| `GOBLIN_MAX_CACHED_WORKSPACES` | 4 retained repository volumes |

Existing volumes can resume at the volume limit. New Work requiring another
volume fails for attention instead of deleting files. Goblin does not
automatically reclaim completed, cancelled, or unfinished Work volumes. Expiration,
export, and deletion require a separate retention policy. The former checkpoint
archive byte limits have been removed. Backend recovery and logging remain a
separate concern.

## Schema and verification

The unreleased SQL baseline contains metadata-only Git checkpoints and PVC
inspection sessions. Update `0001_initial.sql` directly and regenerate EF mappings
from that schema. A local installation upgrading from the archive implementation
must remove the archive columns and obsolete inspection restore fields before
running this version; preserve Work records and PVCs. A Git checkpoint alone is
never proof that filesystem data can be discarded.

Verification covers core suspension without a file archive, preserved dirty and
ignored files across attempts, storage loss and capacity protection, metadata-only
PostgreSQL checkpoints, durable inspection reservations, bounded PVC file reads,
and browser inspection. Run the opt-in PostgreSQL tests explicitly; default test
runs skip them when no connection is configured. Kubernetes and runtime checks
remain necessary for the deployed integration boundary.

A Kubernetes check on 2026-09-26 ran the production worker with pinned Codex
0.155.1 and local model/GitHub broker fixtures. Codex edited the checkout and wrote
a 140 MiB tool file outside Git. The turn produced a result, sent 71 bytes of Git
checkpoint JSON, and suspended with its PVC intact. A separate inspection pod
read the edited file from that PVC, and no workspace archive was created. This
check exercised the real runtime, worker, Kubernetes hosting, and file reader;
GitHub publication and model responses used local fixtures.
