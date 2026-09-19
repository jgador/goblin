# Architecture and verification

Follow [the architecture rewrite plan](docs/architecture-refactoring-plan.md).
The implemented boundary and its current limitations are documented in
[the Work lifecycle core](docs/work-lifecycle.md).

- Work lifecycle rules belong in `backend/src/Goblin.Core/Work/`.
- Keep the core independent of HTTP, persistence, messaging, and runtime SDKs or
  generated protocols. Adapters translate to Goblin-owned product types.
- Every failure requires core-owned attention, including temporary failures.
  Do not add automatic retries of failed Work. Reconcile uncertain execution
  before an explicit retry can dispatch a replacement attempt.
- Preserve agent and attempt identities and execution provenance. Agents outlive
  attempts; attempts must outlive HTTP requests. Runtime sessions are references,
  not Goblin's durable history. Codex is the current default integration.
- PostgreSQL owns durable history; Wolverine coordinates delivery. Commit state
  and dispatch intent atomically. Core claim checks require database concurrency
  enforcement and do not by themselves provide exactly-once external execution.
- HTTP translates requests and failures; it must not own durable execution.
  Keep credentials and raw upstream errors out of public views and Work history.
- Follow the four behavioral boundaries and their compatibility/recovery
  contracts. Preserve the existing transport and protocol tests. Repository
  execution requires the isolation boundary in the plan.
- Keep SQL migrations immutable, regenerate EF mappings from the database, and
  put custom behavior outside generated files. Preserve the frontend technology,
  visual design, branding, accessibility, and HTTP security.

Use `dotnet test backend/tests/Goblin.Core.Tests` for core changes. `npm test`
builds and runs protocol checks, .NET tests, HTTP tests, and deployment tests.
Real PostgreSQL tests are opt-in; report skips rather than claiming persistence
coverage. Run the relevant browser and real-runtime checks when integrating
those boundaries.
