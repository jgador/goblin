# Work-centered workspace

The workspace has three regions: browse Work on the left, read and control the
selected Work in the center, and inspect its Activity on the right. The code is in
`frontend/src/work/`: `app.ts` handles navigation and commands, `contracts.ts`
holds the Goblin HTTP views, and `surface.ts` renders saved state read-only.

Opening the workspace selects the remembered Work, or the most recently updated
Work. An empty workspace opens New work. The **New work** action opens
`/?new=work`, so refreshing a new-work form does not select an existing item.
Selected Work uses `/work?item=<id>`.

## Implemented behavior

- The Work views are **All work**, **My work**, **Needs attention**, and
  **Completed**. My work is open Work assigned to any agent. All work includes
  cancelled items. Every view orders by update time. Selection is independent of
  filters.
- Search covers the loaded Work objectives, IDs, agent names, decision questions
  and answers, results, artifact names and references, repository names, history
  text, and linked conversations. Untracked conversations are searched too. When
  navigation is collapsed, results open beneath the search field.
- The center shows identity and status, **Goal**, **Progress**, **Decisions** (when
  any exist), and **Outputs**. Goal is the saved objective. Progress has three rows (Goal captured,
  Work on the outcome, Review and complete), each with its evidence. Runtime
  success never marks Work complete; a result with requested changes does not
  complete the execution row.
- New Work, including tracked conversations, is assigned to the default Goblin
  agent in the creation transaction and stays `Ready`. Starting Work never creates
  an assignment. The agent view marks the default with `isDefault`.
- The start and retry panel shows the agent with a **Uses Codex** label.
  **Options** holds model and reasoning choices; they persist across refresh for
  that Work. A disconnected Codex connection shows **Connect Codex**. Start is not
  offered until the connection is available.
- Execution, repository setup, approval, answer, changes, cancel, retry, and
  reconcile controls are in Progress. They send expected versions and reserved
  string IDs, and support unconfirmed command resubmission. Nothing retries
  automatically. Reading or expanding content sends no commands.
- Decisions show saved questions and answers, with the attempt and timestamps in
  the details. Results, artifacts, and executions share one output list ordered by
  time; the first three are shown. Execution details show the attempt's agent,
  runtime, requested and reported model, reasoning effort, repository grant,
  environment, earlier turns, history, failure category, and pending cleanup.
- The Work composer sends `AddContext`, or `Answer` / `RequestChanges` when the
  current action needs it. Its helper text names the operation it will save.
  **Discuss goal** starts a context draft; it does not change the goal. The
  conversation expands above the composer and collapses back to the Work view.
  The header **Ask Goblin** button opens an unscoped conversation, which saves
  ideas and offers **Track this work**. It does not promise a live agent reply.
- Activity is one chronological list of product events, labeled with the source
  Goblin Web, Goblin, or Agent. Linked Web conversation messages already become
  `ContextAdded` events and are not shown twice. Claims, cleanup completion, and
  runtime progress reports appear only in execution details.
- Below 1200px, Activity becomes a drawer. At 760px and below, navigation also
  becomes a drawer. Both make the rest of the page inert, close with Escape, and
  return focus to their opener. Polling keeps drafts, open disclosures, scroll
  positions, focus, and native select interaction.

## Command recovery and conversations

The browser separates an unconfirmed submission from a saved transition. If a
response is lost, session storage keeps the original command and offers
**Resend command**. Server receipts deduplicate its ID and payload. **Keep saved
state** dismisses the pending submission. Polling, reconnect, and refresh read the
saved state without replaying commands.

Untracked conversations store context and offer **Track this work**; they do not
invent assistant replies or execute. Tracking creates linked Work assigned to the
default agent. Unsent drafts survive navigation and Settings within the page, but
not a browser refresh. Model choices and pending command recovery use session
storage.

## Integrations and Knowledge

The sidebar opens `/integrations` and `/knowledge` without losing the current Work
draft. Integrations is a searchable list with **All** and **Connected** tabs.
GitHub and Slack status come from their APIs. ChatGPT shows only whether it is
configured and never counts as connected. Actions open the matching Settings page.
A failed status read shows **Status unavailable**. Microsoft Teams is labeled
**Planned** and has no action. AI runtime connections stay under Agents and
Settings.

Knowledge shows a disabled search and no sources. There is no knowledge indexing,
sync, or retrieval API, so the page shows no sources, record counts, sync times,
or search results. Work conversations, decisions, and outputs stay with Work.

## Workspace inspection

The Work page has no workspace file view or terminal. The backend
[inspection API](workspace-lifecycle.md#inspection) still exists.

## Backend and schema gaps

These need backend work, not browser state or invented data. Follow the
[architecture guide](architecture.md) and [Work lifecycle](work-lifecycle.md).

- **Goal and milestones:** There is no goal-edit command or evidence-based
  milestone model. Adding one needs core commands, versioning, history, and
  explicit evidence; runtime progress text is not proof of completion.
- **Decisions and activity:** Recorded questions and answers are not general
  proposals or approvals. Richer decisions and cross-channel activity need durable
  identity, source, evidence, deduplication, and access checks. Only Web and Slack
  have conversation associations.
- **Search and artifacts:** Search covers loaded views, not a global index. A
  global search needs permissions and pagination. Artifact content and execution
  logs need authorized, redacted retrieval, safe previews, and retention rules.
- **Steering and metadata:** A conversation saves context, answers, or requests
  changes; it never changes Work or dispatches execution implicitly. Steering
  needs explicit, validated core commands with atomic dispatch intent.
  Parent/follow-up links, due dates, tags, projects, and agent health need their
  own product models, not a generic workflow engine.

## Verification

Run `make test` and `make browser-test`. Browser fixtures cover layout, responsive
drawers, disclosures, search, context isolation, and refresh behavior. They do
not prove persistence. Durable Work browser journeys and real PostgreSQL suites
need `GOBLIN_TEST_POSTGRES_APP` and the database test configuration; report skips.
