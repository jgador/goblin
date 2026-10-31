# Architecture

Goblin is a modular monolith built around durable Work. Agents are stable Goblin
identities; runtimes execute attempts for those agents. Codex is the only
implemented runtime. PostgreSQL owns product history, Wolverine coordinates
delivery, and the browser shows saved state and submits commands.

The four behavioral boundaries are the first four sections below.

## Work and application coordination

`backend/src/Goblin.Core/Work/` owns lifecycle transitions, decisions, attention,
attempts, and result approval. Core types have no HTTP, persistence, messaging,
process-configuration, runtime SDK, or generated-protocol dependencies.
`Goblin.Application` turns commands into transactional changes and coordinates
connections, execution, recovery, and conversations. `Goblin.Persistence` holds
the SQL-first PostgreSQL mappings.

Rules:

- Work, agent, attempt, and runtime-session identities are separate. Agents
  outlive attempts, and accepted attempts outlive HTTP requests. Runtime sessions
  are references, not Goblin's history.
- Work state, command receipts, and dispatch intent commit atomically. Claims need
  database concurrency enforcement; a claim does not guarantee exactly-once
  external execution.
- Every observed failure requires core-owned attention, including temporary
  failures. Message redelivery never authorizes another execution. Only an
  explicit retry creates a new attempt, after uncertain execution is reconciled.
- A storage failure must not produce a fake saved transition.

States, commands, and recovery rules are in [Work lifecycle](work-lifecycle.md).
Storage is in [PostgreSQL](database.md).

## Connections and runtime adapters

`Goblin.Contracts` holds Goblin-owned integration contracts.
`Goblin.Integrations.Codex` owns Codex authentication, transport, protocol
translation (generated types are in `Goblin.Protocol`), model discovery, and
sessions. GitHub, Slack, and ChatGPT have their own `Goblin.Integrations.*`
projects. Raw upstream errors and credentials stay out of public views and Work
history. A connection rejects overlapping verification prompts, and rejects
account changes while an attempt holds it.

A second runtime is a proposal, not supported. It must map to Goblin inputs,
events, results, and safe failures without changing core rules or assuming the
Codex App Server protocol. A possible first experiment is a GitHub Copilot CLI
adapter on a test repository; it must prove fencing, failure attention,
cancellation, credential cleanup, and recovery before any capability is
advertised. Never add automatic retry or fallback. See
[Codex integration](codex-integration.md) and [authentication](authentication.md).

## Execution, isolation, and recovery

`Goblin.Execution` and `Goblin.Execution.Codex` own workers, process and pod
observation, cancellation, cleanup, and workspace hosting. A text question does
not use a repository sandbox.

The isolation boundary: repository execution requires a GitHub selection, a
Goblin-owned grant, and the configured Kubernetes sandbox. It never falls back
to the text-only host. Repository pods have no application database credentials
or Kubernetes service token, run under the `restricted` pod security standard,
and have public-only egress. Goblin brokers Git operations and rechecks the
grant.

Repository Work keeps its files on one PVC; PostgreSQL stores checkpoint
metadata only. Closing a browser does not cancel accepted Work. `/readyz` checks
only the application database (when Work is enabled), not Codex.

See [execution hosting](execution-hosting.md), [workspace lifecycle](workspace-lifecycle.md),
and [repository authorization](git-repository-intent.md).

## Browser commands and persisted views

`Goblin.Web` owns HTTP translation, owner access, request validation, same-origin
security, cookies, and static assets. Endpoints submit application commands and
map saved views and failures to HTTP; they do not own durable execution. Public
request and response classes are in `Goblin.Web/Http/Contracts/` with explicit
`JsonPropertyName` attributes.

The browser shows server-owned Work state and sends versioned commands. An
unconfirmed submission keeps its command ID for resubmission. Selection, filters,
disclosures, and drafts are browser-only state. See the
[Work interface](work-centered-ui.md) and [frontend plan](typescript-refactoring.md).

## Verification boundaries

Use [test selection](repository-layout.md#choosing-verification) to choose checks.
Fixtures and protocol tests cannot prove PostgreSQL transactions, authenticated
model execution, GitHub publication, or a second runtime; those need actual
integration runs. Report opt-in test skips.
