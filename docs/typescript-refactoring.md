# TypeScript refactoring review

This review covers the repository's TypeScript browser code, Node scripts,
configuration, fixtures, and tests reviewed against the current owners. The main remaining
maintenance problem is ownership in `frontend/src/work/app.ts`: loading, command
construction, navigation, draft preservation, markup, and interaction still share
module globals. Extract these responsibilities in working increments while
preserving the existing browser technology and visual design.

Follow the [architecture guide](architecture.md),
[Work lifecycle](work-lifecycle.md), [contract values](contract-values.md), and
[environment variable guide](environment-variables.md). Browser modules observe
persisted Work and submit commands. Work transitions, attention, retries,
reconciliation, and execution ownership remain backend responsibilities.

## Completed foundations

The preceding refactor extracted model selection, catalog loading, and picker
markup. The current code includes the following boundaries:

- `api/client.ts` handles Goblin JSON requests, same-origin credentials, read
  timeouts, public error envelopes, and safe fallbacks for non-JSON failures.
  Callers decide how an unauthorized response clears their view. It performs no
  retries. HTML assets and the external IP lookup keep their own adapters.
- `api/read-scope.ts` invalidates old workspace reads on reset. Work refreshes,
  connection refreshes, and settings reads cannot apply obsolete responses after
  locking. Timezone preferences also reject completions from before a reset.
- `work/command-submission.ts` owns submission exclusivity and the browser's
  unconfirmed-command record. It saves the payload before dispatch, restores it
  without dispatching, preserves decimal identity strings, and resends only on
  explicit request. Backend storage and command deduplication remain authoritative.
  Corrupt saved commands produce a recovery notice instead of crashing startup.
- `api/workspace-contracts.ts` gives the Work and settings screens one definition
  of agent, connection, and runtime capability views.

Codex settings can cancel their controller during an unfinished mount. GitHub
settings cancel listeners and requests on disposal and prevent overlapping status
polls. System readings and timezone requests use the shared JSON adapter.
The direct-script compiler explicitly includes the environment catalog. The logs
browser checker writes its runtime files and screenshots under `.artifacts/logs-ui`.

The backend's static asset allowlist includes the new modules and the prior model
picker extraction. An offline test checks dependencies of all publicly routed
browser modules against that allowlist. Browser API mocks distinguish script
requests from JSON requests so newly extracted modules load normally.

## Next implementation sequence

1. Extract Work loading and connection observations into a workspace controller.
   Give it a typed view state and retain partial availability: connection settings
   must remain usable when Work storage fails. Keep command submission independent
   of read cancellation, since accepted Work outlives an HTTP request.
2. Extract navigation and draft state. Capture the submitted context before every
   asynchronous identity reservation, and apply confirmation to that context.
   Preserve separate new Work, conversation, and selected Work drafts. Give
   URL/history handling and repository approval draft invalidation explicit owners.
3. Extract repository setup and Work command construction. Replace internal
   `Record<string, unknown>` command builders with public payload types and typed
   factories. Keep parsing at form and storage boundaries. Repository approval
   sends the saved authorization ID; frontend code must not grant Git authority.
4. Extract view sections and DOM preservation. Render sidebar, home, conversation,
   selected Work, and activity from typed inputs. Keep focus, cursor, scroll,
   details, native select, and slider preservation in a small DOM controller.
5. Finish settings controller ownership and external input typing, then improve
   shared test fixtures and process launchers. Use behavioral tests for stale
   reads, disposal, recovery, and user interaction as each boundary moves.

## Other maintenance candidates

Keep API transport, read cancellation, command recovery, generated contract
values, and timezone data with their current owners. Add typed payload builders
and validate external data at boundaries as consumers need them; do not manually
edit generated files. Preserve static-asset allowlist checks when adding modules. Replace fragile HTML
substring markers in `frontend/scripts/copy-assets.mts` with a shared template or
fail when markers are missing, and verify both pages receive their markup.

Outside the browser, prioritize typed validation for `scripts/release-github.mts`
external JSON while preserving immutable release assets, approvals, and manual
Azure installation. Share isolated process and browser fixture setup only where
lifecycle ownership matches; register cleanup before waiting for startup and use
bounded waits. Keep malformed-wire fixtures and boundary-specific security,
authorization, recovery, and failure assertions visible in tests.

## Verification and limits

This is an unfinished maintenance plan, not a record of checks passing today.
For each extraction, run affected frontend/module tests, typechecking, static asset
dependency checks, and the relevant browser journeys. Preserve stale-read rejection,
exact command identities/recovery, focus, drafts, and authorization. See
[test selection](repository-layout.md#choosing-verification).

PostgreSQL-backed application/Work journeys are opt-in and establish durability
only when run against the configured disposable database. Browser mocks, local
logging fixtures, and schema checks do not establish live GitHub publication,
authenticated model execution, or an Azure deployment.
