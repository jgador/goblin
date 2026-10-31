# Workspaces, conversation turns, and Git checkpoints

Each repository Work owns one Sandbox and one PVC. PostgreSQL stores
conversations, history, execution records, inspection sessions, and small Git
checkpoint records. **Workspace files and filesystem archives are never stored in
PostgreSQL.** Goblin does not archive the workspace after a turn.

## One workspace per Work

The first repository execution creates a Sandbox and private PVC named
`work-<work-id>`. Both persist across turns, explicit retries, and requested
revisions. The Work snapshot records the environment reference and the last
attempt, workspace allocation, and turn that could have changed files. Attempts
keep separate claims, runtime sessions, and repository grants. A workspace is
bound to one repository; a different repository needs separate Work.

Suspension stops the pod and keeps the Sandbox object and PVC. **The PVC is the
workspace: never delete it to suspend or release compute.** New compute gets fresh
attempt credentials and inputs; see
[execution hosting](execution-hosting.md#durable-ownership) for fencing.

A new attempt branch starts from the local Git HEAD on the PVC, with
unpublished commits, staged and untracked files, installed tools, and ignored
outputs. A new Codex thread gets the saved Work context and checks `git status`
and `git diff`. If retained storage is missing, Work requires attention; Goblin
never replaces lost files with a fresh checkout or an older checkpoint.

Repository pods mount the Work PVC at `/workspace`, and its `.goblin/runtime` and
`.goblin/tmp` subdirectories at `/runtime` and `/tmp`. Files in all three persist
across suspension and retries. A non-root init container creates the
subdirectories. Add writable top-level mounts only in
`SandboxManifestBuilder.PersistentDirectories`, which sets both the init step and
the mounts. There is no automatic cache cleanup or per-directory quota.

The PVC requests 2 GiB from the default storage class. The k3s default,
`local-path`, does not enforce this size.

Codex authentication and native sessions live at `/run/codex`, a pod-lifetime
`emptyDir` with no size limit. They are discarded with the pod and never copied to
the PVC. Each new pod gets current credentials through a read-only Secret mount.

## Conversation and execution

An agent question pauses the attempt. The answer queues the next turn of the same
attempt; this is not a retry. The runtime either keeps compute running or
releases it while waiting. There is no idle timer. After release, the next turn
first runs with tools disabled, using saved Work context. If it needs files, the
same workspace resumes for a repository turn.

Failures, uncertain execution, cancellation, and retry follow
[Work lifecycle](work-lifecycle.md). Failed Work is never retried automatically.

## Files and Git checkpoints

At the end of a turn the worker stops the runtime, checks that no other processes
run, and commits all repository changes. When push is approved it publishes
through the GitHub broker; otherwise the controller verifies the commit from a Git
bundle without publishing. `workspace_checkpoint` stores Work, attempt, turn,
workspace number, repository, branch, commit SHA, and time; it holds no file data.
The checkpoint also anchors verified repository setup memory.

`.goblin/changes.patch` holds the latest turn's Git diff. A checkpoint does not
back up untracked or ignored files, tools, or build outputs, and never authorizes
deleting a volume. Files outside published commits depend on the retained PVC and
any backups the operator manages.

## Inspection

The Work page has no inspection UI. The inspection API, available when
`GOBLIN_EXECUTION_NAMESPACE` is set, lists verified Git checkpoints and inspection
sessions. A session starts a separate `inspect-<session-id>` pod that mounts the
Work PVC read-only, with a 128 MiB `/tmp`. The inspected attempt must be the last
one that used the workspace, execution must have stopped, compute must be
released, and cleanup must be confirmed. A Work has at most one active session,
and execution waits until it stops. The pod gets no agent, database, or Kubernetes
credentials.

File and terminal requests need an available session that belongs to the Work;
browsers cannot choose Kubernetes targets. File reads reject path traversal,
symbolic links, non-regular files, and private Git and `.goblin` files (except
`.goblin/changes.patch`). The file list stops at 1,000 entries and a preview at
1 MiB; use the terminal for more. There is no archive download or restore.

The terminal is a streaming command shell, not a full-screen terminal emulator. It needs
authentication and a same-origin request, and the session and sign-in are
rechecked every 5 seconds while connected. Disconnecting does not stop the
session; an explicit stop request releases the pod.

## Capacity and retention

The database serializes admission of execution claims and inspection sessions.
Repository attempts that are running, uncertain, cleanup-pending, or holding
compute, and inspection sessions that are starting, available, stopping, or need
attention, count toward
`GOBLIN_MAX_SANDBOXES`; extra work waits. A failed inspection session keeps its
place until explicitly stopped.

| Configuration | Default |
| --- | --- |
| `GOBLIN_MAX_SANDBOXES` | 2 |
| `GOBLIN_SANDBOX_CPU_LIMIT` | 2 |
| `GOBLIN_SANDBOX_MEMORY_LIMIT` | 2Gi |
| `GOBLIN_MAX_CACHED_WORKSPACES` | 4 Work PVCs |

Existing PVCs can resume at the PVC limit. Work that needs a new PVC beyond the
limit fails for attention instead of deleting files. Goblin never deletes Work
PVCs automatically, including for completed or cancelled Work.

The SQL baseline holds Git checkpoints and inspection sessions; see
[database setup](database.md#apply-the-sql-schema). Recreating the database does
not authorize deleting Work PVCs.
