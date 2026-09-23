# Repository setup memory

Goblin remembers successful repository preparation across Work items without
requiring setup files to be added to the repository. Codex decides what the
current task needs, inspects the checkout and installed tools, and can learn new
requirements during work (for example, adding Python to a .NET repository).
Existing repository instructions take precedence over learned observations.

## Remembering and recalling

The worker obtains repository-scoped observations through its existing broker
capability. PostgreSQL access stays in the controller. Memory is scoped to the
connected GitHub account and repository ID; a disabled repository, changed
account, or revoked grant cannot retrieve it. Observations record a topic,
reason, exact tool versions, preparation commands, verification checks, relevant
file hashes, environment identity, verification time, and Work/attempt/turn and
checkpoint provenance. Raw process output is not retained. Credentials and
private URLs must never be included; validation rejects common credential
formats and credential paths.

Before starting each repository turn, the worker selects observations whose
environment and input hashes still match. It fingerprints common manifests,
lockfiles, tool-version files, Dockerfiles, and AGENTS.md files, including added
or removed files. Codex supplies additional relevant paths such as custom setup
scripts. Ordinary source edits can reuse memory; changed requirements cause
reassessment. Hash equality is only a relevance filter: Codex must still inspect
current instructions, task needs, and installed tools. Unrecognized requirements
and task-specific changes remain the agent's responsibility.

Environment identity includes the configured image reference, OS, architecture,
and worker assembly version. Use immutable image references for reproducible
environments; a mutable tag is not a content digest. Matching identity never
proves that a tool remains installed on a replacement container.

Recall selects the latest observation per topic/environment/input variant before
bounding the database result to 64 candidates. The worker supplies at most eight
matching topics and 24 KB of context. This is bounded recall, not a guarantee
that every historical variant will be offered. Old observations are not deleted
on a timer or globally marked obsolete: a three-month-old configuration may
still apply on an older branch. Later successful verification refreshes a
variant; changed inputs retain a separate variant.

## Verification and durability

Codex's structured result can propose setup observations. A proposal alone is
not persisted. After retiring Codex, the worker runs each supplied check inside
the sandbox using `/bin/sh`, the original environment, and the repository root.
Checks should inspect versions and dependency availability, must exit with zero,
and must match the expected trimmed standard output. Output is bounded and the
verification batch has a two-minute deadline. Verification rejects changed
requirements, escaping paths, and symbolic-link inputs. Checks are execution
evidence from the worker, not a security attestation or proof that the recipe
will reproduce the environment indefinitely.

Only after all checks pass and a workspace checkpoint is saved does the worker
submit the observations. The controller rechecks the current attempt, turn,
repository grant, and checkpoint under the same database concurrency lock used
by Work. A batch is atomic; identical duplicate delivery does not change its
verification time, and conflicting or stale writes are rejected. Failure follows
the existing Work attention policy, with no automatic retry. Repository setup
commands and candidates are excluded from public runtime progress/results.

Preparation commands are remembered for the agent to assess, never automatically
replayed by the controller or worker. Repository scripts execute only within
the isolated sandbox. No new repository files, memory dashboard, or confirmation
step are required. Codex may make an occasional nonblocking suggestion to add
portable setup instructions when useful.

## Storage and scope

PostgreSQL remembers preparation knowledge. PVCs and checkpoints retain eligible
workspace files. Installs under `/runtime` or `/tmp` disappear on replacement;
non-root tool installations can use writable workspace paths. Large dependency
trees still count against checkpoint limits. Shared prepared environments and
cross-Work dependency caches are separate work; this feature reduces repeated
discovery and does not guarantee that installation can be skipped.

The schema is part of the unreleased `0001_initial.sql`, with EF mappings
regenerated from a fresh database. No compatibility migration is added. Existing
local data is not reset by this change; a local database using an older baseline
must be updated deliberately before running the new application.
