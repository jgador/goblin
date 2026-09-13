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

For example, this installs the tested version on Linux x86-64 without root:

```bash
task_gitleaks_dir=$(mktemp -d)
curl -fL --retry 3 -o "$task_gitleaks_dir/gitleaks.tar.gz" \
  https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz
printf '%s  %s\n' \
  '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb' \
  "$task_gitleaks_dir/gitleaks.tar.gz" | sha256sum --check - && \
  tar -xzf "$task_gitleaks_dir/gitleaks.tar.gz" -C "$task_gitleaks_dir" gitleaks && \
  mkdir -p "$HOME/.local/bin" && \
  install -m 755 "$task_gitleaks_dir/gitleaks" "$HOME/.local/bin/gitleaks"
rm -rf "$task_gitleaks_dir"
export PATH="$HOME/.local/bin:$PATH"
```

Keep `~/.local/bin` on your shell's `PATH` for later sessions. Then, from this
repository, using its existing Node.js 22+ requirement:

```bash
gitleaks version
npm run secrets:setup
npm run secrets:scan
```

Setup enables the checked-in `.githooks/pre-commit` through this checkout's local
`core.hooksPath`. It refuses to replace an existing hook configuration. If you
already manage hooks, add `node scripts/check-secrets.mjs staged` to your existing
pre-commit hook and propagate its exit status. Git for Windows includes the shell
needed to run the hook. Gitleaks and Node must also be on the `PATH` used by your
Git client.

## Scan commands

| Command | Coverage |
| --- | --- |
| `npm run secrets:scan` | Full contents of the current index, plus tracked working-tree files and non-ignored untracked files, including hidden files. |
| `npm run secrets:staged` | Full index contents of staged additions and modifications, including renames and type changes. Runs automatically before each commit after setup. |
| `npm run secrets:history` | All commits reachable from local refs, including secrets added and removed in intermediate commits. Run before pushing existing commits. |
| `npm run test:secrets` | Isolated integration tests against the installed Gitleaks CLI and Git hook. |

The scanner reads exact index blobs, so fixing a working-tree file without
restaging it does not hide a staged secret. A staged deletion can remove a secret
without blocking the cleanup commit. The history command checks refs already
available locally; it does not fetch from GitHub. Local hooks must be set up in
each clone and do not enforce checks on GitHub or on commits that bypass hooks.

Findings show only the snapshot, path, line, rule, and (for history) commit.
Secret values and source excerpts are never printed. Temporary snapshots and
redacted reports are created in a private directory outside the checkout and
removed when the scan finishes. Exit status is `0` for no findings, `1` for
candidates needing review, and `2` for an incomplete scan or tool error.

Ignored, untracked runtime files such as `.goblin-auth/`, `.goblin-browser-test/`,
`auth.json`, `owner-token`, and `.env` are not opened. Tracked files are checked
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

Review the reported location locally. Replace real credentials with runtime
configuration, remove private files from the index, and restage corrected files
before rescanning. If a real key entered Git history, revoke or rotate it;
deleting the current file does not remove the old commit.

Do not add blanket exclusions or a baseline merely to make the scan pass.
Any justified false-positive exception in `.gitleaks.toml` should match a specific
synthetic value and path, with a reason. The wrapper deliberately ignores
`gitleaks:allow` comments and `.gitleaksignore` files so they cannot silently
suppress a candidate.
