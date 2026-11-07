# GitHub Actions

[The workflow directory](../.github/workflows/) defines triggers, jobs, commands,
permissions, environments, and action pins. Markdown there is executable gh-aw
configuration, not ordinary prose. Release and deployment procedures belong in
[releases](releases.md), [website production](../website/production/README.md), and
[Chisel operations](../deploy/chisel/README.md).

## Naming convention

Use short Title Case workflow names, lowercase kebab-case filenames, and short lowercase
job IDs with at most one underscore. Omit job display names; use descriptive step names.
Keep required job IDs unique across workflows because workflow titles do not qualify
status-check names.

The platform's required result is `test_results`; the website's is `validate`.
Non-required jobs may reuse IDs. Keep reusable caller/called paths short.

## CI selection and required result

[config/ci-verification.json](../config/ci-verification.json) is the source of truth for
triggers, registered project manifests, verification commands, dependencies, and
conservative fallbacks. [scripts/ci.mts](../scripts/ci.mts) detects changes, runs
groups, and reports results. Its [tests](../scripts/ci.test.mts) define comparison and
selection edge cases.

Ordinary Markdown changes skip runtime groups; executable workflow Markdown does not.
Renames classify both sides, and a missing comparison fails detection. Unregistered
projects and unsupported languages fail rather than implying coverage.

`test_results` always runs. Detection and every selected group must succeed; only
unselected groups may skip. Missing, cancelled, or unexpectedly skipped jobs cannot
pass. Do not add workflow-level path filters that prevent this required result from
reporting.

Adding a language or independent project requires policy registration, real build/test
commands, toolchain setup, a workflow job and selection output, and required-result
dependencies. A group without an owning runner is not coverage. Run
`make ci-test make-test scripts-typecheck` after changes. Check publication and
merge-queue behavior require a real Actions run.

## Action runtimes

JavaScript actions, including nested actions in composites, must declare
`runs.using: node24`. setup-node controls repository commands, not action runtimes. Keep
SHA pins and review newly referenced actions.

[global.json](../global.json), the
[.NET setup action](../.github/actions/setup-dotnet/action.yml), and
[Dockerfile](../Dockerfile) must select the same SDK release. Update the setup action's
download checksum with the version. Its public direct feed avoids the redirect used by
the generic installer.

## Agentic maintenance

[CI Maintenance](../.github/workflows/ci-policy.md) and
[Docs Maintenance](../.github/workflows/documentation.md) propose bounded draft PRs.
Their Markdown sources own triggers, actor restrictions, allowed files, budgets, and
prompts; generated lock files must change with those sources.

Only owner-initiated runs in this repository can infer or publish. Fork PRs, other
actors' reruns, and maintenance branches cannot start maintenance inference. The
deterministic preparation step has no OpenAI key. The key stays in gh-aw's isolated API
proxy and is excluded from agent shells.

CI proposals may update the policy only when the configured runner covers each
registered project. They cannot introduce a missing toolchain or weaken fallbacks. Docs
proposals must establish actual drift from current code; private refactoring does not
require prose updates. AGENTS.md and imported skills are outside that workflow's edit
scope. Routing is heuristic and incomplete candidate reviews must be reported.

The agent has read-only GitHub permissions. Scope validation and threat detection must
pass before a separate safe-output job creates a draft PR. Failures cannot raise budgets
or bypass these checks. Review actual billing separately from estimated credit limits;
local tests cannot prove provider access or spending.

gh-aw packages committed local history. Only inside these workflows, the scoped snapshot
command may make a local commit on the generated maintenance branch; this exception does
not authorize commits in developer checkouts. Keep the proposal's explicit base branch
and run CI on it before merging: PRs created by `GITHUB_TOKEN` do not automatically
trigger CI. Maintenance never satisfies the required result by itself.

The compiler pin is recorded with its [action lock](../.github/aw/actions-lock.json);
install the matching gh-aw version. After editing executable workflow Markdown,
regenerate and verify:

```bash
gh aw compile ci-policy documentation --no-check-update
make ci-test maintenance-test make-test scripts-typecheck
```

Review restricted secret or action changes before approving compiler warnings.
[scripts/agentic-maintenance.mts](../scripts/agentic-maintenance.mts) and its
[tests](../scripts/agentic-maintenance.test.mts) own context limits, runner coverage,
proposal scope, and snapshot validation. Live triggers, inference, billing, draft
publication, and check reporting need actual GitHub runs.

## Rename references and roll out required checks

Update job dependencies, expressions, reusable paths, scripts selecting checks, ruleset
templates, and affected operational guidance together. Plan the live ruleset update
before merging: changing [platform](../.github/goblin-ruleset.json) or
[website](../website/production/github-ruleset.json) JSON does not change GitHub.

The filename `platform-release.yml` and `master` ref are release signing identity.
Renaming the workflow prevents existing installers from being reused. See
[release authentication](releases.md#verify-and-publish-the-candidate). Azure
installation remains manual; publishing assets never installs Goblin.
