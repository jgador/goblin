# Architecture and refactoring plan

Status: proposed; implementation has not started.

Reviewed on 2026-09-19 against
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

Make two preparatory refactors, followed by one small durable Work feature.
Preserve existing behavior during structural changes. Treat changes to
readiness and the introduction of durable execution as explicit behavior
changes with their own validation.

Do not perform a broad rewrite. Introduce abstractions when current or
near-term behavior demonstrates their value. File size and aesthetic
consistency alone are not reasons to refactor.

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
  integration. Introduce the shared execution contract with the first Work
  consumer, covering only the behavior that flow needs. Adding another runtime
  should not require rewriting Work lifecycle rules or public Work contracts.
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

The main open choices for brainstorming are connected:

| Choice | Provisional shape | Consequence to explore |
| --- | --- | --- |
| Agent identity and runtime choice | Assign Work to a stable Goblin agent with Codex as its initial runtime; show the runtime used in execution history. | The same coworker could use another runtime later. Separate agent identities tied to each product are another possible experience. |
| Context handoff | A change of runtime starts a new attempt with Goblin's persisted context and artifacts. | Work stays continuous, but runtime-private context is not assumed portable; a handoff may need a prepared summary. |
| Failure and fallback | Leave a failed or uncertain attempt visible and let the user choose a retry or another available runtime. | This preserves control over possible repeated actions; automatic fallback would need an explicit policy and recovery rules. |

These choices remain proposals. The request to support other agents does not
by itself settle switching behavior, automatic fallback, or collaboration
between multiple agents on one Work item.

## Current evidence

- The live application manages workspace access, Codex authentication, and
  connection verification. There are no Claude or GitHub Copilot integrations.
- [Authentication](../backend/src/Goblin.Web/Auth/Authentication.cs) owns login,
  account state, prompt validation, execution, and the queue that serializes
  these operations.
- [GoblinApplication](../backend/src/Goblin.Web/GoblinApplication.cs) combines
  registration, HTTP security, request parsing, static assets, endpoints, and
  Codex recovery.
- The public [HTTP contracts](../backend/src/Goblin.Web/Http/ApiContracts.cs)
  expose the generated Codex `PlanType`. Codex transport and prompt execution
  also throw the HTTP-oriented `PublicError`.
- The [Work UI](../frontend/src/work/app.ts) uses simulated state and replies.
  The initial [work_items table](../backend/database/migrations/0001_work_items.sql)
  contains only `id` and `objective`; it is not connected to the preview.
- PostgreSQL persistence and migration tooling exist. Wolverine is not yet
  integrated.
- `/readyz` depends entirely on Codex readiness, and the
  [Kubernetes readiness probe](../deploy/auth/sandbox.yaml) uses it.
- Codex currently runs as a child of the web application in the same pod and
  OS user. The application database certificate mount is not an execution
  isolation boundary.

## 1. Separate connection management from execution

The existing authentication design fits the connection preview, but durable
Work execution should have its own owner.

Give connection management and connection verification explicit
responsibilities. Keep Codex login messages, thread/turn handling, and protocol
types inside the Codex integration. Map runtime failures to application
failures, then map those failures to HTTP responses at the HTTP boundary.

Replace the generated Codex `PlanType` in the public HTTP contract with a
Goblin-owned representation while preserving the existing wire format.
Keep that account summary specific to the Codex connection; future integrations
may have different authentication methods and account metadata.

Preserve the existing coordination contract:

- Overlapping prompts are rejected.
- Account changes wait for the current prompt to finish.
- Cancellation and timeouts clean up runtime operations.
- Raw upstream errors and credentials do not reach the browser.

Extracting methods into separate services must not remove the shared
coordination that protects these behaviors.

Introduce the durable agent-execution interface alongside its first Work
consumer, when the required behavior is concrete. Today's deliberately
restricted connection-check prompt should not define the future execution
model. Use Goblin-owned inputs, execution events, results, and failures at that
boundary; keep Codex thread and turn messages inside its adapter.

Use the existing
[HTTP authentication and prompt tests](../tests/integration/auth.test.ts) and
[Codex transport tests](../backend/tests/Goblin.Tests/CodexClientTests.cs) as
regression coverage. Add focused tests where the new boundaries expose an
important contract.

## 2. Keep composition in the host and separate application health

`GoblinApplication.cs` is currently small enough to understand. The reason to
extract responsibilities is that Work endpoints and orchestration would
otherwise accumulate there.

Move connection endpoint mapping and runtime lifecycle management into their
owning modules. Keep shared HTTP security and error translation centralized.
Avoid introducing a generic module framework.

Separate application readiness from runtime availability. Goblin should remain
available to inspect Work and repair connections when Codex fails. Runtime
availability should remain visible as a separate capability status for each
configured integration and connection. A failed runtime should not hide Work
history or prevent dispatch through another healthy, suitable runtime when one
is implemented and configured.

Make the readiness change separately from mechanical extraction. Update the
Kubernetes probe, installer expectations, and relevant tests together. Define
application readiness around the services actually required by the enabled
application features.

Validate that a runtime failure does not prevent the UI and application APIs
from exposing state and recovery controls.

## 3. Establish the architecture through one durable Work flow

This is a product increment beyond the preparatory refactors.

Implement the smallest flow that can:

1. Create and retrieve Work independently of a conversation.
2. Assign it to a stable agent identity.
3. Record a distinct execution attempt.
4. Dispatch execution through Wolverine.
5. Persist the result or failure and expose it to the UI.

Start with one Goblin agent and Codex as its runtime. Keep Work identity, agent
identity, execution-attempt identity, and runtime session identity distinct.
Record the runtime selected for the attempt explicitly, even while Codex is
the only implementation. Keep Codex thread and turn references within that
runtime's execution metadata rather than using them as Work or agent IDs.

Establish these contracts:

- Work state and dispatch intent commit atomically.
- Closing a browser does not cancel accepted durable Work.
- A retry creates or resumes an explicitly tracked attempt.
- Duplicate delivery cannot blindly start another execution.
- Uncertain execution after a crash has an explicit recovery path.
- A completed runtime turn does not automatically mean the intended Work
  outcome is achieved.

When runtime switching is introduced, the proposed handoff creates a new attempt
on the same Work. It cannot silently resume a Codex session through another
integration. Resolve uncertain external actions before dispatching a replacement
attempt. Switching is not required for the first flow using only Codex.

Use short database transactions around state changes. Do not hold a transaction
open throughout agent execution. Wolverine coordinates delivery and retries;
application rules determine which transitions and retries are valid.
Durable messaging must not be treated as a guarantee that external actions
happen exactly once.

The existing `/api/prompt` can retain its request-cancellation behavior for
connection verification.

As this flow reaches the UI, replace simulated Work transitions with server
commands and persisted views. This is the useful point to separate preview
fixtures from rendering. Keep UI navigation and presentation state local, while
the server owns durable Work state.

Before enabling repository actions, give executions their own sandbox.
Execution isolation is compatible with keeping the application a modular
monolith.

Add integration coverage for persisted state transitions, atomic dispatch,
duplicate delivery, restart recovery, and browser disconnection. Exercise real
PostgreSQL for the persistence and transaction contracts.

Use a test double at the runtime boundary to check that Work orchestration
does not require Codex protocol types or thread semantics. Before enabling a
second real integration, validate its advertised capabilities and exercise
context handoff and recovery through that integration. A test double alone
does not establish compatibility with Claude or GitHub Copilot.

## Repository principles

Document these principles in the repository and reference them from
`AGENTS.md` when the implementation establishes the boundaries:

1. Work lifecycle rules belong with Work.
2. Runtime-specific types stay inside runtime integrations.
3. HTTP translates requests and failures; it does not own durable execution.
4. PostgreSQL owns Goblin's durable history; runtime sessions are references to
   execution context.
5. Agents outlive attempts; attempts outlive HTTP requests.
6. Add boundaries when behavior needs them, and preserve existing contracts
   during extraction.
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

Do not add generic repositories, additional application services deployed over
the network, separate Outcomes infrastructure, or a broad folder reorganization
without demonstrated need. Keep business behavior outside regenerated files.

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
