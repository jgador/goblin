# TypeScript refactoring review

This review covers the repository's TypeScript browser code, Node scripts,
configuration, fixtures, and tests as of October 1, 2026. The main remaining
maintenance problem is ownership in `frontend/src/work/app.ts`: loading, command
construction, navigation, draft preservation, markup, and interaction still share
module globals. Extract these responsibilities in working increments while
preserving the existing browser technology and visual design.

Follow the [architecture plan](architecture-refactoring-plan.md),
[Work lifecycle](work-lifecycle.md), [contract values](contract-values.md), and
[environment variable guide](environment-variables.md). Browser modules observe
persisted Work and submit commands. Work transitions, attention, retries,
reconciliation, and execution ownership remain backend responsibilities.

## Completed foundations

The preceding refactor extracted model selection, catalog loading, and picker
markup. This continuation adds the following boundaries:

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

## Browser file inventory

All files under `frontend/src` were included in the review. Paths in this table
are relative to that directory. Recommendations below describe remaining work.

| Files | Finding and recommendation |
| --- | --- |
| `work/app.ts` | Highest priority. Its global state still coordinates unrelated responsibilities and its action handler mixes local UI changes with durable command construction. Follow the extraction sequence above. |
| `work/contracts.ts`, `api/contracts.ts`, `api/workspace-contracts.ts` | Keep public views separate from internal controller state. Add typed Work/conversation payloads and endpoint-specific adapter methods; account APIs already have path-indexed request/response types. Check nullable wire fields against the backend before tightening them. |
| `work/command-submission.ts` | Keep recovery and exclusivity here. The payload intentionally remains opaque in this increment; typed builders are the next step. Storage validation checks restoration safety, while the server validates authority and lifecycle. |
| `api/client.ts`, `api/read-scope.ts` | Shared transport and reset foundations. Keep successful response assertions at this boundary; add validation where external data can affect behavior. Add new abstractions only when a consumer needs them. |
| `work/model-selection.ts`, `work/model-catalog.ts`, `work/model-picker.ts` | Useful existing boundaries with selection and stale-request coverage. Keep runtime model names extensible. Narrow catalog adapter signatures to model operations when the wider API typing work lands. |
| `work/surface.ts` | Rendering, search text, and derived progress share a file. Extract pure presentation selectors if they acquire independent consumers; preserve the distinction between product progress and runtime events. |
| `work/presentation.ts` | Shared UI helpers currently live inside Work and are imported by settings. Move them to a UI directory during the view extraction and type icon names from the icon definition so misspellings cannot silently choose a fallback. |
| `work/sample-fixtures.ts` | Documented design references, unused by the live UI. Keep them explicitly separate from persisted state; move to design fixtures when reorganizing views. |
| `connection/codex.ts` | Transport is now shared, but account transitions, verification presentation, and DOM behavior remain coupled. Separate connection-check results and their messages from panel rendering. Preserve explicit manual retry after failed checks. |
| `connection/app.ts` | Small composition entry point; no further extraction needed. |
| `settings/settings.ts` | Provider names are now typed internally. Replace the long mount branch with explicit provider controllers when splitting panel markup; give each controller mount/reset/dispose ownership. Preserve connection polling across closing and reopening Settings. |
| `settings/github.ts` | Status polling, repository browsing, actions, and markup remain coupled. Extract typed GitHub adapter operations and repository selection state. Parse DOM action strings once, and ensure older repository reads cannot replace a later selection. |
| `settings/system.ts` | Already owns cancellation and stale readings. Keep machine DTOs at an API boundary and extract formatting/chart markup only when it simplifies consumers. Do not display unavailable metrics as healthy zeroes. |
| `settings/timezone.ts` | Preference access, external location lookup, and display formatting are separate concepts. Split the external lookup adapter when extending it; retain visitor-browser lookup, omitted credentials, timeouts, and local fallback. |
| `settings/timezone-picker.ts` | DOM interaction, filtering, suggestion state, and saving are combined. Extract pure option/search/suggestion functions if this UI grows, while retaining cancellation and optimistic concurrency on saves. |
| `api/values.ts`, `settings/timezone-places.ts` | Generated data. Change the owning declarations or generators; do not refactor generated output manually. |

## Tooling and test file inventory

Every TypeScript file in these groups was included in the review. Test files
should retain readable journeys and explicit negative fixtures; reducing their
line counts is not an objective.

| Files | Finding and recommendation |
| --- | --- |
| `config/environment.mts` | Declarative catalog with named reads/sets at consumers. Keep it data-only and explicitly covered by script typechecking. |
| `playwright.config.ts`, `frontend/scripts/clean-assets.mts`, `scripts/clean-output.mts` | Small configuration/cleanup boundaries. Playwright already writes results under `.artifacts/playwright`; preserve this. |
| `frontend/scripts/copy-assets.mts` | Codex panel extraction uses HTML substring markers. Replace this with an explicit shared template or fail when markers are missing, with build validation that proves both pages receive the required markup. |
| `scripts/update-timezone-places.mts` | Scoped generator. Preserve checked-in output, IANA provenance, and alias processing. No broad redesign needed. |
| `scripts/generate-contract-values.mts`, `scripts/generate-contract-values.test.mts`, `tests/contracts/values.typecheck.ts` | Ownership and stale-output checks are valuable. Keep extraction intentionally narrow and fail on unsupported declarations. Expand compile-time checks alongside typed payloads. |
| `scripts/release-github.mts`, `scripts/release-github.test.mts` | High priority outside the browser: `any` permits unchecked release catalogs, GitHub API results, and release records. Parse external JSON into validated owned types, then separate pure catalog/identity rules from GitHub effects. Preserve immutable published assets, approval requirements, and recovery semantics; keep Azure installation manual. |
| `scripts/check-secrets.mts`, `scripts/check-secrets.test.mts`, `scripts/install-git-hooks.mts` | Security-sensitive existing behavior with substantial negative coverage. Keep index/worktree/history and redaction contracts. Prefer typed scanner reports at the tool boundary and shared fixtures over rewriting scanning logic. |
| `scripts/check-codex.ts`, `scripts/check-headlamp.ts`, `scripts/check-logs-ui.ts` | Repeated isolated backend/browser setup and port discovery. Share resource ownership and cleanup so failures before the main `try` cannot leak children or contexts. Keep real-runtime checks opt-in and all in-repository investigation output under `.artifacts`. |
| `tests/support/backend.ts`, `tests/support/setup.ts` | Startup waits need explicit deadlines, child-error handling, and cleanup registered before awaiting startup. Share a bounded line-protocol launcher without moving execution into browser or HTTP request ownership. |
| `tests/support/browser-server.ts`, `tests/support/goblinctl.ts` | Thin fixture composition and binary selection. Preserve private test credentials, named environment definitions, and separation from production tooling. |
| `tests/fixtures/fake-codex.mts` | Scenario behavior is concentrated in one dispatch loop. Split scenario handlers around shared protocol helpers when extending it. Deliberately malformed and incomplete payloads must remain possible. |
| `tests/frontend/model-selection.test.ts`, `model-catalog.test.ts`, `api-client.test.ts`, `command-submission.test.ts` | Behavioral module coverage. Reuse small storage/deferred fixtures if duplication grows; retain exact identity, cancellation, and stale-completion assertions. |
| `tests/e2e/workspace.spec.ts`, `controls.spec.ts`, `refresh.spec.ts`, `model-picker.spec.ts`, `repository-authorization.spec.ts` | Repeated mocked workspace data. Share typed view factories and a scoped API fixture while allowing malformed wire fixtures explicitly. Keep focus/drafts, recovery, authorization, and execution provenance assertions local to journeys. |
| `tests/e2e/settings.spec.ts`, `system.spec.ts`, `timezone.spec.ts`, `connection.spec.ts` | Settings-specific mock state and polling controls. Consolidate shared session/agent/connection setup; retain provider-specific failures and disposal tests. |
| `tests/e2e/auth.spec.ts`, `local-login.spec.ts`, `work.spec.ts`, `setup.spec.ts`, `install.spec.ts` | Real boundary and installer journeys. Preserve HTTP security, opt-in persistence, and standalone installer contracts. Share setup only where lifecycle ownership is the same. |
| `tests/integration/auth.test.ts`, `local-login.test.ts`, `preferences.test.ts`, `work.test.ts`, `github.test.ts` | Repeated backend lifecycle and HTTP helpers. Share startup/cleanup and typed JSON reads while retaining origin, cookie, invalid-payload, identity, and concurrency cases. Report real PostgreSQL skips. |
| `tests/integration/headlamp.test.ts`, `logs.test.ts` | Duplicated upstream/HTTP streaming fixtures. Share resource cleanup and byte-stream helpers, preserving explicit proxy allowlists and credential exclusion tests. |
| `tests/deployment/azure.test.ts` | A large fixture generator combines many fake tools and installer phases. Extract offline tool fixtures and scenario configuration into support modules. Keep installer ordering, recovery, secret protection, and failure assertions visible in each test. |
| `tests/deployment/postgres.test.ts`, `migration.test.ts`, `local-password.test.ts` | Generated fake executables should share fixture-writing helpers and typed scenario configuration. Preserve migration ownership, TLS identity, password bytes, and negative credential inheritance cases. |
| `tests/deployment/local.test.ts`, `setup.test.ts`, `names.test.ts`, `environment.test.ts`, `browser-assets.test.ts` | Retain their focused boundary contracts. Consolidate repetitive temporary-directory cleanup where useful; avoid generalized infrastructure that obscures what each test verifies. |

## Verification and limits

Frontend and tooling compilation, 42 frontend behavioral tests, contract and
dependency checks, nine contract/release tests, browser asset dependency checks,
and protocol/Kubernetes generator checks pass. The backend builds without warnings.
The full `npm test` run passed, including 142 JavaScript cases and one skipped
PostgreSQL-dependent HTTP case.

All 54 relevant browser cases pass across Work, model selection, connections,
settings, timezone, and installation. One stale timezone message assertion was
corrected to expect the public API failure message; all ten timezone cases were
then rerun successfully. These journeys cover stale reads after locking, exact
command recovery after refresh, corrupt recovery state, drafts, focus, and layout.

Separate checks against a disposable PostgreSQL 18.6 instance passed ten
persistence tests and 114 application tests, including durable command recovery.
The ordinary suite skips PostgreSQL-dependent tests without configured test
connections. The real logging pipeline and VMUI browser checks also passed,
including collector recovery after an outage and ingestion access restrictions.
These checks used local fixtures and did not exercise live GitHub publication,
authenticated model execution, or an Azure deployment.
