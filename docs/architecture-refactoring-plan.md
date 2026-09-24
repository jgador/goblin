# Architecture and rewrite plan

Status: the four rewrite areas are implemented in the current working tree: durable
Work, integration boundaries, conditional execution hosting, and the persisted UI.
Validation and remaining integration limits are recorded in the implementation
checkpoint below. Future runtime switching and collaboration remain deferred.

Initial code review on 2026-09-19 against
[`master` at `caecffc`](https://github.com/jgador/goblin/commit/caecffcb5276a35a7a0520ed412c253d1af77635).

## Architectural north star

> Work is the product.
>
> Agents execute Work.
>
> Codex is the primary runtime today.
>
> Other runtimes can execute Work.
>
> Postgres remembers.
>
> Wolverine coordinates.
>
> The UI observes and controls.

Goblin is evolving from a web application around Codex into a durable,
self-hosted AI coworker platform.

Work is the durable product concept. Over time, it can encompass objectives,
progress, decisions, execution attempts, artifacts, reviews, and outcomes.
Conversations are one way humans interact with and modify Work; they are not
the fundamental unit of the system.

Codex is the current primary coding agent integration. The product must also
be ready to use other agents, including Claude and GitHub Copilot. Those are
future integration targets; the current application only integrates Codex.

In this design, an **agent** is a durable Goblin identity, while a **runtime** is
the integration that executes an attempt on its behalf. This distinguishes the
assigned coworker from products such as Codex, Claude, or GitHub Copilot.
Executions are temporary attempts that use a selected runtime, initially Codex.
PostgreSQL owns durable application state and history. Wolverine coordinates
asynchronous work, durable messaging, retries, and inbox/outbox behavior. The UI
displays persisted state and issues commands.

Prefer a modular monolith with strong logical boundaries. Keep deployment to
the Goblin application, PostgreSQL, and isolated agent executions in the
self-hosted environment. Introduce network boundaries only for a concrete need.

## Scope and approach

The user selected all four rewrite candidates during the architecture brainstorm
on 2026-09-19:

1. Application core.
2. Agent integration and connection management.
3. Execution hosting and lifecycle.
4. Work UI state and API interaction.

This decision supersedes the earlier restriction against a broad rewrite and
the sequence of two preparatory refactors followed by one small Work feature.
Rebuild these four areas around durable Work and support for multiple runtimes.
Much of durable Work is new development; the implementations being replaced
are the preview's application coordination, connection model, execution hosting,
and browser-owned Work state.

Design the boundaries together and deliver them in runnable increments. The
first durable Work flow is a milestone across the four areas; completion of that
flow alone does not complete the rewrite scope. Preserve workspace access,
credential protection, and explicit concurrency and recovery contracts as
implementations change. Validate intentional changes to readiness, cancellation,
and durable execution as product behavior.

Retain the existing technology foundations described under Areas to preserve.
Introduce abstractions for the responsibilities and runtime differences in this
scope. File size and aesthetic consistency alone are not reasons to replace code.

The detailed design remains open to the implementing agent. Recheck the
repository before implementation if it has moved beyond the reviewed commit.

## Readiness for other agents

The user-established direction is to keep Codex primary now while preparing
for agents such as Claude and GitHub Copilot. The boundaries below are proposed
ways to satisfy that direction. The first durable Work flow still uses Codex;
it does not require implementing all integrations or a plugin framework.

- **Goblin owns continuity.** Work, agent identities, conversations, decisions,
  artifacts, and execution history use Goblin-owned records. Runtime session
  references may help resume an execution, but are not the source of truth for
  the product's history. A different runtime can receive persisted context and
  artifact references without needing to read a Codex thread.
- **Integrations own runtime details.** Keep authentication, credential handling,
  transport, protocol types, model selection, and session mechanics within each
  integration. Design the shared execution contract around durable Work and
  validate it with concrete consumers and runtime capabilities. Adding another
  runtime should not require rewriting Work lifecycle rules or public Work
  contracts.
- **Capabilities are explicit.** Starting work, reporting progress, cancellation,
  interactive approvals, and resuming after a restart can differ by runtime.
  Validate required capabilities before dispatch and expose unsupported behavior
  clearly. Preserve runtime-specific options where useful; do not model every
  runtime as a Codex App Server or promise identical behavior.
- **Attempts record their executor.** Record the selected runtime and connection
  for each attempt, plus its model and runtime session references when available.
  Keep credentials out of Work history. A later configuration change must not
  rewrite which runtime performed an earlier attempt.

Validate the supported CLI, SDK, or service interface for Claude and GitHub
Copilot when choosing their integrations. Their exact mechanisms and capability
sets remain open; listing them here is not a claim of implemented support.

The connected choices and their current status are:

| Choice | Current direction | Consequence to explore |
| --- | --- | --- |
| Agent identity and runtime choice | Assign Work to a stable Goblin agent with Codex as its initial runtime; show the runtime used in execution history. | The same coworker could use another runtime later. Separate agent identities tied to each product are another possible experience. |
| Context handoff | A change of runtime starts a new attempt with Goblin's persisted context and artifacts. | Work stays continuous, but runtime-private context is not assumed portable; a handoff may need a prepared summary. |
| Failure and fallback | Decided: every failure requires core-owned attention, with explicit user action before retrying failed Work. | Uncertain attempts must be reconciled first. Runtime switching and automatic fallback remain deferred. |

The initial implementation follows the plan's stable Goblin identity and Codex
execution direction. Runtime switching, context handoff, automatic fallback, and
collaboration between multiple agents on one Work item remain deferred proposals.

### Failure and attention policy

During implementation review on 2026-09-19, the user suggested that **Needs
attention** belong in the core as well as the UI, and selected **"Surface every
failure for attention"** over automatically recovering temporary failures.

- Work owns its attention state and reason. Input requests, result review,
  failures, and uncertain execution can all require attention. Execution attempts
  retain their distinct statuses and provenance; a failed attempt does not end
  the Work item.
- Every observed dispatch or execution failure requires attention, including
  temporary failures and failures before execution starts. There is no automatic
  retry of failed Work. An explicit user action requests a new tracked attempt.
- Message redelivery is not authorization to repeat execution. Wolverine's
  transport recovery must respect persisted attention and attempt ownership;
  handlers must not automatically retry failed runtime operations.
- An uncertain attempt requires reconciliation before replacement. Confirming
  that it stopped resolves uncertainty, but does not authorize a retry. A late
  confirmed result goes to review; completion still requires approval.
- Cancellation records intent until stopping is confirmed. A failed cancellation
  requires attention and does not assert that execution stopped.
- A database outage can prevent recording an attention transition immediately.
  HTTP callers must receive a failed/pending command, never a fabricated saved
  transition. Durable dispatch and host reconciliation must retain enough evidence
  to surface the failure when storage returns, without restarting Work.

These are application behaviors, not browser-only labels. Choosing a second real
runtime for the planned integration experiment remains open and does not block
the initial Codex implementation.

## Baseline reviewed before implementation

The reviewed application managed workspace access, Codex authentication, and a
restricted verification prompt. Authentication and execution coordination lived
in the web project; public account DTOs referenced generated Codex types. The
Work screen used simulated replies and local state. PostgreSQL tooling existed,
but Wolverine and durable Work were not connected. Readiness depended on Codex.
These are the baseline problems addressed by the implementation below.

### Execution environments — clarified by the user

Work does not imply a repository or an agent sandbox. A question can execute
without provisioning one. An explicit request to change a known repository uses
a new isolated agent sandbox, brokered GitHub access from Goblin sign-in, and the
Goblin agent's Git author identity. The first implementation used a fresh sandbox per repository attempt and a saved
branch as the continuation checkpoint. The user-approved workspace lifecycle now
uses one persistent Sandbox/PVC per Work, multiple runtime turns per attempt,
semantic AI keep/suspend decisions, and verified code-plus-file checkpoints
before reclaiming a completed Work volume. A
conversation message does not itself allocate a sandbox. See
[workspace lifecycle](workspace-lifecycle.md).

A permanent taxonomy for questions, research, and investigations, and broader
workspace reuse policy, are implementation/product details still deferred. They
do not block these boundaries. Unspecified repositories are not guessed and
repository commands are never sent to the text-only host.

## 1. Application core

Rebuild application coordination around Work, Agents, Connections, and execution
attempts. Replace the ownership currently concentrated in `Authentication` and
`GoblinApplication` with modules that own their product behavior. Keep host
composition, shared HTTP security, and error translation clear and centralized.

Work owns objectives, lifecycle transitions, decisions, result review, and
completion rules. Persist conversation history and artifact references needed
for continuity. Agents have stable identities; each execution attempt records
its Work, assigned agent, runtime, connection, and runtime session references.
Keep Work identity, agent identity, execution-attempt identity, and runtime
session identity distinct.

HTTP endpoints submit application commands and return persisted views. Map
runtime failures to application failures, then to HTTP responses at the HTTP
boundary. Product rules and public Work contracts use Goblin-owned types.

Use PostgreSQL for durable state and Wolverine for asynchronous coordination.
Work state and dispatch intent commit atomically. Keep database transactions
short; application rules determine valid transitions and retries. A completed
runtime turn does not automatically mean the intended Work outcome is achieved.

This area is complete when Work can be created independently of a conversation,
assigned, executed, reviewed, and retrieved with its durable history through
these boundaries. Durable Work execution belongs to the application core,
independently of authentication.

## 2. Agent integration and connection management

Replace the single Codex connection model with explicit connections and runtime
adapters. Separate connection setup and verification from durable execution.
Keep Codex primary while making room for Claude, GitHub Copilot, and other
integrations without changing Work lifecycle rules.

Design shared execution inputs, events, results, failures, and capability
reporting around Work. Keep authentication methods, account metadata, model
selection, protocol types, and session mechanics specific to each integration.
Today's restricted connection-check prompt must not define durable execution.
Replace the generated Codex `PlanType` in the existing HTTP contract with a
Goblin-owned representation while preserving its wire format.

Record the selected connection and runtime for each attempt. Coordinate account
changes with the executions using that connection, and make concurrency limits
explicit for each runtime and execution environment. Preserve the current
connection-verification contracts while replacing their implementation:

- Overlapping verification prompts are rejected.
- Account changes wait for the current verification prompt to finish.
- Cancellation and timeouts clean up runtime operations.
- Raw upstream errors and credentials do not reach the browser or Work history.

The existing `/api/prompt` can retain request-cancellation behavior for connection
verification. Accepted durable Work has its own lifecycle and cancellation
commands, independent of the browser request.

Plan an early integration experiment against one additional real runtime to
test the shared contract. The choice of Claude or GitHub Copilot, its supported
CLI/SDK/service interface, and the production delivery timing remain open.
Use test doubles for contract coverage, but validate advertised capabilities
against a real integration before claiming support.

This area is complete when Codex operates through the shared boundaries,
connection state and availability are explicit, and Work orchestration has no
dependency on Codex protocol types or thread semantics. Full feature coverage
for every future integration is separate from this rewrite scope.

## 3. Execution hosting and lifecycle

Replace the web application's ownership of the Codex child process with an
explicit execution host and lifecycle. Give repository executions isolated
environments with controlled workspace access and credentials. Keep execution
isolation compatible with the modular monolith; the application and database
remain the durable owners of Work.

Define how execution environments are created, assigned, observed, stopped,
recovered, and cleaned up. Record execution ownership so that a restart or
duplicate message cannot blindly launch a competing attempt. Preserve workspace
state and artifact references needed for recovery; retain uncertain attempts
for reconciliation before deciding whether to resume or replace them.

Establish these contracts:

- Closing a browser does not cancel accepted durable Work.
- A retry creates or resumes an explicitly tracked attempt.
- Duplicate delivery cannot blindly start another execution.
- Uncertain execution after a crash has an explicit recovery path.
- Cancellation records intent and reconciles whether execution actually stopped.
- Workspaces and credentials are accessible only to the executions that need them.

Wolverine coordinates delivery and retries; durable messaging does not guarantee
that external actions happen exactly once. Reconcile uncertain external actions
before dispatching a replacement attempt. If runtime switching is introduced,
the proposed handoff starts a new attempt on the same Work with recorded context
and workspace references. Cross-runtime resume behavior remains an open design
choice and cannot assume native sessions are portable.

Separate application readiness from runtime availability. Goblin must remain
available to inspect Work and repair connections when Codex fails. Report
availability for each configured integration and connection. A failed runtime
must not prevent dispatch through another healthy, suitable runtime once it is
implemented and configured.

Update the Kubernetes manifests, installer behavior, credential mounts, workspace
storage, and readiness tests together. Define application readiness around the
services required by enabled features. Application database credentials must
stay outside agent execution environments.

This area is complete when real Codex execution uses the isolation boundary and
restart, cancellation, duplicate delivery, and cleanup behavior are validated.
Repository actions require this boundary before they are enabled.

## 4. Work UI state and API interaction

Replace the Work preview's local lifecycle mutations and simulated replies with
server commands and persisted views. Rebuild state handling around Work identity,
execution progress, conversations, decisions, outputs, and result review.
Keep navigation, selected tabs, and presentation state local to the browser.

Persist actions such as creating Work, answering a decision, requesting changes,
and approving a result. An approval must still be visible after a refresh or in
another browser. Distinguish a pending command from a server-confirmed transition,
and handle failed or repeated submissions without inventing completed actions.

Support progress updates and reconnect behavior that retrieves authoritative
state. Show failures, uncertain attempts, and recovery controls. Identify which
runtime performed an attempt, and expose capability differences when they affect
an action. The user experience for selecting agents, changing runtimes, and
automatic fallback remains open.

Separate sample fixtures from application rendering. Preserve the current visual
design, branding, frontend technology, accessibility, and HTTP security while
replacing state ownership. Real Work uses authenticated APIs and persisted data.

This area is complete when the Work journey operates through the real APIs and
its history, decisions, and approvals survive refresh and reconnection. Simulated
transitions must no longer drive the integrated product experience.

## Delivery and validation across the four areas

Start with an end-to-end milestone: create Work, assign a stable agent, commit
dispatch intent, execute with Codex in an isolated environment, persist the
result, and review it through the UI. Use this flow to validate all four designs
and expose gaps early. Complete each area's contracts beyond that first flow.

Use the existing
[HTTP authentication and prompt tests](../tests/integration/auth.test.ts) and
[Codex transport tests](../backend/tests/Goblin.Tests/CodexClientTests.cs) as
regression coverage. Add meaningful tests for the new boundaries, including:

- Persisted Work transitions, decisions, result review, and execution provenance.
- Atomic dispatch, duplicate delivery, restart recovery, and cancellation.
- Credential and workspace isolation during real execution.
- Browser disconnection, refresh, reconnect, and repeated command submission.
- Runtime failure while Work history and recovery controls remain available.
- Runtime contracts that do not require Codex-specific types or session semantics.

Exercise real PostgreSQL for persistence and transaction contracts, real Codex
for execution and isolation, and browser journeys for durable UI behavior.

The scenario "Codex edits files, Goblin restarts, and another runtime continues"
connects the four areas: persisted attempts, context handoff, workspace recovery,
and a comprehensible UI history. Keep this as an exploration scenario until
handoff policy is settled. Validate it against a second real integration before
claiming cross-runtime continuation; a test double alone is insufficient.

## Repository principles

Document these principles in the repository and reference them from
`AGENTS.md` when the implementation establishes the boundaries:

1. Work lifecycle rules belong with Work.
2. Runtime-specific types stay inside runtime integrations.
3. HTTP translates requests and failures; it does not own durable execution.
4. PostgreSQL owns Goblin's durable history; runtime sessions are references to
   execution context.
5. Agents outlive attempts; attempts outlive HTTP requests.
6. Use the four agreed behavioral boundaries to guide the rewrite, with explicit
   compatibility and recovery contracts as implementations change.
7. Codex is the current default integration, not a dependency of Work lifecycle
   rules. Each attempt records its runtime; capability differences stay explicit.

Let product behavior naturally form modules such as Work, Agents, Connections,
and Conversations. Add Outcomes or other modules when their behavior warrants
an independent boundary; do not create empty placeholders.

The [App Server migration notes](app-server-migration.md) distinguish today's
ephemeral Codex conversations from the proposed durable model. Goblin should
own its durable conversation history and decisions. Runtime context,
compaction, and resume mechanics can remain runtime concerns. Keep that
ownership consistent as Claude, GitHub Copilot, or other integrations are added.

Automate the consequential rules first:

- Dependency checks prevent product rules and contracts from referencing
  runtime-specific protocols or SDKs, including Codex, or ASP.NET types. HTTP
  adapters remain free to use ASP.NET.
- Integration tests protect the durability and execution contracts above.
- Existing protocol-generation checks continue to protect generated code.

Maintainability is continuous architectural hygiene. Refactor in response to
observed coupling, repeated behavior, or a feature that would otherwise land in
the wrong place.

## Areas to preserve

Retain the generated protocol models, Codex transport implementation,
SQL-first migrations, EF Core mappings, and current frontend technology.

The transport already has tests for concurrency, framing, failure, cancellation,
and process replacement. Preserve that investment while introducing boundaries
around it.

Reorganize files and projects where needed to enforce the four agreed boundaries.
Keep business behavior outside regenerated files. Generic repositories,
additional application services deployed over the network, and separate Outcomes
infrastructure still require demonstrated need beyond selecting this rewrite scope.

## Review validation

During the initial review:

- Frontend and test-tooling builds passed.
- The protocol-generation drift check passed.
- Backend dependency restore did not complete in the review environment.
  No .NET, HTTP integration, or PostgreSQL test results were claimed.
- No application code was changed.

Run the relevant existing tests and new contract tests for each implementation
increment. A documentation-only addition does not establish that the proposed
architecture has been implemented.

## Implementation checkpoint — 2026-09-19

Rechecked against `f0ebced`, including Codex 0.155.1. The user's failure policy
and conditional-sandbox clarification are implemented. No remaining conflict
blocks the agreed Codex implementation.

- **Application:** `Goblin.Core` owns Work transitions and snapshots;
  `Goblin.Application` owns transactional commands, connection reservations,
  conversation tracking, execution coordination, and recovery. PostgreSQL stores
  history, attempts, and command receipts. Wolverine's durable inbox/outbox is
  migrated explicitly; the runtime application cannot create schema. Claims
  commit before external starts. Repeated commands return their saved response.
- **Integrations:** Codex transport and account management moved out of Web into
  `Goblin.Integrations.Codex`. Shared contracts contain no generated protocol or
  HTTP types. Runtime capabilities and connection availability are explicit.
  Verification, account changes, active attempts, and cleanup share connection
  reservation rules. Optional GitHub device sign-in supplies repository access.
- **Hosting:** `Goblin.Execution` owns independent text workers and Kubernetes
  Agent Sandboxes for repository changes. Workers receive scoped inputs and
  credentials; repository pods have no application database mount or Kubernetes
  token. Durable claims, host identity fences, process/pod observation, confirmed
  cancellation, and cleanup recovery prevent blind redelivery. Cleanup failures
  require attention and explicit reconciliation. `/readyz` checks the enabled
  application database dependency independently of Codex.
- **UI:** `/work` loads authenticated persisted views, submits versioned commands,
  records decisions and approvals, displays execution provenance and recovery
  controls, and retains an unconfirmed command for safe resubmission. Conversation
  messages and tracking are durable. Sample data is separate and never drives
  live state. There are no fabricated assistant replies.
- **Deployment:** installers provision certificate-authenticated PostgreSQL and
  run the migration job before replacing the app. The execution namespace has
  restricted pod policy, separate RBAC, and public-only network egress. The image
  contains the worker, pinned Codex, Git, and the migration tool.

See [Work lifecycle](work-lifecycle.md), [execution and recovery](execution-hosting.md),
[the Work screen](work-experience-preview.md), and [database setup](database.md)
for the implemented contracts and operating instructions. The
[second-runtime experiment](second-runtime-experiment.md) is planned; no Claude,
Copilot execution, runtime handoff, fallback, or multi-agent capability is advertised.

Validation in this session includes real PostgreSQL/Wolverine tests, core and
protocol tests, browser journeys through the real Work APIs, existing HTTP and
deployment regressions, a container build, and an isolated Kubernetes Codex
startup/cleanup check. The final verification record below distinguishes these
from an authenticated model task and GitHub push, which require configured
service credentials and have not been exercised here.

### Verification record

- The complete solution builds without warnings. All 122 .NET tests pass,
  including 39 core cases, protocol/transport coverage, real PostgreSQL and TLS
  authentication, Wolverine dispatch/restart, a real storage outage, connection
  reservations, process fencing/cancellation, and cleanup recovery. None skipped.
- All 75 HTTP and deployment regression tests pass, including migration ordering,
  account compatibility, readiness, access security, and installer recovery.
- Browser coverage passes: the existing 12 connection/setup journeys and five
  durable Work journeys. Work coverage includes lost-response resubmission,
  refresh, another browser, mobile layout, and unavailable connection services.
- The pinned real Codex binary passes isolated credential storage, restart, and
  logout checks. Kubernetes smoke checks confirm non-root/read-only execution,
  no database credentials or service token, blocked private-network access, real
  Codex initialization, result observation, suspension, credential removal, and
  rejection of duplicate starts. These checks do not submit a model task.
- The container builds, generated protocol and Azure setup bundle are current,
  and secret scanning reports no findings. Temporary test databases and sandbox
  resources are removed after verification.

Authenticated model execution and GitHub branch publication remain live-service
validation limits. The second-runtime document is an experiment plan and CLI
interface inspection, not an implemented second adapter. Workspace archival,
formal Work classification, runtime switching, automatic fallback, and multiple
agents on one Work item remain deferred as described above.
