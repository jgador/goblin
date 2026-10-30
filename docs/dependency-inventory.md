# Dependency and tooling inventory

Read selected versions from their owning manifests and lockfiles. This inventory
maps those inputs and their verification; it avoids duplicating package versions
and transitive counts that drift after updates. A declaration is not proof of what
is installed on a server or what the latest upstream release is.

## Authoritative inputs

| Scope | Version/dependency owners | Resolved inputs and verification |
| --- | --- | --- |
| .NET SDK and application | [global.json](../global.json), [shared build properties](../backend/Directory.Build.props), project files under [backend](../backend/) | SDK feature-band roll-forward and NuGet restore determine the installed/resolved graph. Keep EF, scaffolding tools, and Wolverine packages compatible. |
| EF developer tool | [dotnet-tools.json](../.config/dotnet-tools.json), [scaffold script](../backend/scripts/scaffold-database.sh) | Developer-only tooling; regenerated mappings and database tests verify its output. |
| Root Node tooling and Codex | [package.json](../package.json), [package-lock.json](../package-lock.json) | npm's lock owns platform-specific bundles; Playwright owns browser revisions. |
| Browser application | [frontend/package.json](../frontend/package.json), [frontend/package-lock.json](../frontend/package-lock.json) | Independent compiler/npm tasks; static asset checks protect served module URLs. |
| Public website | [website/package.json](../website/package.json), [website/package-lock.json](../website/package-lock.json), [Dockerfile](../website/Dockerfile) | Vite is development-only; the shipped site is static Nginx. |
| Promo video | [video/package.json](../website/video/package.json), [video/package-lock.json](../website/video/package-lock.json) | Remotion, React, and related tools are development-only and are not deployed with the site. |
| Rust | [rust-toolchain.toml](../rust-toolchain.toml), [Cargo.toml](../Cargo.toml), crate manifests under [tools](../tools/), [Cargo.lock](../Cargo.lock) | Locked graph, nextest, rustfmt, and Clippy; see [Rust development](rust-development.md). |
| Native developer tools | [setup-rust.sh](../scripts/setup-rust.sh), [Rust action](../.github/actions/setup-rust/action.yml), [deployment action](../.github/actions/setup-deployment/action.yml), [kubectl installer](../scripts/install-kubectl.sh) | nextest, Insta, DotSlash, Bicep, and kubectl pins stay with their setup/download owners. |
| Installer and service images | [dependencies.toml](../dependencies.toml), [dependencies.lock.json](../dependencies.lock.json) | `make dependencies-check` checks references/provenance; see [deployment dependencies](dependencies.md). |
| Application images | [Dockerfile](../Dockerfile), [GitHub CLI installer](../deploy/install-gh.mjs) | Node/.NET bases and checksum-pinned native resources belong to the image build. |
| Kubernetes components | [installer.sh](../deploy/azure/setup/installer.sh), [manifests](../deploy/) | K3s, cert-manager, and Agent Sandbox own bundled components. Headlamp/logging/database references also follow the catalog. |
| Slack setup helper | [SlackSetup.cs](../backend/src/Goblin.Integrations.Slack/SlackSetup.cs), [SlackCliPackage.cs](../backend/src/Goblin.Integrations.Slack/SlackCliPackage.cs) | On-demand private helper with version/checksum verification; no permanent CLI is shipped. |
| Schemas | [Codex selection](../backend/schemas/codex/selection.json), [Kubernetes selection](../backend/schemas/kubernetes/selection.json) | Source checksums, selection, generators, and runtime pins must be reviewed together. |
| GitHub Actions | [workflows](../.github/workflows/), [shared actions](../.github/actions/) | References are authoritative, including commit-pinned actions; see [workflow contracts](github-actions.md). |
| Imported skills | [skills-lock.json](../skills-lock.json), [third-party notices](../THIRD_PARTY_NOTICES.md) | Imported instructional snapshots retain separate provenance/license obligations. |

Git, Make, Docker/Buildx, Bash, curl, systemd, CA certificates, archive tools,
and OS packages also participate. Exact versions require a host/image inventory.
Optional editor extensions belong to [extensions.json](../.vscode/extensions.json).
Gitleaks installation belongs to [secret scanning](secret-scanning.md).

## Update monitoring proposal

Automated version-update monitoring is not configured. Renovate is a suitable
option for native manifests, toolchains, images, and Actions, with custom extraction
for downloads and schema pins. Its [Dependency Dashboard](https://docs.renovatebot.com/key-concepts/dashboard/)
is one edited issue, not a notification issue for every release. Dependabot is
another option for native package formats. Service installation or scheduled issue
automation requires a separate implementation task.

Discover all package roots, image inputs, and custom downloads; update checksums
and digests with versions; regenerate selected schemas and Azure review assets;
and coordinate duplicated pins. Prefer stable releases and deliberate major-version
review. Define dashboard, vulnerability-PR, and merge policies explicitly.

PR CI selects language checks plus shared integration and installer/browser
checks from dependency filenames and source extensions. Shared or unclassified
inputs select all groups; see [CI selection](github-actions.md#ci-selection-and-required-result).
Updates still need checks for the affected integration.
PostgreSQL tests are opt-in, authenticated runtime checks need actual service
access, and Azure installation remains manual.

## Resolved software inventories

A complete SBOM must use actual build inputs: every npm lock, the resolved Cargo
graph, restored NuGet packages, built images, OS packages, and bundled native
components. Optional/platform-specific entries are not all shipped everywhere.
Preserve generated inventories under `.artifacts/` rather than maintaining a
transitive package table in this guide.
