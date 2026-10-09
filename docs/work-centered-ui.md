# Work-centered workspace

The workspace uses three regions: browse Work on the left, understand and control
the selected Work in the center, and inspect its Activity on the right. This is a
layout and information architecture change using the existing frontend,
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
Runtime adapters and the database schema remain unchanged.

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
No database, execution, or external permission contracts change.

## Workspace inspection and continuation

The Work page does not currently expose workspace file inspection or a terminal.
The Open workspace panel has been removed pending a new design. Backend
[workspace lifecycle](workspace-lifecycle.md) support for checkpoints, continuation,
and inspection sessions remains available.

## Backend and schema gaps

These are follow-up requirements, not local browser state or fabricated data.
Follow [the architecture plan](architecture-refactoring-plan.md) and
[Work lifecycle contracts](work-lifecycle.md) when implementing them.

| Capability | Current boundary | Follow-up |
| --- | --- | --- |
| Separate title, normalized goal, and short context | `Objective` supplies both title and Goal; no goal-edit command exists. | Add core-owned goal revision commands, revision history, expected-version checks, and approval rules where needed. Store a distinct title/context only if they have clear semantics. Context text must not silently overwrite the objective. |
| Meaningful, evidence-backed milestones | The UI can prove lifecycle state, but has no milestone records or evidence associations. Runtime `ProgressReported` text is a report, not proof. | Model milestone identity, state, evidence references, source, and validation. Define how confirmed PRs, tests, deployments, and telemetry update milestones. Retain the distinction between proposed plans and verified completion. |
| Rich decisions and provenance | Decisions are runtime questions and recorded answers. They do not identify a proposer, approver, policy, or arbitrary rationale. | Add core commands for proposals, decisions, and approvals; persist actor/source identity, rationale, evidence, and supersession. Do not infer named human approval from conversational wording. |
| Multiple conversation sources per Work | Web conversations can attach to Work. Slack now stores approved actors, thread associations, deduplicated message/event references, and source provenance. | General cross-channel attachment and other external adapters remain future work. Preserve one Work across sources and accurate provenance before labeling other integrations. |
| Unified external Activity | Activity currently projects Work history. There is no external event ingestion contract, visibility policy, or pagination. | Add a Goblin-owned activity projection with durable sequence, source, actor, original reference, Work/attempt/evidence correlation, deduplication, and access checks. Keep low-level runtime events in execution details. |
| Global discovery | Search is an in-browser search over loaded views; it does not search an independent file/project/people index. | Add permission-aware, paginated cross-entity search and contextual deep links. Do not download all histories as the dataset grows. |
| Artifact previews and execution logs | Results render as text; GitHub references can be opened. Other references are inspectable but have no content endpoint. | Add authenticated artifact retrieval, safe content types/previews, authorization, retention, and redacted execution-log pagination. Never expose credentials or raw upstream errors. |
| Natural-language steering | The composer saves context, answers a pending question, or requests changes. Arbitrary chat does not edit status, stop execution, or dispatch work. | Interpret proposed changes into explicit core commands with validation and any required approval. Commit state and Wolverine dispatch intent together; do not turn prose into an authoritative transition. |
| Follow-up Work and optional metadata | No parent/follow-up relationship, due date, tags, project hierarchy, or standalone agent health aggregation is modeled here. | Introduce only product-justified fields and relationships with core commands and persisted projections; avoid a generic workflow engine. |

Until first deployment, consolidate future schema changes in
`backend/database/migrations/0001_initial.sql`, regenerate EF mappings, and keep
custom behavior outside generated files. PostgreSQL continues to own history;
Wolverine coordinates dispatch. Attempts, agent identities, uncertain execution,
cleanup, and explicit retries retain their existing recovery contracts.

## Verification

Run `make test` and `make test-browser`. Browser fixtures cover layout, responsive
drawers, progressive disclosure, search, context isolation, and refresh behavior.
They do not establish persistence coverage. The durable Work browser journeys and
real PostgreSQL suites remain opt-in with `GOBLIN_TEST_POSTGRES_APP` and the
repository's database-test configuration; report skips explicitly.

Validation for this increment (2026-09-23): `make test` passed (164 .NET tests,
87 HTTP/deployment tests, and protocol checks). The full browser run passed 33
tests; the final targeted workspace run passed nine, including the additional
320px layout check. Real PostgreSQL coverage was not run: 24 .NET tests, one HTTP
test, and six durable Work browser journeys were skipped. The pinned Codex
initialization/storage/process-replacement/logout check also passed; it uses an
isolated synthetic credential and does not establish authenticated model execution
coverage.
