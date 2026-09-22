# Workspaces, conversation turns, and checkpoints

Work remains the primary product object. Conversation turns, logical execution
attempts, runtime turns, physical sandbox allocations, and inspection sessions
have separate lifetimes. Colors, branding, controls, and the Work-centered layout
are unchanged.

## Conversation and execution

An agent question pauses a logical attempt. Answering records the human response
and atomically queues its next runtime turn through Wolverine. Each turn has a
new durable claim, a create-only worker fence, and recorded runtime provenance.
Late dispatches and outcomes from earlier turns cannot launch or change a later
turn. Completed results, explicit requested revisions, and retries retain their
existing review and failure semantics. Failed Work is never retried automatically.
Older persisted questions from the previous implementation retain their original
completed-attempt semantics.

The runtime returns a `releaseWorkspace` decision with a question. Goblin can keep
the existing allocation for continued investigation, or checkpoint and release
it. There is no warm timer or idle inference. After release, an answer first runs
with tools disabled using saved Work context. If file access is necessary, the
runtime requests a workspace and Goblin queues a repository turn of the same
attempt. Recreating compute does not create a retry. Native runtime sessions are
references; durable conversation and execution records remain Goblin-owned.

## Preservation and release

The worker retires its runtime and verifies that no other processes remain before
checkpointing. It commits intended Git changes, publishes through the existing
broker, and archives the workspace, including Git-ignored files and outputs.
Internal claim files and credentials are excluded; credentials remain outside
the workspace. A generated diff is stored under reserved `.goblin/changes.patch`.

The controller independently confirms the published commit on the recorded branch.
PostgreSQL commits archive bytes, digest, repository, branch, exact commit, Work,
attempt, turn, and allocation identity in one transaction. Only a verified record
can justify successful release. Upload, publication, storage, and cleanup failures
surface attention and preserve the local workspace. A prior checkpoint does not
authorize discarding newer unsaved changes.

On restoration, the broker detects changes to the recorded remote branch. The
worker checks the restored Git HEAD against the exact commit. Archives are
extracted only inside an isolated environment, with traversal, external symlink,
and size checks. Processes and RAM are not checkpointed; continuation uses saved
files and product context. This provides survival of sandbox replacement, not
protection against loss of the entire database/storage installation. PostgreSQL
and volume backups remain necessary for machine-level disaster recovery.

## Inspection

Open workspace appears beside Recent outputs. File and diff browsing reads a
saved archive without starting an agent. Historical checkpoints can be selected
and downloaded. A browser terminal creates a separately recorded, read-only
inspection allocation. It receives neither agent authentication nor a service
account token. A checkpoint download capability is mounted only into its restore
init container and revoked after startup. Existing retained volumes without a
checkpoint can be inspected after their execution stops.

Inspection sessions are explicitly stopped. Closing a panel disconnects its
terminal but does not infer that its session should be destroyed. Sessions remain
visible on reopening and count against capacity. Terminal connections require
Goblin authentication and same-origin validation, and authorization is rechecked
while connected. Kubernetes exec is restricted to the recorded inspection pod and
fixed container; the browser cannot choose an arbitrary Kubernetes target.

The first terminal is a command-oriented shell with streaming output and interrupt,
not a full terminal emulator. Full-screen terminal applications, a code editor,
editable human handoff, and app previews are outside this increment.

## Capacity and storage

The database serializes admission with execution claims and inspection reservations.
Additional sessions queue when capacity is occupied. Failed inspection allocations
retain reservations until explicitly stopped. Each repository pod also has CPU,
memory, and the existing hard execution deadline; a deadline failure requires
attention, never an automatic replacement. Manually created operator workloads
are outside Goblin's admission queue.

| Configuration | Default |
| --- | --- |
| `GOBLIN_MAX_SANDBOXES` | 2 |
| `GOBLIN_SANDBOX_CPU_LIMIT` | 2 |
| `GOBLIN_SANDBOX_MEMORY_LIMIT` | 2Gi |
| `GOBLIN_MAX_CHECKPOINT_BYTES` | 128 MiB compressed |
| `GOBLIN_MAX_WORKSPACE_STORAGE_BYTES` | 2 GiB of checkpoint archives |
| `GOBLIN_MAX_CACHED_WORKSPACES` | 4 retained repository volumes |

Checkpoints preserve history and are not silently expired. Full archive storage
blocks additional repository admission; an archive that exceeds a limit produces
attention and retains its local files. Local volumes may be reclaimed under
pressure only after confirming the latest changes in that allocation are saved
and no pod mounts the volume. Suspended sandbox identity fences remain retained.

## Schema and rollout

The unreleased baseline includes turn projections, workspace checkpoints, and
inspection sessions. EF mappings are regenerated from the database. An existing
local database needs a backed-up, transactional additive upgrade and baseline
verification before reconciling its migration checksum. Do not reset its Work or
volumes. Older applications cannot interpret new Waiting attempts; rollback after
new execution requires a compatible application or explicit recovery.

## Verification

The local rollout on 2026-09-23 exercised a real Codex repository execution and
GitHub publication. One logical attempt completed four runtime turns: an initial
checkpoint and release, a conversation-only request for file access, restoration
into a new sandbox, and an immediate reply in that retained sandbox. Three verified
checkpoints preserved the tracked changes and a Git-ignored output throughout.

Browser validation opened the saved file and diff, restored a separate inspection
sandbox, streamed real Kubernetes exec output, confirmed writes were rejected, and
stopped the inspection explicitly. The validation branch, draft PR, databases,
and sandbox resources were cleaned up. Existing local Work records and repository
volumes were preserved through the additive schema upgrade.

Regression coverage includes conversation ownership and stale deliveries,
capacity reservations during queued continuation, paused-execution failures,
durable inspection start/stop delivery, archive limits and unsafe paths, output
schema compatibility, and saved-file rendering. Real PostgreSQL application and
persistence suites were run separately from the default suites that skip database
tests when no connection is configured. The standard browser suite's optional
database journeys remain opt-in; the live validation above covers the integrated
workspace lifecycle on the deployed host type.
