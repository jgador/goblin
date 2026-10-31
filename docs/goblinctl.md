# Native administration with goblinctl

Goblin ships a permanent operator CLI. The web UI controls Work; Work lifecycle
rules and SQL migrations remain in .NET. The CLI installs and inspects the host,
serves the temporary setup UI, manages local services and credentials, and runs
PostgreSQL provisioning. It does not retry failed Work or modify Work history.

The main application is component `platform`; the independently deployed public
site is component `website`. Both use namespace `goblin`. Installer manifests are
in `deploy/platform/` and `deploy/azure/platform/`; embedded setup includes
`deploy/azure/install-platform.sh`. Pod operations select `app=platform` and leave website
pods alone. See [Kubernetes names](kubernetes-names.md) for the full naming and
persistent-storage ownership. Embedded input changes require a new installer
publication; the pin records the selected publication's immutable provenance.

Production uses `goblinctl-x86_64-unknown-linux-musl.tar.gz`, following Codex's
native Linux distribution model. This is a statically linked Linux executable;
Ubuntu 24.04 on x86-64, including WSL with systemd, is the supported installation
host. The distribution target does not promise support for arbitrary distributions.
Python, Rust, and Cargo are unnecessary on installed hosts. Bash, systemd, curl,
Git, k3s, and the application's existing build/runtime dependencies remain.

## Commands

The PostgreSQL commands on this page assume a root shell.

```bash
goblinctl install --hostname goblin.example --certificate-email owner@example.com --source-ref COMMIT
sudo goblinctl install status --json
sudo goblinctl install retry
sudo goblinctl status --json
sudo goblinctl doctor --json
sudo goblinctl logs --follow
sudo goblinctl password set --replace
goblinctl db setup
goblinctl db host
goblinctl db connect wsl --local --forward-port 55432
goblinctl db migrate IMAGE
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
make local-start                 # builds the current CLI for development
sudo goblinctl --repo "$PWD" local start      # uses a precompiled binary
sudo goblinctl local stop                     # retains application data and PVCs
sudo goblinctl local retry --refresh          # retries a failed install with the current checkout
sudo goblinctl local reset --yes              # deletes only this owned local cluster
goblinctl --repo "$PWD" password set --replace
sudo goblinctl local start                    # loads the changed local verifier
goblinctl local database
```

The local source snapshot includes Git-visible working-tree changes and untracked
files, excludes ignored stores/build products, and refuses symlinks. A completed
installation resumes without rebuilding. Failed installation requires explicit
retry. `retry` reuses the failed attempt's source snapshot; `retry --refresh`
first replaces that snapshot and the installed setup assets with the current
checkout and CLI, so a fix can be tested without deleting the cluster. It accepts
only a stopped, failed installation. Stop is suspension; reset is explicit deletion
of the owned test cluster. The native CLI, its license documents, and the verified
download cache in `/var/cache/goblin/downloads` remain after reset.

The installer retains its existing root worker and unprivileged DynamicUser setup
service as separate processes. Public setup exposes static assets, status,
health, and the read-only `/setup/events` progress stream. Its local health socket appears only after the public listener binds.
The server bounds request sizes, connection count and timeouts. The existing
handoff marker, installer lock, rollback, and retained-source retry rules remain.

Public Azure setup is read-only and has no retry helper connection. Use the root
CLI to retry. Local loopback setup exposes `POST /setup/retry`, accepting a JSON body
with the owner password. The setup service
relays it, unread, to `goblin-setup-control.socket`. For each connection, systemd starts a root
`goblinctl internal setup-control` process, one at a time. That process returns
`not-failed` unless the installation has failed, checks the password against the
private verifier, and starts `goblin-installer.service` unless it is already
active. Responses are `202 started`, `400 invalid`, `401 unauthorized`,
`409 not-failed` or `running`, `429 throttled`, and `503 unavailable`. Five
consecutive wrong passwords lock page retries for one minute, doubling up to 15
minutes; the root-only `retry-throttle.json` beside the verifier records this.
Requiring JSON makes cross-site browser requests fail CORS preflight.

PostgreSQL client profiles are independent of `--repo` and installer state. See
[the database guide](database.md) for WSL/Azure connections, Windows exports,
refresh, cleanup and per-command profile selection. Client commands need `psql`
and OpenSSL; Azure access also needs SSH.

Developer-only `make relay-start`, `make relay-status`, and `make relay-stop`
commands live in xtask. See [ChatGPT local testing](chatgpt-local-testing.md).
The production CLI contains no WSL relay commands.

## Parallel installation and live progress

After retaining and validating the source snapshot, the installer builds Goblin
alongside k3s setup. K3s setup downloads the pinned k3s binary and airgap
system-image archive in parallel, verifies both, and keeps them in
`/var/cache/goblin/downloads`. Retries and local resets reuse them, and an
installation removes cached files from other k3s versions. The upstream install
script then runs with `INSTALL_K3S_SKIP_DOWNLOAD=true`. K3s imports the archive at
startup. Traefik is disabled. A separate worker installs pinned F5 NGINX from a
checksum-verified inline Helm chart and starts its Service as `ClusterIP`.

After k3s is ready, NGINX, certificate manager, Sandbox, and the service-image prefetch
run concurrently. Each add-on pulls its own images before applying manifests.
PostgreSQL waits for both add-ons and the prefetch; image import waits for the build
and k3s; migration waits for PostgreSQL and image import. Deployment waits for the
migration and image downloads. Verification waits for NGINX and application readiness. Azure then serves progress
and ACME validation publicly before enabling the final HTTPS Goblin route.
Downloads come first, so the fixed rollout timeouts measure startup only.
The production Dockerfile keeps its original build stages and asset packaging.

Installer downloads resume from partial files. They fail only when under 1 KiB/s
arrives for two minutes, with six attempts in total; permanent HTTP errors stop
immediately. Four workers prefetch service images by default. Set
`GOBLIN_IMAGE_PULL_WORKERS=1` through `8` in a `goblin-installer.service` systemd
environment override to tune smaller hosts. Containerd cancels an image pull that
stops receiving data; the installer resumes it up to three times. A two-hour limit
per pull only ends a hang that containerd misses. Kubernetes and BuildKit also
perform their own pulls. Service references come from validated application
manifests and the embedded PostgreSQL manifest; add-on references use the
installer's pinned release versions.

The setup page shows concurrent steps and a live installation log, including build
stages, cache reuse, individual image downloads and database checks. Each running
task also shows its latest activity, such as k3s download size and rate. These
`progress` updates replace the task detail without adding log entries. The latest
300 structured entries, with timestamps and attempt IDs, are retained in the
atomic status document. Refreshing the page restores that tail. EventSource
reconnects with an event ID between bounded SSE batches; status polling continues
through the ingress handoff. “Follow latest” controls scrolling without pausing
collection. Public messages are authored progress events and reconstructed
BuildKit records; arbitrary package/compiler output, credentials and upstream
errors remain in the private host log, available through `sudo goblinctl logs`.

Each worker runs in an owned process group. A failure starts no new tasks. Workers
already running finish, so their downloads and builds are kept for the retry, and
the page reports how many are finishing. A service stop still stops all workers
before recovery releases the attempt lock. State writes
use a separate short file lock so concurrent updates cannot overwrite each other.
Failed installations disable their boot unit and require an explicit retry, either
from the setup page with the owner password or with `goblinctl install retry`;
the setup page remains available. The retained source archive, password, completed
Docker/k3s images, build cache, certificates and PVCs are reused and checked on retry.
Saved step completion alone never causes infrastructure readiness checks to be skipped.

The database migration command reconciles its named, owned job after interruption.
An active job is observed, a completed job is collected, and a confirmed failed job
can be replaced by an explicit invocation. A different image or owner requires
inspection. PostgreSQL's advisory lock and transactional migration ledger remain
authoritative for schema application. Installation failure never resets the database.

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
make rust-build
make rust-test
make rust-test-doc
make rust-lint
make rust-format-check
make test
make browser-test ARGS=tests/e2e/setup.spec.ts
```

Install the pinned helpers and configure your editor using the
[Rust development guide](rust-development.md).

`make dev-start DB_PROFILE=wsl` prepares the selected database profile and owner credentials,
then launches .NET through `cargo xtask dev --profile wsl`.
Protocol/Kubernetes generators, EF scaffolding, and the .NET SQL migration engine
remain authoritative. Production shell helpers find the installed CLI via
`GOBLINCTL` or PATH; developer scripts can use `GOBLINCTL="$PWD/target/debug/goblinctl"`.
`--repo` selects the checkout used by local installation and owner-password
commands. PostgreSQL client commands use the per-user profile store independently
of this flag; see [profile storage](database.md#profile-management-and-storage).
The local ownership file retains its existing `repo` JSON key.

## Releases

[The release guide](releases.md) covers coordinated Goblin and goblinctl
candidates, publication approval, website installation, and optional development
pin updates. Preparing a candidate does not publish it.

To build a review artifact locally:

```bash
rustup target add x86_64-unknown-linux-musl
cargo build --locked --release -p goblinctl --target x86_64-unknown-linux-musl
cargo xtask package --binary target/x86_64-unknown-linux-musl/release/goblinctl
```

`cargo xtask release build-installer` instead requires a clean checkout and compiles into a
fresh temporary target directory before packaging. Coordinated preparation performs
the same isolated build for its frozen release source;
`package` alone does not prove an arbitrary supplied binary came from this checkout.

Local packages are written to `.artifacts/goblinctl/`, outside the test-output
directory that `make tools-build` cleans.

The archive contains `goblinctl` and its license documents; setup assets are compiled into that binary.
Archive order, timestamps, ownership, modes and compression are deterministic for
the same binary. Compiler-level reproducibility across machines is not claimed.
The checksum covers final archive bytes. Bootstrap downloads the exact pinned
version over HTTPS and validates it before execution. A failed download prevents
setup readiness; there is no `latest` fallback. Existing worker shutdown/recovery
and locking precede activation of the new binary. A stable
`/opt/goblin/bin/goblinctl` points into versioned directories; `/usr/local/bin/goblinctl`
exposes it on PATH.

Goblin is preproduction and supports current installation formats. Recreate
verified disposable installations when those formats change; in-place application
or cluster upgrade tooling is not provided. CLI repair/retry reuses the current
installation's retained inputs and does not authorize retrying failed Work.

## Verification

Use [Rust checks](rust-development.md#build-format-lint-and-test), offline release
compiler/input checks, deterministic packaging, and affected deployment/browser
journeys for tooling changes. A local review archive is not a published installer.
Only authenticated release metadata establishes the pin used for published Azure
assets. Real PostgreSQL, systemd/Kubernetes behavior, and manual Azure installation
require their actual integration checks; report skips and unverified boundaries.
