# Native administration with goblinctl

`goblinctl` is Goblin's operator CLI. It installs and inspects the host, serves
the temporary setup UI, manages local services and owner credentials, and runs
PostgreSQL provisioning and migration Jobs. The web UI controls Work; Work
lifecycle rules and SQL migrations stay in .NET. The CLI never retries failed Work
or changes Work history.

The main application is component `platform`; the public site is component
`website`. Both use namespace `goblin`. Pod operations select `app=platform` and
leave website pods alone. See [Kubernetes names](kubernetes-names.md).

The release archive is `goblinctl-x86_64-unknown-linux-musl.tar.gz`, a statically
linked Linux executable. The supported host is Ubuntu 24.04 on x86-64, including
WSL with systemd. Installed hosts do not need Python, Rust, or Cargo; the
installer adds curl, Docker, and k3s when they are missing.

## Commands

```bash
sudo goblinctl install --hostname goblin.example --certificate-email owner@example.com --source-ref COMMIT
sudo goblinctl install status --json
sudo goblinctl install retry
sudo goblinctl status --json
sudo goblinctl doctor --json
sudo goblinctl logs --follow
sudo goblinctl password set --replace
sudo goblinctl db setup
sudo goblinctl db host
sudo goblinctl db migrate IMAGE
```

`install` with `--hostname` requires `--certificate-email` and starts a public
HTTPS installation; `--source-ref` defaults to `master`. Without `--hostname` it
starts a dedicated local installation, like `local start`.

`status` checks live services and application readiness; `install status` shows
installer progress. `doctor` also checks the cert-manager rollouts and the
ownership and mode of the private configuration directory. These checks are
read-only, print versioned JSON with `--json`, and exit 1 when a check fails. They
do not prove certificate authentication or a working model run. `logs` shows the
last 100 lines of the bootstrap and installer logs; `--follow` streams them.

`password set` keeps a valid saved verifier; `--replace` chooses a new password.
It prompts twice without echo, or reads `GOBLIN_LOCAL_PASSWORD` for unattended
setup. Passwords and verifiers never go into CLI arguments. On an installed host,
`--replace` requires a finished installation and fails while the installer lock is
held. It updates the Kubernetes Secret, saves the verifier, and restarts the
application pod, which ends existing sessions. If it fails after the Secret is
replaced, check the pod and finish its restart. `--path` manages a verifier file
instead of a running host.

Dedicated local installations check ownership and remember their checkout:

```bash
make local-start                 # runs the CLI from the current source
sudo goblinctl --repo "$PWD" local start      # uses a precompiled binary
sudo goblinctl local stop                     # keeps application data and PVCs
sudo goblinctl local retry --refresh          # retries a failed install with the current checkout
sudo goblinctl local reset --yes              # deletes only this owned local cluster
goblinctl --repo "$PWD" password set --replace
sudo goblinctl local start                    # loads the changed local verifier
goblinctl local database
```

`local status`, `local logs`, and `local password` inspect the local installation.
The local source snapshot includes tracked and untracked Git-visible files,
excludes ignored files, and refuses symlinks. A completed installation starts
again without rebuilding. A failed installation needs an explicit retry. `retry`
reuses the failed attempt's source snapshot. `retry --refresh` works only on a
stopped, failed installation; it first replaces the snapshot and installed setup
assets with the current checkout and CLI, so a fix can be tested without deleting
the cluster. `stop` suspends; `reset` deletes the owned test cluster and its
application data but keeps the repository password verifier, the native CLI, and
the Docker and download caches.

The installer runs a root worker and a separate unprivileged (`DynamicUser`) setup
service. Public setup serves static assets, status, health, and the read-only
`/setup/events` progress stream, with bounded request sizes, connections, and
timeouts. Public Azure setup cannot retry; use `sudo goblinctl install retry`.

Local setup also accepts `POST /setup/retry` with a JSON body containing the owner
password. Requiring JSON makes cross-site browser requests fail CORS preflight.
The setup service passes the body unread to `goblin-setup-control.socket`, where
systemd starts one root `goblinctl internal setup-control` process at a time. It
returns `not-failed` unless the installation failed, checks the password against
the private verifier, and starts `goblin-installer.service` unless it is already
running. Responses are `202 started`, `400 invalid`, `401 unauthorized`,
`409 not-failed` or `running`, `429 throttled`, and `503 unavailable`. Five
consecutive wrong passwords lock page retries for one minute, doubling up to 15
minutes; root-only `retry-throttle.json` beside the verifier records this.

PostgreSQL client profiles do not depend on `--repo` or installer state. See
[the database guide](database.md) for WSL and Azure connections, Windows exports,
and profile selection. Client commands need `psql` and OpenSSL; Azure access also
needs SSH.

Developer-only `make relay-start`, `make relay-status`, and `make relay-stop` run
through xtask; see [ChatGPT local testing](chatgpt-local-testing.md). The
production CLI has no relay commands.

## Parallel installation and live progress

The installer builds the Goblin image while it sets up k3s. After k3s is ready,
NGINX, cert-manager, Agent Sandbox, and the service-image prefetch run in
parallel; PostgreSQL, migration, deployment, and verification follow in
dependency order. Traefik is disabled; the pinned F5 NGINX chart is
checksum-verified and its Service starts as `ClusterIP`.

The pinned k3s binary and airgap image archive are verified and kept in
`/var/cache/goblin/downloads`. Retries and local resets reuse them; files for
other k3s versions are removed. Downloads resume from partial files and fail only
when less than 1 KiB/s arrives for two minutes, with six attempts in total.
Permanent HTTP errors stop at once.

Four workers prefetch service images by default. To tune a smaller host, set
`GOBLIN_IMAGE_PULL_WORKERS` to `1` through `8` in a systemd environment override
for `goblin-installer.service`. Each image pull is tried up to three times; a
two-hour limit per pull ends hangs that containerd misses.

The setup page shows concurrent steps, the latest activity of each step, and a
live log of build stages, cache reuse, image downloads, and database checks. The
status document keeps the latest 300 structured entries, so a page refresh
restores them. Public messages contain only authored progress events and BuildKit
records; package and compiler output, credentials, and upstream errors stay in the
private host log (`sudo goblinctl logs`).

After a failure, no new tasks start; running workers finish so their downloads and
builds can be reused. A failed installation disables its boot unit and needs an
explicit retry, from the setup page with the owner password or with
`goblinctl install retry`. A retry runs every step again and reapplies manifests.
It reuses and checks the retained source archive, password, images, build cache,
certificates, and PVCs. Installation failure never resets the database.

`goblinctl db migrate` checks its owned `goblin-schema` Job first. It waits for an
active Job, collects a completed one, and replaces a failed one. A Job with a
different image or owner needs manual inspection. The
database's advisory lock and migration ledger still control schema application.

## Source and build boundaries

The Rust workspace has two crates:

- `tools/goblinctl`: the CLI and its library. It embeds an explicit allowlist of
  setup assets, shell scripts, and PostgreSQL manifests, so an installed host does
  not need the checkout.
- `tools/xtask`: developer and CI commands (`cargo xtask --help`), including
  packaging, releases, dependency resolution, Azure asset generation, and
  development startup. It uses the CLI library.

See the [Rust development guide](rust-development.md) for tools, build, lint, and
test commands.

`make dev-start DB_PROFILE=wsl` prepares the database profile and owner
credentials, then starts .NET through `cargo xtask dev --profile wsl`. Shell
helpers find the CLI through `GOBLINCTL` or `PATH`; developer scripts can set
`GOBLINCTL="$PWD/target/debug/goblinctl"`. `--repo` selects the checkout used by
local installation and owner-password commands; PostgreSQL client commands use the
per-user [profile store](database.md#profile-management-and-storage) instead.

## Releases

[The release guide](releases.md) covers coordinated Goblin and goblinctl
candidates, publication approval, website installation, and development pin
updates. Changes to embedded inputs need a new installer publication.

To build a local review archive in `.artifacts/goblinctl/`:

```bash
make release-package-local
```

`cargo xtask release build-installer` requires a clean checkout whose commit is
on `origin/master`, and builds in a fresh temporary target directory before
packaging. `cargo xtask package --binary PATH` alone does not prove that a binary
came from this checkout.

The archive contains `goblinctl`, `LICENSE`, `NOTICE`, and
`THIRD_PARTY_NOTICES.md`; setup assets are compiled into the binary. Archive
order, timestamps, ownership, modes, and compression are deterministic for the
same binary; compiler output is not claimed to be reproducible across machines.

Bootstrap downloads the exact pinned version over HTTPS and checks its checksum,
archive contents, and version before running it; there is no `latest` fallback.
It stops the installer and takes the installer lock before activating the new
binary. `/opt/goblin/bin/goblinctl` points into versioned directories, and
`/usr/local/bin/goblinctl` puts it on `PATH`.

Goblin is preproduction and supports only current installation formats. When
formats change, recreate disposable installations; there is no in-place upgrade
tooling.

## Verification

For tooling changes, use the [Rust checks](rust-development.md#build-format-lint-and-test),
`make release-check-installer`, deterministic packaging, and the affected
deployment and browser journeys. A local review archive is not a published
installer. Real PostgreSQL, systemd and Kubernetes behavior, and manual Azure
installation need their own integration checks; report skips.
