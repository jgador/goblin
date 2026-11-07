# Develop with the WSL k3s installation

Run the same setup page and background installer used by Azure directly in Ubuntu 24.04
on WSL 2. The installer installs k3s, cert-manager, Agent Sandbox, and Goblin in this
Linux environment from your current checkout, including uncommitted changes. For
resource definitions and storage ownership, see
[architecture](../../docs/architecture.md#resource-and-storage-ownership).

`dev-*` runs Goblin directly in WSL; `k3s-*` manages this checkout's installed
single-node k3s environment. The commands explicitly select the WSL cluster, independent
of your ambient kubectl context.

## Start

With systemd enabled in WSL, run from the repository root:

```bash
make k3s-start
```

On first start, choose and confirm your Goblin password in the terminal. Open
**http://localhost:8788** in your Windows browser to see installation progress. When
Goblin is ready, the page opens it at the same address; enter your password.

The command requests `sudo` when needed, returns once the page is available, and leaves
installation running in the background. Keep WSL running; closing the browser or
terminal does not stop the installer. Starting an existing installation keeps its data
and password.

Goblin is preproduction. Recreate this installation when database, application, or
cluster formats change; use the [reset commands](#stop-resume-or-start-clean).

Windows reaches the page through WSL's default localhost forwarding. A forwarder from
loopback port 8788 to Linux port 80 keeps access working when Kubernetes takes over from
the setup page. The setup page stops and disables itself after installation; the
forwarder remains.

The progress page shows stages, tasks, and live logs. Progress counts completed tasks,
not time remaining. On failure, running tasks finish and no new tasks start; enter the
Goblin password and select **Retry installation** to resume.

Open **Settings → Tools → Cluster**, or **http://localhost:8788/headlamp/**, for
read-only Kubernetes access in Headlamp with your Goblin login. See
[cluster access](../../docs/logging.md#cluster-view).

To use a different browser port, choose it on the first start; changing it later
requires a reset:

```bash
make k3s-start ARGS="--http-port 8888"
```

## Requirements

Use Ubuntu on x86-64 with systemd, curl, Git, and Rust (the make targets build
`goblinctl` from the checkout). Allow several GiB of free memory and disk space for
Kubernetes, image downloads, and the application build. The installer needs Linux ports
80 and 443 and the browser port free. Windows port 80 can stay in use.

Docker Desktop is not required. The installer uses the native Linux Docker engine with
its own private client configuration for anonymous public-image pulls. It does not use
or change your Docker configuration, registry logins, credential helpers, contexts, or
builders. Docker Hub's anonymous pull limits apply.

If systemd is disabled, add this to `/etc/wsl.conf`, keeping other settings:

```ini
[boot]
systemd=true
```

Then run `wsl --shutdown` from Windows and reopen WSL. The runner refuses to adopt an
existing k3s or Goblin installation that it did not create. Use a dedicated WSL
distribution if your current one already has one.

## Progress and logs

```bash
make k3s-status
make k3s-logs
make k3s-logs ARGS=--follow
make k3s-retry
make k3s-retry ARGS=--refresh
```

`k3s-retry`, like **Retry installation** on the page, restarts a failed installation
with its saved source snapshot, password, and data. After fixing installer or
application code, use `make k3s-retry ARGS=--refresh`. It replaces the failed attempt's
source snapshot and installer with your current checkout and keeps the cluster,
completed images, and downloads. If bootstrap failed before the installer started, run
`make k3s-start` again. A completed installation reports `ready`, and both setup
services are stopped and disabled.

Logs are stored in WSL:

- `/var/log/goblin-bootstrap.log`
- `/var/log/goblin-installer.log`
- `.goblin-local/bootstrap.log` (output of the local start command)

For Kubernetes diagnostics, use `sudo k3s kubectl` and `sudo journalctl -u k3s`.

The installer provisions PostgreSQL and applies schema migrations before starting
Goblin. PostgreSQL is reachable from WSL at `localhost:5432` through
`goblin-local-postgres.socket`, which binds only to loopback and forwards to the
database Service in Kubernetes. Certificate authentication still applies. For client
certificates and Windows access, see
[the PostgreSQL workflow](../../docs/database.md#wsl-and-windows-pgadmin).

To enable or repair this endpoint for an installed database:

```bash
make k3s-configure-database
```

Inspect it from a root shell with `journalctl -u goblin-local-postgres.service`.

## Stop, resume, or start clean

```bash
make k3s-stop
make k3s-start
```

`stop` shuts down this runner's installer, browser and database forwarders, and
Kubernetes containers, and keeps all data. `start` resumes them.

For a full clean slate, stop direct development servers and browser tests first:

```bash
make k3s-reset
make k3s-start
```

The Make target supplies the CLI's `--yes` automatically.

**Reset deletes this runner's Kubernetes cluster, Goblin application data, logs, cluster
credentials, and saved owner password.** The CLI embeds `scripts/k3s-reset.sh` and runs
it in WSL with sudo; it needs Bash, Git, and `jq`. It checks that the installation
belongs to this checkout, stops its systemd services, and runs
`/usr/local/bin/k3s-uninstall.sh` to remove the whole local cluster. It then removes
Goblin installation files and clears `.goblin-platform/`, `.goblin-local/`,
`.goblin-secrets/` (including the saved password), `.goblin-browser-test/`, and
`.artifacts/playwright/`. Tracked files remain. Mounted or symlinked cleanup paths,
another checkout's installation, and installation leftovers without an ownership record
stop the cleanup. It runs no kubectl commands and does not read `~/.kube/`.

**Deleted data can only be recovered from an existing backup.** Password setup and
sign-in are required again. Docker images, shared caches (including
`/var/cache/goblin/downloads`), developer tools, other artifacts, and database client
profiles remain. Each installation checks cached downloads against their pinned
checksums; to download them again, run `sudo rm -rf /var/cache/goblin`. Run
`make k3s-start` to reinstall.

## Deploy application edits

Iterate with the required affected tests directly in WSL. Use this command when the
changed behavior needs the application running inside k3s:

```bash
make k3s-deploy
```

The command also accepts explicit `GOBLIN_PUBLIC_ORIGIN` and `GOBLIN_CHATGPT_ENABLED`
environment values for the platform container, just as the application does in
production. Unset values retain the Sandbox's current settings. Changing settings
restarts the platform even when the image is unchanged. ChatGPT requires an HTTPS public
origin. See
[plugin testing](../../docs/plugin-testing.md#use-the-installed-wsl-k3s-platform) for
the existing Chisel gateway setup.

The command requires a completed installation belonging to this checkout. It locks out
concurrent installer operations, checks the loopback Kubernetes API and single-node
topology, and compares SQL and installation manifests with the original installation
snapshot. Incompatible schema or manifest changes require a deliberate reset and
reinstall; this command neither applies migrations nor updates infrastructure manifests.
Its explicit application settings are applied to the current Sandbox without editing the
installation manifests.

Docker builds the current working tree, including uncommitted runtime source, using
normal exclusions and cache. Deployment imports changed image content, updates the
platform and future worker image, and restarts only the platform. Existing executions
keep their recorded image and identity. Explicit settings changes also restart the
platform; unchanged image/settings still get health checks. Concurrent resource updates
are rejected rather than overwritten. PostgreSQL, logging, ingress, credentials, and
PVCs remain in place.

Success requires the expected pod image to be ready and `/readyz` to succeed through the
installed browser endpoint, normally `http://localhost:8788`. When the public origin is
HTTPS, that loopback endpoint forwards directly to the platform Service, preserving the
upstream Host and HTTPS header. The route is retained under
`/var/lib/goblin/local-test/browser-upstream` and in the existing systemd forwarder; it
survives stop/start and pod replacement. Configure the loopback public origin again to
restore the normal NGINX route. The CLI also checks OAuth discovery through the local
endpoint when ChatGPT is enabled. These checks verify the WSL endpoint. Connect your
HTTPS proxy or tunnel to it before opening the public browser URL; deployment does not
start that connection. Deployment verifies pod configuration and prints the expected
image, pod, endpoint, and diagnostics path under `.artifacts/k3s-deploy/`. On failure,
inspect that run and fix the cause before deploying again. Deployment never resets data
or retries failed Work.

Verify the actual changed interaction, route, worker behavior, or log output in the
running installation. Readiness proves startup, not feature correctness. See
[verification selection](../../docs/repository-layout.md#choosing-verification) for when
cluster checks are necessary. Installer changes need installation tests; `k3s-deploy` is
an application image update.

## Password storage

Local setup uses the
[private password verifier](../../docs/authentication.md#password-setup-and-storage)
shared with direct development. The original password cannot be recovered from it. The
original password cannot be displayed; `make k3s-locate-password` reports the verifier's
location.

The installer copies it into its private installation directory and the same Kubernetes
Secret and read-only container mount as Azure. If the checkout's copy is missing, the
installer reuses its saved verifier.

You can prepare the verifier separately with `make password-set`. For unattended
first-time setup, supply `GOBLIN_LOCAL_PASSWORD` in the process environment; it does not
replace an existing verifier.

To change the local password, run:

```bash
make password-set ARGS=--replace
```

Restart `make dev-start` to load it. For a completed full installation, run
`make k3s-start` to update the Secret and restart the application pod. Finish an
in-progress installation before changing its password.

The local source snapshot excludes Git-ignored files and rejects symlinks. Kubernetes
readiness, cert-manager admission, image building, application readiness, and the
browser handoff are real. Azure resource creation, DNS, and Azure networking need manual
checks on an Azure VM.

## ChatGPT plugin development

Initial installation uses F5 NGINX over loopback HTTP with ChatGPT disabled. The
existing `k3s-deploy` workflow can configure the installed platform for the public HTTPS
connection. Follow [Plugin testing](../../docs/plugin-testing.md#local-testing) for the
complete start, tunnel, deploy, install, test, and return-to-localhost session in one
document. [Chisel installation](../chisel/README.md) covers one-time client and
administrator setup.
