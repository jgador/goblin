# Repository instruction audit and proposal

Status: audit and follow-up plan for [issue #39](https://github.com/jgador/goblin/issues/39).
Reviewed on 2026-10-04 against `master` at
[`12bc95a`](https://github.com/jgador/goblin/commit/12bc95a46ad2c0f904decbab71a5424e59af05f2).
The proposals below are not active repository instructions. This change adds the
audit and its README link; applying the proposals belongs in scoped follow-up PRs.

The review uses OpenAI's
[Rethinking skills and prompts for GPT-6 Astra](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra)
as a starting point: make descriptions identify their actual trigger, load details
when relevant, state outcomes rather than an unnecessarily fixed itinerary, and
revisit completion and authorization boundaries. Those ideas are evaluated against
Goblin's own requirements. No recommendation depends on a particular model being
more capable or trustworthy, and no architecture or security constraint is
proposed for removal.

## Inventory and authority

The checkout has one `AGENTS.md` and four repository-local `SKILL.md` files;
there are no nested `AGENTS.md` files or skill reference files at this baseline.
The inventory includes their referenced contributor guidance and supporting
operator guides relevant to the identified decisions. Feature documentation is
read for the affected boundary, not as a prerequisite for every edit.

Categories used below:

- **Constraint:** a durable product, security, privacy, recovery, or authorization
  requirement. Keep it explicit; relaxing it needs a separate identified decision.
- **Convention:** a default for design, compatibility, or organization. Preserve
  it unless the task deliberately changes it, with callers and evidence updated.
- **Workflow:** procedures and tools for a particular kind of work. Load on demand.
- **Historical:** an earlier decision, workaround, inventory, or verification
  record. Retain useful provenance, but do not treat it as current verification.

`AGENTS.md` is the repository-wide instruction entry point. Skill bodies govern
their stated workflows only when invoked or selected according to their triggers.
The authoritative policy source is identified below; scripts and tests establish
what currently runs, rather than silently overriding policy. When instructions,
implementation, and a task disagree, identify the mismatch. Repository guidance
remains subject to the agent's higher-priority instructions and the user's scope.

| File or group | Purpose and scope | Category and policy source | Proposed disposition |
| --- | --- | --- | --- |
| [`AGENTS.md`](../AGENTS.md) | Rules for all repository work; architecture, construction, verification, artifacts, cloud policy, and doc routing | Constraint/convention/router; repository-wide policy | Keep critical constraints. Add conditional routes, completion criteria, and explicit defaults; move detailed tool recipes out. |
| [`brainstorm`](../.agents/skills/brainstorm/SKILL.md) | Explore product/design possibilities and retain decision evidence | Workflow; skill's trigger and discussion boundaries | Shorten description; keep a small root and move examples/map details to references if needed. |
| [`check-secrets`](../.agents/skills/check-secrets/SKILL.md) | Explicit local review of pending snapshots and requested outgoing commits | Constraint/workflow; skill authorization, privacy rules, and scan scope | Put explicit-only trigger first; retain snapshot coverage and redaction; separate review detail from root boundaries. |
| [`commit-push`](../.agents/skills/commit-push/SKILL.md) | Explicit all-pending-changes commit/push workflow | Constraint/convention/workflow; skill's authorized scope and commit preferences | Keep invocation, scope, no-force/no-bypass rules, and reuse of valid checks. Move message reference material; clarify separately authorized PR work and attribution precedence. |
| [`clean-slate`](../.agents/skills/clean-slate/SKILL.md) | Destructive reset of this checkout's owned WSL test installation | Constraint/workflow; skill's ownership, deletion, and recovery boundaries | Preserve explicit invocation and gates in root; route standard reset, process cleanup, and conditional leftovers to references. |
| [`architecture-refactoring-plan.md`](architecture-refactoring-plan.md) | Architectural decisions, original rewrite scope, and dated implementation evidence | Constraint/historical; north star and user-selected failure/isolation policy | Keep decisions and provenance; replace unconditional entry-point reading with task routing to current boundary docs. |
| [`work-lifecycle.md`](work-lifecycle.md), [`workspace-lifecycle.md`](workspace-lifecycle.md), [`execution-hosting.md`](execution-hosting.md) | Current Work identity, history, authorization, continuity, storage, and execution recovery | Constraint/workflow; implemented behavioral contracts | Keep authoritative boundary references and meaningful integration checks. Label dated verification separately from current requirements. |
| [`git-repository-intent.md`](git-repository-intent.md) | Saved repository previews, owner authorization, immutable grants, and publication permissions | Constraint/workflow; Goblin's application authority contract | Keep application authorization distinct from contributor Git authorization; load for repository integration changes. |
| [`formatting.md`](formatting.md), [`generated-csharp-formatting.md`](generated-csharp-formatting.md) | Formatting setup, current constructor/spacing defaults, generator audit | Convention/workflow/historical; formatting guide plus current hook/scaffolding implementation | Keep formatting separate from design preservation. Clarify historical EF postprocessing description and link current procedure. |
| [`csharp-record-audit.md`](csharp-record-audit.md), [`typed-json-contracts.md`](typed-json-contracts.md) | DTO/record decisions, intentional dynamic JSON boundaries, and compatibility evidence | Convention/constraint/historical; construction defaults in `AGENTS.md`, boundary contracts, dated audit evidence | Keep rationale and fixtures; state that retained declarations and counts describe a baseline, not a permanent whitelist. |
| [`contract-values.md`](contract-values.md) | Owners, cross-language generation, exact parsing, and compatibility | Constraint/workflow; owning C#/Rust declarations and adapter wire contracts | Keep required generation/drift checks; avoid freezing all wire changes when an explicit contract migration is requested. |
| [`environment-variables.md`](environment-variables.md) | Definition catalogs, parsing boundaries, defaults, and credential forwarding | Constraint/workflow; language catalogs and existing configuration boundaries | Keep named definitions and allowlists; scope verification to affected languages and consumers instead of mandating all suites. |
| [`database.md`](database.md) | SQL-first schema, certificate access, EF generation, migrations, real database checks | Constraint/workflow; deployment-dependent migration policy in `AGENTS.md` | Keep immutability after first real deployment. Make the later-table example conditional on that boundary. |
| [`rust-development.md`](rust-development.md), [`goblinctl.md`](goblinctl.md) | Pinned developer tools, commands, native operator/shipping boundaries | Convention/constraint/workflow/historical; toolchain files, Cargo/just commands, production tooling policy | Keep tool pins and Python-free operational tooling; move invocation detail out of always-loaded guidance and distinguish reports from compiler outputs. |
| [`secret-scanning.md`](secret-scanning.md) | Gitleaks setup, index/tree/history scope, limits, and response to findings | Constraint/workflow; local scanner and explicit contextual review | Keep privacy, narrow exceptions, and truthful coverage; explain broader history scan versus requested outgoing-range review. |
| [`repository-layout.md`](repository-layout.md), [`README.md`](../README.md), [`app-server-migration.md`](app-server-migration.md) | Discovery, build commands, and current integration responsibilities | Workflow/historical; current source layout and package commands | Keep useful navigation; synchronize stale status/command summaries without making them new policies. |
| [`releases.md`](releases.md), [`dependencies.md`](dependencies.md), [`release-workflow-plan.md`](release-workflow-plan.md) | Shipping inputs, release approvals, immutable publication, dependency selection | Constraint/workflow/historical; maintainer release policy and release implementation | Keep separate release authorization and manual Azure installation. Route here only for release/dependency/shipping changes. |
| [`deploy/local/README.md`](../deploy/local/README.md), [`deploy/azure/README.md`](../deploy/azure/README.md) | Install/stop/reset operations and manual cloud installation | Constraint/workflow; installation ownership and manual cloud policy | Keep warnings at relevant operations; do not turn a contributor edit into an installation or reset. |
| [`.codex/config.toml`](../.codex/config.toml), [`package.json`](../package.json), [`justfile`](../justfile), [`playwright.config.ts`](../playwright.config.ts), [nextest config](../.config/nextest.toml) | Executable formatting/test behavior and output locations | Workflow evidence; current checked-in configuration | Use to verify command claims. Hook trust remains environment-specific; no hook, test configuration, or automation changes in this audit. |

## Findings and decisions

| ID | Evidence at the reviewed baseline | Recommendation and reason |
| --- | --- | --- |
| F1 | `AGENTS.md` opens with unconditional “Follow” the 487-line rewrite plan. The Rust paragraph embeds setup/check/style details, although environment/contract links already have useful task conditions. | **Clarify/move.** Keep root architecture invariants and route deeper reading by affected boundary. Preserve the historical plan rather than deleting its decisions. A documentation correction should not imply replaying the architecture rewrite. |
| F2 | `AGENTS.md` allows constructor signatures and positional records to change during refactoring. `formatting.md` says only formatting should preserve shape. The record audit explicitly documents intentional construction/deconstruction changes and treats seven parameters as a review threshold. | **Keep/clarify.** These are already aligned; there is no current blanket prohibition to remove. Preserve regular class constructors and readonly dependency fields. Add a common default-versus-requested-change explanation and distinguish historical serialization fixtures from immutable construction APIs. |
| F3 | `environment-variables.md` requires `npm test`, `just fmt-check`, and Clippy for adding any variable. The full npm lifecycle builds native/frontend/.NET tools, generates deployment assets, and runs several independent suites. `commit-push` already permits documentation-only checks and reuse for unchanged content. | **Merge/clarify.** Share risk-based completion guidance in the root; keep specialized required checks in their owners. C#-only configuration work needs its consumers and relevant deployment-name/isolation checks, not automatically unrelated Rust linting. Broad suites remain useful for changes spanning those boundaries. |
| F4 | Root and rewrite-plan preservation language includes frontend visual design alongside HTTP security. Contract docs describe unchanged fields for earlier refactors. | **Clarify.** Treat existing visual design and external representation as compatibility defaults, not vetoes on a requested redesign or contract migration. Security, accessibility, credential isolation, durable history, and recovery remain constraints. Identify intentional contract impact and update consumers and tests together. |
| F5 | Root requires all in-repository temporary test output in `.artifacts/`; Playwright already complies. Rust guidance describes nextest JUnit output under `target/nextest/`, and the repository has normal `target/`, `dist/`, `frontend/dist/`, `bin/`, and `obj/` outputs. | **Clarify.** Distinguish agent-created investigation files/reports from established build products. Propose routing nextest reports to `.artifacts/` in a tooling follow-up, while retaining ordinary compiler/cache locations. Private scanner snapshots outside the checkout already satisfy the current in-repository rule; no privacy exception needs removal. |
| F6 | `check-secrets` describes reviewing “before committing or pushing” before mentioning explicit invocation. `commit-push` includes attribution in its trigger text. `brainstorm` describes conversational method at length; `clean-slate` lists multiple exclusions in its description. | **Clarify/move.** Put actual trigger and scope first, with proposed descriptions below. Keep explicit invocation requirements for review/publishing/destruction and create/edit/explain exclusions in bodies. Reading a skill for this audit is not invoking it. |
| F7 | The 257-line `clean-slate` body covers owned reset, standalone listeners, custom storage, database artifacts, Docker, host PostgreSQL, and leftovers; initial reading includes Docker build guidance unconditionally. | **Move.** Keep authorization, ownership proof, secret protection, and verified completion in the root. Load container/database/custom-path details only when inventory finds them. Do not shorten away stop-order, mount/symlink checks, uncertain ownership, or no-reinstall boundaries. |
| F8 | `commit-push` says the workflow does not create a PR/release, even though a user can separately request a PR. It also mandates a Codex trailer, which can conflict with an execution environment disabling attribution. | **Clarify.** The skill itself grants no PR/release authority; separately authorized PR work can continue after push. Preserve configured human Git identity and genuine coauthors; apply attribution only where allowed by current higher-priority instructions. Do not change the explicit skill's all-pending-changes default into an ordinary task default. |
| F9 | `database.md` defines an editable prerelease `0001_initial.sql` until first real deployment, but “Add another table later” unconditionally starts with `0002_work_notes.sql`. | **Clarify.** Show both predeployment consolidation and postdeployment additive migration paths. Check the recorded deployment boundary; do not assume local provisioning froze the baseline or reset an existing database to resolve uncertainty. |
| F10 | `generated-csharp-formatting.md` opens with current EF formatting, but “Generator follow-up” calls postprocessing encoding/newlines only. `scaffold-database.sh` now also runs whitespace/style passes, matching `database.md`. | **Clarify.** Mark the old paragraph as historical and point current procedure at the scaffold script/database guide. Keep dated counts and test results as provenance, not present-tense evidence. |
| F11 | README's status paragraph calls GitHub integration/repository tasks future work, while later README sections and current Work/repository docs describe them. Some build/test summaries omit checks now present in `package.json`. | **Merge/clarify.** Synchronize entry-point status and command descriptions with current supported behavior. Describe unverified live publication separately; a stale status paragraph must not prohibit improvements to an implemented feature. |

### Constraints retained in every follow-up

- Work rules stay in Core without HTTP, database, messaging, process configuration,
  or runtime SDK/generated-protocol dependencies. Adapters translate product types.
- PostgreSQL owns durable history; Wolverine coordinates delivery. State, history,
  command receipts, and dispatch intent commit atomically. Preserve identities,
  provenance, concurrency enforcement, and recovery; do not claim exactly-once
  external execution from a core claim check alone.
- Every failed Work requires attention and an explicit retry. Reconcile uncertain
  execution and confirm cleanup/cancellation before replacement. Local test reruns
  and repair of a contributor change do not authorize automatic product retries.
- Workspace files remain on the Work PVC; Git checkpoint records contain metadata
  only. Suspension does not authorize deleting storage or archiving it into SQL.
- Credentials, private auth stores, and raw upstream failures remain outside public
  responses and Work history. Preserve HTTP security, accessibility, repository
  isolation, scoped credential forwarding, and exact repository grants.
- Keep SQL-first schema ownership, generated EF/protocol sources, contract owners,
  and the first-real-deployment migration boundary. A requested refactor does not
  authorize destroying history, bypassing migration checks, or hand-editing output.
- Azure installation remains manual; repository automation must not log into Azure,
  provision resources, perform live Azure tests, or clean up cloud resources.
- Explicit skill invocation, proven cleanup ownership, publication permissions,
  release approvals, no force-push/history rewrite/hook bypass, and protection of
  unrelated user work remain meaningful authorization boundaries.

No change to those constraints is proposed. Splitting procedures, clarifying
defaults, and distinguishing contributor actions from Goblin application authority
must preserve their meaning across different models and runtimes.

## Concrete follow-up edits

### Repository entry point and conditional reading

Keep the root's invariant bullets and manual Azure policy. Replace the opening
unconditional plan reference with a short architecture summary and task router.
Move detailed Rust command/style recipes to the existing Rust guide, keeping the
pinned toolchain and operational tooling boundary visible. Add `formatting.md` and
the JSON/record guides as conditional routes rather than new required reading.

Proposed routing table for `AGENTS.md`:

| Work being changed | Read the relevant sections |
| --- | --- |
| Work rules, transactions, identities, or recovery | `docs/work-lifecycle.md`; `docs/architecture-refactoring-plan.md` for architectural decisions |
| Execution, workspaces, credentials, or Git authority | `docs/execution-hosting.md`, `docs/workspace-lifecycle.md`, `docs/git-repository-intent.md` |
| C# construction, DTO/record semantics, or JSON boundaries | `docs/formatting.md`, `docs/csharp-record-audit.md`, `docs/typed-json-contracts.md` |
| Closed/shared contract values | `docs/contract-values.md`; regenerate from owners and run required drift checks |
| Environment reads/forwarding or defaults | `docs/environment-variables.md`; inspect each affected consumer and allowlist |
| SQL or EF mappings | `docs/database.md`; establish the first-real-deployment boundary before choosing a migration |
| Rust/operator tooling or embedded assets | `docs/rust-development.md`, `docs/goblinctl.md`; release guides when shipping inputs change |
| Formatting setup or generator output | `docs/formatting.md`; current generator/scaffold procedure when applicable |
| Release/dependency workflows or manual installation | `docs/releases.md`, `docs/dependencies.md`, applicable deployment guide |
| Secret scanning | `docs/secret-scanning.md`; explicit contextual review uses `$check-secrets` |

This routes reading; it does not automatically invoke publishing or cleanup skills.
Agents without skill support should still find the constraints and relevant docs
from `AGENTS.md` and README.

Add this completion guidance to `AGENTS.md`:

> Continue authorized implementation through relevant verification and fixes until
> the requested outcome is complete. Reuse successful checks when their inputs,
> dependencies, configuration, and environment remain valid; rerun affected checks
> after changes or failures. Report the checks actually run and any material gaps.
> Ask when necessary information or authorization is missing, ownership is unclear,
> or a consequential change exceeds the request. Do not treat a first implementation
> or a routine local verification step as a required review stop.
>
> A request to implement alone does not authorize publishing, release, merge, or
> destructive cleanup. An explicit request to create a branch, commit, push, and
> open a PR authorizes those stated actions without another routine confirmation.
> Preserve unrelated working-tree and index changes. The explicit `$commit-push`
> skill has its own all-pending-changes scope; it is not selected automatically.
> Goblin's saved owner approvals and repository grants are product requirements;
> contributor task authorization does not bypass them.

Do not claim all local commands are harmless: tests can start child processes,
and opt-in database suites create/drop fixtures using supplied connections.
Check fixture ownership and configured targets before using those integrations.

### Compatibility and construction

Keep the current regular-constructor guidance. Add the following shared
clarification in the root and link it from the construction/contract guides:

> Preserve observable behavior, public properties, JSON representation, and
> relied-upon record semantics by default. A requested refactor may change
> construction signatures or positional declarations; update callers and relevant
> tests together. A requested contract change may change its representation with
> explicit impact and migration/consumer handling. Preserve unaffected behavior.
> Formatting alone changes neither API shape nor semantics. Historical audits,
> constructor counts, and captured fixtures explain earlier decisions; they do
> not freeze those declarations forever. Do not overwrite compatibility evidence
> merely to make tests pass.

`ConstructorCompatibilityTests` protects exact bytes for command fingerprints as
well as ordinary JSON. A constructor-only change should retain those checks. A
deliberate wire change needs reviewed new expectations and a plan for persisted
snapshots, command replay/deduplication, and old consumers wherever affected.
Records with domain equality or immutable copying must retain those semantics
unless changing them is explicitly in scope. Seven constructor parameters stays
a review heuristic, not a universal cap or justification for arbitrary wrappers.

In `database.md`, split the later-table procedure by deployment state. In
`generated-csharp-formatting.md`, make the outdated postprocessing account past
tense and link the current scaffold procedure. In README and command tables,
synchronize current capability and npm lifecycle descriptions. Leave historical
verification records dated; do not relabel them as fresh passes.

### Skill descriptions and progressive disclosure

Proposed exact frontmatter descriptions (preserve the existing names):

| Skill | Proposed `description` |
| --- | --- |
| `brainstorm` | `Explore product or design ideas when the user wants brainstorming. Do not add a brainstorming phase to a clear implementation request.` |
| `check-secrets` | `Review pending Git changes for exposed credentials. Run only when explicitly invoked as $check-secrets; reviewing alone does not authorize publication.` |
| `commit-push` | `Commit all pending Goblin changes together and push to GitHub. Run only when explicitly invoked as $commit-push; honor any narrower requested scope.` |
| `clean-slate` | `Remove this checkout's verified Goblin WSL test installation and data. Run only when explicitly invoked as $clean-slate; leave installation stopped.` |

Each root body must keep the trigger, create/edit/explain non-invocation rule where
applicable, ownership/scope, authorization limits, essential privacy gates, and
completion/reporting criteria before linking detail. Existing session authorization
still applies to separately requested actions. Shorter descriptions must not cause
an ordinary “fresh install” request to select destructive cleanup.

Candidate reference split, only where it reduces irrelevant reading:

| Skill | Keep in root | On-demand detail proposed inside that skill's `references/` |
| --- | --- | --- |
| `brainstorm` | Provisional sketch, follow user's attention, distinguish exploration from decisions, stop at requested scope | `scenarios.md` for examples; `working-map.md` for larger discussion checkpoints. A short skill can remain unsplit if routing adds friction. |
| `check-secrets` | Explicit read-only authority, exact pending/index/outgoing scope, redact before displaying content, incomplete-scan reporting | `review.md` for scanner/pattern categories and snapshot collection; `goblin-storage.md` when auth paths, packaging, or scanner exclusions are affected. |
| `commit-push` | Invocation, all-pending default, scope overrides, preserve identity, valid-check reuse, inspect outgoing range, no force/bypass, verify remote result | `commit-messages.md` for syntax/examples and environment-dependent attribution. Keep task-specific staging guidance visible before mutation. |
| `clean-slate` | Explicit destructive intent, resolved checkout/WSL ownership, removal notice, source/secret protection, verify absence and free ports, no reinstall | `owned-reset.md` for standard inventory/reset order; `listeners.md` when owned standalone listeners exist; `leftovers.md` for observed custom paths, Docker/database artifacts, and retained mounts. |

Move existing safeguards with their applicable procedure, not into optional
background prose. Unknown ownership remains a stop for the affected deletion;
missing data is not permission to invent ownership. Do not auto-invoke any of
these skills to edit or evaluate its instruction file.

### Verification scope and reuse

Place general completion/reuse guidance in the root, with specialized checks in
the appropriate guide. Proposed scope matrix:

| Change | Minimum relevant evidence; expand when the changed boundary warrants it |
| --- | --- |
| Documentation only | Review statements against current sources, validate local links, and `git diff --check`; no runtime suite by default |
| C# construction/refactor | Build affected projects and run relevant behavior, mapping, serialization, and equality tests; Core tests for Core changes; preserve transport regressions when that boundary changes |
| Closed/shared C#/Rust values | Required `npm run contracts:generate`, `npm run contracts:check`, `npm run test:contracts`, affected .NET/Rust tests, and browser/type checks for consumers |
| Environment configuration | Affected catalog/consumer tests, matching deployment-name/default/isolation checks, and format/lint for changed languages; full suite for broad configuration effects |
| SQL, transactions, persistence, or recovery | Affected Core/application tests plus real PostgreSQL integration for database guarantees; required mapping regeneration/drift review; skips are explicit coverage gaps |
| Runtime, browser, sandbox, or installation integration | Existing relevant transport/HTTP/browser/deployment checks and real-runtime/Kubernetes checks for claims about those boundaries; no automated Azure installation |
| Explicit commit/push after verification | Reuse valid results, check staged/outgoing scope and whitespace, run configured hooks, verify destination/remote commit; publishing alone does not require rerunning unchanged suites |

Do not substitute unit or fixture tests for database atomicity, authenticated model
execution, live GitHub publication, or deployed sandbox isolation. Do not claim an
opt-in suite passed when it skipped. Reusing old results requires knowing which
bytes, dependencies, configuration, and environment they cover; dated docs are
not reusable test evidence for a new checkout. If a contract owner changes, its
generation checks remain required even when the rest of a suite is reusable.

For artifacts, keep `.artifacts/<task-or-tool>/` for agent-created investigation
files, reports, screenshots, traces, and logs. A follow-up should explicitly list
established build/cache outputs as allowed locations and move configurable test
reports such as nextest JUnit under `.artifacts/`. Keep private scanner snapshots
outside the checkout, removed after scanning; never collect real credentials in
general test reports. Review hook full-scope formatting behavior before assuming
a turn changed only the requested files.

## Representative before/after comparison

These are runnable task specifications for a future instruction-change PR, not
claims that before/after model experiments were run in this audit. “Before risk”
is a static inference from the instructions. Use two fresh disposable checkouts
of the same application baseline, apply only the proposed instruction changes to
one, and give the same model/runtime the same task, tools, and fixture access.
Compare an additional contributor model only when available; avoid hardcoding a
model name or a token target as the success criterion. Do not run cleanup while
evaluating publishing or ordinary edits.

| Task and prompt | Before risk at baseline | Expected after behavior and evidence |
| --- | --- | --- |
| **Documentation correction:** “Fix the duplicated word in the Password storage paragraph of `deploy/local/README.md`.” | Unconditional architecture-plan reference can add irrelevant reading. This checkout currently says “the same the Rust”. | Read the applicable guidance and paragraph, make the correction, check links/whitespace as relevant, and finish without application tests or skill invocation. No install/reset/cloud actions. |
| **C# refactor:** “Simplify construction of `RuntimeModel` in the shared and HTTP contracts using named initialization for optional metadata. Update callers; preserve JSON names, defaults, and model-cache behavior.” | Existing constructor rules already permit this; preservation prose elsewhere can still be mistaken for a fixed API. | Read construction/JSON guidance, update both types/mappings/callers, build, and run relevant model/API serialization tests. Keep cache copies separate and identify external C# construction impact. Do not change Core dependencies or freeze a seven-parameter constructor because it is already present. |
| **Contract change:** “Add `NoticeKind.Warning` with wire value `warning` and a browser presentation; preserve existing error/info values and integer rejection.” | Earlier “unchanged” descriptions can be read as a ban; a narrow C# edit can miss generated browser consumers. | Update the owning enum and affected mappings/renderers/tests, regenerate/check shared values, and run contract plus relevant backend/browser checks. Explain the additive consumer impact and retain unknown/numeric rejection. No fabricated state or unrelated lifecycle change. |
| **Explicit commit/push:** “Commit the documentation correction on a new branch, push it, and open a PR linked to its issue.” Also evaluate `$commit-push` separately with a known additional pending change and an explicit narrow-scope variant. | Explicit-only skill wording or its no-PR scope can be mistaken for denying ordinary task authorization. Fresh verification may be repeated, and attribution instructions may conflict with the environment. | Ordinary task publishes only its authorized changes, reuses valid documentation checks, verifies outgoing commits/destination, and completes the separately requested PR. Explicit skill follows its all-pending default or the stated narrowing. No force-push, release, merge, or unrelated-data cleanup; honor current attribution instructions. |

For publish comparisons, use an explicitly authorized test repository/branch;
otherwise evaluate through a disposable local bare remote and report that live
GitHub/PR creation was not exercised. Do not infer live coverage from that fixture.

Record: baseline and instruction revision, model/runtime, prompt, files read,
applicable skill selections, clarification/approval stops and their reasons,
commands/checks and reused evidence, changed files, result, and coverage gaps.
Expected success is relevant guidance discovery, completed authorized work,
preserved constraints, and no unnecessary stop or repeated check. Fewer tokens or
commands alone is not success. Keep temporary transcripts/output in `.artifacts/`
and share a concise, redacted result in the follow-up PR.

## Follow-up sequence and maintenance

1. **Guidance and consistency:** update root routing/completion/defaults, environment
   verification scope, record/JSON compatibility wording, database example,
   generated-formatting history, and README/command summaries (F1–F4, F9–F11).
   Review retained constraints side by side. This needs documentation validation,
   not an application redesign or release.
2. **Skill boundaries and disclosure:** update four descriptions, split details
   where useful, clarify separate PR authority and attribution precedence (F6–F8).
   Check positive explicit invocations and negative create/edit/explain/general
   install requests. Keep each skill's essential gates before reference routing.
3. **Artifact/tool alignment:** clarify established output locations and route
   configurable reports consistently (F5). If configuration changes, run the
   affected formatter/test runner and verify actual output paths, including failures.
4. **Comparison:** run the four task specifications against the same baseline before
   and after the relevant instruction changes. Report observed differences and
   remaining risks; do not report this audit's predictions as measured improvement.

These are proposed scopes, not newly created issues, scheduled automation, or
authorization to execute cleanup, release, cloud work, or the sample code changes.

Revisit when a contributor runtime/model materially changes or a recurring problem
appears: irrelevant reading, contradictory instructions, unfinished authorized work,
repeated verification, or an ownership/authorization misunderstanding. The next
instruction PR should record the symptom, affected rule, original purpose/source,
chosen category, disposition, and relevant comparison result. Update this inventory
when adding guidance; prefer correcting the owning rule over adding another global
warning. No model-specific branch, ongoing benchmark service, or scheduled audit
is needed. A constraint change must be named and reviewed as such, never hidden
inside instruction cleanup.

## Audit completion and limits

The inventory, classified findings, proposed wording/routes, four comparison tasks,
and scoped follow-ups address issue #39's audit-and-plan outcome. The review checked
current instruction files against package commands, hook/test output configuration,
scaffolding, and existing compatibility/contract tests. Application behavior,
active instruction files, skills, hooks, workflows, dependencies, and releases are
unchanged. No before/after model trials, runtime suites, opt-in PostgreSQL checks,
Kubernetes installation, authenticated model task, or live application publication
test was performed for this documentation-only audit.
