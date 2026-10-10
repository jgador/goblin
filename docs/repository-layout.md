# Repository layout

Goblin has separate backend and frontend source trees and ships as one ASP.NET
Core application. Run the commands below from the repository root.

```text
backend/
  src/Goblin.Core/         Work lifecycle rules and Goblin-owned execution types
  src/Goblin.Contracts/   Public account/runtime contracts and environment names
  src/Goblin.Application/ Durable commands, conversations, dispatch, and recovery
  src/Goblin.Execution/   Runtime hosts, process evidence, and isolated sandboxes
  src/Goblin.Execution.Codex/     Codex worker entry points for those hosts
  src/Goblin.Integrations.Codex/   Codex account, transport, and Work adapter
  src/Goblin.Integrations.GitHub/  GitHub device sign-in and private credentials
  src/Goblin.Web/          ASP.NET Core composition and HTTP adapters
    Access/               Workspace access, passwords, and browser sessions
    Http/                 HTTP contracts and public errors
  src/Goblin.Protocol/     Generated Codex protocol models and serialization
  src/Goblin.Persistence/  Database-first EF Core context, entities, and registration
  database/migrations/    Ordered SQL schema changes
  tools/Goblin.Database/  SQL migration runner
  tools/Goblin.Database.Scaffolding/  Developer-only EF tooling host
  tests/                  .NET tests and the test-only application host
  schemas/codex/           Checked-in inputs to protocol generation
  schemas/kubernetes/      Pinned schemas and selected execution fields
  scripts/                 C# protocol and Kubernetes file-based generators
  Goblin.slnx              .NET solution
  Directory.Build.props   Shared .NET build settings
tools/goblinctl/            Native operator CLI and shared installation code
tools/xtask/               Developer and release tooling
Cargo.toml                 Rust workspace; toolchain and dependencies are pinned
Makefile                   Repository task runner for builds, checks, and local operations
.config/nextest.toml        Rust test-runner profiles
.vscode/                   Rust/TOML editing and native debugging configuration
frontend/
  src/connection/          Connection screen: HTML, TypeScript, and CSS
  src/work/                Persisted Work UI: HTML, TypeScript, and CSS
  src/api/contracts.ts     Goblin HTTP types used by the UI and application tests
  public/assets/           Static assets copied into the browser build
  scripts/                 Browser build helpers
  package.json             Standalone frontend npm package and TypeScript dependency
  tsconfig.json            Browser compiler settings; no Node.js types
tests/
  integration/             HTTP tests against the C# test host
  e2e/                     Playwright browser journeys
  deployment/              Azure bootstrap and installation tests
  support/                 Backend launcher and browser test server
  fixtures/                Fake Codex process used by .NET and application tests
config/environment.mts     Documented environment definitions for Node tooling
deploy/                    Azure infrastructure and Kubernetes manifests
assets/branding/           Original artwork, exports, and packaged fonts
docs/                      Setup, architecture, and development documentation
scripts/                   Repository checks and test-tool build helpers
```

See [environment variables](environment-variables.md) for the C#, Rust, and
TypeScript catalogs, shared semantics, and contributor guidance.
The [TypeScript refactoring review](typescript-refactoring.md) records the current
browser boundaries, file inventory, and next implementation steps.

The root `Makefile` coordinates builds, checks, tests, and local operations.
The root `package.json` has no task scripts or npm workspaces; it only pins
non-frontend Node tooling (Playwright, Prettier, TypeScript, Node types) and the
Codex runtime distribution. Its `package-lock.json` preserves the runtime and
tool pins. `frontend/package.json` and `frontend/package-lock.json` independently
own frontend dependencies and npm scripts. No backend or test tooling is added
to the frontend package. `make install` installs both locked Node packages and
fetches locked Rust dependencies; first install the Rust helpers with
`make setup-rust` and load the Cargo environment as described below. The root
`tsconfig.json` compiles Node.js test tooling, while `frontend/tsconfig.json`
compiles browser source independently. `global.json` stays at the root so the
pinned .NET SDK applies to commands run from either the root or `backend/`.

Node.js 24 or newer runs the `.mts` build helpers, secret-scanning scripts, and
fake Codex fixture directly. `.mts` is TypeScript with explicit ES module
semantics, including when a test copies a script outside the repository. These
scripts do not depend on compiled output or an installed TypeScript runner.
`tsconfig.scripts.json` checks them without emitting files and permits only
erasable TypeScript syntax. Native Node execution strips types without checking
them; `make typecheck-scripts` performs that check as part of the full build
and `make typecheck`.

The installer page in `deploy/azure/setup/app.js` remains JavaScript because the
native goblinctl setup server packages it directly for the browser. Moving its source
to TypeScript would require generating and checking the packaged JavaScript.

## Build and test

Install GNU Make, Node.js 24+, and the .NET SDK selected by `global.json`. On
Ubuntu/WSL, `build-essential` includes Make. Then run:

```bash
make setup-rust
source "$HOME/.cargo/env"
make install
```

Use `make help` to list targets. Workflows are serialized even with `make -j`
because cleaning compiled tools and copying frontend assets share build outputs.
Pass tool options through `ARGS`, for example
`make test-browser ARGS="tests/e2e/setup.spec.ts --headed"` or
`make local-start ARGS="--http-port 8888"`. Rust selectors use `CARGO_ARGS`, for
example `make test-rust CARGO_ARGS="-p goblinctl"`.

Frontend-only work continues inside its own package:

```bash
cd frontend
npm ci
npm run build
npm run typecheck
```


| Command | Purpose |
| --- | --- |
| `make install` | Install both locked Node packages and fetch locked Rust dependencies |
| `make setup-rust` | Install the pinned Rust development tools |
| `make format-rust` | Format Rust using Codex conventions |
| `make lint-rust` | Lint the Rust workspace |
| `make test-rust` | Run Rust tests with nextest and then documentation tests |
| `make check` | Run Rust formatting/lints, the full offline test suite, and type checks |
| `make format` | Format TypeScript, Rust, and handwritten C# |
| `make local-start` | Start the full local installation with existing safety checks |
| `make local-status` | Report installation status without changing it |
| `make local-reset ARGS=--yes` | Explicitly reset only the owned local installation |
| `make build` | Build native tools, frontend assets, test tooling, and the .NET solution |
| `make dev` | Build, provision the local password on first run, and start the application |
| `make setup-password` | Choose and confirm a password; save only its verifier in `.goblin-secrets/` |
| `make build-assets` | Build only the frontend |
| `npm --prefix frontend run build` | Run the frontend package build directly |
| `make build-backend` | Build the .NET solution using any existing frontend output |
| `make typecheck` | Check browser source, test tooling, and direct Node scripts; build .NET |
| `make typecheck-scripts` | Check directly executed TypeScript scripts without building |
| `make test` | Build, check generated contracts/protocols, and run Rust, .NET, frontend, HTTP, release, and offline deployment tests |
| `make test-browser` | Build and run Playwright journeys |
| `make test-codex` | Build and check the pinned Codex binary's local storage/transport with synthetic credentials; no authenticated model task |
| `make protocol-generate` | Regenerate C# models from the checked-in schemas |
| `make protocol-check` | Verify that generated models match the schemas |
| `make kubernetes-generate` | Regenerate selected Kubernetes and Agent Sandbox models |
| `make kubernetes-check` | Verify those models match the pinned schemas and selection |

After building frontend assets, publish with:

```sh
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish
```

### Choosing verification

Choose checks for the behavior and boundaries changed. Local edits, fixture tests,
and fixing regressions from the requested change can proceed without approval at
each step. Reuse passing results for unchanged content; broaden or repeat checks
when new changes, failures, unresolved risks, or required coverage justify it.
Use meaningful regression tests for behavior changes rather than assertions that
only repeat the implementation.

| Change | Relevant verification |
| --- | --- |
| Prose or agent instructions only | Review the diff, relative links/anchors, and commands against their owners; run `git diff --check`. Runtime tests are unnecessary unless executable behavior also changes. |
| Work core | `dotnet test backend/tests/Goblin.Core.Tests`; add boundary checks below when callers or integrations change. |
| Other C# behavior | The affected project under `backend/tests/`; use `make test` when changes span application, transport, HTTP, or deployment contracts. Preserve existing protocol and transport coverage. |
| Shared C#/Rust contract values | Generate and validate using [the contract guide](contract-values.md#cross-language-changes), including `make contracts-check`. |
| Rust tooling | `make format-rust`, `make lint-rust CARGO_ARGS="-p <crate>"`, and `make test-rust CARGO_ARGS="-p <crate>"`. The last command includes documentation tests; review snapshots before accepting them. |
| Browser code or UI behavior | `npm --prefix frontend run typecheck`, affected frontend tests, and relevant `make test-browser ARGS="tests/e2e/<journey>.spec.ts"` journeys when integrating the browser boundary. Inspect the changed interaction and visual result. |
| Schema, persistence, dispatch, or database concurrency | Affected .NET tests plus real PostgreSQL checks with the explicit setup in [the database guide](database.md#application-configuration-and-verification). |
| Codex transport or execution hosting | Existing transport/protocol tests and `make test-codex` where relevant; authenticated execution/isolation needs the real-runtime checks described in [execution hosting](execution-hosting.md#verification-limits). |
| Installer or deployment contracts | Offline generation and deployment checks in `make test`; consult [formatting deployment assets](formatting.md#deployment-assets) for generated inputs. Azure installation stays manual. |

`make test` is the broad offline suite with real PostgreSQL checks gated by explicit
test configuration. `make check` adds Rust formatting/lints and type checks. These
are useful integration/CI gates, not prerequisites for every local edit. Required
CI checks still apply; scoped local results do not imply that CI ran.

The ordinary HTTP/browser harness uses runtime fixtures. `make test-codex` uses the
real pinned executable with an isolated home and synthetic credentials, without
submitting a model task. Neither establishes authenticated execution coverage.
PostgreSQL and database-backed browser journeys can skip without their test setup;
report those skips. Use the documented test configuration for live checks and keep
credentials private. A missing live setup leaves that coverage unverified; complete
the available checks and report the gap rather than claiming persistence or runtime
verification. Do not add Azure login, provisioning, live deployment, or cleanup to
these checks.

## Build output and browser routes

The frontend build writes JavaScript, HTML, CSS, and static assets to
`frontend/dist/`. The backend project copies this directory into its output and
published `wwwroot/`. Browser URLs are mapped explicitly in
`backend/src/Goblin.Web/GoblinApplication.cs`: `/`, `/app.js`, and `/styles.css`
serve the `connection/` files; `/work` and `/work/*` serve the `work/` files.
Runtime assets keep their `/assets/*` URLs.

The root `dist/` holds compiled test tooling. It is separate from the frontend
build and is not included in the application image. Both output directories,
.NET `bin/` and `obj/`, `.artifacts/`, and local runtime data remain ignored by Git.
The root Dockerfile builds frontend assets before publishing the backend, using
the repository root as its build context.

Keep original artwork in `assets/branding/` and copy only the assets used by the
browser into `frontend/public/assets/`. Codex schemas belong under
`backend/schemas/codex/`; Goblin account/runtime contracts belong to `Goblin.Contracts`; Work command/view
contracts belong to `Goblin.Application/Work`. Browser types describe these public
Goblin APIs, including `frontend/src/api/contracts.ts` for connection setup. They describe different APIs.

For PostgreSQL setup, SQL changes, and regenerating the EF Core classes after
adding tables, see the [database guide](database.md).

The [Work lifecycle](work-lifecycle.md) is used by the live application. Core and
public contracts are independent of runtime protocols and HTTP. Orchestration
depends on the shared execution host contract; composition selects the concrete
Codex/text/sandbox adapters. [Execution hosting](execution-hosting.md) documents
configuration, reservations, recovery, and validation limits.
