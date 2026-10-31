# Agentic maintenance

Two [GitHub Agentic Workflows](https://github.github.com/gh-aw/) (gh-aw) use Codex
with the repository's `OPENAI_API_KEY` secret to propose maintenance changes as
draft PRs. They do not replace CI or its required `test_results` check.

| Workflow | Source | Triggers | Allowed changes |
| --- | --- | --- | --- |
| CI Maintenance | [ci-policy.md](../.github/workflows/ci-policy.md) | Pushes to any branch except `gh-aw/**`; manual dispatch | `config/ci-verification.json` only |
| Docs Maintenance | [documentation.md](../.github/workflows/documentation.md) | Pull requests opened, synchronized, or reopened; manual dispatch | `docs/**/*.md`, the root `README.md`, and `README.md` files under `backend/`, `frontend/`, `tools/`, `website/`, `deploy/`, `tests/`, and `scripts/` |

Neither workflow has a schedule. The `.lock.yml` files are generated from the
Markdown sources by gh-aw `v0.92.0`; change and commit them together. The
setup-action pin is in [actions-lock.json](../.github/aw/actions-lock.json).

## Owner-only execution

Only `jgador` can start inference or publish output in `jgador/goblin`. Both
`github.actor` and `github.triggering_actor` must be `jgador`, so another user's
rerun of an owner's run cannot start the agent, detection, or publication jobs.
Authorization uses the event actor, so the owner can review a pushed or merged
change whose commits someone else wrote. A contributor's push may show a skipped
CI Maintenance run; it does not use the OpenAI key. Docs Maintenance runs only for
PRs whose source branch is in this repository; fork PRs are skipped. To start a
run manually, use **Actions → Docs Maintenance → Run workflow** (or CI Maintenance).

Branches starting with `gh-aw/` are skipped, including PR source branches and
manual runs, so maintenance PRs do not start more maintenance. Normal CI still runs
on them. Runs for the same workflow and ref wait in a queue (`queue: max`) instead
of replacing each other. The daily budget can still skip runs; use a manual run to
catch up.

The `prepare` job checks authorization again in
[agentic-maintenance.mts](../scripts/agentic-maintenance.mts) and has no OpenAI
secret. CI Maintenance reads the full push range. Docs Maintenance checks out the
PR head commit and compares it with the merge base of the PR base and head, so
changes made only on the base branch are excluded. The packet keeps only the PR
action, source repository, and branch from the event; it does not include PR
titles or bodies. Manual runs review the whole tracked snapshot instead of a diff,
which can cost more.

## CI policy upkeep

`prepare` finds every tracked `.csproj`, `Cargo.toml`, and `package.json` file (the
policy's `manifestExtensions` and `manifestFilenames`), lists added and removed
manifests, and writes `proposed-policy.json`. Codex checks related build changes
and proposes inventory, trigger, command, and dependency corrections.

Validation accepts a project only when its runner covers it: the .NET solution used
by `backend-test` or a direct `dotnet test`/`dotnet build` command, the Cargo
workspace members used by `rust-test`, or a recognized Node command
(`scripts-typecheck`, `frontend-build`, or `npm --prefix DIR run check|test|build|typecheck`).
It rejects new groups, removed triggers or dependencies, and changes to
unsupported-language rules, documentation skips, full-verification fallbacks, and
`git diff --exit-code` checks. An unfamiliar build layout fails coverage
validation; extend `coverageProblems` in the script when adding a new runner shape.

A new language that needs a CI job or toolchain is reported as incomplete, because
this workflow can change only `config/ci-verification.json`. CI keeps blocking
unregistered projects until the proposed policy is reviewed and included on the
feature branch.

## Documentation upkeep

`prepare` selects documents that reference changed files, `README.md` files for
changed subtrees, and owning guides by path: CI files for
[GitHub Actions](github-actions.md), build manifests for
[repository layout](repository-layout.md), SQL for the [database guide](database.md),
environment definitions for [environment variables](environment-variables.md),
Rust for [Rust development](rust-development.md), and release scripts for
[releases](releases.md). If a non-Markdown change matches no document,
`docs/README.md` is the candidate for routing. Changes under `.agents/` and
`.lock.yml` files are ignored. Codex checks candidate claims against the change and
current code and fixes demonstrated drift. It may remove an obsolete document if it
repairs incoming links. `AGENTS.md` and imported skills are outside the allowed
files.

Selection is heuristic and can miss indirect relationships. The packet reports
omitted files, a truncated diff, and candidates beyond the 12-document limit; the
agent must then report an incomplete review. Each run opens at most one draft PR
with up to 12 files and a 128 KB patch. No established drift means no PR.

## Token use and spending

Both workflows use `openai/gpt-6.1-sol` with medium reasoning, at most 12 turns,
an 8-minute agent timeout, and 40 AI Credits per run. gh-aw threat detection has
its own 10-credit budget and blocks publication if it fails. Together these target
**US$0.50 per run**, or US$1.00 when both workflows review the same change. Each
workflow also has a 100-credit daily limit.

In gh-aw accounting, one AI Credit is US$0.01. It is an estimate from the compiled
model prices and provider usage; an in-flight request or a billing difference can
exceed it. Check actual charges in the OpenAI usage dashboard. See
[gh-aw cost management](https://github.github.com/gh-aw/reference/cost-management/)
and [billing](https://github.github.com/gh-aw/reference/billing/).

Limits before and during inference:

- If `prepare` finds no work, the agent job does not start.
- Project inventory and document selection run in Node without model calls.
- Packets contain at most 100 changed paths, 12 candidate documents, a
  24,000-byte diff, and 64,000 bytes in total.
- GitHub MCP tools are off (`github: false`), Codex plugins and history
  persistence are off, and the skill catalog is limited to 1,000 context tokens.
  The prompts forbid loading imported skills, web browsing, subagents, resuming
  old conversations, installing dependencies, full builds, and retrying failed
  approaches.
- Harness retries are off. The context-rebuild guard uses factor 10 with a
  100,000-input-token floor, and three consecutive proxy cache misses stop further
  requests. These guards can stop valid work; read the incomplete report before
  raising them.

Check actual usage after representative runs:

```bash
gh aw logs ci-policy --last 5
gh aw logs documentation --last 5
gh aw audit <run-id>
```

Compare uncached input, cached input, output, turns, and tool calls. Repeated reads
without progress matter more than a high cached-input total. Never raise budgets
automatically after failures.

## Publication and verification

The agent job has read-only GitHub permissions. The OpenAI key goes to gh-aw's API
proxy and is excluded from the agent sandbox environment. A separate safe-output
job creates the draft PR only after the post-agent validation and gh-aw threat
detection succeed. It cannot merge or push to the target branch. Allowlists limit
the files and the branch names (`gh-aw/ci-policy-*`, `gh-aw/documentation-*`).

gh-aw publishes only committed history, so the agent runs
`node scripts/agentic-maintenance.mts snapshot MODE`. It validates scope, links,
and coverage and commits the edits on a local `gh-aw/MODE-<head>` branch. This
commit is allowed only inside these workflows; developer checkouts still follow
the commit and push rules in [AGENTS.md](../AGENTS.md). The post-agent
`validate-proposal` step fails on uncommitted edits, an unchanged head, a branch
mismatch, or a bundle that does not contain the validated commit.

The repository must allow GitHub Actions to create pull requests. PRs created with
`GITHUB_TOKEN` do not start CI (see
[triggering CI](https://github.github.com/gh-aw/reference/triggering-ci/)). Run CI
on the proposal branch before merging, or add a separately scoped CI trigger
token. The OpenAI key grants no GitHub permissions.

Draft PRs set `create-pull-request.base-branch` to the reviewed branch:
`github.head_ref` for Docs Maintenance PR runs, otherwise `github.ref_name`. Keep
it explicit; an implicit base is the default branch, and the patch limits would
then count unrelated feature changes. Incomplete reviews and failures stay in the
run report; automatic failure issues are off. Maintenance results do not satisfy
`test_results`.

Install the compiler with `gh extension install github/gh-aw --pin v0.92.0`. After
editing a Markdown workflow, review newly referenced actions and secrets, then
regenerate the lock files and run the local checks:

```bash
gh aw compile ci-policy documentation --no-check-update
make ci-test maintenance-test make-test scripts-typecheck
```

The compiler warns about new restricted secrets or action changes; review them
before using `--approve`. Local tests cover authorization, inventory, runner
coverage, document selection, limits, scope and link checks, and compiled job
guards without API calls. Live triggers, model access, billing, PR publication,
and check reporting need real GitHub runs.
