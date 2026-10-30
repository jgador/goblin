# Git repository setup memory

Goblin remembers verified repository preparation across Work items without adding
files to the repository. Existing repository instructions take precedence. Codex
still inspects the checkout, installed tools, and the current task; remembered
commands are suggestions, never automatically replayed.

## Recall

The worker uses its broker capability; PostgreSQL access stays in the controller.
Memory is scoped to the connected GitHub account and repository ID. A disabled
repository, changed account, or revoked grant cannot retrieve it. Observations
record topics, reasons, versions, commands, checks, file hashes, environment,
verification time, and Work/attempt/turn and checkpoint provenance. Raw process
output is not retained. Credentials and private URLs are prohibited; validation
rejects common credential formats and credential paths.

Before each repository turn, recall filters observations by environment identity
(image reference, OS, architecture, and worker assembly version) and input hashes.
Inputs include common manifests, lockfiles, tool-version files, Dockerfiles and
AGENTS.md files, including added or removed files; Codex can add relevant paths.
Matching hashes indicate relevance, not proof that tools are installed or a recipe
still works. Mutable image tags are not content digests. Changed requirements need
reassessment; ordinary source edits can reuse memory.

Recall selects the latest observation per topic/environment/input variant before
limiting the query to 64 candidates and supplying at most eight topics and 24 KB.
Old variants are not expired: they may apply to older branches. Successful
verification refreshes a variant; changed inputs create another variant.

## Verification and persistence

A structured Codex proposal is not persisted by itself. After retiring Codex,
the worker runs each check in the sandbox using `/bin/sh`, the original
environment and repository root. Checks should inspect versions and dependencies,
exit zero, and match expected trimmed stdout. Output is bounded and the batch has
a two-minute deadline; changed requirements, escaping paths, and symlink inputs
are rejected. These checks are evidence, not a security attestation or guarantee
of future reproducibility.

After all checks pass and a Git checkpoint is saved, the controller rechecks the
attempt, turn, repository grant, and checkpoint under the Work concurrency lock.
The batch is atomic; identical duplicate delivery does not change verification
time, and stale or conflicting writes fail under the normal attention policy,
without automatic retry. Setup commands and candidates are excluded from public
progress and results. Repository scripts run only in the isolated sandbox.

## Workspace and storage

PostgreSQL stores observations and Git provenance, not filesystem archives.
`/workspace`, `/runtime`, and `/tmp` share the Work PVC and persist across pod
replacement; tools outside those mounts do not gain that durability. Codex
credentials and sessions in `/run/codex` have pod lifetime. See [workspace mounts](workspace-lifecycle.md#one-workspace-per-work).
Large dependencies consume PVC storage. Shared prepared environments and
cross-Work caches are separate work; memory does not guarantee installation can be
skipped. Codex may suggest portable setup instructions without blocking work.

The schema belongs to the unreleased `0001_initial.sql`; regenerate EF mappings
from a fresh database. Recreate development databases with changed baselines only
deliberately, as described in [database setup](database.md#apply-the-sql-schema).
There is no compatibility migration or automatic reset.
