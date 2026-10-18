# Codex model selection

Model and reasoning choices live in **Options** on the selected Work's Start/Retry
panel. Conversation composers save context without model controls. The popover
shows the current model and effort, with Low, Medium, High, and Extra High stops;
unsupported stops are disabled. The model list initially shows the Codex default
and three visible models; **Show more** loads up to ten.

Before an attempt starts, choices are browser-tab drafts in session storage for
that Work. A changed connection invalidates an incompatible draft. Start and Retry
record the requested model and effort on the new attempt in PostgreSQL; later
agent or catalog changes do not rewrite it. Execution details show both the
requested model and the model Codex reported.

The controller uses Codex App Server `model/list`, follows its pagination cursor,
and discards hidden models. The complete visible catalog is stored as one JSONB
value in `connection_model_catalog`, scoped to the connection's authentication
generation and the installed Codex executable stamp. Agent sandboxes have no
catalog database access. A successful fetch atomically replaces the old value.

Refresh is scheduled after a successful account change, when the Codex
executable changes, when the picker opens with a catalog older than 24 hours,
or when the user presses **Refresh models**. The controller coalesces concurrent
refreshes. A failed fetch retains the last successful catalog, marks it stale,
and applies a one-minute cooldown before another eligible automatic refresh.
Manual refresh can retry sooner.
If no catalog exists, Work can still use the Codex default. A changed account
never receives the previous account's catalog.

Replacing the CLI binary while Goblin remains running does not replace its
already running App Server. Goblin keeps the catalog stale in that case; restart
Goblin to launch the updated CLI and complete the refresh.

Catalog discovery is separate from Work execution. A discovery failure does not
create or retry an attempt. If Codex rejects a selected model during execution,
the usual Work failure and attention policy applies; there is no automatic model
fallback.
