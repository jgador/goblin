# Development and verification

Goblin ships the backend and frontend as one ASP.NET Core application. The
[architecture guide](architecture.md) explains service ownership; browse
[backend/src](../backend/src/), [frontend/src](../frontend/src/), and [tools](../tools/)
for implementation. The standalone [website](../website/README.md) has its own package
and workflow.

## Build and test

Run commands from the repository root. Install GNU Make, Node.js 24 or newer, the SDK
selected by [global.json](../global.json), and the
[Rust prerequisites](rust-development.md#tool-versions-and-installation):

```bash
make rust-setup
source "$HOME/.cargo/env"
make install
make build
```

Start without a database using `make dev-start ARGS=--without-database`; this disables
Work. For Work, use a migrated [PostgreSQL profile](database.md#wsl-and-windows-pgadmin)
and `make dev-start DB_PROFILE=wsl`. Restart after source edits. Full installation and
container-dependent behavior use the [WSL k3s workflow](../deploy/k3s/README.md).

[Makefile](../Makefile) and [Makefile.dev](../Makefile.dev) own commands and ordering.
`make help` lists them. Workflows remain serial even with `make -j` because they share
outputs. Follow target naming conventions in those files' headers when adding commands.
Pass tool flags with `ARGS` and Rust selectors with `CARGO_ARGS`, for example:

```bash
make browser-test ARGS="tests/e2e/setup.spec.ts --headed"
make rust-test CARGO_ARGS="-p goblinctl"
```

Frontend-only build and typechecking are available through its
[package scripts](../frontend/package.json). Root Node dependencies serve test and
developer tooling; keep them out of the frontend package. Native Node type stripping
does not typecheck scripts; `make scripts-typecheck` does.

After building browser assets, publish to a task directory:

```bash
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish/application
```

VS Code selects [backend/Goblin.slnx](../backend/Goblin.slnx). If C# Dev Kit has an
editor-generated solution open, use **.NET: Open Solution** and select that file.
Temporary projects belong under `.artifacts/`; they are not part of that solution.

## Docker builds and cleanup

`make docker-build` builds the application image. Docker's `--no-cache` reruns
instructions but keeps persistent cache mounts. Removing an image keeps builder cache
too. For a cold benchmark, use a fresh named builder per sample and place archives and
logs under `.artifacts/docker-benchmark/`.

`make docker-clean-images` targets image repositories starting with `goblin`.
`make docker-clean-cache` prunes unused cache of the default builder across all
projects. Inspect ownership and other consumers before cleanup; neither operation is
part of ordinary verification.

## Choosing verification

Choose checks for the behavior and boundaries you changed. Reuse passing results for
unchanged content; repeat or broaden checks for new changes, failures, unresolved risks,
or required coverage. Write regression tests that check behavior, not tests that only
repeat the implementation.

Use direct WSL tests for fast edit/debug cycles. Real PostgreSQL integration checks can
use the database already installed in k3s without running the application there. When
behavior depends on the container image, mounted configuration, ingress, networking,
RBAC, worker scheduling/recovery, PVCs, or deployed logging, verify it against the owned
WSL k3s installation. Use
[`make k3s-deploy`](../deploy/k3s/README.md#deploy-application-edits) after the affected
tests pass, confirm the reported image is running, and exercise the changed behavior.
Repeat deployment for new runtime changes or a deployment failure; reuse unchanged
passing checks. Report any required checks that could not be performed. Schema,
infrastructure manifests, and installer changes need their own installation
verification; image deployment cannot apply them.

| Change | Relevant verification |
| --- | --- |
| Prose or agent instructions only | Review the diff, relative links/anchors, and commands against their owners; run `git diff --check`. |
| Work core | `dotnet test backend/tests/Goblin.Core.Tests`; add the boundary checks below when callers or integrations change. |
| Other C# behavior | The affected project under `backend/tests/`; use `make test` when changes span application, transport, HTTP, or deployment contracts. Keep existing protocol and transport coverage. |
| HTTP routes or API contracts | `make backend-test` and `make node-test` (frontend, HTTP integration, and deployment tests). |
| Shared C#/Rust contract values | Follow [the contract guide](contract-values.md#cross-language-changes), including `make contracts-check`. |
| Generated Codex or Kubernetes models | `make protocol-check` or `make kubernetes-check`. |
| Rust tooling under `tools/` | `make rust-format`, `make rust-lint CARGO_ARGS="-p <crate>"`, and `make rust-test CARGO_ARGS="-p <crate>"`; see [Rust development](rust-development.md#build-format-lint-and-test). |
| Browser code or UI behavior | `npm --prefix frontend run typecheck`, `make frontend-test`, and the relevant `make browser-test ARGS="tests/e2e/<journey>.spec.ts"` journeys. Inspect the changed interaction and visual result. |
| Directly run `.mts` scripts | `make scripts-typecheck` and the script's `node --test scripts/<name>.test.mts` (`make ci-test`, `make maintenance-test`, `make release-test`, `make make-test`, `make chisel-check`, `make secrets-test`). |
| Standalone website | `npm --prefix website run check` (and `npm --prefix website/video run check` for the video), then inspect the affected pages with `make website-start`. |
| Schema, persistence, dispatch, or database concurrency | Affected .NET tests plus `make database-test DB_PROFILE=wsl` against real PostgreSQL, set up as in [the database guide](database.md#application-configuration-and-verification). |
| Codex transport or execution hosting | Existing transport/protocol tests and `make codex-test` where relevant; authenticated execution and isolation need the real-runtime checks in [execution hosting](execution-hosting.md#verification-limits). |
| Kubernetes manifests, installer, or deployment contracts | `make test` (deployment tests, `make chisel-check`, `make dependencies-check`, `make deploy-generate`); see [deployment assets](formatting.md#deployment-assets). Installer changes also need `make release-build release-check-installer`, `make browser-test ARGS="tests/e2e/setup.spec.ts tests/e2e/install.spec.ts"`, and `make platform-check-image`. Azure installation stays manual. |
| Environment variables | Follow [adding a variable](environment-variables.md#adding-a-variable). |
| CI policy or workflows | `make ci-test make-test scripts-typecheck`; see [GitHub Actions](github-actions.md#ci-selection-and-required-result). Check publication and merge-queue behavior need a real Actions run. |

`make test` is the broad offline suite; real PostgreSQL checks run only with explicit
test configuration. It does not run Rust lints, browser journeys, `make codex-test`, or
website checks. `make check` adds Rust formatting and lint checks and type checks.
Neither is required for every local edit.

CI selection and reporting are defined by
[the CI policy](../config/ci-verification.json) and explained in
[GitHub Actions](github-actions.md#ci-selection-and-required-result). Local results do
not mean CI ran.

Offline installer tests run as an ordinary user. `make tools-build` builds the test-only
`host_fixture` Rust example, which runs host configuration against a private filesystem
with mocked infrastructure commands. It is not shipped. The production
`goblinctl db host` command still requires root.

The HTTP and browser tests use runtime fixtures. `make codex-test` uses the real pinned
executable with synthetic credentials and submits no model task. Neither covers
authenticated execution. PostgreSQL and database-backed browser journeys skip without
their test setup; report those skips. Keep live-test credentials private. Without a live
setup, report that coverage as unverified instead of claiming persistence or runtime
verification. Do not add Azure login, provisioning, live deployment, or cleanup to these
checks.

## Generated and published assets

Browser output belongs to `frontend/dist/` and is copied into the application's
`wwwroot/`; root `dist/` contains test tooling and is not shipped.
[StaticAssets.cs](../backend/src/Goblin.Web/Http/StaticAssets.cs) explicitly maps
browser modules and routes. New modules need routes; the
[browser asset tests](../tests/deployment/browser-assets.test.ts) check dependencies.
Connection screens share `/connection/panel.html`; add provider-specific behavior to
that shared route rather than creating separate panels.

[Codex schema inputs](../backend/schemas/codex/) change with the runtime pin; follow
[protocol regeneration](../backend/src/Goblin.Protocol/README.md). The setup UI's
`deploy/azure/setup/app.js` stays JavaScript because
[goblinctl embeds it directly](../tools/goblinctl/src/assets.rs), without a frontend
build step.

The [Dockerfile](../Dockerfile) and [.dockerignore](../.dockerignore) define production
inputs and exclusions. Keep developer tools and private runtime state out of images.
Original artwork stays under [assets/branding](../assets/branding/README.md); only
required runtime copies belong in public assets. Keep `frontend/public/assets/branding/`
in the application Docker build context: the Slack assembly embeds its upload icon from
that source directory. The ChatGPT logo is published as browser content.
