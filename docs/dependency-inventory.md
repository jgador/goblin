# Dependency and tooling inventory

Snapshot of repository declarations on 2026-09-27. Versions below are the
repository's selected versions, not a claim about the latest upstream release or
the software installed on a particular server. This inventory documents the
inputs to update monitoring; monitoring has not been configured.

Deployment and build image rows were refreshed on 2026-10-01. Other rows retain
the original snapshot date.

The manifests, lockfiles, scripts, and deployment files linked below remain the
source of truth. An updater should read those files, rather than this dated
inventory. The tables cover direct application dependencies and explicitly
selected development/deployment tools. Transitive and OS packages require the
resolved inventories described at the end.

## Languages, SDKs, and package managers

| Software | Repository selection | Source |
| --- | --- | --- |
| C# / .NET SDK | SDK `10.0.100`, with `rollForward: latestFeature` | [global.json](../global.json) |
| .NET / ASP.NET Core | Target `net10.0`; build image `10.0.401-noble`; runtime image `10.0.12-noble` | [Directory.Build.props](../backend/Directory.Build.props), [Dockerfile](../Dockerfile) |
| Rust / Cargo | Toolchain `1.95.0`; edition `2024`; minimum Rust `1.95` | [rust-toolchain.toml](../rust-toolchain.toml), [Cargo.toml](../Cargo.toml) |
| Clippy, rustfmt, rust-src | Components of the selected Rust toolchain | [rust-toolchain.toml](../rust-toolchain.toml) |
| Node.js | Local minimum `>=24`; build image `26.10.0-bookworm-slim`; release CI selects `24` | [package.json](../package.json), [Dockerfile](../Dockerfile), [release workflow](../.github/workflows/goblin-release.yml) |
| npm | Supplied by the Node installation/image; no independent version pin | [package.json](../package.json), [Dockerfile](../Dockerfile) |
| TypeScript | `7.0.2` | Root and frontend package manifests below |
| NuGet | Supplied by the .NET SDK; no independent CLI pin | [global.json](../global.json) |

SDK roll-forward settings, major/minor container tags, and CI version selectors
can resolve to newer builds without changing their declaration. Pinning image
digests would make those build inputs individually reviewable.

## npm packages

There are five unique directly declared external npm packages. The standalone frontend
package also declares TypeScript. Sources: [package.json](../package.json),
[frontend/package.json](../frontend/package.json),
[package-lock.json](../package-lock.json), and
[frontend/package-lock.json](../frontend/package-lock.json).

| Package | Declared version | Use |
| --- | --- | --- |
| `@openai/codex` | `0.155.1` | Agent runtime, distributed with native platform bundles |
| `@playwright/test` | `1.63.0` | Browser tests |
| `@types/node` | `24.13.4` | Node type definitions |
| `prettier` | `3.9.8` | TypeScript formatting |
| `typescript` | `7.0.2` | Root tooling and frontend compilation |

Playwright manages the browser revisions used by its tests. Track those through
the Playwright package and its browser installation, rather than independently
upgrading Chromium to an arbitrary release.

## C# / NuGet packages

There are eleven unique directly referenced NuGet packages across the projects,
plus the separately installed `dotnet-ef` tool.

| Package | Declared version | Source |
| --- | --- | --- |
| `Microsoft.EntityFrameworkCore` | `10.0.12` | [Persistence project](../backend/src/Goblin.Persistence/Goblin.Persistence.csproj) |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `10.0.3` | [Persistence project](../backend/src/Goblin.Persistence/Goblin.Persistence.csproj) |
| `Microsoft.EntityFrameworkCore.Design` | `10.0.12` | [Database tool](../backend/tools/Goblin.Database/Goblin.Database.csproj) |
| `Microsoft.Extensions.Hosting` | `10.0.12` | [Database tool](../backend/tools/Goblin.Database/Goblin.Database.csproj) |
| `WolverineFx.EntityFrameworkCore` | `6.39.1` | [Application project](../backend/src/Goblin.Application/Goblin.Application.csproj) |
| `WolverineFx.Postgresql` | `6.39.1` | [Application project](../backend/src/Goblin.Application/Goblin.Application.csproj) |
| `WolverineFx.RuntimeCompilation` | `6.39.1` | [Application project](../backend/src/Goblin.Application/Goblin.Application.csproj) |
| `Yarp.ReverseProxy` | `2.3.0` | [Web project](../backend/src/Goblin.Web/Goblin.Web.csproj) |
| `Microsoft.NET.Test.Sdk` | `17.14.1` | [Test projects](../backend/tests) |
| `xunit` | `2.9.3` | [Test projects](../backend/tests) |
| `xunit.runner.visualstudio` | `3.1.1` in protocol tests; `3.1.4` in the other test projects | [Protocol tests](../backend/tests/Goblin.Protocol.Tests/Goblin.Protocol.Tests.csproj), [test projects](../backend/tests) |
| `dotnet-ef` (tool) | `10.0.12` | [dotnet-tools.json](../.config/dotnet-tools.json) |

These are project declarations; use a restore to establish the actual resolved
NuGet graph. Keep related EF tooling/packages and Wolverine packages compatible
when proposing updates. The existing xUnit runner version difference is recorded
here without changing it.

## Rust crates

There are nineteen unique direct external crates. Sources:
[workspace manifest](../Cargo.toml),
[goblinctl manifest](../tools/goblinctl/Cargo.toml),
[xtask manifest](../tools/xtask/Cargo.toml), and [Cargo.lock](../Cargo.lock).
`xtask` also depends on the local `goblinctl` crate.

| Crate | Manifest requirement | Locked version |
| --- | --- | --- |
| `anyhow` | `1` | `1.0.104` |
| `base64` | `0.22` | `0.22.1` |
| `chrono` | `0.4` | `0.4.45` |
| `clap` | `4` | `4.6.7` |
| `flate2` | `1` | `1.1.10` |
| `fs2` | `0.4` | `0.4.3` |
| `http-body-util` | `0.1` | `0.1.5` |
| `hyper` | `1` | `1.11.1` |
| `hyper-util` | `0.1` | `0.1.21` |
| `libc` | `0.2` | `0.2.189` |
| `pbkdf2` | `0.12` | `0.12.2` |
| `rand` | `0.8` | `0.8.8` |
| `rpassword` | `7` | `7.5.4` |
| `serde` | `1` | `1.0.229` |
| `serde_json` | `1` | `1.0.151` |
| `sha2` | `0.10` | `0.10.9` |
| `tar` | `0.4` | `0.4.46` |
| `tempfile` | `3` | `3.27.0` |
| `tokio` | `1` | `1.53.1` |

## Development and deployment tools

| Software | Selected version | Source |
| --- | --- | --- |
| GNU Make | System package, no repository version pin | [Makefile](../Makefile) |
| cargo-nextest | `0.9.103` | [Rust setup](../scripts/setup-rust.sh), [CI setup action](../.github/actions/setup-rust/action.yml) |
| cargo-insta | `1.46.3` | [Rust setup](../scripts/setup-rust.sh) |
| DotSlash | `0.5.7` | [Rust setup](../scripts/setup-rust.sh) |
| GitHub CLI (`gh`) | `2.101.0` for the application image | [GitHub CLI installer](../deploy/install-gh.mjs) |
| Slack CLI | `4.8.0`, downloaded only into a disposable setup directory; Linux x64/ARM64 SHA-256 pinned | [Slack setup adapter](../backend/src/Goblin.Integrations.Slack/SlackSetup.cs) |
| Bicep | `0.47.16` in release CI | [Release workflow](../.github/workflows/goblin-release.yml) |
| kubectl | `1.36.4` in release CI; also bundled with K3s | [Release workflow](../.github/workflows/goblin-release.yml), [installer](../deploy/azure/setup/installer.sh) |
| Gitleaks | Documented baseline `8.30.1`, allowing newer `8.x` | [Secret scanning guide](secret-scanning.md) |
| Rustup | Setup downloads the upstream installer without a version pin | [Rust setup](../scripts/setup-rust.sh) |

The Rust helper tools are development dependencies and do not belong in
production installations. Insta and DotSlash are available for future use; the
repository currently has no Insta snapshots or DotSlash manifests. See
[Rust development](rust-development.md).

## Infrastructure, images, and generated schemas

| Software/input | Selected version | Source |
| --- | --- | --- |
| PostgreSQL | `18.6-bookworm` | [Database deployment](../deploy/postgres/postgres.yaml), [verification job](../deploy/postgres/verify.yaml) |
| K3s | `v1.37.0+k3s1` | [Installer](../deploy/azure/setup/installer.sh) |
| K3s logging test image | `rancher/k3s:v1.37.0-k3s1` | [Logging smoke check](../scripts/check-logging.sh) |
| Agent Sandbox | `v1.0.4` | [Installer](../deploy/azure/setup/installer.sh) |
| cert-manager | `v1.21.2` | [Installer](../deploy/azure/setup/installer.sh) |
| Headlamp | `v0.45.0`, with image digest | [Headlamp deployment](../deploy/auth/headlamp.yaml) |
| VictoriaLogs | `v1.53.0`, with image digest | [Logging deployment](../deploy/auth/logging/workloads.yaml) |
| Fluent Bit | `5.1.3`, with image digest | [Logging deployment](../deploy/auth/logging/workloads.yaml) |
| Kubernetes schema | `v1.37.0`, with content checksums | [Schema selection](../backend/schemas/kubernetes/selection.json) |
| Agent Sandbox schema | `v1.0.4`, with content checksum | [Schema selection](../backend/schemas/kubernetes/selection.json) |
| Codex protocol schemas | Coupled to the selected Codex runtime | [Protocol generator](../backend/scripts/GenerateProtocol.cs), [schema files](../backend/schemas/codex) |
| Ubuntu / Debian base distributions | Ubuntu Noble and Debian Bookworm image families; Ubuntu 24.04 CI runners | [Dockerfile](../Dockerfile), [VM definition](../deploy/azure/modules/vm.bicep), [workflows](../.github/workflows) |

K3s also supplies components such as containerd, Traefik, and cluster networking.
Their exact versions are determined by the selected K3s release and deployed
resources. They are not independently pinned by these application manifests.

## GitHub Actions

Sources: [source checks](../.github/workflows/checks.yml),
[release workflow](../.github/workflows/goblin-release.yml), and
[shared Rust setup](../.github/actions/setup-rust/action.yml).

| Action | Selected reference |
| --- | --- |
| `actions/checkout` | SHA annotated `v6.0.2` in Rust CI; `v4` in release CI |
| `actions/setup-node` | `v4` |
| `actions/setup-dotnet` | `v4` |
| `actions/upload-artifact` | `v4` |
| `actions/download-artifact` | `v4` |
| `actions/attest-build-provenance` | `v2` |
| `dtolnay/rust-toolchain` | SHA annotated `1.95.0` |
| `taiki-e/install-action` | SHA annotated `v2.62.49` |

## Tools inherited from the environment

Git, Docker Engine, Docker Buildx/BuildKit, Bash, curl, tar/gzip, CA certificates,
systemd, coreutils, util-linux, sed, grep, and ripgrep are used by build,
installation, administration, or verification flows. Their exact versions are
not pinned individually in the repository. Docker packages come from the host
distribution in [install-app.sh](../deploy/azure/install-app.sh); Git and CA
certificates come from the runtime image's package repositories in the
[Dockerfile](../Dockerfile). Development Rust setup also documents the
distribution's `build-essential` package.

VS Code is optional. The recommended extensions are `rust-lang.rust-analyzer`,
`tamasfe.even-better-toml`, and `vadimcn.vscode-lldb`, with no extension version
pins. See [extensions.json](../.vscode/extensions.json).

These environment dependencies need an installed-system or container inventory
to report an exact current version. A repository version checker alone cannot
report whether deployed machines have received OS updates.

## Monitoring approach

Renovate is a good fit for this combination of npm, NuGet, Cargo, Rust toolchains,
Docker/Kubernetes images, GitHub Actions, and tools downloaded from release URLs.
Its [Dependency Dashboard](https://docs.renovatebot.com/key-concepts/dashboard/)
maintains one GitHub issue with pending upgrades. Dashboard approval can gate
ordinary update PRs, and automerge can remain disabled. Vulnerability remediation
PRs can bypass dashboard approval under Renovate's defaults; configure that
policy explicitly if strictly issue-only behavior is required.

The dashboard is one continuously edited issue, not a new issue for every
release. Dashboard edits should not be relied on to generate a fresh GitHub
notification for every update. If separate notification issues are a requirement,
use a scheduled GitHub Actions job with a TypeScript checker, registry/release
lookups, and GitHub issue creation. Deduplicate by dependency and target version,
and record current/new versions, source paths, and release links. A permanently
running service is unnecessary for either approach.

Configuration work should include:

- Native managers for package manifests, lockfiles, SDK/toolchain files, images,
  and GitHub Actions. Enable Kubernetes manifest discovery for the deployment
  paths.
- Custom extraction for versions embedded in installer scripts, release download
  URLs, the GitHub CLI installer, and schema source declarations. Use upstream
  registries/releases as the version source.
- NuGet `rangeStrategy: bump` for the bare version declarations, following the
  current [NuGet manager guidance](https://docs.renovatebot.com/modules/manager/nuget/).
- Updates to checksums and image digests alongside their selected versions.
  Simple version substitution is insufficient for checksum-verified downloads.
- Coordinated changes to duplicated tool versions, generated schema inputs, and
  generated Azure templates. Regenerate outputs through the repository tools.
- Stable releases by default, visible major upgrades for deliberate review, and
  no automatic merging. Keep vulnerability monitoring enabled separately.
- Full PR validation before upgrades are accepted. Current ordinary PR CI runs
  Rust checks; the broader `make test` and setup-browser checks are currently in
  the release workflow. Extend PR CI before relying on automated dependency PRs.
  Run the relevant browser/runtime checks for affected integrations and explicitly
  configure real PostgreSQL tests when persistence coverage is needed.

The [Dependabot version update service](https://docs.github.com/en/code-security/dependabot/dependabot-version-updates/about-dependabot-version-updates)
is a simpler GitHub-native option for standard dependency update PRs. The extra
script and infrastructure pins make Renovate's custom managers particularly
useful here.

## Complete resolved inventories

The root tooling lockfile contains 34 external package entries, including
optional platform-specific packages. The Cargo lockfile contains 121 external
package entries, including transitive/platform dependencies. Those entries are
not all installed or shipped on every platform.

Generate the full software bill of materials from the actual restore/build
inputs: both npm lockfiles, Cargo's locked dependency graph, the restored NuGet graph,
and built container images. Include OS packages and bundled native runtime
components when describing a production image. This document is not a complete
image SBOM. Preserve the generated inventories as build artifacts rather than
maintaining hundreds of transitive version declarations by hand.
