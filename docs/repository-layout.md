# Repository layout

Goblin has separate backend and frontend source trees and ships as one ASP.NET
Core application. Run the commands below from the repository root.

```text
backend/
  src/Goblin.Core/                  Work lifecycle rules and Goblin-owned execution types
  src/Goblin.Contracts/             Public account/runtime contracts and environment names
  src/Goblin.Application/           Durable commands, conversations, dispatch, and recovery
  src/Goblin.Execution/             Runtime hosts, process identity, and isolated sandboxes
  src/Goblin.Execution.Codex/       Codex worker entry points for those hosts
  src/Goblin.Integrations.Codex/    Codex account, transport, and Work adapter
  src/Goblin.Integrations.ChatGpt/  ChatGPT plugin token and OAuth callback validation
  src/Goblin.Integrations.GitHub/   GitHub device sign-in and private credentials
  src/Goblin.Integrations.Slack/    Slack setup, Socket Mode, and thread delivery
  src/Goblin.Web/                   ASP.NET Core composition and HTTP adapters
  src/Goblin.Protocol/              Generated Codex protocol models and serialization
  src/Goblin.Persistence/           Database-first EF Core context and entities
  database/migrations/              SQL schema migrations
  tools/Goblin.Database/            SQL migration runner
  tools/Goblin.Database.Scaffolding/  Developer-only EF tooling host
  tests/                            .NET tests and the test-only application host
  schemas/                          Pinned Codex and Kubernetes inputs to code generation
  scripts/                          C# protocol and Kubernetes generators, EF scaffolding
  Goblin.slnx                       .NET solution
frontend/
  src/                              Browser source by screen, plus shared api/ contracts
  public/assets/                    Static assets copied into the browser build
  scripts/                          Browser build helpers
tools/goblinctl/                    Native operator CLI and shared installation code
tools/xtask/                        Developer and release tooling (`cargo xtask`)
tests/                              Node, HTTP, deployment, and Playwright tests with shared support
website/                            Standalone static website
config/                             Node environment catalog and CI verification selection
deploy/                             Azure templates, Kubernetes manifests, and installer inputs
assets/                             Original artwork, exports, and fonts
scripts/                            Repository checks, CI, release, and maintenance scripts
docs/                               Setup, architecture, and development documentation
```

See [environment variables](environment-variables.md) for the C#, Rust, and
TypeScript catalogs. The [TypeScript refactoring review](typescript-refactoring.md)
lists open browser refactoring steps.

The root `Makefile` holds shared builds, checks, tests, and release tooling. It
includes `Makefile.dev` for local application, database, website, and Docker
commands. Always run `make <target>` from the repository root; `make help` lists
targets from both files. Targets use `<scope>-<action>[-<qualifier>]`, such as
`backend-build` or `secrets-scan-staged`. Repository-wide workflows keep bare
names: `install`, `build`, `typecheck`, `check`, `test`, and `format`. Use
`reset` for a fresh environment, `clean` to remove outputs or caches, and `stop`
to halt services.

The root `package.json` has no task scripts or npm workspaces. It pins
non-frontend Node tooling (Playwright, Prettier, TypeScript, Node types) and the
Codex runtime. `frontend/package.json` and its lockfile own frontend dependencies
and npm scripts; do not add backend or test tooling to it. The root
`tsconfig.json` compiles Node test tooling into the root `dist/`, while
`frontend/tsconfig.json` compiles browser source without Node types. `global.json`
stays at the root so the pinned .NET SDK applies from the root and from `backend/`.

`website/` has its own package manifest and lockfile. Application install, build,
and test commands do not include it, and the application Docker build excludes
it. See the [website guide](../website/README.md).

Node.js 24 or newer runs the `.mts` scripts, build helpers, and fake Codex fixture
directly, without compiled output. Native Node execution strips types without
checking them; `make scripts-typecheck` checks them through `tsconfig.scripts.json`,
which permits only erasable TypeScript syntax.

`deploy/azure/setup/app.js` stays JavaScript because goblinctl embeds it directly
for the browser.

## Build and test

Install GNU Make, Node.js 24+, and the .NET SDK selected by `global.json`. On
Ubuntu/WSL, `build-essential` includes Make. Then run:

```bash
make rust-setup
source "$HOME/.cargo/env"
make install
```

Workflows run serially even with `make -j`, because they share build outputs.
Pass tool options through `ARGS`, for example
`make browser-test ARGS="tests/e2e/setup.spec.ts --headed"` or
`make local-start ARGS="--http-port 8888"`. Rust selectors use `CARGO_ARGS`, for
example `make rust-test CARGO_ARGS="-p goblinctl"`.

Frontend-only work can stay inside its package:

```bash
cd frontend
npm ci
npm run build
npm run typecheck
```

| Command | Purpose |
| --- | --- |
| `make install` | Install both locked Node packages and fetch locked Rust dependencies |
| `make build` | Build native tools, frontend assets, test tooling, and the .NET solution |
| `make typecheck` | Check generated contract values, browser source, test tooling, and direct Node scripts; build .NET |
| `make test` | Run the offline suite: script and contract tests, builds, Rust, .NET, frontend, HTTP, release, and deployment tests, and generated-model checks |
| `make check` | Rust formatting and lint checks, `make test`, and `make typecheck` |
| `make format` | Format TypeScript, Rust, and handwritten C# |
| `make frontend-build` / `make frontend-test` | Build the frontend / run the compiled frontend tests |
| `make backend-build` / `make backend-test` | Build the .NET solution with existing frontend output / run .NET tests |
| `make browser-test` | Build and run Playwright journeys |
| `make codex-test` | Check the pinned Codex binary's local storage and transport with synthetic credentials; no authenticated model task |
| `make protocol-generate` / `make protocol-check` | Regenerate / check C# Codex protocol models |
| `make kubernetes-generate` / `make kubernetes-check` | Regenerate / check selected Kubernetes and Agent Sandbox models |
| `make ci-test` | Test CI change selection and the required-result policy |
| `make dev-start DB_PROFILE=wsl` | Build and start the application with the selected PostgreSQL profile and local owner password |
| `make artifacts-clean` | Delete everything in `.artifacts/` except its `.gitkeep` |

Owners of other targets: [Rust development](rust-development.md),
[database guide](database.md) (`database-*`),
[local installation](../deploy/local/README.md) (`local-*`, `dev-reset`,
`password-set`), [ChatGPT local testing](chatgpt-local-testing.md) (`relay-*`),
[Chisel](../deploy/chisel/README.md) (`chisel-check`),
[website guide](../website/README.md) (`website-*`),
[secret scanning](secret-scanning.md) (`secrets-*`), and
[agentic maintenance](agentic-maintenance.md) (`maintenance-test`).

After building frontend assets, publish with:

```sh
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish
```

### Docker builds

`make docker-build` builds and times the `linux/amd64` image
`goblin-platform:local` with plain progress. Pass build options through `ARGS`,
such as `make docker-build ARGS=--no-cache`.

`--no-cache` reruns build instructions but does **not** empty persistent cache
mounts. Removing the image also keeps the builder cache.
`make docker-clean-images` removes image tags whose repository name starts with
`goblin`; containers that use an image can block its removal.
`make docker-clean-cache` prunes all unused cache of the default builder, for
**all projects**; loaded base images remain.

The Dockerfile copies all of `backend/src` before the web restore, so new
production C# projects need no extra copy step, and any backend source change
reruns the restore.

For a cold-build benchmark, create a fresh builder **for every sample** and
export to a new archive:

```bash
(
    set -euo pipefail
    benchmark_dir=".artifacts/docker-benchmark/$(date +%Y%m%d-%H%M%S)-$$"
    benchmark_builder="goblin-cold-$$"
    mkdir -p "$benchmark_dir"
    docker buildx create --name "$benchmark_builder" --driver docker-container \
        --driver-opt image=moby/buildkit:v0.26.2
    trap 'docker buildx rm --force "$benchmark_builder"' EXIT
    docker buildx inspect --bootstrap "$benchmark_builder" > "$benchmark_dir/builder.txt"
    docker buildx du --builder "$benchmark_builder" > "$benchmark_dir/cache-before.txt"
    /usr/bin/time -f 'elapsed_seconds=%e\nexit_code=%x' -o "$benchmark_dir/time.txt" \
        docker buildx build --builder "$benchmark_builder" --platform linux/amd64 \
        --no-cache --pull --progress=plain --provenance=false \
        --output "type=docker,dest=$benchmark_dir/image.tar" \
        --tag goblin-platform:benchmark --file Dockerfile . \
        > "$benchmark_dir/build.log" 2>&1
    cat "$benchmark_dir/time.txt"
)
```

Builder startup is outside the timer. Use `--file` to select each Dockerfile
variant. Run at least three samples per variant in alternating order on an idle
machine, and compare medians and ranges. Check that each `cache-before.txt`
reports `0B` and that base-image digests match across samples. Network and host
caches still affect results.

### Choosing verification

Choose checks for the behavior and boundaries you changed. Reuse passing results
for unchanged content; repeat or broaden checks for new changes, failures,
unresolved risks, or required coverage. Write regression tests that check
behavior, not tests that only repeat the implementation.

| Change | Relevant verification |
| --- | --- |
| Prose or agent instructions only | Review the diff, relative links/anchors, and commands against their owners; run `git diff --check`. |
| Work core | `dotnet test backend/tests/Goblin.Core.Tests`; add the boundary checks below when callers or integrations change. |
| Other C# behavior | The affected project under `backend/tests/`; use `make test` when changes span application, transport, HTTP, or deployment contracts. Keep existing protocol and transport coverage. |
| Shared C#/Rust contract values | Follow [the contract guide](contract-values.md#cross-language-changes), including `make contracts-check`. |
| Rust tooling | `make rust-format`, `make rust-lint CARGO_ARGS="-p <crate>"`, and `make rust-test CARGO_ARGS="-p <crate>"`. |
| Browser code or UI behavior | `npm --prefix frontend run typecheck`, `make frontend-test`, and the relevant `make browser-test ARGS="tests/e2e/<journey>.spec.ts"` journeys. Inspect the changed interaction and visual result. |
| Standalone website | `npm --prefix website run check`, then inspect the affected pages with `make website-start`. |
| Schema, persistence, dispatch, or database concurrency | Affected .NET tests plus real PostgreSQL checks set up as in [the database guide](database.md#application-configuration-and-verification). |
| Codex transport or execution hosting | Existing transport/protocol tests and `make codex-test` where relevant; authenticated execution and isolation need the real-runtime checks in [execution hosting](execution-hosting.md#verification-limits). |
| Installer or deployment contracts | Offline generation and deployment checks in `make test`; see [deployment assets](formatting.md#deployment-assets). Azure installation stays manual. |

`make test` is the broad offline suite; real PostgreSQL checks run only with
explicit test configuration. `make check` adds Rust formatting and lint checks
and type checks. Neither is required for every local edit. CI selects
verification groups from changed files using
[`config/ci-verification.json`](../config/ci-verification.json); see
[GitHub Actions](github-actions.md#ci-selection-and-required-result). Local
results do not mean CI ran.

Offline installer tests run as an ordinary user. `make tools-build` builds the
test-only `host_fixture` Rust example, which runs host configuration against a
private filesystem with mocked infrastructure commands. It is not shipped. The
production `goblinctl db host` command still requires root.

The HTTP and browser tests use runtime fixtures. `make codex-test` uses the real
pinned executable with synthetic credentials and submits no model task. Neither
covers authenticated execution. PostgreSQL and database-backed browser journeys
skip without their test setup; report those skips. Keep live-test credentials
private. Without a live setup, report that coverage as unverified instead of
claiming persistence or runtime verification. Do not add Azure login,
provisioning, live deployment, or cleanup to these checks.

## Build output and browser routes

The frontend build writes JavaScript, HTML, CSS, and static assets to
`frontend/dist/`. `Goblin.Web` copies this directory into its output and
published `wwwroot/`. Browser URLs are mapped explicitly in
`backend/src/Goblin.Web/Http/StaticAssets.cs`; add new browser modules there.
`/`, `/work`, `/integrations`, `/knowledge`, and `/settings` serve the workspace
shell, also with a trailing slash, behind the workspace access screen. Settings
selects its section with `?section=connections` (or `agents`, `codex`, `github`,
`system`, `cluster`, `logs`, `timezone`, `integrations`, `slack`, or `chatgpt`).
The connection panel is shared with Settings through `/connection/panel.html`.
Runtime assets keep their `/assets/*` URLs, including the bundled Inter fonts.
Shared platform controls and font declarations live in `frontend/src/controls.css`.

The root `dist/` holds compiled test tooling and is not part of the application
image. Both `dist/` directories, .NET `bin/` and `obj/`, `.artifacts/`, and local
runtime data are ignored by Git. The root Dockerfile uses the repository root as
its build context and excludes `docs/`, `tests/`, `website/`, and the Rust
tooling. It copies `frontend/public/assets/branding/` because backend assemblies
embed the Slack icon and the ChatGPT plugin logo.

Keep original artwork in `assets/branding/` and copy only the assets the browser
uses into `frontend/public/assets/`. Codex schemas belong under
`backend/schemas/codex/`. Goblin account/runtime contracts belong to
`Goblin.Contracts`; Work command/view contracts belong to `Goblin.Application/Work`.
Browser types describe these public APIs, including `frontend/src/api/contracts.ts`
for connection setup and `frontend/src/work/contracts.ts` for Work views.
