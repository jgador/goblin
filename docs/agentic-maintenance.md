# Agentic maintenance

Two [GitHub Agentic Workflows](https://github.github.com/gh-aw/) use Codex with the
repository's `OPENAI_API_KEY` secret to propose maintenance changes. They do not
replace ordinary CI or its required `test_results` check.

| Workflow | Source | Triggers | Allowed changes |
| --- | --- | --- | --- |
| CI Maintenance | [ci-policy.md](../.github/workflows/ci-policy.md) | Branch pushes and manual dispatch | `config/ci-verification.json` only |
| Docs Maintenance | [documentation.md](../.github/workflows/documentation.md) | Pull requests opened, updated with commits, or reopened; manual dispatch | Markdown guides under `docs/` and project `README.md` files |

The corresponding `.lock.yml` files are generated executable workflows. Keep the
sources and generated files together. Compilation uses gh-aw `v0.92.0`; its
reviewed setup-action pin lives in [actions-lock.json](../.github/aw/actions-lock.json).

## Owner-only execution

Only `jgador` can start inference or publish maintenance outputs in `jgador/goblin`.
Docs Maintenance has no push trigger. Start it manually from **Actions → Docs
Maintenance → Run workflow**, or open, update, or reopen a pull request. PR runs
require a source branch in this repository; fork PRs are excluded. CI Maintenance
continues to accept branch pushes and manual dispatches. The original
`github.actor` and `github.triggering_actor` must both be `jgador`; another user's
rerun of an owner-created run cannot start the agent, detector, or publication
jobs. Contributors, bots, forks, comments, schedules, and CI completion
events do not invoke these agents. A contributor's push may produce a skipped
Actions entry, but it does not use the OpenAI key. A change merged or pushed by the
owner can be reviewed even when its commits were originally authored by someone
else: authorization applies to the GitHub event actor.

Branches beginning `gh-aw/` are excluded, including PR source branches and manual
selections, to prevent maintenance from invoking itself. Ordinary CI still applies
to their changes. Runs for the same workflow and ref are serialized with GitHub's
maximum pending queue, so a new run does not replace the prior pending review. Budget guardrails can
still skip runs; use a manual snapshot review when catching up.

The preparation job checks authorization again in code and has no OpenAI secret.
CI preparation reads the complete push range. PR preparation checks out the exact
source commit and compares it with the merge base of the PR's base and head.
This excludes unrelated changes made only on the base branch. The packet retains
only the PR action, source repository, and branch needed to recheck snapshot
authorization; it does not include PR titles or bodies. Preparation discovers
projects, selects candidate docs, and builds bounded context. Manual runs inspect
the selected tracked snapshot; they can be more expensive without a change delta.

## CI policy upkeep

The deterministic pass discovers `.csproj`, `Cargo.toml`, and `package.json` files
anywhere in the tracked repository. It produces added/removed manifest lists and
`proposed-policy.json`; Codex does not have to reconstruct the inventory.

Codex reviews relevant build/configuration changes and shared consumers, then
proposes inventory, trigger, command, and dependency corrections. The scoped
validator compares manifests against the current .NET solution, Cargo workspace
members, and recognized Node runners before accepting registration. It preserves
existing triggers, conservative fallbacks, unsupported-language errors, required
integration dependencies, and generated-file checks. Unfamiliar build layouts fail
coverage validation rather than being assumed tested; extend the deterministic
coverage inspector when introducing a different runner shape.

New languages that require GitHub workflow jobs or toolchains are reported as
incomplete. This workflow cannot change its own execution permissions or create
new CI jobs: those files are outside its publication allowlist. Existing CI keeps
blocking unregistered projects until the proposed configuration is reviewed and
included on the feature branch. Automation removes inventory bookkeeping; it does
not remove review of changed test coverage.

## Documentation upkeep

The preparation pass searches current guides for references to changed files,
finds subtree READMEs, and identifies owning build/database/environment/Rust/CI
and release guides. Unmapped behavior is routed through `docs/README.md`.
Codex checks candidate claims against the source changes and current code, then
corrects demonstrated drift. Whole obsolete documents can be removed with their
incoming links repaired. Imported skills and `AGENTS.md` are excluded from edits.

Candidate detection is a heuristic, so it can miss indirect relationships.
Packets disclose omitted files, truncated diffs, and candidates beyond the
12-document limit. The agent must report incomplete review rather than claim
all documentation is current. Each run creates at most one draft PR, with up to
12 documentation files and a 128 KiB patch. No established drift means no PR.

## Token use and spending

The main agent uses `openai/gpt-6.1-sol` with medium reasoning effort, a maximum of
12 turns, an eight-minute agent-step timeout, and 40 AI Credits. gh-aw's separate
output security review is retained with 10 AI Credits and blocks publication on
failure. Together these budgets target the chosen **US$0.50 per workflow run**;
both workflows reviewing the same change can target US$1.00 combined. Each
workflow also has a 100-credit rolling daily guardrail. Manual dispatches can
bypass gh-aw's daily guardrail; their per-run budgets still apply. Medium reasoning
can consume more tokens, so the existing budget may end a review earlier.

One AI Credit represents US$0.01 in gh-aw's accounting. Accounting uses compiled
model prices and provider usage; it is an estimate, and an in-flight request or
provider billing difference can exceed the nominal amount. Confirm actual charges
in the OpenAI usage dashboard. See [gh-aw cost management](https://github.github.com/gh-aw/reference/cost-management/)
and [billing](https://github.github.com/gh-aw/reference/billing/).

The implementation avoids token use before inference:

- No candidates means the agent job never starts.
- Project inventories and documentation routing run in Node without model calls.
- Packets contain at most 100 changed paths, 12 candidate docs, 24 KiB of diff, and
  64 KiB overall; the agent reads focused sections instead of the whole repository.
- Broad GitHub MCP read tools, web search, imported plugins, long-lived memory,
  old conversation resume, subagents, full builds, and repeated verification are
  excluded from the maintenance prompts. The Codex skill catalog is limited to
  1,000 context tokens without changing any imported skills.
- Harness retries are disabled. The context-rebuild guard is lowered to factor
  10 with a 100,000-input-token floor, and three consecutive proxy cache misses
  stop further requests. These guards can stop legitimate work too; inspect the
  incomplete report before increasing them.

A new workflow run starts fresh. Within a run, tool results and conversation
history are still part of later model requests. Repeated large file reads,
verbose logs, high reasoning effort, and repeated approaches increase usage.
[OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching)
reduces processing for matching prefixes; it does not make a growing conversation
free or guarantee that separate workflow runs share a cache. The maintained
prompts keep instructions stable and put changing evidence in the context packet.
[GPT-6.1 Sol](https://developers.openai.com/api/docs/models/gpt-6.1-sol) supports
medium reasoning effort. The output security review inherits the model; its
separate 10-credit budget still applies.

Inspect actual usage after representative runs:

```bash
gh aw logs ci-policy --last 5
gh aw logs documentation --last 5
gh aw audit <run-id>
```

Compare uncached input, cached input, output, turns, and tool activity. A high
cumulative input count can reflect cached history reuse rather than waste;
repeated equivalent reads without progress are a more useful signal. Never
increase budgets automatically after failures.

## Publication and verification

The agent receives read-only GitHub permissions. The OpenAI credential is supplied
to gh-aw's API proxy and excluded from the sandbox and shell environment. A
separate safe-output job creates draft PRs after local scope/coverage/link checks
and gh-aw threat detection succeed. It cannot merge or push to the target branch.
The output allowlists constrain files and `gh-aw/<mode>-*` branch names.

gh-aw packages committed history, so the agent uses the owner-gated `snapshot`
command to validate and commit only the allowed edits on a local proposal branch
before requesting its draft PR. This workflow-specific commit packages the
automated proposal; developer checkouts still follow the explicit commit/push
agreement. A post-agent check rejects uncommitted edits, an unchanged proposal
head, or a bundle that omits the validated snapshot. It blocks publication when
the packaged content differs from the reviewed files.

The repository must allow GitHub Actions to create pull requests. With only
`OPENAI_API_KEY`, PR publication uses the normal `GITHUB_TOKEN`; GitHub does not
start CI automatically for its resulting events. See [triggering CI](https://github.github.com/gh-aw/reference/triggering-ci/).
Run CI on the proposed branch before merging or add a deliberately scoped CI
trigger credential separately. An OpenAI API key does not grant GitHub write or
workflow permissions.

Draft PRs explicitly set `create-pull-request.base-branch` to the branch being
reviewed: `github.head_ref` for documentation PR runs, otherwise `github.ref_name`.
Documentation repairs target the source branch of the original PR. This keeps feature-branch history out of
the maintenance diff and lets coverage repairs be included on that branch before
its required checks pass. Leaving the base implicit can select the repository's
default branch and make the file limits count unrelated feature changes.
The Actions summary records the preparation decision, agent and security-review
token usage, estimated credits, validation outcome, and draft PR or no-change
result. Incomplete reviews and failures remain in the run report; automatic
failure issues are disabled. Maintenance conclusions do not satisfy or override
the ordinary CI `test_results` check.

Install the compiler locally with `gh extension install github/gh-aw --pin v0.92.0`.
After editing a Markdown workflow, review the newly referenced actions/secrets
and regenerate its lock file:

```bash
gh aw compile ci-policy documentation --no-check-update
make ci-test maintenance-test make-test scripts-typecheck
```

The compiler may require approval for new restricted secret/action references.
Review those concrete changes before using `--approve`; the owner-authorized
initial configuration uses OpenAI inference and SHA-pinned GitHub/gh-aw actions.
Local tests exercise authorization, inventory changes, runner coverage, candidate
routing, context limits, scope/link enforcement, and compiled job guards without
spending API tokens. Live trigger rejection, model access, billing, draft PR
publication, and check reporting still require actual GitHub runs.
