# Deployment dependencies

[`dependencies.toml`](../dependencies.toml) contains only the published goblinctl
release Goblin uses and the separate service images it deploys:

```toml
[goblinctl]
version = "0.1.4"

[images]
postgres = "postgres:18.6-bookworm"
headlamp = "ghcr.io/headlamp-k8s/headlamp:v0.45.0"
```

Goblin and goblinctl keep independent versions. `goblinctl.version` selects the
published installer; the Cargo workspace version identifies the installer being
built during ordinary development. Coordinated release preparation may supply an
independent goblinctl build version without editing Cargo files. The candidate's
release record owns its exact installer pairing; preparation can reuse a newer
matching publication or build one without changing this development pin.
Cargo, npm, and NuGet dependencies stay in their native project
files. Tool versions and download checksums stay in their existing setup files.

Each image owns what it contains. Goblin's Dockerfile selects its Node and .NET
base images, and the final Goblin image includes the .NET runtime. Those build
dependencies do not need entries in this deployment catalog or its lock. Docker
handles image layers, while Cargo, npm, and NuGet handle their own transitive
package dependencies.

To override a build image explicitly, add its repository and desired tag under
`[images]`, for example `dotnet-runtime = "mcr.microsoft.com/dotnet/aspnet:10.0-noble"`.
The resolver then updates and checks that image tag in the Dockerfile and
logging check script. Dockerfile references use tags without digests; an override's
resolved digest stays in the lock as metadata. Removing an override stops catalog
management; the current reference in the source file becomes the build's default.

goblinctl is released from Goblin's own repository, `jgador/goblin`. The release
tooling owns that convention and defaults to it; the catalog only selects the
version. The generated lock records the repository for provenance. Moving
goblinctl to a separate repository would require a deliberate tooling change.

## Updating dependencies

Edit an image reference in `dependencies.toml`, then run:

```bash
cargo xtask dependencies resolve
cargo xtask dependencies check --locked
```

Resolution looks up new or changed tags with Docker Buildx, checks support for the
installer's `linux/amd64` platform, and updates matching image references in the
deployment YAML. Tags stay as tags; a YAML reference containing `@sha256:...` also
gets its digest updated from the lock. PostgreSQL uses tags only. Explicit
build-image overrides also update the Dockerfile and logging check script.
It preserves existing digests for unchanged tags. Use `resolve --refresh` to
refresh all tags deliberately. It does not pull image layers or run package
managers. Review the updated catalog, lock, and references together.

`dependencies.lock.json` records resolved image digests and the selected goblinctl
release manifest under `goblinctl.release`, including its source, checksum, and
shipping-input inventory. It declares `"platform": "linux/amd64"` once for all images. Each image
keeps one digest: the immutable image index (or the manifest for a single-platform
image). Resolution verifies that every image supports that platform; hashes for
other architectures are not stored. For references using tags only, the digest is
resolution metadata; the image registry supplies the contents of that tag when
pulled. It is generated; edit the TOML file rather
than the lock. The `list` subcommand shows the installer version and resolved
images. The `check --locked`
subcommand verifies committed files without downloading or changing them.

For a new published goblinctl release, run the authenticated pin commands:

```bash
cargo xtask release pin-installer --version X.Y.Z
```

This updates the release selection and dependency lock together. Azure assets are
generated during release preparation. It does not refresh Docker image tags.

## Checked-in image selections

Read selected versions from the catalog, deployment manifests, installer, and
Dockerfiles; upstream bundles own their component images. Chisel's development
gateway and its deployment are described in [the Chisel guide](../deploy/chisel/README.md).

The installer also pins F5 NGINX Ingress Controller 5.7.0 and Helm chart 2.8.0.
It verifies the corresponding official ingress template and changes only its
upstream Host directives to preserve the browser authority, including a local
forwarded port. Controller upgrades must refresh and verify both template and
chart checksums; forwarded-header trust stays disabled.
Kubernetes 1.36/cert-manager 1.21 uses a published supported pairing; see the
[installation reference](../deploy/azure/reference.md#what-deployment-success-means).

PostgreSQL 18 mounts its PVC at `/var/lib/postgresql` and stores the database in
`/var/lib/postgresql/18/docker`. Superseded PostgreSQL data formats are unsupported.
Changing an image tag cannot
upgrade database files. The startup script rejects an older Goblin data layout.
Recreate verified disposable development databases/installations deliberately
when formats change; no in-place upgrade workflow is provided. Source edits do
not change a running installation. See [database setup](database.md#apply-the-sql-schema).

## Source files and compatibility

Edit the Dockerfile, deployment YAML, and scripts directly. The resolver changes
only image references, matching their repository names to the catalog. It preserves
commands, settings, formatting, and comments. If an image moves to another registry
or repository, keep its catalog key (such as `postgres`) so the previous lock can
identify the references to update. Use one version per image repository.

For a new workload, write its image reference in the normal YAML file and add that
repository and tag to `dependencies.toml`, then run `resolve`. CI rejects deployed
service image tags that differ from the lock or are missing from the catalog,
verifies digests where the YAML explicitly uses them, and
checks any explicit build-image overrides. The Goblin
image itself is built from source; installation supplies its resulting image tag.

`deploy/install-request.json` is generated from the actual application deployment
resources. goblinctl validates it using the Rust `InstallRequest` type that also
prepares the deployment files. `goblinctl metadata --json` exposes the schema from
that same type. The authenticated release check invokes the published installer's
actual validator, and deployment tests check installation behavior.

The release fingerprint includes installer code, scripts, and embedded PostgreSQL
manifests. **Platform Release** reports changes since the pinned release.
A new binary is needed to distribute installer changes, but those changes can
merge and be batched before a maintainer chooses to release.
Changing an application image such as Headlamp does not change installer inputs.
Image digests establish artifact identity; behavioral compatibility still requires
integration tests. Images inside upstream k3s, cert-manager, and Agent Sandbox
releases remain managed by those upstream releases.

CI runs `dependencies check --locked` and source tests. **Platform Release**
selects and verifies the installer, then obtains one approval to publish the exact
Goblin–goblinctl pair. It creates no dependency-pin PR. The [release guide](releases.md) describes the
complete routine and installation verification.
The **Installation Assets** workflow prepares the authenticated installation catalog
after publication and calls **Website Release** to publish the website image and
deploy it after production approval. The website serves the catalog and Azure
templates directly; see [GitHub Actions](github-actions.md) for their boundaries.

## Dependency inventory and updates

Read versions and resolved inputs from their owners, not a copied inventory:

- .NET: `global.json`, `backend/Directory.Build.props`, project files, and
  `.config/dotnet-tools.json`. Restore resolves packages; regenerate EF mappings
  with `backend/scripts/scaffold-database.sh`.
- Node and Codex: root, frontend, website, and website/video `package.json`
  files and their corresponding lockfiles. Website video tools are development-only.
- Rust: `rust-toolchain.toml`, workspace and crate manifests, and `Cargo.lock`;
  setup scripts and shared actions own native tool pins. See [Rust development](rust-development.md).
- Deployment: the catalog and lock above, Dockerfiles, installer and manifests.
  Slack setup pins its private on-demand helper; Codex and Kubernetes schema
  selections own their checksums and generators.
- GitHub Actions references and `skills-lock.json` own workflow and imported-skill
  pins; see [workflow contracts](github-actions.md) and [third-party notices](../THIRD_PARTY_NOTICES.md).

Version-update monitoring is not configured. Any future automation should discover
all package roots, images, downloads, and schema pins; update checksums, regenerate
assets, and review major upgrades. Dependency changes need affected integration
checks: PostgreSQL tests are opt-in, authenticated runtime checks require service
access, and Azure installation remains manual. See [CI selection](github-actions.md#ci-selection-and-required-result).

A complete SBOM must use actual restored packages, lockfiles, built images, OS
packages, and bundled components. Host inventories may differ from declarations;
store generated inventories under `.artifacts/`.
