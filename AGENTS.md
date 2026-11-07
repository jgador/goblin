# Working agreement

Deliver the requested outcome through implementation, affected callers, affected
documentation, and relevant verification. Fix regressions caused by the change before
stopping. Make routine reversible choices within scope; ask when missing information
materially changes the outcome, authorization, or destructive scope. Continue
independent work.

Leave completed changes uncommitted for user review. Commit and push only when the user
explicitly invokes `$commit-push`; follow the
[commit and push skill](.agents/skills/commit-push/SKILL.md). Requests to implement,
fix, create a branch, or complete a task authorize editing and verification, not
committing or pushing. Autonomy instructions, tool permissions, passing checks, and
earlier commits do not grant commit or push authorization. Preserve existing staged
changes and leave new edits unstaged. Report the completed work and verification without
prompting the user to commit or push.

Follow user instructions over skill guidelines and respect each skill's triggers.
Discussing or editing a skill does not invoke it. If guidance blocks progress, identify
its file/section, explain the conflict and what is needed, without exposing confidential
content.

# Read guidance for the task

Use the [documentation index](docs/README.md) to select guidance for the task; it is not
a required reading list. Work changes need the [architecture](docs/architecture.md) and
[lifecycle](docs/work-lifecycle.md) rules; persistence changes need the
[database guide](docs/database.md). Choose checks with
[verification selection](docs/repository-layout.md#choosing-verification). Instruction
and skill changes follow the [agent workflow](docs/agent-workflow.md), including its
documentation maintenance policy. Check claims against current code and tests rather
than old plans or test reports.

For execution, recovery, or workspace storage, follow
[execution hosting](docs/execution-hosting.md) and [workspace lifecycle](docs/workspace-lifecycle.md).
Environment reads, sets, removals, and forwarding follow
[environment variables](docs/environment-variables.md); shared behavior and values follow
[contract values](docs/contract-values.md). C# construction and JSON contracts follow
[C# conventions](docs/formatting.md#c-construction-and-contracts); Rust under `tools/`
follows [Rust development](docs/rust-development.md). Workflow and required-check changes
follow [GitHub Actions](docs/github-actions.md).

# Project constraints

- Work lifecycle rules belong in `backend/src/Goblin.Core/Work/`, independent of
  HTTP, persistence, messaging, process configuration, and runtime SDKs or generated
  protocols. Adapters translate to Goblin-owned product types.
- Every failure requires core-owned attention, including temporary failures.
  Do not add automatic retries of failed Work. Reconcile uncertain execution
  before an explicit retry can dispatch a replacement attempt.
- Preserve agent and attempt identities and execution provenance. Agents outlive
  attempts; attempts must outlive HTTP requests. Runtime sessions are references,
  not Goblin's durable history. Codex is the current default integration.
- PostgreSQL owns durable history; Wolverine coordinates delivery. Commit state
  and dispatch intent atomically. Core claim checks require database concurrency
  enforcement and do not by themselves provide exactly-once external execution.
- Workspace files remain on the Work PVC. Never store filesystem archives in
  PostgreSQL; Git checkpoint records contain provenance metadata only. Suspending
  compute does not require archiving or authorize deleting the PVC.
- HTTP translates requests and failures; it must not own durable execution.
  Keep credentials and raw upstream errors out of public views and Work history.
- Follow the four behavioral boundaries and their compatibility/recovery
  contracts. Preserve the existing transport and protocol tests. Repository
  execution requires the isolation boundary in the architecture guide.
- Goblin is preproduction. Support only the current application, configuration,
  and installation formats; remove superseded paths instead of adding backward
  compatibility, migration shims, or in-place upgrade tooling. Development Goblin,
  PostgreSQL, and k3s installations can be recreated when formats change.
- Until the first production deployment, consolidate schema changes into
  `backend/database/migrations/0001_initial.sql`; breaking changes are allowed.
  Local development provisioning does not freeze this unreleased baseline.
  After production deployment, keep SQL migrations immutable. Regenerate EF
  mappings from the database and put custom behavior outside generated files.
- Preserve the frontend technology, visual design, branding, accessibility, and
  HTTP security. Preserve public property contracts, JSON behavior, and relied-upon
  record semantics unless the task calls for changes; identify external impact.
- Keep operational tooling Python-free and developer tools out of production.
- Azure installation is manual. Keep automation free of Azure login, live deployment
  tests, resource provisioning, and cloud cleanup. Preserve templates, installer
  assets, and offline checks for manual use.

# Verification and artifacts

Run relevant checks without repeated approval for local edit/test steps. Core changes
require `dotnet test backend/tests/Goblin.Core.Tests`; owning C#/Rust contract changes
require `make contracts-check`. Use the test-selection guide for other boundaries.
Repeat or broaden passing checks only for new changes, failures, unresolved risks,
or required coverage. Report failures and skips; PostgreSQL tests remain opt-in.
Claim persistence or authenticated runtime coverage only from actual integration checks.

Iterate with the required affected tests directly in WSL. When behavior depends on
containers or Kubernetes, run `make k3s-deploy` and verify the affected behavior in
this checkout's owned WSL k3s installation. Confirm the expected image is running;
readiness alone is not feature verification. Preserve data; reset only when explicitly
requested. Report performed checks and unverified behavior. See the linked
[verification guide](docs/repository-layout.md#choosing-verification) for selection.

Keep all temporary files created inside this repository under `.artifacts/` in
task/tool subdirectories, including scratch scripts, fixtures, test results,
reports, screenshots, traces, and logs. Configure test tools to use those paths.
