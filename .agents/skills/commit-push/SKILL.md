---
name: commit-push
description: "Commit and push Goblin changes to GitHub using Conventional Commits and Codex attribution. Run only when explicitly invoked as $commit-push."
---

# Commit and push

Use this workflow in the Goblin repository when the user explicitly invokes
`$commit-push` to commit and push. That invocation authorizes both actions for the
requested changes; proceed without another routine confirmation. Requests to
create, edit, or explain the skill do not invoke the workflow.

## Workflow

1. Review the branch, upstream, GitHub remote, staged and unstaged diffs, and
   relevant untracked files. Read recent commit messages if they are not already
   in context. Use paths or scope supplied by the user; otherwise use changes
   belonging to the current task. Resolve ambiguous scope before committing.
2. Reuse checks already completed for unchanged content. Run any remaining checks
   appropriate to the changes and `git diff --check`; documentation-only edits
   need no runtime tests. Report failures accurately and fix issues within scope.
3. Stage the intended files or hunks explicitly, then review the staged diff and
   run `git diff --cached --check`. Preserve unrelated edits and staged content;
   keep them out of the commit. Split independent requested changes when useful.
4. Write the message using the conventions below. Save it to a temporary file
   outside the checkout and use `git commit -F` to preserve literal text and
   newlines. Keep the user's configured Git author and committer identity.
5. Inspect all outgoing commits, since pushing publishes more than the newest
   commit. Push the current branch to its configured upstream in `jgador/goblin`.
   Without an upstream, use `origin` and the same branch name with
   `git push --set-upstream origin <branch>`. Check the destination before pushing;
   do not assume the branch is `master` or publish unrelated commits.
6. Verify the committed message and trailer, confirm the remote branch contains
   the commit, and report the short SHA, subject, branch, and push result. Mention
   any remaining uncommitted changes briefly.

If there are no changes to commit, push only pending commits covered by the
request; otherwise report that everything is up to date. Do not create an empty
commit. For a detached HEAD, unclear destination, or rejected push, report the
specific blocker and retain any local commit. Do not force-push, rewrite history,
bypass hooks, or create a PR or release as part of this workflow.

## Commit messages

Use the local rules below during commits. The
[Conventional Commits 1.0.0 source](https://www.conventionalcommits.org/en/v1.0.0/)
is reference-only; fetch it only when the user asks to review or update these
conventions.

```text
<type>[optional scope][!]: <description>

[optional body]

[optional footers]
```

The standard's essentials:

- A header requires a type, colon, space, and short description. An optional
  scope names a code area in parentheses, such as `fix(azure): ...`.
- Use `feat` for a new feature (SemVer minor) and `fix` for a bug fix (patch).
  Other types are allowed, including `docs`, `chore`, `refactor`, `perf`, `test`,
  `build`, `ci`, and `style`; they imply no version bump on their own.
- The optional body starts one blank line after the header and can contain
  multiple paragraphs. Separate footers from the body, or header if there is no
  body, with a blank line.
- Footers use `Token: value` or `Token #value`, such as `Refs: #123`. Use hyphens
  instead of spaces in tokens, as in `Co-authored-by`; `BREAKING CHANGE` is the
  exception. Footer values may span lines until another footer begins.
- A breaking change can occur with any type and implies SemVer major. Mark it
  with `!` immediately before the colon, a `BREAKING CHANGE: <explanation>`
  footer, or both. When using only `!`, explain the incompatibility in the
  description. `BREAKING-CHANGE` is an equivalent footer token.
- The standard is case-insensitive except for the uppercase breaking-change
  footer tokens. Lowercase types are this repository's preference.

Repository preferences:

- Use a lowercase type and short imperative description without a trailing
  period. Use `azure` for `deploy/azure` and `codex` for Codex workflow changes;
  omit the scope when no single area fits.
- Add a short body only when it helps explain the change. Include a concise
  `Validation:` paragraph with checks actually performed and material limits;
  distinguish user-reported verification from checks run in this session.
- End every Codex-assisted commit with the exact trailer below, separated from
  the body by a blank line. Preserve other genuine co-authors and avoid duplicates.

```text
Co-authored-by: codex <242516109+Codex@users.noreply.github.com>
```

Example, when the stated validation has passed:

```text
docs(azure): simplify portal deployment guide

Shorten portal and naming guidance and keep readiness checks easy to find.
Remove local-checkout deployment instructions.

Validation: git diff --check passed.

Co-authored-by: codex <242516109+Codex@users.noreply.github.com>
```
