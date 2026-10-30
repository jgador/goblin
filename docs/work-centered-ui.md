# Work-centered workspace

The workspace uses three regions: browse Work on the left, understand and control
the selected Work in the center, and inspect its Activity on the right. It uses the existing frontend,
typography, colors, controls, icons, and Settings surfaces.

Opening the workspace selects the remembered Work, or the most recently updated
Work. An empty workspace opens New work. The New work action explicitly opens
`/?new=work`, so refreshing a new-work form does not select an existing item.
Selection continues to use `/work?item=<id>`.

## Implemented behavior

- Navigation has All Work, Assigned to Goblin, Needs Attention, Recently Updated,
  and Completed. Assignment includes open Work assigned to any Goblin agent.
  All Work includes cancelled items. Recently Updated orders by update time;
  other views order by creation time. Selection is independent of filters.
- The global search searches currently loaded Work objectives, decision questions
  and answers, results, artifact names and references, repository names, assigned
  agent names, history text, and saved conversations. It finds parent Work for
  matching content. When navigation is collapsed, results open beneath search.
- The center presents identity/status, Goal, Progress, Decisions, and compact
  outputs. Goal uses the persisted objective. Progress is explicitly labeled
  **Work lifecycle**, with inspectable evidence for goal capture, execution, and
  result approval. Runtime success alone never marks Work complete. Older results
  and results with requested changes do not complete the current execution step.
- New Work, including tracked conversations, is assigned to the default Goblin
  coworker in the creation transaction and remains Ready. The UI and execution
  use that saved assignment; starting Work never creates an assignment. The
  agent projection identifies the default through `isDefault` for the new Work
  composer's connection status. Development data from before automatic assignment
  is not supported and can be reset.
- The start/retry panel shows Goblin with a quieter **Uses Codex** label. **Options**
  holds model and reasoning choices beside the action they affect; conversation
  composers save context without execution controls. Choices persist across refresh
  for that Work. Disconnected Codex opens connection setup, and unavailable or
  changing connections keep starting disabled until ready.
- Existing execution, repository setup, approval, answer, changes,
  cancel, retry, and reconciliation commands remain available in Progress.
  They retain expected versions, reserved string identities, and unconfirmed
  command resubmission. No retry is automatic. Reading or expanding content
  issues no commands.
- Decisions display persisted questions and answers, with the attempt and recorded
  timestamps in disclosure details. Results, artifacts, and attempts share one
  output list ordered by recorded time. The first three are shown initially.
  Execution details retain the historical agent, runtime, model, repository grant,
  environment, lifecycle events, failure category, and cleanup state.
- The Work composer sends `AddContext`, or `Answer` / `RequestChanges` when the
  current action requires it. Its helper text describes which operation will be
  saved. **Discuss goal** prepares a context draft; it does not mutate the goal.
  Conversation expands above the composer and collapses back to the Work view.
  Global **Ask Goblin** opens the existing unscoped conversation flow, which saves
  ideas and offers Track this work. It does not promise a live agent reply.
- Activity is one chronological stream of meaningful Work events. Sources reflect
  the current Web / Goblin / agent integration. Linked Web conversation messages
  already become `ContextAdded` events, so they are not appended a second time.
  Claims, cleanup bookkeeping, and runtime progress reports remain in execution
  inspection instead of cluttering Activity or becoming authoritative milestones.
- Below 1200px, Activity becomes a drawer. At 760px and below, navigation also
  becomes a drawer. Both isolate keyboard focus, support Escape, and restore focus
  to their opener. The center and scoped composer remain usable. Polling retains
  drafts, open disclosures, scroll positions, focus, and native select interaction.

Rendering is split between `frontend/src/work/app.ts` (navigation and command
coordination), `contracts.ts` (Goblin HTTP views), and `surface.ts` (read-only
presentation of durable state). The Web host explicitly serves the new surface
module through its existing static asset allowlist. The Work and conversation
stores persist default coworker assignment through the existing core command.
Runtime adapters and persistence own execution independently of this rendering layer.

## Command recovery and conversations

The browser distinguishes an unconfirmed submission from a saved transition. If
a response is lost, session storage retains the original command and offers
**Resend command**. Server receipts deduplicate its ID and payload. **Keep saved
state** dismisses the pending submission. Polling, reconnect, and refresh retrieve
authoritative state without replaying commands automatically.

Untracked conversations store context and offer **Track this work**; they neither
invent assistant replies nor execute. Tracking creates linked Work with the default
agent assignment. Ordinary unsent drafts survive navigation and Settings within
the page, but not a browser refresh. Model choices and pending command recovery
have their own session-storage persistence.

## Integrations and Knowledge

The sidebar opens dedicated `/integrations` and `/knowledge` pages without losing
the current Work draft. Integrations is an ungrouped, searchable directory with
All and Connected tabs. GitHub and Slack status comes from their existing APIs;
connection actions reuse Settings authentication, repository selection, and
explicit Slack identity grants. Failed status reads are shown as unavailable,
and Teams is labeled planned with no connection action. AI runtime connections
remain under Agents and Settings.

Knowledge currently shows an unavailable search and empty sources. There is no
public knowledge indexing, synchronization, or retrieval endpoint, so the page
does not invent sources, record counts, sync times, or attributed search results.
Existing repository setup memory remains behind its account/repository-scoped
execution grant; Work conversations, decisions, and outputs stay with Work.

## Workspace inspection and continuation

The Work page does not currently expose workspace file inspection or a terminal.
The Open workspace panel has been removed pending a new design. Backend
[workspace lifecycle](workspace-lifecycle.md) support for checkpoints, continuation,
and inspection sessions remains available.

## Backend and schema gaps

These are follow-up requirements, not local browser state or fabricated data.
Follow [the architecture guide](architecture.md) and
[Work lifecycle contracts](work-lifecycle.md) when implementing them.

- **Goal and milestones:** The objective supplies the title and Goal; there is no
  goal-edit command or evidence-backed milestone model. Future revisions need
  core commands, versioning, history, and explicit evidence; runtime progress text
  is not proof of completion.
- **Decisions and activity:** Recorded questions and answers are not general
  proposals or approvals. Future rich decisions and cross-channel activity need
  durable identity, provenance, evidence, deduplication, and access checks. Web
  and Slack have existing associations; other adapters remain future work.
- **Discovery and artifacts:** Search covers loaded views, not a global index.
  Future search needs permissions and pagination. Artifact content and execution
  logs need authorized, redacted retrieval, safe previews, and retention rules.
- **Steering and metadata:** Conversation saves context, answers, or requests
  changes; it does not implicitly change Work or dispatch execution. Any future
  steering needs explicit validated core commands and atomic dispatch intent.
  Parent/follow-up relations, due dates, tags, project hierarchy, and standalone
  agent health need product-justified models rather than a generic workflow engine.

Until the first production deployment, consolidate future schema changes in
`backend/database/migrations/0001_initial.sql`, regenerate EF mappings, and keep
custom behavior outside generated files. PostgreSQL continues to own history;
Wolverine coordinates dispatch. Attempts, agent identities, uncertain execution,
cleanup, and explicit retries retain their existing recovery contracts.

## Verification

Run `make test` and `make browser-test`. Browser fixtures cover layout, responsive
drawers, progressive disclosure, search, context isolation, and refresh behavior.
They do not establish persistence coverage. The durable Work browser journeys and
real PostgreSQL suites remain opt-in with `GOBLIN_TEST_POSTGRES_APP` and the
repository's database-test configuration; report skips explicitly.
