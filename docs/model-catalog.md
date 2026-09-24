# Codex model selection

The composer on New Work, saved conversations, and Work has a compact model
button. Its popover shows the current model and reasoning effort, with a slider
for Low, Medium, High, and Extra High. Unsupported stops are disabled for the
chosen model. The model row opens a list with the Codex default plus three
visible models; **Show more** loads up to ten. A choice made before
Work exists follows the new or tracked Work through agent assignment.
Conversation messages save context; they do not run a model. If the assigned
agent uses another connection, the picker resets and asks for a new choice.
Before an attempt starts, choices are browser-tab drafts in session storage.
Start and Retry record the requested model and effort on the new attempt in
PostgreSQL; later agent or catalog changes do not rewrite that attempt. The
execution view shows both the requested model and the model Codex reported.

The controller uses Codex App Server `model/list`, follows its pagination cursor,
and discards hidden models. The complete visible catalog is stored as one JSONB
value in `connection_model_catalogs`, scoped to the connection's authentication
generation and the installed Codex executable stamp. Agent sandboxes have no
catalog database access. A successful fetch atomically replaces the old value.

Refresh is scheduled after a successful account change, when the Codex
executable changes, when the picker opens with a catalog older than 24 hours,
or when the user presses **Refresh models**. The controller coalesces concurrent
refreshes. A failed fetch retains the last successful catalog, marks it stale,
and waits one minute before an automatic retry. Manual refresh can retry sooner.
If no catalog exists, Work can still use the Codex default. A changed account
never receives the previous account's catalog.

Replacing the CLI binary while Goblin remains running does not replace its
already running App Server. Goblin keeps the catalog stale in that case; restart
Goblin to launch the updated CLI and complete the refresh.

Catalog discovery is separate from Work execution. A discovery failure does not
create or retry an attempt. If Codex rejects a selected model during execution,
the usual Work failure and attention policy applies; there is no automatic model
fallback.
