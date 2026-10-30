# Working with coding agents

This guide applies OpenAI's [Rethinking skills and prompts for GPT-6 Astra](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra)
and [GPT-6 Astra prompting guidance](https://developers.openai.com/api/docs/guides/latest-model/gpt-6-astra.md#prompting-best-practices)
to contributing to Goblin: relevant context, clear outcomes, and precise boundaries.
These are contributor instructions; model selection for
Goblin's own execution attempts is documented in [the model catalog](codex-integration.md#model-selection-and-discovery).

## Give each instruction a home

[AGENTS.md](../AGENTS.md) holds rules that apply across the repository and links
to task-specific guidance. Architecture, language conventions, commands, and
integration limits belong in their owning documents. Link specialized guidance to
the tasks that need it, following dependencies when needed.

Label established constraints, implemented behavior, and deferred proposals in
plans and audits. Historical checkpoints do not establish today's test results.
Use the [documentation index](README.md) to find current owners. Consolidate a
completed plan into its lasting guide instead of leaving it as a second source
of behavior; repair incoming links when removing or renaming documents.

## Define the result

When useful, specify the intended outcome, relevant context, constraints, and
evidence of completion; no fixed prompt form is required. Match verification to the work: a documentation edit may need a
link and diff review, while implementation needs behavior checks. Exploration
should have a clear stopping point. See [test selection](repository-layout.md#choosing-verification).

## Continue within the authorized scope

The [working agreement](../AGENTS.md#working-agreement) authorizes routine local
implementation and verification. Use authorization already given in the conversation.
When an external or destructive step still needs permission, prepare the reviewable
changes and complete independent checks first. Explain the exact remaining action
and why authorization is missing.

The repository's confidentiality, credential, storage, and manual Azure installation
rules remain binding. Permission to rerun local tests does not change the product's
explicit retry and reconciliation rules for failed Work. Separate the contributor's
development loop from the Work lifecycle being implemented.

When explaining a blocker, distinguish a stated requirement from an interpretation;
general caution should not become an invented approval gate.

## Keep skills narrowly scoped

The repository's skills live under `.agents/skills/`. Their descriptions select a
workflow; keep them concise and specific about when that workflow is useful. Avoid
keyword triggers that load a whole workflow for a tangential mention. A skill with
several independent workflows should link to the relevant details as needed.

The design and animation skills identified in `skills-lock.json` come from
[Emil Kowalski's skills](https://github.com/emilkowalski/skills). Their imported
instructions, recipes, and reference files retain the upstream MIT license,
including `Copyright (c) 2026 Emil Kowalski`. Keep the full
[license](../.agents/skills/licenses/emilkowalski-skills/LICENSE) with any copies
or substantial portions. See [third-party notices](../THIRD_PARTY_NOTICES.md)
for the checked revision and license scope.

Respect the existing selection boundaries:

| Skill | Intended use |
| --- | --- |
| `brainstorm` | Exploratory product/design discussion; a clear implementation request can proceed directly. |
| `check-secrets` | Explicit secrets review of pending changes or the requested outgoing scope. |
| `commit-push` | Explicit invocation to commit and push with the skill's stated scope. |
| `clean-slate` | Explicit invocation to remove the verified local test installation and data. |

Read a selected skill for its full contract. Discussing or editing `commit-push` or
`clean-slate` does not activate publication or deletion. Keep checks that establish
ownership or protect data even when simplifying the surrounding prose.

## Maintain instructions with evidence

When changing instructions, keep a rule where it changes a real decision: a product
invariant, a repository convention, a workflow boundary, or a verification obligation.
Move specialized detail to its owner and link it from the applicable task. Prefer
fixing contradictory or outdated guidance over appending another warning. Keep the
root instructions usable by other coding models; evaluate model-specific advice on
the models and tasks that actually use it.

Review instructions against representative tasks: a README typo, a Work
lifecycle fix, a database change without live PostgreSQL, an edit to a cleanup or
publication skill, and an implementation with reversible checks. Check completion,
unnecessary pauses, verification scope, and boundaries. These are review scenarios,
not a mandatory benchmark suite; wording alone does not prove behavior improved.

Close a task with the result, checks actually performed, and any material gaps or
remaining decisions. Keep temporary evaluation outputs under `.artifacts/`.
