---
name: tldr
description: Give a concise answer to a complex question, a short recap, or a high-level plan. Invoke explicitly with `$tldr`.
---

# TL;DR

Lead with what the user needs to know. Keep the reasoning, constraints, and
uncertainty that could change their decision. For questions about Goblin,
check the relevant code, tests, or [guides](../../../docs/README.md).

- **Question.** Answer it directly, then give the main reason and any
  qualification that affects the answer.
- **Recap.** Give the conclusion or current state and any open decision that
  matters. If invoked on its own, recap the current discussion.
- **Plan.** State the intended outcome and outline the next steps in dependency
  order. Identify the first milestone the user can verify and mention manual
  checks that affect sequencing. On revision, account for completed steps and
  the user's latest constraints.

Use more detail when the user asks for it. Keep plan-only requests read-only.
When the user also requests implementation, complete that work and use this
format for the report.
