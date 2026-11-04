# Work-centered workspace

The workspace has three regions: Work navigation on the left, the selected Work
in the center, and its Activity on the right. The code is in `frontend/src/work/`:
`app.ts` handles navigation, rendering, and commands, `contracts.ts` holds the
HTTP views, and `surface.ts` renders saved Work state.

Opening the workspace selects the Work remembered for the browser tab, or the
most recently updated Work. An empty workspace opens New work at `/?new=work`.
Selected Work uses `/work?item=<id>`.

New work centers one goal input and an explicit **Create work** action. Creating
Work saves the goal; execution starts separately from Progress. Connection setup
guidance appears only when the AI connection needs attention. The Work list comes
first in the sidebar, with Knowledge, Settings, and system resources
below it. Agents are available from Settings and the selected Work's composer.

## Implemented behavior

- The **Work view** dropdown offers **All work**, **My work** (open Work with an
  agent), **Needs attention**, and **Completed**, with counts. Lists are ordered
  by update time. Search ignores the selected view.
- Search covers loaded Work objectives, IDs, agent names, decisions, results,
  artifact names and references, repository names, history text, and linked and
  untracked conversations. It is not a server-side index.
- The center shows **Goal**, **Progress**, **Decisions** (when any exist), and
  **Outputs**. Progress has three rows: Goal captured, Work on the outcome, and
  Review and complete. Runtime success never marks Work complete; a result with
  requested changes does not complete the second row.
- New Work, including tracked conversations, is assigned to the default agent
  and stays `Ready`.
- The start panel shows the agent and **Uses Codex**. **Options** holds model and
  reasoning choices, kept in session storage per Work. Without an available Codex
  connection it shows **Connect Codex** or a status notice instead of Start.
- Start, repository setup, approve, answer, request changes, cancel, retry, and
  reconcile controls are in Progress. They send expected versions and reserved
  IDs. Nothing retries automatically, and reading or expanding content sends no
  commands.
- Outputs is one time-ordered list of results, artifacts, and executions; the
  first three are shown, with **View all** for the rest. Execution details show
  the agent, runtime, requested and reported model, reasoning effort, repository
  and branch, environment, earlier turns, history, failure, and pending cleanup.
- The composer sends `AddContext`, or `Answer` / `RequestChanges` when the current
  state needs it; its note names the action. **Discuss goal** starts a context
  draft and does not change the goal. The header **Ask Goblin** button opens an
  unscoped conversation with **Track this work**. It gives no agent reply.
- Activity is one time-ordered list of product events labeled Goblin Web, Goblin,
  or Agent. Linked conversation messages appear once, as `ContextAdded`. Claims,
  cleanup completion, and progress reports appear only in execution details.
- Below 1200px Activity becomes a drawer; at 760px and below navigation does too.
  Drawers make the rest of the page inert, close with Escape, and return focus.
- The page refreshes every 3 seconds while visible, and when it becomes visible
  or goes online. A refresh keeps drafts, open disclosures, scroll positions, and
  focus. It does not re-render while a native select has focus.
- A brief startup session request failure keeps the opening screen visible.
  Repeated failures, or a lost connection after opening, show a reconnecting
  status that clears when the session request succeeds, including at the password
  screen. Connection status is separate from command errors and recovery notices.

## Command recovery and conversations

If a command response is lost, session storage keeps the original command and the
page offers **Resend command** or **Keep saved state**. Server receipts deduplicate
the resent command. Refresh reads saved state and never replays commands.

Untracked conversations store context and offer **Track this work**; they do not
execute. Unsent drafts survive navigation within the page, not a browser reload.

## Connections and Knowledge

**Settings → Connections** (`/settings?section=connections`) is the shared home
for Codex, ChatGPT, GitHub, Slack, and Microsoft Teams. One list shows their
purpose, current status, and a setup or management link. Provider details use
`/settings?section=connections&provider=<name>`, keep Connections selected in
Settings navigation, and return through **Back to connections**. Workspace and
Knowledge shortcuts open these same settings. There is no separate Integrations
page or provider section in the Settings navigation.

Codex runs Work using the owner's ChatGPT account or API key. The ChatGPT plugin
reads explicitly shared Work; its configuration status does not attest to an
installed or authenticated connection. Codex, GitHub, and Slack status come from
their APIs. A failed read shows **Status unavailable**. Microsoft Teams is
**Planned** and has no action.

`/knowledge` shows a disabled search and no sources. There is no knowledge
indexing or retrieval API.

## Workspace inspection

The Work page has no workspace file view or terminal. The backend
[inspection API](workspace-lifecycle.md#inspection) exists.

## Backend gaps

These need backend work, not browser state. See [Work lifecycle](work-lifecycle.md).

- No command edits the goal, and there is no milestone model.
- Decisions are recorded questions and answers, not general approvals. Only Web
  and Slack have conversation links.
- There is no global search, artifact content retrieval, or execution log view.
- Conversations never change Work or start execution implicitly. Parent links,
  due dates, tags, projects, and agent health have no product model.

## Verification

Run `make test` and `make browser-test`. Browser fixtures do not prove
persistence. Durable Work browser tests and PostgreSQL suites need
`GOBLIN_TEST_POSTGRES_APP`; report skips.
