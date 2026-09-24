# Workspaces, conversation turns, and checkpoints

Work remains the primary product object. Conversation turns, logical execution
attempts, runtime turns, physical sandbox allocations, and inspection sessions
have separate lifetimes. Colors, branding, controls, and the Work-centered layout
are unchanged.

## One workspace per Work

A Work provisions its Sandbox and private PVC when it first needs repository
files. Both use `work-<work-id>` and persist across turns, retries, and requested
revisions. The Work snapshot in PostgreSQL records the opaque workspace address
and the last attempt, allocation, and turn that could have changed its files.
Attempts retain separate claims, runtime sessions, and repository grants.
The current repository adapter binds this workspace to one repository; changing
repositories requires separate Work.

Suspension terminates the pod and releases its CPU and memory while retaining
the Sandbox object and PVC. A runtime failure requires attention and confirmed
cleanup before an explicit retry can start another attempt in that workspace.
Ordinary questions retain the existing semantic keep/suspend decision. A result
awaiting approval keeps its files, usually with compute suspended.

New compute receives fresh credentials and input mounts specific to the attempt
and allocation. Kubernetes resource-version checks fence ownership changes;
delayed starts and cleanup cannot take over a newer allocation. Worker claim
files include both attempt and turn IDs. An existing allocation's duplicate
start cannot reactivate it after suspension.

The worker prepares the next authorized branch at the surviving local Git HEAD,
preserving unpublished commits, the index, dirty files, untracked files and
ignored outputs. A fresh Codex thread receives Work context and inspects Git
status/diffs. Native Codex sessions remain in ephemeral runtime storage; retaining
or restoring them is not required. Missing retained storage requires attention
and is not silently replaced by an older checkpoint.

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
runtime requests a workspace and Goblin resumes the same Work workspace for a
repository turn of the same attempt. Recreating compute does not create a retry. Native runtime sessions are
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

Surviving workspace files take precedence over archived checkpoints. On an
initial checkpoint restoration, the broker detects changes to the recorded remote branch. The
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

[Repository setup memory](repository-setup-memory.md) preserves preparation
knowledge across Work items independently of these workspace files. It does not
keep a container running or share a mutable workspace between Work items.

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
pressure only after Work is approved and completed, its latest workspace changes
have a verified checkpoint, cleanup has
finished, and no pod mounts the volume. Unfinished and cancelled Work volumes
remain retained. Existing volumes can resume even when the retained-volume count
is full; a new Work requiring storage fails for attention instead of deleting
unfinished files. Suspended Sandbox identities remain retained.

One-month expiration is deferred. Discarding unfinished files or exporting them
before expiration requires a separate retention policy. Recovery and logging of
the Goblin .NET backend itself are outside this change.

## Schema and installation

The Work workspace reference is stored in the existing JSONB snapshot, so it
needs no additional SQL columns or EF mappings. Execution and inspection use
`k8s/<namespace>/work-<work-id>` from that saved workspace.

Goblin is unreleased. Local test installations are disposable and should be
reset when the storage contract changes. Update `0001_initial.sql` and regenerate
EF mappings directly when database changes are needed. The current baseline
includes turn projections, workspace checkpoints, and inspection sessions.
See [local test installations](kubernetes-names.md#local-test-installations).

## Verification

The initial 2026-09-25 implementation was checked with the core and application suites, real
PostgreSQL persistence/recovery tests, the full `npm test` gate, and browser Work
and workspace journeys. The default gate skips PostgreSQL-dependent cases;
those and the optional Work HTTP test were exercised separately against temporary
databases. One browser lost-response check timed out during the combined run and
passed when rerun alone.

A temporary k3s namespace exercised the production worker and host with real
Codex startup. Fixture commands wrote tracked, untracked and ignored files while
Codex was active, then interrupted the native process. Suspension removed the
pod. An explicit new attempt reused the same PVC UID, preserved the exact dirty
Git state and ignored output, selected its new authorized branch, and created a
different native session. Cancellation and subsequent suspension were confirmed.
The broker was a local test fixture; this did not publish to GitHub. Authenticated
model requests failed before editing, so successful model completion remains
unverified by this check. Test sandboxes and their private inputs were removed.

The subsequent clean-slate simplification removed workspace format conversion
and updated the affected fixtures. Compilation was checked; tests were not rerun
during cleanup because they recreate local application and test state.

### Earlier checkpoint/restoration verification

The local rollout on 2026-09-23 exercised a real Codex repository execution and
GitHub publication. One logical attempt completed four runtime turns: an initial
checkpoint and release, a conversation-only request for file access, restoration
into a new sandbox, and an immediate reply in that retained sandbox. Three verified
checkpoints preserved the tracked changes and a Git-ignored output throughout.

Browser validation opened the saved file and diff, restored a separate inspection
sandbox, streamed real Kubernetes exec output, confirmed writes were rejected, and
stopped the inspection explicitly. The validation branch, draft PR, databases,
and sandbox resources were cleaned up.

Regression coverage includes conversation ownership and stale deliveries,
capacity reservations during queued continuation, paused-execution failures,
durable inspection start/stop delivery, archive limits and unsafe paths, output
schema compatibility, and saved-file rendering. Real PostgreSQL application and
persistence suites were run separately from the default suites that skip database
tests when no connection is configured. The standard browser suite's optional
database journeys remain opt-in; the live validation above covers the integrated
workspace lifecycle on the deployed host type.
