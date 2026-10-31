# Working with coding agents

These are rules for contributing to Goblin with coding agents. Model selection for
Goblin's own execution attempts is in
[the model catalog](codex-integration.md#model-selection-and-discovery).

## Give each instruction a home

[AGENTS.md](../AGENTS.md) holds rules that apply across the repository and links
to task guidance. Put architecture, language conventions, commands, and
integration limits in their owning documents; use the
[documentation index](README.md) to find them. In plans and audits, label
established constraints, implemented behavior, and proposals; old checkpoints do
not prove current test results. When a plan is complete, merge it into its lasting
guide and repair incoming links.

## Continue within the authorized scope

The [working agreement](../AGENTS.md#working-agreement) authorizes routine local
implementation and verification; match verification to the change using
[test selection](repository-layout.md#choosing-verification). When an external or
destructive step still needs permission, finish the reviewable changes and
independent checks first, then name the exact remaining action and the missing
authorization. Do not turn general caution into an invented approval gate.

Rerunning local tests is not a retry of failed Work. The product's explicit retry
and reconciliation rules still apply to the Work lifecycle being implemented.

## Keep skills narrowly scoped

Repository skills live under `.agents/skills/`. A skill's description decides when
it loads: keep it short and specific, and avoid keyword triggers that load a whole
workflow for a passing mention.

The skills listed in `skills-lock.json` are imported from
[Emil Kowalski's skills](https://github.com/emilkowalski/skills) under the MIT
license, `Copyright (c) 2026 Emil Kowalski`. Keep the full
[license](../.agents/skills/licenses/emilkowalski-skills/LICENSE) with any copies
or substantial portions. [Third-party notices](../THIRD_PARTY_NOTICES.md) record
the revision and license scope.

The repository-authored skills have these selection boundaries:

| Skill | Intended use |
| --- | --- |
| `brainstorm` | Exploratory product or design discussion; a clear implementation request proceeds directly. |
| `check-secrets` | Explicit `$check-secrets` review of pending changes for credentials. |
| `commit-push` | Explicit `$commit-push` invocation to commit and push all pending changes. |
| `clean-slate` | Explicit `$clean-slate` invocation to remove this checkout's Goblin installation and test data from WSL. |

Read a selected skill for its full contract. Discussing or editing `commit-push` or
`clean-slate` does not start publication or deletion. When simplifying a skill,
keep the checks that confirm ownership or protect data.

## Maintain instructions with evidence

Keep a rule only where it changes a real decision: a product invariant, a
repository convention, a workflow boundary, or a verification obligation. Fix
contradictory or outdated guidance instead of adding another warning. Keep root
instructions usable by other coding models.

Review instruction changes against representative tasks: a README typo, a Work
lifecycle fix, a database change without live PostgreSQL, an edit to a cleanup or
publication skill, and an implementation with reversible checks. Check completion,
unnecessary pauses, verification scope, and boundaries. Keep evaluation outputs
under `.artifacts/`.
