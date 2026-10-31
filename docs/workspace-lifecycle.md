# Workspaces, conversation turns, and Git checkpoints

Each repository Work owns one persistent Sandbox and PVC. PostgreSQL stores Work
conversations, lifecycle history, execution records, inspection sessions, and
small Git checkpoint records. **Workspace files and filesystem archives are never
stored in PostgreSQL.** Goblin does not archive the workspace after a turn.

## One workspace per Work

The first repository execution creates a Sandbox and private PVC named
`work-<work-id>`. Both persist across turns, explicit retries, and requested
revisions. The Work snapshot records their opaque address and the last attempt,
allocation, and turn that could have changed files. Attempts keep separate
claims, runtime sessions, and repository grants. A workspace is bound to one
repository; a different repository needs separate Work.

Suspension stops the pod and releases CPU and memory but keeps the Sandbox object
and PVC. It does not need a filesystem backup. A runtime failure requires
attention and confirmed cleanup before an explicit retry. A proposed result leaves
Work waiting for review with its files kept.

New compute receives fresh attempt-specific credentials and inputs. Kubernetes
resource versions fence ownership changes, so delayed starts and cleanup cannot
take over newer allocations. Each attempt and turn has a create-only claim file.
A duplicate start cannot reactivate a suspended allocation.

The next authorized branch starts at the surviving local Git HEAD, keeping
unpublished commits, staged changes, dirty and untracked files, installed tools,
and ignored outputs. A new Codex thread receives the saved Work context and
checks `git status` and `git diff`. Native sessions live only in pod storage.
Missing retained storage requires attention; Goblin never replaces lost files
with a fresh checkout or an older checkpoint.

Repository executions mount the same Work PVC at `/workspace`, `/runtime`, and
`/tmp`; the last two use the `.goblin/runtime` and `.goblin/tmp` subdirectories.
Tool homes, dependency caches, and scratch files persist across suspension and
retries. Private worker directories are excluded from previews and Git
checkpoints. A non-root init container creates the directories without changing
the checkout. Add more writable top-level mounts only in
`SandboxManifestBuilder.PersistentDirectories`, which configures both
initialization and PVC subpaths. There is no automatic cache cleanup, separate
PVC, or per-directory quota.

The PVC requests 2 GiB in total. Whether that is enforced depends on the storage
driver: local-path provisioning reports the request but sets no filesystem quota.

Codex's writable authentication and native sessions live at `/run/codex`, on a
pod-lifetime `emptyDir` with no size limit. They are discarded with the pod and
never copied to the Work PVC. Each new pod receives current credentials through a
read-only Secret mount, so credentials are never kept in an inspectable Work
volume.

## Conversation and execution

An agent question pauses the attempt. Answering saves the decision and
atomically queues the next runtime turn of the same attempt. The runtime chooses
to keep compute running for interactive work or to suspend it while waiting.
There is no idle timer or background inference. After suspension, the next turn
first runs with tools disabled, using saved Work context. If it needs file access,
the same Work workspace resumes for a repository turn. This is not a retry.

Runtime failure, cleanup failure, uncertain execution, cancellation, and explicit
retry follow the core attention and ownership rules in
[Work lifecycle](work-lifecycle.md). Failed Work is never retried automatically.

## Files and Git checkpoints

At the end of a turn the worker stops the runtime, checks that no other processes
are running, and commits all repository changes. When push is approved it
publishes through Goblin's GitHub broker; otherwise it sends a Git object bundle
and the controller verifies the commit without publishing. `workspace_checkpoint`
records the Work, attempt, turn, allocation, repository, branch, commit SHA, and
time. It holds Git metadata only, with no archive data. The internal checkpoint
endpoint accepts a bounded JSON body with the turn number and commit SHA.

Files stay on the PVC. `.goblin/changes.patch` holds the latest turn's Git diff
for inspection. The Git checkpoint also anchors verified repository setup memory.
It does not back up untracked or ignored files, tools, or build outputs, and never
authorizes deleting a volume. Published commits can be fetched from GitHub; files
outside Git depend on the retained volume and any backups the operator manages.

## Inspection

The Work page has no inspection UI. The inspection API lists verified Git commits
and recorded sessions. Starting an inspection session creates a separate pod that
mounts the Work PVC read-only, with its own 128 MiB temporary directory. Execution
must have stopped, compute must be released, and cleanup must be confirmed.
Inspection and repository execution both reserve capacity and never use the same
Work volume at the same time. The inspection pod gets no agent credentials,
database credentials, Kubernetes token, or archive download.

File, diff, and terminal requests use the recorded inspection pod. File previews
reject path traversal, symbolic links, and private Git or worker files. The file
list stops at 1,000 entries and a preview at 1 MiB; use the terminal for larger
trees. File content is returned as text. Git checkpoints are metadata, not
filesystem snapshots, and there is no archive download or restore.

Disconnecting a terminal does not stop inspection; an explicit stop request
releases the pod. Recorded sessions survive disconnection and count against
capacity. Terminal connections require authentication and same-origin checks,
and availability is rechecked while connected. File requests also need an
available inspection session that belongs to the Work. Browsers cannot choose
Kubernetes targets. The terminal is a streaming command shell, not a full-screen
terminal emulator.

## Capacity and retention

The database serializes admission of execution claims and inspection
reservations. Extra sessions wait for capacity. A failed inspection allocation
keeps its reservation until explicitly stopped. Execution deadlines and resource
failures require attention.

| Configuration | Default |
| --- | --- |
| `GOBLIN_MAX_SANDBOXES` | 2 |
| `GOBLIN_SANDBOX_CPU_LIMIT` | 2 |
| `GOBLIN_SANDBOX_MEMORY_LIMIT` | 2Gi |
| `GOBLIN_MAX_CACHED_WORKSPACES` | 4 retained repository volumes |

Existing volumes can resume at the volume limit. Work that needs a new volume
beyond the limit fails for attention instead of deleting files. Goblin never
reclaims completed, cancelled, or unfinished Work volumes automatically. Expiry,
export, and deletion need a separate retention policy.

## Schema and verification

The SQL baseline contains metadata-only Git checkpoints and inspection sessions.
Change `0001_initial.sql` directly and regenerate EF mappings from it; recreate
development databases when the format changes, as described in
[database setup](database.md#apply-the-sql-schema). Recreating the database does
not authorize deleting Work PVCs. A Git checkpoint alone never proves filesystem
data can be discarded.

Tests cover suspension without a file archive, dirty and ignored files kept across
attempts, storage loss and capacity protection, metadata-only checkpoints,
durable inspection reservations, bounded PVC file reads, and the inspection API.
Run the opt-in PostgreSQL tests explicitly; default runs skip them without a
connection. Kubernetes and runtime checks are still needed for the deployed
boundary.
