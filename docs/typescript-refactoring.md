# TypeScript refactoring review

The main open maintenance problem in the TypeScript code is
`frontend/src/work/app.ts`: loading, command construction, navigation, draft
preservation, markup, and interaction still share module globals. Extract these
responsibilities in working steps, and keep the existing browser technology and
visual design.

Follow the [architecture guide](architecture.md),
[Work lifecycle](work-lifecycle.md), [contract values](contract-values.md), and
[environment variable guide](environment-variables.md). Browser modules observe
persisted Work and submit commands. Work transitions, attention, retries,
reconciliation, and execution ownership stay in the backend.

Keep the existing owners: `api/client.ts` (Goblin JSON requests), `api/read-scope.ts`
(stale-read cancellation), `work/command-submission.ts` (unconfirmed-command
recovery), `api/workspace-contracts.ts` (shared agent, connection, and runtime
views), `work/surface.ts` (progress, decision, output, activity, and conversation
rendering), the
`work/model-*.ts` modules, generated contract values, and timezone data.

## Next implementation sequence

1. Extract Work loading and connection observations into a workspace controller.
   Give it a typed view state and keep partial availability: connection settings
   must stay usable when Work storage fails. Keep command submission independent
   of read cancellation, because accepted Work outlives an HTTP request.
2. Extract navigation and draft state. Capture the submitted context before every
   asynchronous identity reservation, and apply confirmation to that context.
   Keep separate new Work, conversation, and selected Work drafts. Give
   URL/history handling, settings section state, and repository approval draft
   invalidation explicit owners.
3. Extract repository setup and Work command construction. Replace internal
   `Record<string, unknown>` command builders with public payload types and typed
   factories. Keep parsing at form and storage boundaries. Repository approval
   sends the saved authorization ID; frontend code must not grant Git authority.
4. Move sidebar, home, and selected Work rendering out of `app.ts` and render them
   from typed inputs. Keep focus, cursor, scroll, details, native select, and
   slider preservation in a small DOM controller.

Add behavioral tests for stale reads, disposal, recovery, and user interaction as
each boundary moves.

## Other maintenance candidates

Add typed payload builders and validate external data at boundaries as consumers
need them; do not edit generated files by hand. Add new browser modules to the
static asset routes; `tests/deployment/browser-assets.test.ts` checks that every
module dependency has a route. Replace the fragile HTML substring markers in
`frontend/scripts/copy-assets.mts` with a shared template, or fail when markers
are missing, and check that both pages receive their markup.

Outside the browser, add typed validation for the external JSON that
`scripts/release-github.mts` reads (it uses `any` today), while keeping immutable
release assets, approvals, and manual Azure installation. Share process and
browser fixture setup only where the lifecycle owner is the same; register cleanup
before waiting for startup and use bounded waits. Keep malformed-wire fixtures and
boundary-specific security, authorization, recovery, and failure assertions
visible in tests.

## Verification and limits

This is an open plan, not a record of passing checks. For each extraction, run
the affected frontend tests, typechecking, the static asset dependency check, and
the relevant browser journeys. Preserve stale-read rejection, exact command
identities and recovery, focus, drafts, and authorization. See
[test selection](repository-layout.md#choosing-verification).

PostgreSQL-backed application and Work journeys are opt-in and show durability
only when run against the configured disposable database. Browser mocks, local
logging fixtures, and schema checks do not show live GitHub publication,
authenticated model execution, or an Azure deployment.
