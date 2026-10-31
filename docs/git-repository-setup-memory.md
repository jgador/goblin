# Git repository setup memory

Goblin remembers verified repository preparation across Work items without adding
files to the repository. Existing repository instructions take precedence. Codex
still inspects the checkout, installed tools, and the current task; remembered
commands are suggestions and are never replayed automatically.

## Recall

The worker reads and writes memory through its broker capability; only the
controller accesses PostgreSQL (`repository_setup_memory`). Memory is scoped to the
GitHub connection, account, and repository ID. Reads and writes require the current
attempt and an enabled repository on the same connected account. Observations record
topic, reason, tools and versions, commands, checks, input file hashes, environment,
verification time, and the Work, attempt, turn, and checkpoint they came from. Raw
process output is not kept. Credentials and private URLs are not allowed;
validation rejects common credential formats and credential paths.

Before each repository turn, recall keeps only observations whose environment
identity (image reference, OS, architecture, and worker build ID) and input hashes
match. Inputs include common manifests, lockfiles, tool-version files, Dockerfiles,
and `AGENTS.md` files, including added or removed files, plus the files Codex listed
for the observation. Matching hashes show relevance, not that tools are installed or
that a recipe still works. Mutable image tags are not content digests. Changed
requirements need reassessment; ordinary source edits can reuse memory.

Recall takes the latest observation per topic, environment, and input variant, limits
the query to 64 candidates, and supplies at most eight topics and 24 KB. Old
variants do not expire because they may apply to older branches. Successful
verification refreshes a variant; changed inputs create another variant.

## Verification and persistence

A Codex proposal is not saved on its own. After Codex exits, the worker runs each
check in the sandbox with `/bin/sh`, the original environment, and the repository
root. A check must exit zero and its trimmed stdout must match the expected output.
Each command has a 30-second limit and bounded output, and the batch has a
two-minute deadline. Goblin rejects observations whose inputs change during checks,
paths that leave the checkout, and symlinked inputs. These checks are evidence, not a
security guarantee or proof of future reproducibility.

After all checks pass and a Git checkpoint is saved, the controller checks the
attempt, turn, repository grant, and checkpoint again under the application
transaction lock. The batch is atomic. An identical duplicate write does not change
the verification time; a conflicting or stale write fails under the normal attention
policy, without automatic retry. Setup commands and candidates are excluded from
public progress and results. Repository scripts run only in the isolated sandbox.

## Workspace and storage

PostgreSQL stores observations and Git checkpoint metadata, not filesystem archives.
`/workspace`, `/runtime`, and `/tmp` share the Work PVC and survive pod replacement;
tools installed outside those mounts do not. Codex credentials and sessions in
`/run/codex` last only as long as the pod. See
[workspace mounts](workspace-lifecycle.md#one-workspace-per-work). Large
dependencies use PVC storage. Memory does not guarantee that installation can be
skipped, and there is no shared cache across Work items.
