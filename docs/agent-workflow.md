# Working with coding agents

This guide applies OpenAI's [Rethinking skills and prompts for GPT-6 Astra](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra)
and [GPT-6 Astra prompting guidance](https://developers.openai.com/api/docs/guides/latest-model/gpt-6-astra.md#prompting-best-practices)
to contributing to Goblin: relevant context, clear outcomes, and precise boundaries.
These are contributor instructions; model selection for
Goblin's own execution attempts is documented in [the model catalog](model-catalog.md).

## Give each instruction a home

[AGENTS.md](../AGENTS.md) holds rules that apply across the repository and links
to task-specific guidance. Architecture, language conventions, commands, and
integration limits belong in their owning documents. Link specialized guidance to
the tasks that need it, following dependencies when needed.

Label established constraints, implemented behavior, and deferred proposals in
plans and audits. Historical checkpoints do not establish today's test results.

## Define the result

A useful request identifies the outcome, relevant context, constraints, and what
would demonstrate completion. Provide those details when they matter; this is an
optional prompt shape, not a form that must be filled out before work can start:

```text
Goal: <observable behavior or artifact to change>
Context: <reproduction, files, or relevant documentation>
Constraints: <compatibility, scope, and external-action limits>
Done when: <result and evidence needed to review it>
```

For example:

```text
Fix the Work sidebar losing the selected item after a browser refresh.
Preserve the current design and HTTP contracts. Done when selection survives
refresh and navigation, the affected browser journey passes, and the interaction
has been inspected. Fix regressions caused by the change and report any check
that could not run.
```

For a documentation task, completion can be much smaller: correct the explanation,
check links and command references, and review the final diff. For exploration,
state the stopping point, such as comparing two options and recommending one.
For implementation, include the behavior and evidence needed to review the result.
See [test selection](repository-layout.md#choosing-verification) for verification scope.

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

Review the proposed instructions against representative situations:

- A README typo reaches a checked diff without unrelated setup or runtime tests.
- A Work lifecycle fix runs the core tests and preserves explicit failure recovery.
- A database change reports missing real PostgreSQL coverage rather than treating
  skipped tests as proof of durability.
- A request to edit a cleanup or publication skill changes its documentation
  without performing its destructive or external actions.
- An implementation request continues through relevant verification without a new
  approval request at each reversible step.

These are review scenarios, not a required benchmark suite for every edit. Distinguish
reviewing the wording from observing actual agent behavior. When evaluating a prompt
change, compare completion, unnecessary pauses, verification scope, and boundary
adherence on representative tasks; do not claim improvement from wording alone.

Close a task with the result, checks actually performed, and any material gaps or
remaining decisions. Keep temporary evaluation outputs under `.artifacts/`.
