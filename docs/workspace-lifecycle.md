# Workspaces, conversation turns, and Git checkpoints

Each repository Work owns one Sandbox and one PVC. PostgreSQL stores conversations,
history, execution records, inspection sessions, and small Git checkpoint records.
**Workspace files and filesystem archives are never stored in PostgreSQL.** Goblin does
not archive the workspace after a turn.

## One workspace per Work

The first repository execution creates a Sandbox and private PVC named `work-<work-id>`.
Both persist across turns, explicit retries, and requested revisions. The Work snapshot
records the environment reference and the last attempt, workspace allocation, and turn
that could have changed files. Attempts keep separate claims, runtime sessions, and
repository grants. A workspace is bound to one repository; a different repository needs
separate Work.

Approving a result or cancelling Work queues deletion of its PVC after execution cleanup
and any inspection session have stopped. Local workspace files are lost when the PVC
is deleted. The Work record, attempts, results, and verified checkpoint metadata stay
in PostgreSQL. The Sandbox identity remains suspended to fence delayed execution
messages.

Suspension stops the pod and keeps the Sandbox object and PVC. **The PVC is the
workspace: never delete it to suspend or release compute.** New compute gets fresh
attempt credentials and inputs; see
[execution hosting](execution-hosting.md#durable-ownership) for fencing.

A new attempt uses the Work's branch and starts from the local Git HEAD on the PVC,
with unpublished commits, staged and untracked files, installed tools, and ignored outputs. A new Codex
thread gets the saved Work context and checks `git status` and `git diff`. If retained
storage is missing, Work requires attention; Goblin never replaces lost files with a
fresh checkout or an older checkpoint.

Repository pods retain `/workspace`, `/runtime`, and `/tmp` on the Work PVC. The
[manifest builder](../backend/src/Goblin.Execution/SandboxManifestBuilder.cs) defines
mounts and [SandboxHost](../backend/src/Goblin.Execution/SandboxHost.cs) owns PVC
requests; add writable mounts through the builder's shared directory definition. There
is no inactivity-based eviction or per-directory quota. The k3s `local-path` storage class
does not enforce requested PVC sizes.

Codex authentication and native sessions live at `/run/codex`, a pod-lifetime
`emptyDir`. They are discarded with the pod and never copied to the PVC. Each new pod
gets current credentials through a read-only Secret mount.

## Conversation and execution

An agent question pauses the attempt. The answer queues the next turn of the same
attempt; this is not a retry. The runtime either keeps compute running or releases it
while waiting. There is no idle timer. After release, the next turn first runs with
tools disabled, using saved Work context. If it needs files, the same workspace resumes
for a repository turn.

Failures, uncertain execution, cancellation, and retry follow
[Work lifecycle](work-lifecycle.md). Failed Work is never retried automatically.

## Files and Git checkpoints

At the end of a turn the worker stops the runtime, checks that no other processes run,
and commits all repository changes. When push is approved it publishes through the
GitHub broker; otherwise the controller verifies the commit from a Git bundle without
publishing. `workspace_checkpoint` stores Work, attempt, turn, workspace number,
repository, branch, commit SHA, and time; it holds no file data. The checkpoint also
anchors verified repository setup memory.

Before releasing compute, the controller compares the starting commit recorded on the
Work with the verified checkpoint and saves a standard `git format-patch` export under
`/data/platform/work-diffs/<work-id>/<checkpoint-id>.patch` in the installed platform's private
volume. The export applies as one commit with `git am`. For a cumulative export spanning
multiple commits, Git generates the patch from a temporary commit with the checkpoint's
tree. The real checkpoint SHA remains the reference for GitHub publication.

**Review changes** opens the latest saved patch from the Work page without opening a
sandbox or inspection session. On `Completed` or `Cancelled`, Goblin queues deletion of that Work's
patch directory with the status transition. The same cleanup removes superseded copies
for open Work. The review export is limited to 4 MiB.

Work created before the review export gets a saved patch on its next verified repository
checkpoint. Prior checkpoints and their recorded branches remain in Work history.

`.goblin/changes.patch` holds the latest turn's Git diff for workspace inspection. A checkpoint does not back up
untracked or ignored files, tools, or build outputs, and never authorizes deleting a
volume. Files outside published commits depend on the retained PVC and any backups the
operator manages.

## Inspection

The Work page has no inspection UI. The inspection API, available when
`GOBLIN_EXECUTION_NAMESPACE` is set, lists verified Git checkpoints and inspection
sessions. A session starts a separate `inspect-<session-id>` pod that mounts the Work
PVC read-only with private temporary storage. The inspected attempt must be the last one
that used the workspace, execution must have stopped, compute must be released, and
cleanup must be confirmed. A Work has at most one active session, and execution waits
until it stops. The pod gets no agent, database, or Kubernetes credentials.
Closing Work stops its inspection session and prevents opening another.

File and terminal requests need an available session that belongs to the Work; browsers
cannot choose Kubernetes targets. File reads reject path traversal, symbolic links,
non-regular files, and private Git and `.goblin` files (except `.goblin/changes.patch`).
File lists and previews are bounded; use the terminal for larger reads. There is no
archive download or restore.

The terminal is a streaming command shell, not a full-screen terminal emulator. It needs
authentication and a same-origin request, and the session and sign-in are rechecked
while connected. Disconnecting does not stop the session; an explicit stop request
releases the pod.

## Capacity and retention

The database serializes admission of execution claims and inspection sessions.
Repository attempts that are running, uncertain, cleanup-pending, or holding compute,
and inspection sessions that are starting, available, stopping, or need attention, count
toward `GOBLIN_MAX_SANDBOXES`; extra work waits. A failed inspection session keeps its
place until stopped. The default is 50 sandbox slots.

Configuration names are in the [environment catalogs](environment-variables.md). The
[runtime configuration](../backend/src/Goblin.Web/Hosting/WebRuntimeConfiguration.cs)
owns capacity defaults; [platform manifests](../deploy/platform/) own deployment
overrides and isolation.

The default `GOBLIN_MAX_CACHED_WORKSPACES` is 50 Work PVCs. Existing PVCs can resume at
the limit. Work that needs a new PVC reports **Workspace volume limit reached**.
Closing Work releases a slot after Kubernetes removes its claim. The close transition
queues deletion, so installing this behavior leaves volumes from previously closed Work
in place.

The SQL baseline holds Git checkpoints and inspection sessions; see
[database setup](database.md#apply-the-sql-schema). Recreating the database does not
authorize deleting Work PVCs.
