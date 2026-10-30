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
  src/Goblin.Integrations.Slack/   Slack setup, Socket Mode, and thread delivery
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
Makefile.dev               Included local development, database, website, and Docker commands
.config/nextest.toml        Rust test-runner profiles
.vscode/                   Rust/TOML editing and native debugging configuration
frontend/
  src/connection/          Connection screen: HTML, TypeScript, and CSS
  src/work/                Persisted Work UI: HTML, TypeScript, and CSS
  src/api/                 HTTP contracts, generated values, transport, and read scopes
  src/settings/            Connection, integration, system, and timezone panels
  src/integrations/        Integration directory and connection presentation
  public/assets/           Static assets copied into the browser build
  scripts/                 Browser build helpers
  package.json             Standalone frontend npm package and TypeScript dependency
  tsconfig.json            Browser compiler settings; no Node.js types
website/
  index.html, website.css, website.js  Standalone communications website
  install/                 Installation UI and empty local release catalog
  package-installation.mts Validates and packages provider assets from GitHub Releases into the image
  assets/                  Website fonts, provider marks, and Goblin artwork copies
  references/              Retained design reference and earlier GIF export
  package.json             Independently locked Vite development dependency
  video/                   Development-only Remotion promo video with its own lockfile
  video/renders/           Committed promo and logo MP4 renders; not deployed
  compose.yaml, Dockerfile Detached Docker development and static Nginx runtime
  k8s/                     Website-only manifests for the existing local k3s cluster
  manage.sh                Website development and local deployment commands
  load-test.mjs            Fresh browser visits and website container resource measurements
tests/
  frontend/                Browser module behavior and command/model recovery tests
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

The root `Makefile` coordinates shared builds, checks, tests, and release tooling.
It includes `Makefile.dev` for local application, database, website, and Docker
commands. Continue using `make <target>` from the repository root; included targets
share the shell settings, workflow ordering, and argument forwarding. Each target's
commands are defined in one file, and `make help` lists targets from both files.
Targets use `<scope>-<action>[-<qualifier>]`, such as `backend-build`, `rust-test-doc`,
and `secrets-scan-staged`. Repository-wide workflows keep bare names: `install`,
`build`, `typecheck`, `check`, `test`, and `format`; `help` lists the commands.
Both Makefiles group targets by scope using multiline comment banners. Use `reset`
to return an environment to a fresh state, `clean` to remove outputs or caches,
and `stop` to halt services.

The root `package.json` has no task scripts or npm workspaces; it only pins
non-frontend Node tooling (Playwright, Prettier, TypeScript, Node types) and the
Codex runtime distribution. Its `package-lock.json` preserves the runtime and
tool pins. `frontend/package.json` and `frontend/package-lock.json` independently
own frontend dependencies and npm scripts. No backend or test tooling is added
to the frontend package. `make install` installs both locked Node packages and
fetches locked Rust dependencies; first install the Rust helpers with
`make rust-setup` and load the Cargo environment as described below. The root
`tsconfig.json` compiles Node.js test tooling, while `frontend/tsconfig.json`
compiles browser source independently. `global.json` stays at the root so the
pinned .NET SDK applies to commands run from either the root or `backend/`.

`website/` is an independent static site, with its own package manifest and
lockfile. `make website-start` installs its development tooling inside Docker;
application install/build/test commands do not include it. The website's HTML,
CSS, JavaScript, and assets are copied directly into a separate static image.
See the [website guide](../website/README.md) for detached development and local
k3s deployment. The application Docker build excludes `website/`; the website
Dockerfile has its own narrow build-context allowlist.

Node.js 24 or newer runs the `.mts` build helpers, secret-scanning scripts, and
fake Codex fixture directly. `.mts` is TypeScript with explicit ES module
semantics, including when a test copies a script outside the repository. These
scripts do not depend on compiled output or an installed TypeScript runner.
`tsconfig.scripts.json` checks them without emitting files and permits only
erasable TypeScript syntax. Native Node execution strips types without checking
them; `make scripts-typecheck` performs that check as part of the full build
and `make typecheck`.

The installer page in `deploy/azure/setup/app.js` remains JavaScript because the
native goblinctl setup server packages it directly for the browser. Moving its source
to TypeScript would require generating and checking the packaged JavaScript.

## Build and test

Install GNU Make, Node.js 24+, and the .NET SDK selected by `global.json`. On
Ubuntu/WSL, `build-essential` includes Make. Then run:

```bash
make rust-setup
source "$HOME/.cargo/env"
make install
```

Use `make help` to list targets. Workflows are serialized even with `make -j`
because cleaning compiled tools and copying frontend assets share build outputs.
Pass tool options through `ARGS`, for example
`make browser-test ARGS="tests/e2e/setup.spec.ts --headed"` or
`make local-start ARGS="--http-port 8888"`. Rust selectors use `CARGO_ARGS`, for
example `make rust-test CARGO_ARGS="-p goblinctl"`.

Frontend-only work continues inside its own package:

```bash
cd frontend
npm ci
npm run build
npm run typecheck
```


| Command | Purpose |
| --- | --- |
| `make artifacts-clean` | Recursively delete everything in `.artifacts/` except its root `.gitkeep`, then print confirmation |
| `make install` | Install both locked Node packages and fetch locked Rust dependencies |
| `make rust-setup` | Install the pinned Rust development tools |
| `make rust-format` | Format Rust using Codex conventions |
| `make rust-lint` | Lint the Rust workspace |
| `make rust-test` | Run Rust tests with nextest and then documentation tests |
| `make ci-test` | Verify changed-file selection, Git comparisons, Actions reporting, and the required merge-result policy |
| `make maintenance-test` | Verify owner-only maintenance, candidate routing, context bounds, and scoped validation without API calls |
| `make check` | Run Rust formatting/lints, the full offline test suite, and type checks |
| `make format` | Format TypeScript, Rust, and handwritten C# |
| `make local-start` | Start the full local installation with existing safety checks |
| `make local-status` | Report installation status without changing it |
| `make local-reset ARGS=--yes` | Explicitly reset only the owned local installation |
| `make dev-reset` | Reset the owned WSL installation and known checkout runtime/test state, including the saved password |
| `make build` | Build native tools, frontend assets, test tooling, and the .NET solution |
| `make dev-start DB_PROFILE=wsl` | Build, refresh the selected PostgreSQL profile, prepare the owner password, and start the application |
| `make relay-start ARGS="--origin https://HOST --ssh USER@HOST --identity /path/to/key"` | Connect an existing dedicated Azure HTTPS test VM to the local Goblin build |
| `make relay-status` / `make relay-stop` | Inspect the development tunnel or explicitly restore the test VM's installed Goblin upstream |
| `make chisel-check` | Render the AK3S Chisel manifests and verify deployment credential handling without contacting Kubernetes |
| `make docker-build` | Time the complete Goblin image build; pass extra Docker options through `ARGS` |
| `make docker-clean-images` | Remove image tags whose repository name starts with `goblin`, including registry-qualified names |
| `make docker-clean-cache` | Clear unused cache for the default Docker builder across all projects |
| `make database-migrate DB_PROFILE=wsl` | Apply SQL migrations with the profile’s admin role |
| `make database-scaffold DB_PROFILE=wsl` | Regenerate EF mappings with the profile’s app role |
| `make database-test DB_PROFILE=wsl` | Explicitly run the .NET database integration tests |
| `make password-set` | Choose and confirm a password; save only its verifier in `.goblin-secrets/` |
| `make frontend-build` | Build only the frontend |
| `make website-start` | Start the website in Docker detached, with live reload at localhost:5173 |
| `make website-stop` | Stop the website development container |
| `make website-status` / `make website-logs` | Inspect the website development container; logs accepts `ARGS=--follow` |
| `make website-deploy` | Build the static website, import into existing local k3s, and await rollout |
| `make website-preview` | Forward the k3s website to localhost:5174 while the terminal stays open |
| `make website-test-load` | Measure the website container with one fresh browser visit/minute; defaults to ten visits plus a minute idle |
| `npm --prefix website/video run render` | Render the website promo video to `website/video/renders/` after `npm --prefix website/video ci` |
| `npm --prefix website/video run render:logo` | Render the looping Goblin logo animation to `website/video/renders/` after `npm --prefix website/video ci` |
| `npm --prefix frontend run build` | Run the frontend package build directly |
| `make backend-build` | Build the .NET solution using any existing frontend output |
| `make typecheck` | Check browser source, test tooling, and direct Node scripts; build .NET |
| `make scripts-typecheck` | Check directly executed TypeScript scripts without building |
| `make test` | Build, check generated contracts/protocols, and run Rust, .NET, frontend, HTTP, release, and offline deployment tests |
| `make browser-test` | Build and run Playwright journeys |
| `make codex-test` | Build and check the pinned Codex binary's local storage/transport with synthetic credentials; no authenticated model task |
| `make protocol-generate` | Regenerate C# models from the checked-in schemas |
| `make protocol-check` | Verify that generated models match the schemas |
| `make kubernetes-generate` | Regenerate selected Kubernetes and Agent Sandbox models |
| `make kubernetes-check` | Verify those models match the pinned schemas and selection |

After building frontend assets, publish with:

```sh
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish
```

### Docker builds

Build and time the complete platform image from the repository root:

```bash
make docker-build
```

The target uses Bash's `time` for a `linux/amd64` build tagged
`goblin-platform:local` and prints Docker's plain progress. Pass additional build
options through `ARGS`, such as `make docker-build ARGS=--no-cache`. The shell's
`real` value includes the entire build and image export. BuildKit's individual
step durations overlap: frontend compilation, web dependency restore,
database-tool restore/publish, native runtime downloads, and runtime package
installation are independent branches. Each .NET restore has its own download
cache so its lock does not serialize the other branch. After frontend assets and
web dependencies are ready, one `dotnet publish` compiles the application and
packages the browser's gzip/Brotli copies and endpoint manifest. MSBuild builds
independent referenced projects in parallel by default.

The first build downloads the pinned base images, Codex runtime, GitHub CLI,
and package dependencies. Later builds reuse Docker layers, npm and NuGet
downloads, APT indexes, and the checksum-verified GitHub CLI archive. Restored
NuGet packages stay in the SDK restore layer alongside their metadata, so an
evicted download cache cannot break `--no-restore` compilation. Frontend
edits reuse the web restore, rebuild browser assets, and rerun the application
publish, including C# compilation; this trades some frontend-only rebuild speed
for one compilation and packaging pass on cold builds. Restored packages, native
tools, and the database tool remain reusable. Application files follow the large
native layers in the final image so those layers stay reusable. C# edits rerun the
web restore and publish while reusing NuGet downloads and the database migration
tool; migration-tool edits rebuild only that tool. Published browser assets have
their own final-image layer, while Slack's icon remains embedded in its .NET assembly.
Ubuntu package indexes are limited to `main`, which owns the runtime packages and
their dependencies, with security and update channels kept.

Keep the same builder and its cache for normal development. `--no-cache` forces
build instructions to run again, but does **not** empty persistent cache mounts.
Removing an image with `docker image rm goblin-platform:local` also retains the
builder cache. `make docker-clean-images` removes tagged Goblin images with normal
`docker image rm` behavior; references from containers can prevent removal.
`make docker-clean-cache` prunes unused cache for the default builder across all
projects. Run it before the timed build to clear existing build cache; base images
already loaded into Docker remain available.

The Dockerfile copies the complete `backend/src` directory before the web restore,
preserving project paths and including new referenced production
C# projects without additional per-project copy instructions. Backend source
changes invalidate the web restore layer.

For a cold-build benchmark, create a fresh isolated builder **for every sample**.
This starts with no base-image blobs, cached layers, npm/NuGet downloads, APT
indexes, or GitHub CLI archives. Export to a new archive so previously loaded
images in the host daemon cannot affect export timing:

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

Builder startup is outside the timer; pulling and extracting the application
base images, dependency installation, compilation, packaging, and archive export
are inside it. Loading the archive with `docker load --input <image.tar>` is a
separate verification step. No existing builder cache is pruned. Save the original
Dockerfile before editing and use `--file` to select each variant. Run at least
three samples per variant, alternate their order, keep the machine otherwise
idle, and compare medians and ranges. Check that each `cache-before.txt` reports
`0B` and that base-image digests match across samples. Registry/CDN and operating
system caches remain outside this Docker cache isolation, so download and host
load variation still affect the results.

Cold-build measurements on 2026-10-25 used this archive-export method on
`linux/amd64`, with 8 logical CPUs (Intel Core i7-1255U), 11.7 GiB available to
Docker, Docker 29.1.3, Buildx 0.30.1, and BuildKit 0.26.2:

| Version | Samples (seconds) | Median | Range |
| --- | --- | --- | --- |
| Before: shared restore, separate web build/publish | 106.43, 104.51, 106.23 | 106.23 s | 104.51–106.43 s |
| After: independent database restore, one web publish | 108.61, 94.80, 97.84 | 97.84 s | 94.80–108.61 s |

The median improved by **8.39 seconds (7.9%)**. The web compile/publish steps
fell from a median of 29.8 to 20.2 seconds. Each sample began with `0B` of builder
cache and the same three base-image digests. The slower optimized sample spent
38.8 seconds pulling/extracting the SDK image, versus 24.0 seconds in the first
baseline; these results include that network variation. Image verification found
399 identical files and an endpoint manifest differing only in `Last-Modified`
timestamps, with identical paths, permissions, and ownership. The compressed
browser assets and native tools were retained.

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
| Rust tooling | `make rust-format`, `make rust-lint CARGO_ARGS="-p <crate>"`, and `make rust-test CARGO_ARGS="-p <crate>"`. The last command includes documentation tests; review snapshots before accepting them. |
| Browser code or UI behavior | `npm --prefix frontend run typecheck`, affected frontend tests, and relevant `make browser-test ARGS="tests/e2e/<journey>.spec.ts"` journeys when integrating the browser boundary. Inspect the changed interaction and visual result. |
| Standalone website | `npm --prefix website run check`, then inspect the affected interactions and layouts with `make website-start`. Check local container/deployment changes against the actual Docker or k3s runtime when available; these commands do not invoke the application or Azure installer. |
| Schema, persistence, dispatch, or database concurrency | Affected .NET tests plus real PostgreSQL checks with the explicit setup in [the database guide](database.md#application-configuration-and-verification). |
| Codex transport or execution hosting | Existing transport/protocol tests and `make codex-test` where relevant; authenticated execution/isolation needs the real-runtime checks described in [execution hosting](execution-hosting.md#verification-limits). |
| Installer or deployment contracts | Offline generation and deployment checks in `make test`; consult [formatting deployment assets](formatting.md#deployment-assets) for generated inputs. Azure installation stays manual. |

`make test` is the broad offline suite with real PostgreSQL checks gated by explicit
test configuration. `make check` adds Rust formatting/lints and type checks. These
are useful broad integration gates, not prerequisites for every local edit. CI
selects verification groups from extensions and recognized filenames using
[`config/ci-verification.json`](../config/ci-verification.json); documentation-only
changes still report `test_results` while skipping runtime groups. Required
CI checks still apply; scoped local results do not imply that CI ran.
See [GitHub Actions](github-actions.md) for workflow names, required checks, and
ruleset maintenance.
**Installation Assets** prepares verified snapshots directly from GitHub Releases
and calls **Website Release**, which serves the website and installation files at
`goblinboard.com/install/` after production approval.

Offline installer tests run as an ordinary user. `make tools-build` builds the
test-only `host_fixture` Rust example, which calls host configuration with a
private filesystem, mocked infrastructure commands, and a supplied port probe.
It is excluded from the shipped CLI archive and application image. The production
`goblinctl db host` command still requires root and checks the real host port.
PostgreSQL profile tests include a long configuration path to cover CI checkout
layouts and SSH tunnel reuse, disconnect, and recovery.

The ordinary HTTP/browser harness uses runtime fixtures. `make codex-test` uses the
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
`backend/src/Goblin.Web/Http/StaticAssets.cs`. `/`, `/work`, `/integrations`,
`/knowledge`, and `/settings` serve the workspace shell. Settings selects its
section with `?section=connections` (or `codex`, `github`, `agents`, `timezone`,
`integrations`, `slack`, `system`, `cluster`, or `logs`). Page routes also accept
a trailing slash and show the workspace access screen before unlocking.
The connection panel is shared with Settings through `/connection/panel.html`.
Runtime assets keep their `/assets/*` URLs, including locally bundled Inter fonts.

The platform and installer use the website’s green palette, white panels, and
Inter typography. Shared platform controls and font declarations live in
`frontend/src/controls.css`. The standalone installer embeds its own fonts;
the website installation page shares those fonts and their license.

The root `dist/` holds compiled test tooling. It is separate from the frontend
build and is not included in the application image. Both output directories,
.NET `bin/` and `obj/`, `.artifacts/`, and local runtime data remain ignored by Git.
The root Dockerfile compiles frontend assets and the backend in parallel, then
finishes SDK publishing, using the repository root as its build context. The
publish stage copies `frontend/public/assets/branding/` for the Slack icon and
ChatGPT plugin-package logo embedded in backend assemblies. Documentation remains
excluded from the image build context.

Keep original artwork in `assets/branding/` and copy only the assets used by the
browser into `frontend/public/assets/`. Codex schemas belong under
`backend/schemas/codex/`; Goblin account/runtime contracts belong to `Goblin.Contracts`; Work command/view
contracts belong to `Goblin.Application/Work`. Browser types describe these public
Goblin APIs, including `frontend/src/api/contracts.ts` for connection setup and
`frontend/src/work/contracts.ts` for Work views.

The [database guide](database.md) covers PostgreSQL connections, Windows pgAdmin,
daily development, schema rules, and EF mappings.

The [Work lifecycle](work-lifecycle.md) is used by the live application. Core and
public contracts are independent of runtime protocols and HTTP. Orchestration
depends on the shared execution host contract; composition selects the concrete
Codex/text/sandbox adapters. [Execution hosting](execution-hosting.md) documents
configuration, reservations, recovery, and validation limits.
