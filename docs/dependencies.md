# Deployment dependencies

[`dependencies.toml`](../dependencies.toml) contains only two sections:
`[goblinctl] version`, the published goblinctl release Goblin uses, and
`[images]`, the service images it deploys as `name = "repository:tag"`. Any other
section is rejected.

Goblin and goblinctl keep independent versions. `goblinctl.version` selects the
published installer; the Cargo workspace version identifies the installer being
built during development. A release candidate's record owns its exact installer
pairing; preparation can reuse a matching publication or build one without
changing this pin (see the [release guide](releases.md)). goblinctl is released
from `jgador/goblin`; the release tooling fixes that repository.

Cargo, npm, and NuGet dependencies stay in their own project files and lockfiles.
Tool versions and download checksums stay in their setup files. Goblin's
Dockerfile selects its own Node and .NET base images, so they need no catalog
entry.

To manage a build image through the catalog, add its repository and tag under
`[images]`, for example `dotnet-runtime = "mcr.microsoft.com/dotnet/aspnet:10.0-noble"`.
The resolver then updates and checks that tag in the `Dockerfile` and
`scripts/check-logging.sh`. These files keep tags without digests; the digest
stays in the lock. Removing the entry stops catalog management, and the current
reference in the file stays as the default.

## Updating dependencies

Edit an image reference in `dependencies.toml`, then run:

```bash
cargo xtask dependencies resolve
cargo xtask dependencies check --locked
```

`make dependencies-resolve` and `make dependencies-check` run the same commands.
Resolution uses Docker Buildx to look up new or changed tags, requires
`linux/amd64` support, and updates matching image references in YAML under
`deploy/`. Tags stay tags; a YAML reference that already contains `@sha256:...`
also gets its digest updated. Resolution also regenerates
`deploy/install-request.json`. It keeps existing digests for unchanged tags; use
`resolve --refresh` to resolve every tag again. It does not pull image layers or
run package managers. Review the catalog, lock, and updated references together.

[`dependencies.lock.json`](../dependencies.lock.json) is generated; edit the TOML
file instead. It declares `"platform": "linux/amd64"` once and records one digest
per image: the image index, or the manifest for a single-platform image. For
tag-only references the digest is metadata; the registry supplies the tag's
contents when it is pulled. The lock also records the selected goblinctl release
under `goblinctl.release`, including its source revision, archive checksum, and
shipping-input hashes. `dependencies list` shows the installer version and
resolved images. `check --locked` verifies committed files without downloading or
changing them.

To select a newly published goblinctl release:

```bash
cargo xtask release pin-installer --version X.Y.Z
```

This downloads and authenticates the release, requires it to match the
checkout's installer inputs and capabilities, then updates the catalog and lock
together. It does not refresh Docker image tags.

## Checked-in image selections

Read selected versions from the catalog, deployment manifests, installer, and
Dockerfile; upstream bundles own their component images. The Chisel development
gateway is described in [the Chisel guide](../deploy/chisel/README.md).

The installer pins F5 NGINX Ingress Controller 5.7.0 and Helm chart 2.8.0 in
`deploy/azure/setup/nginx.sh`. It verifies the official ingress template and
changes only its upstream Host directives to keep the browser authority,
including a local forwarded port. When upgrading the controller, refresh and
verify both the template and chart checksums; keep forwarded-header trust
disabled. The Kubernetes 1.36 and cert-manager 1.21 pairing is described in the
[installation reference](../deploy/azure/reference.md#what-deployment-success-means).

PostgreSQL 18 mounts its PVC at `/var/lib/postgresql` and stores data in
`/var/lib/postgresql/18/docker`. Changing the image tag cannot upgrade database
files, and older data formats are unsupported. When formats change, recreate
disposable development databases and installations; there is no in-place upgrade
workflow. See [database setup](database.md#apply-the-sql-schema).

## Source files and compatibility

Edit the Dockerfile, deployment YAML, and scripts directly. The resolver changes
only image references whose repository matches a catalog entry and keeps
commands, settings, formatting, and comments. If an image moves to another
registry or repository, keep its catalog key (such as `postgres`) so the previous
lock can find the references to update. Use one version per image repository.

For a new workload, write its image reference in the YAML file, add the repository
and tag to `dependencies.toml`, and run `resolve`. CI rejects deployed image
references that differ from the lock or are missing from the catalog, checks
digests where YAML uses them, and checks build-image overrides. The Goblin image
is built from source during installation.

`deploy/install-request.json` lists the resources in `deploy/platform/` and
`deploy/azure/platform/` with their hashes. goblinctl validates it with the Rust
`InstallRequest` type, which also prepares the deployment files, and
`goblinctl metadata --json` prints its schema. `cargo xtask release verify-installer`
runs the published installer's own validator.

Installer shipping inputs are listed in
[`tools/goblinctl/release-inputs.json`](../tools/goblinctl/release-inputs.json),
including installer code, setup scripts, and the PostgreSQL manifests.
**Platform Release** reports which inputs changed since the pinned release.
Installer changes can merge and wait for a later release. Changing an application
image such as Headlamp does not change installer inputs. Digests identify
artifacts; compatibility still needs integration tests. Images inside the
upstream k3s, cert-manager, and Agent Sandbox releases are managed by those
releases.

CI runs `dependencies check --locked` and source tests. **Platform Release**
selects and verifies the installer and publishes the exact Goblin and goblinctl
pair after one approval; it creates no dependency-pin PR. See
[GitHub Actions](github-actions.md) for the installation and website workflows.

## Dependency inventory and updates

Read versions from their owners, not from a copied inventory:

- .NET: `global.json`, `backend/Directory.Build.props`, project files, and
  `.config/dotnet-tools.json`. Regenerate EF mappings with
  `backend/scripts/scaffold-database.sh`.
- Node and Codex: the root, `frontend`, `website`, and `website/video`
  `package.json` files and their lockfiles. Website video tools are
  development-only.
- Rust: `rust-toolchain.toml`, workspace and crate manifests, and `Cargo.lock`;
  setup scripts and shared actions own native tool pins. See
  [Rust development](rust-development.md).
- Deployment: the catalog and lock above, the Dockerfile, installer scripts, and
  manifests. The Slack setup helper version and checksum live in
  `backend/src/Goblin.Integrations.Slack/`.
- GitHub Actions references and `skills-lock.json` own workflow and imported-skill
  pins; see [workflow contracts](github-actions.md) and
  [third-party notices](../THIRD_PARTY_NOTICES.md).

Automated version-update monitoring is not configured. Dependency changes need the
affected integration checks: PostgreSQL tests are opt-in, authenticated runtime
checks need service access, and Azure installation is manual. See
[CI selection](github-actions.md#ci-selection-and-required-result).
