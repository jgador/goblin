# Native administration with goblinctl

Goblin ships a permanent operator CLI. The web UI controls Work; Work lifecycle
rules and SQL migrations remain in .NET. The CLI installs and inspects the host,
serves the temporary setup UI, manages local services and credentials, and runs
PostgreSQL provisioning. It does not retry failed Work or modify Work history.

Production uses `goblinctl-x86_64-unknown-linux-musl.tar.gz`, following Codex's
native Linux distribution model. This is a statically linked Linux executable;
Ubuntu 24.04 on x86-64, including WSL with systemd, is the supported installation
host. The distribution target does not promise support for arbitrary distributions.
Python, Rust, and Cargo are unnecessary on installed hosts. Bash, systemd, curl,
Git, k3s, and the application's existing build/runtime dependencies remain.

## Commands

```bash
goblinctl install --hostname goblin.example --source-ref COMMIT
sudo goblinctl install status --json
sudo goblinctl install retry
sudo goblinctl status --json
sudo goblinctl doctor --json
sudo goblinctl logs --follow
sudo goblinctl password set --replace
sudo goblinctl db setup
goblinctl --repo "$PWD" db credentials --port 55432
sudo goblinctl db migrate IMAGE
```

`status` checks live services and application readiness; `install status` reports
historical installer progress. `doctor` adds certificate-manager rollouts and
private-directory ownership checks. These health checks are read-only, return
versioned JSON with `--json`, and exit 1 when a check fails. They do not establish
end-to-end certificate authentication or an authenticated model execution.

`password set` reuses a valid saved verifier; `--replace` selects a new password.
It prompts twice without echo, or reads `GOBLIN_LOCAL_PASSWORD` for unattended
setup. With an installed Azure host it updates the Kubernetes Secret, retains the
verifier, and restarts the application pod. The private installer lock excludes
concurrent installation. Failure after Secret replacement requires inspecting the
pod and completing its restart. Passwords and verifiers never enter CLI arguments.
`--path` explicitly manages a verifier file instead of changing a running host.

Dedicated local installations retain ownership checks and a checkout association:

```bash
npm run install:local -- start                 # builds the current CLI for development
sudo goblinctl --repo "$PWD" local start      # uses a precompiled binary
sudo goblinctl local stop                     # retains application data and PVCs
sudo goblinctl local reset --yes              # deletes only this owned local cluster
goblinctl --repo "$PWD" password set --replace
sudo goblinctl local start                    # loads the changed local verifier
sudo goblinctl db forward --port 55432
```

The local source snapshot includes Git-visible working-tree changes and untracked
files, excludes ignored stores/build products, and refuses symlinks. A completed
installation resumes without rebuilding. Failed installation requires explicit
retry. Stop is suspension; reset is explicit deletion of the owned test cluster.
The native CLI remains installed after reset.

The installer retains its existing root worker and unprivileged DynamicUser setup
service as separate processes. Public setup exposes only static assets, status,
and health. Its local health socket appears only after the public listener binds.
The server bounds request sizes, connection count and timeouts. The existing
handoff marker, installer lock, rollback, and retained-source retry rules remain.

## Source and build boundaries

The root Rust workspace has two crates:

- `tools/goblinctl`: CLI with shared credential, installation, state/server,
  database, file, and local-administration modules. It embeds an explicit asset
  allowlist, including the existing shell orchestration and manifests. Installed
  operation does not require the original checkout or downloaded application source.
- `tools/xtask`: developer/CI commands for native packaging, release pin updates,
  deterministic ARM generation, development startup, EF newline normalization,
  and optional PostgreSQL tests. It reuses the CLI library.

```bash
just build --workspace
just test --workspace
just test-doc --workspace
just clippy --workspace --all-targets -- -D warnings
just fmt-check
npm test
npm run test:browser -- tests/e2e/setup.spec.ts
```

Install the pinned helpers and configure your editor using the
[Rust development guide](rust-development.md).

`npm start` prepares credentials and launches .NET through `cargo xtask dev`.
Protocol/Kubernetes generators, EF scaffolding, and the .NET SQL migration engine
remain authoritative. Production shell helpers find the installed CLI via
`GOBLINCTL` or PATH; developer scripts can use `GOBLINCTL="$PWD/target/debug/goblinctl"`.
`--repo` selects the credential/settings destination. On a standalone installed
host, database exports default to `/var/lib/goblin/config`.

## Release order

Goblin must pin a compatible **published** installer before merging to `master`.
The [release automation guide](goblinctl-release-automation.md) describes the
required check, companion PRs, publication approval, and initial repository setup.

1. The dependency check opens a companion release PR against the Goblin feature
   branch when shipping installer inputs or required capabilities differ.
2. Review the installer changes and proposed Cargo version. Dispatch
   `.github/workflows/goblinctl-release.yml` from `master` with the companion PR
   number and its exact reviewed head SHA. Candidate tests run independently of
   the already-published dependency check, avoiding a circular dependency.
3. Approve the `goblinctl-release` environment after the candidate tests pass.
   The workflow builds from clean source in a fresh target directory, publishes
   `goblinctl-vMAJOR.MINOR.PATCH`, and attests both archive and dependency metadata.
   Existing tags/releases are never overwritten; pushing a tag does not publish.
4. The pin workflow downloads and verifies those published artifacts, updates the
   companion PR's pin and ARM templates, and explicitly dispatches compatibility
   checks. Merge the companion into the feature branch, then merge the Goblin PR
   once `goblinctl-release-ready` passes on its proposed merge result.

To repair a pin locally after publication:

```bash
cargo xtask pin-release --repo jgador/goblin --version MAJOR.MINOR.PATCH
cargo xtask azure
```

Pinning requires the archive and manifest's GitHub provenance, clean source
metadata, matching installer inputs, and all capabilities required by Goblin.
The manifest records version, target, archive digest, source revision, dirty-source
indicator, Cargo.lock digest, and a versioned per-file installer fingerprint.
Local review packages cannot replace this publication verification.

To build a review artifact locally:

```bash
rustup target add x86_64-unknown-linux-musl
cargo build --locked --release -p goblinctl --target x86_64-unknown-linux-musl
cargo xtask package --binary target/x86_64-unknown-linux-musl/release/goblinctl
```

`cargo xtask release-build` instead requires a clean checkout and compiles into a
fresh temporary target directory before packaging. Release CI uses that command;
`package` alone does not prove an arbitrary supplied binary came from this checkout.

Local packages are written to `.artifacts/goblinctl/`, outside the test-output
directory that `npm run build:tools` cleans.

The archive contains only `goblinctl`; setup assets are compiled into that binary.
Archive order, timestamps, ownership, modes and compression are deterministic for
the same binary. Compiler-level reproducibility across machines is not claimed.
The checksum covers final archive bytes. Bootstrap downloads the exact pinned
version over HTTPS and validates it before execution. A failed download prevents
setup readiness; there is no `latest` fallback. Existing worker shutdown/recovery
and locking precede activation of the new binary. A stable
`/opt/goblin/bin/goblinctl` points into versioned directories; `/usr/local/bin/goblinctl`
exposes it on PATH.

Broader production lifecycle and application/cluster upgrade orchestration remain
future work. The initial CLI implements current operations and live inspection;
changing Work lifecycle behavior or inventing upgrade semantics is outside this migration.

## Migration verification — 2026-09-27

Local verification passed:

- Seven Rust contract tests through nextest, rustfmt, and the shared Codex Clippy
  rules with warnings denied. The complete Stop hook passed after the just migration.
- `npm test`: 246 .NET tests passed and 39 database-dependent tests skipped;
  84 HTTP/deployment cases passed and one database-dependent HTTP case skipped.
- All 42 deployment cases also passed against the initial packaged musl executable after
  moving the worker's database operations to embedded CLI assets. A further
  retained-source check makes obsolete source provisioning scripts fail if invoked;
  installation succeeds using the native tooling.
- After aligning Rust development with Codex, 14 credential, PostgreSQL tooling,
  and setup-server checks passed against the refreshed musl archive.
- All four setup browser journeys passed, including desktop/mobile branding,
  progress/retry, refresh, and handoff.
- The initial migration archive ran under a real hardened DynamicUser systemd service on
  Ubuntu/WSL, serving public status and Unix-socket health with no tools on PATH.
- Repeated packaging produced identical archive bytes. ARM drift checks passed,
  and secret scanning reported no findings.

No fresh Azure deployment or full k3s installation was performed. Database tests
were not enabled, so this does not claim live PostgreSQL coverage. GitHub release
publication remains a rollout step; the current pin identifies the local review
artifact and must be replaced with the committed release manifest before Azure use.
