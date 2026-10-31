# Test the installation from WSL

Run the same setup page and background installer used by Azure directly in Ubuntu
24.04 on WSL 2. The installer installs k3s, cert-manager, Agent Sandbox, and Goblin
in this Linux environment from your current checkout, including uncommitted
changes. For workload, image, and PVC names and storage ownership rules, see
[Kubernetes names](../../docs/kubernetes-names.md).

## Start

With systemd enabled in WSL, run from the repository root:

```bash
make local-start
```

On first start, choose and confirm your Goblin password in the terminal. Open
**http://localhost:8788** in your Windows browser to see installation progress.
When Goblin is ready, the page opens it at the same address; enter your password.

The command requests `sudo` when needed, returns once the page is available, and
leaves installation running in the background. Keep WSL running; closing the
browser or terminal does not stop the installer. Starting an existing installation
keeps its data and password.

Goblin is preproduction. Recreate this installation when database, application,
or cluster formats change; use the [reset commands](#stop-resume-or-start-clean).

Windows reaches the page through WSL's default localhost forwarding. A forwarder
from loopback port 8788 to Linux port 80 keeps access working when Kubernetes takes
over from the setup page. The setup page stops and disables itself after
installation; the forwarder remains.

The progress page shows stages, tasks, and live logs. Progress counts completed
tasks, not time remaining. On failure, running tasks finish and no new tasks start;
enter the Goblin password and select **Retry installation** to resume.

Open **Settings → Cluster → Open cluster**, or
**http://localhost:8788/headlamp/**, for read-only Kubernetes access in Headlamp
with your Goblin login. See [the cluster view](../../docs/cluster-view.md).

To use a different browser port, choose it on the first start; changing it later
requires a reset:

```bash
make local-start ARGS="--http-port 8888"
```

## Requirements

Use Ubuntu on x86-64 with systemd, curl, Git, and Rust (the make targets build
`goblinctl` from the checkout). Allow several GiB of free memory and disk space for
Kubernetes, image downloads, and the application build. The installer needs Linux
ports 80 and 443 and the browser port free. Windows port 80 can stay in use.

Docker Desktop is not required. The installer uses the native Linux Docker engine
with its own private client configuration for anonymous public-image pulls. It
does not use or change your Docker configuration, registry logins, credential
helpers, contexts, or builders. Docker Hub's anonymous pull limits apply.

If systemd is disabled, add this to `/etc/wsl.conf`, keeping other settings:

```ini
[boot]
systemd=true
```

Then run `wsl --shutdown` from Windows and reopen WSL. The runner refuses to adopt
an existing k3s or Goblin installation that it did not create. Use a dedicated
WSL distribution if your current one already has one.

## Progress and logs

```bash
make local-status
make local-logs
make local-logs ARGS=--follow
make local-retry
make local-retry ARGS=--refresh
```

`local-retry`, like **Retry installation** on the page, restarts a failed
installation with its saved source snapshot, password, and data. After fixing
installer or application code, use `make local-retry ARGS=--refresh`. It replaces
the failed attempt's source snapshot and installer with your current checkout and
keeps the cluster, completed images, and downloads. If bootstrap failed before the
installer started, run `make local-start` again. A completed installation reports
`ready`, and both setup services are stopped and disabled.

Logs are stored in WSL:

- `/var/log/goblin-bootstrap.log`
- `/var/log/goblin-installer.log`
- `.goblin-local/bootstrap.log` (output of the local start command)

For Kubernetes diagnostics, use `sudo k3s kubectl` and `sudo journalctl -u k3s`.

The installer provisions PostgreSQL and applies schema migrations before starting
Goblin. PostgreSQL is reachable from WSL at `localhost:5432` through
`goblin-local-postgres.socket`, which binds only to loopback and forwards to the
database Service in Kubernetes. Certificate authentication still applies. For
client certificates and Windows access, see
[the PostgreSQL workflow](../../docs/database.md#wsl-and-windows-pgadmin).

To enable or repair this endpoint for an installed database:

```bash
make local-configure-database
```

Inspect it from a root shell with `journalctl -u goblin-local-postgres.service`.

## Stop, resume, or start clean

```bash
make local-stop
make local-start
```

`stop` shuts down this runner's installer, browser and database forwarders, and
Kubernetes containers, and keeps all data. `start` resumes them.

For another installation test with your latest source changes:

```bash
make local-reset ARGS=--yes
make local-start
```

**Reset deletes this runner's Kubernetes cluster, Goblin application data, logs,
and cluster credentials.** It keeps the checkout's password verifier, the native
CLI, Docker images and build cache, and the verified k3s downloads in
`/var/cache/goblin/downloads`.

For a fresh local development environment, including password setup, run:

```bash
time make dev-reset
```

Stop direct development servers and browser tests first. This target runs
`scripts/dev-clean-slate.sh` in WSL with sudo; it needs Bash, Git, and `jq`. It
checks that the installation belongs to this checkout, stops its systemd services,
and runs `/usr/local/bin/k3s-uninstall.sh` to remove the whole local cluster. It
then removes Goblin installation files and clears `.goblin-platform/`,
`.goblin-local/`, `.goblin-secrets/` (including the saved password),
`.goblin-browser-test/`, and `.artifacts/playwright/`. Tracked files remain.
Mounted or symlinked cleanup paths, another checkout's installation, and
installation leftovers without an ownership record stop the cleanup. It runs no
kubectl commands and does not read `~/.kube/`.

**Deleted data can only be recovered from an existing backup.** Password setup and
sign-in are required again. Docker images, shared caches (including
`/var/cache/goblin/downloads`), developer tools, other artifacts, and database
client profiles remain. Each installation checks cached downloads against their
pinned checksums; to download them again, run `sudo rm -rf /var/cache/goblin`.
Run `make local-start` to reinstall.

## Password storage

Local setup uses the same PBKDF2-SHA256 verifier as Azure (600,000 iterations,
random 16-byte salt). Only the verifier is saved, in
`.goblin-secrets/owner-password` (file mode `0600`, directory mode `0700`). Git
ignores everything in that folder except `.gitkeep`, and Docker builds exclude the
folder. The original password cannot be displayed; `make local-locate-password`
reports the verifier's location.

Both this installer and `make dev-start` use that verifier. The installer copies
it into its private installation directory and the same Kubernetes Secret and
read-only container mount as Azure. If the checkout's copy is missing, the
installer reuses its saved verifier.

You can prepare the verifier separately with `make password-set`. For unattended
first-time setup, supply `GOBLIN_LOCAL_PASSWORD` in the process environment; it
does not replace an existing verifier.

To change the local password, run:

```bash
make password-set ARGS=--replace
```

Restart `make dev-start` to load it. For a completed full installation, run
`make local-start` to update the Secret and restart the application pod. Finish
an in-progress installation before changing its password.

The local source snapshot excludes Git-ignored files and rejects symlinks.
Kubernetes readiness, cert-manager admission, image building, application
readiness, and the browser handoff are real. Azure resource creation, DNS, and
Azure networking need manual checks on an Azure VM.

## ChatGPT plugin development

Local installation uses F5 NGINX over loopback HTTP, and ChatGPT stays disabled.
For a public HTTPS address, use the
[Azure-to-WSL development relay](../../docs/chatgpt-local-testing.md). It keeps
Azure’s certificate and DNS while routing the whole application to local code.
