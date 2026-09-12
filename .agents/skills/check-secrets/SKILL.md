---
name: check-secrets
description: "Review pending Git changes for exposed API keys, credentials, and authentication data before committing or pushing. Invoke explicitly as $check-secrets; inspect file contents and credential patterns, including sk-proj, as well as filenames."
---

# Check secrets before committing

Preserve this user's preference: review all pending changes for leaked keys,
secrets, and auth data using the actual contents. Filename checks alone are
insufficient. Include OpenAI key prefixes such as `sk-proj` and investigate
values that look like credentials even when their format is unfamiliar.

Run when explicitly invoked as `$check-secrets`. Requests to create, edit, or
explain this skill do not invoke the review. Default to a local, read-only
review; this skill itself does not authorize staging, committing, pushing,
credential rotation, or history rewrites. Honor separate authorization already
given in the session.

## Establish the publishable scope

- Unless the user narrows the scope, include every staged change, unstaged
  tracked change, and non-ignored untracked file, including hidden files.
  Use Git's inventory and NUL-delimited paths, such as
  `git status --porcelain=v1 -z` and `git ls-files --others --exclude-standard -z`.
  Ordinary recursive searches can omit hidden files or tracked ignored files.
- Review the full contents of affected files, not only added lines. Inspect
  both the exact index blobs and current working-tree versions when they differ.
  A local fix does not remove a secret from a previously staged version. Read
  blobs into a local scanner without printing their raw contents.
- Account for additions, renames, deletions, and partially staged files. For
  deletions, distinguish a secret removed from the proposed tree from one still
  present in the index or an outgoing commit. Report unresolved merge entries.
- When the requested review includes a push, inspect all outgoing commits to
  the intended destination, including intermediate commits where a secret may
  have been added and later removed. Use the available upstream reference and
  state if its freshness or the range is uncertain. Do not silently expand an
  ordinary pending-change review into an audit of all repository history.
- Record the reviewed paths and versions, using blob IDs or local content hashes
  as appropriate. Recheck if those bytes change before reporting or relying on
  the result. Do not alter the index merely to assemble a scan.

## Inspect contents without leaking them

Use a maintained local scanner such as Gitleaks with full redaction when
available, plus targeted pattern checks and contextual inspection. Check its
version and help for supported options; a scanner's default Git range may omit
the working tree, index, or untracked files. Ensure every snapshot above is
covered. If no scanner is available, use local pattern and entropy checks and
state that limitation. A failed or incomplete scan is not a clean result.

Start with local scanning before displaying diffs or source. Capture matches
inside the process and emit only the file, line, snapshot, and rule/category.
Inspect relevant context with candidate values replaced by `[REDACTED]`. Avoid
raw `rg` matches, raw diffs, or scanner logs that could echo a secret. Use `rg`
for discovery with filename-only output when appropriate. Keep any necessary
scan snapshots in a private temporary directory outside the checkout and remove
them afterward. Do not upload source, test candidate credentials against a
provider, or put secret values in reports, shell arguments, logs, or chat.

Cover these content signals without depending on exact token lengths:

| Signal | What to examine |
| --- | --- |
| OpenAI credentials | `sk-proj`, `sk-svcacct`, and other `sk-` key forms, including inside strings, JSON, URLs, logs, docs, and examples. |
| Other provider tokens | GitHub `github_pat_` and `ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_`, Anthropic `sk-ant-`, Slack `xox...`, AWS access-key IDs and associated secrets, Google API keys and service-account private keys. |
| Auth material | Bearer and Basic authorization values, JWTs, access/refresh/ID tokens, session cookies, client secrets, device codes, owner tokens, and exported login state. |
| Unprefixed secrets | Literal assignments to API-key, token, secret, password, or credential fields; Azure/OpenAI resource keys, connection strings, SAS signatures, database passwords, and URLs containing credentials. |
| Key material | PEM/OpenSSH private-key blocks, signing keys, private certificates/keystores, and embedded private-key JSON. Public certificates and public keys need different classification. |
| Unusual values | High-entropy strings, long hex/base64 values, encoded credential payloads, and credentials assembled from literal fragments. Entropy is a candidate signal, not proof. |

Use filename patterns as an additional check: environment files, `auth.json`,
`owner-token`, credentials/config exports, private keys, logs, caches, backups,
archives, screenshots, and generated artifacts. Do not exclude a changed file
because it is a test, fixture, documentation, lockfile, or binary. Inspect relevant
binary/artifact contents safely when feasible; otherwise identify the unreviewed
files explicitly. Account for LFS pointers, submodules, and symlinks without
silently following them into private data outside the review scope.

Do not let existing scanner allowlists, baselines, ignore comments, or exclusions
silently hide findings in pending content. Inspect their effect and changes to
those rules. Add neither blanket exclusions nor broad allowlists to make a scan
pass.

## Classify candidates and check storage boundaries

Distinguish confirmed secrets, unresolved suspicious values, and explained
non-secrets. Do not assume a credential is fake because it appears in tests or
contains the word "example". Establish why a fixture is synthetic from its value,
construction, and use, without authenticating with it. Likewise, distinguish
environment-variable references, public identifiers, schema URLs, checksums,
and package integrity hashes from actual credential values. A real expired or
revoked credential is still sensitive material to flag.

Verify that local auth storage is actually excluded from Git and absent from the
index. `.gitignore` does not protect files that are already tracked. Check relevant
build/package exclusions when pending changes could copy credentials into an
artifact. Do not open ignored credential stores just to verify their exclusion;
use Git inventory, ignore rules, paths, and metadata.

For Goblin, specifically check `.goblin-auth/`, `.goblin-browser-test/`, Codex
`auth.json`, Goblin `owner-token`, environment files, and `.dockerignore`.
The saved ChatGPT login and owner token are private runtime data. Do not print
or copy them. Keep this a secrets review; do not connect GitHub repositories,
change the user's login, or add authentication features as part of it.

When an actual leak is found, identify every reviewed snapshot containing it.
Explain the needed source/index cleanup. If it is already committed or published,
explain that deleting the current file does not erase history and recommend
revoking/rotating the affected credential. Perform remediation only within the
user's authorized scope, then rescan changed content.

## Report

Lead with findings, or say "No secrets found in the reviewed changes" when all
candidates are resolved and the stated scope was covered. Avoid claiming a
guarantee that the repository contains no secrets.

For findings, give `path:line`, the snapshot or commit, credential category, why
it is suspicious, and the required fix. Redact values completely. Summarize the
reviewed staged/unstaged/untracked or outgoing scope, checks actually run,
important explained false positives, and material coverage gaps. If a scanner
failed, files were skipped, or candidates remain unresolved, state that the
review is incomplete rather than reporting a clean result.
