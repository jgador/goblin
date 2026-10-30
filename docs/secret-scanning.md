# Local secret scanning

Goblin uses the [Gitleaks CLI](https://github.com/gitleaks/gitleaks), which is
MIT-licensed and runs locally without an account, license key, or source upload.
The configuration extends its built-in rules and adds checks for OpenAI key
prefixes and accidentally staged Goblin/Codex authentication files.

## One-time setup per checkout

Install **Gitleaks 8.30.1 or a newer 8.x release** on your `PATH`. On macOS,
`brew install gitleaks` works. For Linux and Windows, download the appropriate
archive from the [official releases](https://github.com/gitleaks/gitleaks/releases)
and verify it against the release's checksums file.

With Gitleaks on your `PATH` and Node.js 24 or newer, run from the repository:

```bash
gitleaks version
make secrets-setup
make secrets-scan
```

Setup enables the checked-in `.githooks/pre-commit` through this checkout's local
`core.hooksPath`. It refuses to replace an existing hook configuration. If you
already manage hooks, add `node scripts/check-secrets.mts staged` to your existing
pre-commit hook and propagate its exit status. Git for Windows includes the shell
needed to run the hook. Gitleaks and Node must also be on the `PATH` used by your
Git client.

The hook and setup scripts run directly as TypeScript through Node's built-in
type stripping; they do not require `make install` or compiled build output.

## Scan commands

| Command | Coverage |
| --- | --- |
| `make secrets-scan` | Full contents of the current index, plus tracked working-tree files and non-ignored untracked files, including hidden files. |
| `make secrets-scan-staged` | Full index contents of staged additions and modifications, including renames and type changes. Runs automatically before each commit after setup. |
| `make secrets-scan-history` | All commits reachable from local refs, including secrets added and removed in intermediate commits. Run before pushing existing commits. |
| `make secrets-test` | Isolated integration tests against the installed Gitleaks CLI and Git hook. |

The scanner reads exact index blobs, so fixing a working-tree file without
restaging it does not hide a staged secret. A staged deletion can remove a secret
without blocking the cleanup commit. The history command checks refs already
available locally; it does not fetch from GitHub. Local hooks must be set up in
each clone and do not enforce checks on GitHub or on commits that bypass hooks.

Findings show only the snapshot, path, line, rule, and (for history) commit.
Secret values and source excerpts are never printed. The wrapper currently creates
snapshots and redacted reports in a private OS temporary directory and removes
them when the scan finishes. This is existing tool behavior; task-authored review
output belongs under `.artifacts/` per [AGENTS.md](../AGENTS.md#verification-and-artifacts).
Exit status is `0` for no findings, `1` for
candidates needing review, and `2` for an incomplete scan or tool error.

Ignored, untracked runtime files such as `.goblin-secrets/owner-password`, `.goblin-platform/`, `.artifacts/playwright/`,
`auth.json`, `owner-password`, and `.env` are not opened. Tracked files are checked
even when an ignore rule matches them. Symlinks are scanned as their link text,
without following the target. Unmerged entries and submodules require separate
review and cause the scan to fail.

Gitleaks uses pattern matching, decoding, and archive inspection (two archive
levels). Its upstream rules still exclude some formats and paths, such as images,
fonts, lockfiles, and Gitleaks configuration files. It cannot prove that every
credential format, binary, image, or encoded value is safe. The existing
[`check-secrets` skill](../.agents/skills/check-secrets/SKILL.md) adds contextual
review, including these gaps and credentials with unfamiliar formats.

## Handling a finding

A narrowly scoped exception permits the `credentials.rs` SHA-256 checksum in
`dependencies.lock.json` and historical release metadata. Other fields and
credential rules remain active. PostgreSQL certificate paths in
`backend/src/Goblin.Web/appsettings.json` are not private keys. Client certificates
use [profile stores](database.md#profile-management-and-storage); keep private
keys out of Git and Docker. Older commits may contain a previously tracked password.

Review the reported location locally. Replace real credentials with runtime
configuration, remove private files from the index, and restage corrected files
before rescanning. If a real key entered Git history, revoke or rotate it;
deleting the current file does not remove the old commit.

Do not add blanket exclusions or a baseline merely to make the scan pass.
Any justified false-positive exception in `.gitleaks.toml` should match a specific
synthetic value or narrowly defined generated metadata field and path, with a
reason. The wrapper deliberately ignores
`gitleaks:allow` comments and `.gitleaksignore` files so they cannot silently
suppress a candidate.
