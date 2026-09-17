---
name: brainstorm
description: "Brainstorm large features or ideas from a high-level proposal, then explore the user's reactions, doubts, strong opinions, and curiosity through connected scenarios and edge cases. Use for exploratory product or design discussions, especially when questionnaires are tiring. Do not insert a brainstorming phase into a clear implementation request."
---

# Brainstorm

Help the user discover what matters by giving them something small and concrete to react to. Carry the work of developing possibilities, tracing consequences, and remembering connections. The user should not have to complete an interview, defend an intuition, or manage the map.

## Start with a sketch

Use the context already available to describe the intended outcome and a plausible shape for the idea. If the idea itself is missing, ask for a rough sentence about it; otherwise begin with a provisional sketch.

Usually offer three to five short, named aspects: for example, the main experience, who controls it, and what happens when it fails. Each should describe an actual proposed behavior and, where useful, its main tradeoff. Choose aspects that matter for this idea; do not reuse a requirements checklist. Keep the first sketch to roughly one screen and defer implementation detail.

Make assumptions visibly provisional. Where a major assumption could anchor the whole discussion, mention a meaningfully different direction briefly. Avoid presenting a polished specification or marking an option as the recommended answer before learning what the user values.

Invite free reactions: "Pick up on anything that catches your attention. A phrase, a disagreement, curiosity, or 'something feels off' is enough." The user can quote a label, tell a story, change the framing, or react to several things at once. There is nothing they must answer in order or copy back.

## Follow attention without inventing a verdict

A selected topic is evidence of attention. Its meaning may be ambiguous or mixed. Use the user's words and distinguish their expressed position from your hypothesis about it.

| User signal | Useful response |
| --- | --- |
| Explicit objection or strong preference | Capture the preference at its stated scope. Explore a design that respects it and make consequential tradeoffs visible. |
| Uncertainty or request for clarification | Explain with a concrete example before asking the user to choose. |
| A gut feeling or "something is off" | Acknowledge the unresolved concern. Offer a small number of plausible interpretations as hypotheses and show situations where they differ. |
| Curiosity | Explore how it works and what follows. Keep curiosity separate from endorsement or a feature request. |
| A small detail or "maybe I'm nit-picking" | Check whether it points to a broader issue such as trust, effort, control, or consistency. It may also remain a local preference; do not dismiss it or inflate it into a principle. |

Do not require the user to classify their reaction or explain why it feels wrong. Infer enough to advance the discussion while keeping uncertain interpretations correctable. For example: "This may be about who gets control, or about how hard a mistake is to undo. Here is a case where those lead to different designs."

Strength of feeling determines where to spend attention; it does not establish a factual claim. Preserve explicit constraints, check factual assumptions when necessary, and explain tensions candidly. Apply inferred values locally until there is evidence for a broader preference.

## Explore through consequences

Focus on the part that drew attention. A useful response reflects the concern, explores one or two concrete situations, and explains how they change the proposal. Adapt that shape rather than repeating a fixed template.

Begin with an ordinary situation. Add a boundary case only when it reveals a meaningful distinction: a different actor, a failure, a reversal, a conflict, or a change in scale. State the consequence that makes it relevant. Add alternatives when they help expose a tradeoff, not merely to produce more choices.

Prefer something the user can react to over a question they must solve. Ask a focused question only when its answer would materially change the next useful exploration and a provisional assumption or example cannot do the job. Questions should be occasional; avoid ending every response with one or using serial or batched questionnaires as the default workflow. Do not make the user review every branch before moving on.

If several reactions arrive together, retain all of them, connect any shared cause, and explore the most consequential connection first. Briefly acknowledge what is parked. If the whole sketch feels wrong, revisit its premise instead of forcing a choice among its parts.

For example, given "Automatic assignment feels off; I can't explain why":

> Assigning someone makes a commitment on their behalf. In a routine case that might save coordination; when that person is already overloaded, it can create work they never accepted. Suggesting an owner and letting them accept preserves more control, at the cost of another step. I'll keep the concern open while we explore that difference.

This develops a hypothesis without recording "the user rejected automation." If they later say speed matters more here, revise the branch accordingly.

## Maintain a connected working map

Keep a compact record of conclusions, evidence, and open possibilities as the discussion grows. Plain Markdown is enough; no graph service or diagram tool is required. Record useful relationships rather than a transcript or private deliberation.

Use stable, short labels when they help navigation. A node may be an outcome, proposal, preference, assumption, scenario, concern, or decision. Keep these distinctions available without displaying a schema every turn:

- **Content and source:** What is being considered, and whether it came from a user statement, an agent proposal or inference, or observed evidence. Preserve a short quote where its nuance matters.
- **Position:** Proposed, open, decided, rejected, parked, or superseded, as supported by the conversation. An explored topic can remain open.
- **Reaction:** What the user expressed, including ambiguity. Keep importance or emotional strength separate from agreement and certainty.
- **Connections:** Label how nodes relate, such as "depends on," "conflicts with," "motivates," "alternative to," or "tested by." Mark speculative links as hypotheses.

For example: `assignment proposal —raised concern→ commitment without consent (hypothesis) —tested by→ overloaded teammate scenario`. A graph can have shared dependencies and cross-links; avoid forcing every concern into a separate question tree.

When a premise or decision changes, follow its connections and mark dependent conclusions for reconsideration. Preserve the reason for a reversal and retire the superseded version. Do not silently retain consequences that depended on the old premise. Consolidate duplicates, park low-value branches, and avoid enumerating every conceivable edge case.

Silence means unexamined. Exploration means explored. Neither establishes agreement. Clear acceptance can settle the stated decision without requiring a formal confirmation ritual. If the user says "go with your recommendation," make the delegated choice and record it as such; do not infer a strong personal preference or approval of unrelated assumptions.

## Keep the conversation light and navigable

Show the active part of the map and the few changes that matter. Avoid repeating the whole sketch, graph, or backlog after every reply. When an upstream choice changes several branches, a brief overview helps the user regain context. Use a compact table or diagram only when the connections are easier to understand visually or the user asks for the map.

Follow the user's interests while also checking consequential dependencies they may not have noticed. At a useful transition, surface the most important overlooked tension with its concrete consequence. Keep the rest parked; an interesting detail should not cause a foundational conflict to disappear.

If the user seems tired or says to move on, reduce the detail and offer a provisional synthesis. Do not interpret fatigue as substantive agreement. Support ordinary requests such as "zoom out," "leave that open," "show the connections," or "summarize where we are" without requiring special commands.

## Converge when useful

When the user asks to synthesize, pause, plan, or proceed, bring together the current direction, explicit preferences and decisions with their reasons, remaining assumptions and concerns, and the scenarios that materially shaped the result. Preserve unresolved contradictions. Suggest a small experiment when observation could resolve a concern better than more discussion.

There is no requirement to exhaust the graph. Use the user's requested stopping point and the value of further exploration. A request to plan or implement should move the work forward within its authorized scope; brainstorming alone does not imply a request to implement.

For long discussions, make a compact checkpoint available so the map can be recovered from the conversation. When saving or resuming a brainstorm is requested, use a Markdown document in the project's existing planning location, or a sensible `docs/brainstorms/<topic>.md` path. Include the active focus, useful nodes and connections, decision sources, and parked concerns. Keep session records separate from this reusable skill. State where a record was actually saved; do not imply durable memory without a saved artifact. On resumption, use that record and the latest user corrections instead of restarting the interview.
