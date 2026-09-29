# Deployment dependencies

[`dependencies.toml`](../dependencies.toml) contains only the published goblinctl
release Goblin uses and the separate service images it deploys:

```toml
[goblinctl]
version = "0.1.1"

[images]
postgres = "postgres:16.15-bookworm"
headlamp = "ghcr.io/headlamp-k8s/headlamp:v0.45.0"
```

Goblin and goblinctl keep independent versions. `goblinctl.version` selects the
published installer; the Cargo workspace version identifies the installer being
built from source. Cargo, npm, and NuGet dependencies stay in their native project
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
managers. Commit the updated files together.

`dependencies.lock.json` records resolved image digests and the selected goblinctl
artifact. It declares `"platform": "linux/amd64"` once for all images. Each image
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
cargo xtask pin-release --version X.Y.Z
cargo xtask azure
```

This updates the release selection, dependency lock, release pin, and ARM templates
together. It does not refresh Docker image tags.

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
manifests. `cargo xtask release-status` reports changes since the pinned release.
A new binary is needed to distribute installer changes, but those changes can
merge and be batched before a maintainer chooses to release.
Changing an application image such as Headlamp does not change installer inputs.
Image digests establish artifact identity; behavioral compatibility still requires
integration tests. Images inside upstream k3s, cert-manager, and Agent Sandbox
releases remain managed by those upstream releases.

CI runs `dependencies check --locked`, source tests, and the advisory release-status
report. It does not publish, bump versions, create PRs, or update release pins.
The [release guide](goblinctl-releases.md) describes manual publication and explicit
deployment verification. Commit all five outputs from the pin commands together.
