# Repository guidance

Goblin currently ships as one ASP.NET Core application with a TypeScript browser
UI. The backend targets .NET 10, repository tooling requires Node.js 24 or newer,
and Python 3 drives protocol generation and deployment tooling. Run repository
commands from the root so the pinned SDK, npm workspace, and shared lockfile are
used.

## Architecture

Follow [the architecture rewrite plan](docs/architecture-refactoring-plan.md).
The implemented boundary and its current limitations are documented in
[the Work lifecycle core](docs/work-lifecycle.md) and
[execution hosting](docs/execution-hosting.md).

- `backend/src/Goblin.Core/` owns product rules and types;
  `Goblin.Application/` owns durable orchestration; `Goblin.Execution/` owns
  text workers and repository sandboxes; integration projects adapt Codex and
  GitHub; `Goblin.Web/` composes the application and HTTP boundary.
- `frontend/src/` contains the connection and persisted Work experiences.
  Cross-application tests live in `tests/`, deployment assets in `deploy/`, and
  checked-in Codex schemas and generated protocol models under `backend/`.
- Work lifecycle rules belong in `backend/src/Goblin.Core/Work/`.
- Keep the core independent of HTTP, persistence, messaging, and runtime SDKs or
  generated protocols. Adapters translate to Goblin-owned product types.
- Every failure requires core-owned attention, including temporary failures.
  Do not add automatic retries of failed Work. Reconcile uncertain execution
  before an explicit retry can dispatch a replacement attempt.
- Preserve agent and attempt identities and execution provenance. Agents outlive
  attempts; attempts must outlive HTTP requests. Runtime sessions are references,
  not Goblin's durable history. Codex is the current default integration.
- Product IDs are positive C# `long` / PostgreSQL `bigint` values. Encode IDs and
  versions as decimal strings at the HTTP/browser boundary; JavaScript numbers
  cannot safely represent the full range.
- PostgreSQL owns durable history; Wolverine coordinates delivery. Commit state
  and dispatch intent atomically. Core claim checks require database concurrency
  enforcement and do not by themselves provide exactly-once external execution.
- HTTP translates requests and failures; it must not own durable execution.
  Keep credentials and raw upstream errors out of public views and Work history.
- Follow the four behavioral boundaries and their compatibility/recovery
  contracts. Preserve the existing transport and protocol tests. Repository
  execution requires the isolation boundary in the plan.
- Treat execution journals and host identity fences as recovery evidence. On
  Linux, a text worker is identified by PID, kernel start ticks, boot ID, and PID
  namespace; wall-clock process start time alone cannot prove that it stopped.
  Never replace an uncertain execution or delete a sandbox fence implicitly.
- Until the first real deployment, consolidate schema changes into
  `backend/database/migrations/0001_initial.sql`; breaking changes are allowed.
  Local development provisioning does not freeze this unreleased baseline.
  After deployment, keep SQL migrations immutable. Regenerate EF mappings from
  the database and put custom behavior outside generated files. Preserve the
  frontend technology, visual design, branding, accessibility, and HTTP security.
- Use regular constructors for class declarations and readonly fields for
  retained dependencies. Preserve existing property contracts and positional
  record declarations.

Generated Codex protocol files and EF entities are inputs to their respective
boundaries, not places for handwritten behavior. Regenerate protocol models with
`npm run protocol:generate`; follow [the database guide](docs/database.md) when
changing SQL or regenerating database-first EF mappings.

## Build and verification

Use `dotnet test backend/tests/Goblin.Core.Tests` for core changes. `npm test`
builds and runs protocol checks, .NET tests, HTTP tests, and deployment tests.
Real PostgreSQL tests are opt-in; report skips rather than claiming persistence
coverage. Run the relevant browser and real-runtime checks when integrating
those boundaries.

Useful root commands are `npm ci`, `npm run build`, `npm run typecheck`,
`npm run protocol:check`, `npm run test:browser`, and `npm run test:codex`.
The repository-local Codex hook formats TypeScript, Python, and C# after a turn;
manual formatting and setup are documented in [formatting setup](docs/formatting.md).
