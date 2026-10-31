# Local secret scanning

Goblin uses the [Gitleaks CLI](https://github.com/gitleaks/gitleaks), which is
MIT-licensed and runs locally without an account, license key, or source upload.
`.gitleaks.toml` extends the built-in rules and adds checks for OpenAI key
prefixes and staged Goblin/Codex authentication files.

## One-time setup per checkout

Install **Gitleaks 8.30.1 or a newer 8.x release** on your `PATH`. On macOS,
`brew install gitleaks` works. For Linux and Windows, download the archive from
the [official releases](https://github.com/gitleaks/gitleaks/releases) and verify
it against the release's checksums file.

With Gitleaks on your `PATH` and Node.js 24 or newer, run from the repository:

```bash
gitleaks version
make secrets-setup
make secrets-scan
```

Setup enables the checked-in `.githooks/pre-commit` through this checkout's local
`core.hooksPath`. It refuses to replace an existing hook configuration. If you
already manage hooks, add `node scripts/check-secrets.mts staged` to your
pre-commit hook and pass on its exit status. Git for Windows includes the shell
the hook needs. Gitleaks and Node must also be on the `PATH` your Git client uses.
The scripts run directly through Node's type stripping and need no `make install`
or build output.

## Scan commands

| Command | Coverage |
| --- | --- |
| `make secrets-scan` | Full contents of the current index, plus tracked working-tree files and non-ignored untracked files, including hidden files. |
| `make secrets-scan-staged` | Full index contents of staged additions and modifications, including renames and type changes. Runs before each commit after setup. |
| `make secrets-scan-history` | All commits reachable from local refs, including secrets added and removed in intermediate commits. Run before pushing existing commits. |
| `make secrets-test` | Isolated integration tests against the installed Gitleaks CLI and Git hook. |

The scanner reads exact index blobs, so fixing a working-tree file without
restaging it does not hide a staged secret. A staged deletion can remove a secret
without blocking the cleanup commit. The history scan checks only refs available
locally; it does not fetch from GitHub. Hooks must be set up in each clone and do
not protect GitHub or commits that bypass hooks.

Findings show only the snapshot, path, line, rule, and (for history) commit.
Secret values and source excerpts are never printed. Snapshots and redacted
reports go to a private OS temporary directory that is removed when the scan
ends. Exit status is `0` for no findings, `1` for candidates to review, and `2`
for an incomplete scan or tool error.

Ignored, untracked runtime files such as `.goblin-secrets/owner-password`,
`.goblin-platform/`, `.artifacts/`, `auth.json`, `owner-password`, and `.env` are
not opened. Tracked files are checked even when an ignore rule matches them.
Symlinks are scanned as their link text, without following the target. Unmerged
entries, submodules, and paths under a symlinked directory need separate review
and make the scan fail.

Gitleaks uses pattern matching, decoding, and archive inspection (two archive
levels). Its upstream rules still skip some formats and paths, such as images,
fonts, lockfiles, and Gitleaks configuration files. It cannot prove that every
credential format, binary, image, or encoded value is safe. The
[`check-secrets` skill](../.agents/skills/check-secrets/SKILL.md) adds contextual
review for these gaps and for credentials with unfamiliar formats.

## Handling a finding

One narrow exception allows the `tools/goblinctl/src/credentials.rs` SHA-256
checksum in `dependencies.lock.json` and generated release metadata. Other fields
and rules stay active. The PostgreSQL certificate paths in
`backend/src/Goblin.Web/appsettings.json` are not private keys. Client
certificates use [profile stores](database.md#profile-management-and-storage);
keep private keys out of Git and Docker.

Review the reported location locally. Replace real credentials with runtime
configuration, remove private files from the index, and restage corrected files
before scanning again. If a real key entered Git history, revoke or rotate it;
deleting the current file does not remove the old commit.

Do not add blanket exclusions or a baseline only to make the scan pass. A
justified false-positive exception in `.gitleaks.toml` must match a specific
synthetic value, or a narrowly defined generated metadata field and path, and
give a reason. The wrapper ignores `gitleaks:allow` comments and `.gitleaksignore`
files so they cannot silently hide a candidate.
