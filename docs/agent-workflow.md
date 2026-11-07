# Working with coding agents

These are rules for contributing to Goblin with coding agents. Repository-wide
authorization and safety requirements are in [AGENTS.md](../AGENTS.md).

## Give each instruction a home

[AGENTS.md](../AGENTS.md) holds rules that apply across the repository and links to task
guidance. Put architecture, language conventions, commands, and integration limits in
their owning documents; use the [documentation index](README.md) to find them.

## Documentation maintenance

Update the owning guide in the same PR when supported usage, an architectural decision
or product invariant, security or data ownership, installation or recovery, a
non-obvious contributor workflow, or compatibility changes. Document intent and stable
contracts; link to tooling, schemas, manifests, and tests for definitions. Private
refactoring, internal renames, routine dependency updates, and generated configuration
normally need no prose change.

Use the existing document for its audience before adding another. Keep the root README
as a quickstart and navigation entry. Put unfinished plans, investigations, and progress
in [GitHub Issues](https://github.com/jgador/goblin/issues); requested local session
records belong under `.artifacts/`. When a decision is implemented, retain its rationale
in the owning guide and close the tracking issue. Check relative links and anchors and
validate affected commands where practical.

## Continue within the authorized scope

The [working agreement](../AGENTS.md#working-agreement) authorizes routine local
implementation and verification; match verification to the change using
[test selection](repository-layout.md#choosing-verification). When an external or
destructive step still needs permission, finish the reviewable changes and independent
checks first, then name the exact remaining action and the missing authorization. Do not
turn general caution into an invented approval gate.

Rerunning local tests is not a retry of failed Work. The product's explicit retry and
reconciliation rules still apply to the Work lifecycle being implemented.

For fast WSL iteration and checks that need a running cluster, follow
[verification selection](repository-layout.md#choosing-verification) and the
[owned k3s deployment workflow](../deploy/k3s/README.md#deploy-application-edits). The
short root instruction selects the workflow; the guides own commands, deployment limits,
and verification examples.

## Keep skills narrowly scoped

Repository skills live under `.agents/skills/`. A skill's description decides when it
loads: keep it short and specific, and avoid keyword triggers that load a whole workflow
for a passing mention.

The skills listed in `skills-lock.json` are imported from
[Emil Kowalski's skills](https://github.com/emilkowalski/skills) under the MIT license,
`Copyright (c) 2026 Emil Kowalski`. Keep the full
[license](../.agents/skills/licenses/emilkowalski-skills/LICENSE) with any copies or
substantial portions. [Third-party notices](../THIRD_PARTY_NOTICES.md) record the
revision and license scope.

Read each [skill](../.agents/skills/) for its trigger and full contract. Discussing or
editing `commit-push` or `clean-slate` does not start publication or deletion. When
simplifying a skill, keep the checks that confirm ownership or protect data.

## Maintain instructions with evidence

Keep a rule only where it changes a real decision: a product invariant, a repository
convention, a workflow boundary, or a verification obligation. Fix contradictory or
outdated guidance instead of adding another warning. Keep root instructions usable by
other coding models.

Review instruction changes against representative tasks: a README typo, a Work lifecycle
fix, a database change without live PostgreSQL, an edit to a cleanup or publication
skill, and an implementation with reversible checks. Check completion, unnecessary
pauses, verification scope, and boundaries. Keep evaluation outputs under `.artifacts/`.
