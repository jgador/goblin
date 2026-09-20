# Test the installation from WSL

Run the same setup page and background installer used by Azure directly in Ubuntu
24.04 / WSL 2. The installer installs k3s, cert-manager, Agent Sandbox, and Goblin
in this Linux environment. It uses your current checkout, including uncommitted
source changes.

## Start

With systemd enabled in WSL, run from the repository root:

```bash
npm run install:local -- start
```

Open **http://localhost:8788** in your Windows browser. You will see the installation
page with actual progress. When Goblin is ready, the page opens it automatically
at the **same address**. On first start, choose and confirm your Goblin password
in the terminal. Enter that password when the workspace opens.

The command requests `sudo` when needed, returns once the page is available, and
leaves installation running in the background. Keep WSL running; closing the
browser or terminal does not stop the installer. Starting an existing installation
preserves its data and password.

There is no nested VM, VM image download, Windows hosts-file edit, or manual
port forwarding. Windows uses WSL's default localhost forwarding. The runner
maintains a small TCP forwarder from loopback port 8788 to Linux port 80, so
Windows access continues when Kubernetes takes over from the setup server.
The setup UI stops and disables itself after installation; the forwarder remains.

Open **Settings → Cluster → Open cluster**, or
**http://localhost:8788/headlamp/**, to inspect Kubernetes in Headlamp using your
Goblin login. This is read-only access to workloads, logs, events, and storage.
The path uses the same chosen browser port. See [the cluster view](../../docs/cluster-view.md).

Azure uses its assigned DNS name instead of localhost. Open the deployment's
`goblinUrl` output to see the same setup page, progress, and handoff. The local
forwarder is only for testing and is not included in Azure provisioning.

## Requirements

Use Ubuntu on x86-64 with systemd, Python 3, curl, and Git. Allow several GiB of
free memory and disk space for Kubernetes, image downloads, and the application
build. The installer needs Linux ports 80 and 443 and the chosen browser port.
Windows port 80 can remain occupied; the Windows browser uses 8788.

If systemd is disabled, add this to `/etc/wsl.conf`, preserving other settings:

```ini
[boot]
systemd=true
```

Then run `wsl --shutdown` from Windows and reopen WSL. The runner refuses to adopt
an existing k3s or Goblin installation that it did not create. Use a dedicated
WSL distribution if your existing distribution already has one.

## Progress and logs

```bash
npm run install:local -- status
npm run install:local -- logs
npm run install:local -- logs --follow
npm run install:local -- retry
```

`retry` restarts an incomplete installation with its retained source snapshot,
password, and data. If bootstrap failed before launching the installer, run
`start` again. A completed installation reports `ready`, and both setup services
are stopped and disabled.

Logs are stored directly in WSL:

- `/var/log/goblin-bootstrap.log`
- `/var/log/goblin-installer.log`
- `.goblin-local/bootstrap.log` (local command's bootstrap output)

Kubernetes diagnostics are available through `sudo k3s kubectl` and
`sudo journalctl -u k3s`. The installer provisions PostgreSQL and applies schema migrations before
starting Goblin. To repair or export local tooling credentials after installation,
run `bash deploy/postgres/setup.sh` to enable
[PostgreSQL certificate authentication](../../docs/database.md). For this runner,
setup also enables persistent local database access at `localhost:55432` and
configures tooling for that port. Use `--port` to choose a different local port.

To enable or repair local access for an already configured database:

```bash
npm run install:local -- database
```

The endpoint runs as `goblin-local-postgres.socket`/`.service` under systemd and
binds only to loopback. It forwards raw TCP to an internal Kubernetes Service;
individual client resets do not stop other connections, and Kubernetes follows
replacement database pods. No terminal or `kubectl port-forward` is required.
Certificate authentication and server verification still apply. Inspect it with
`sudo journalctl -u goblin-local-postgres.service`.

## Stop, resume, or start clean

```bash
npm run install:local -- stop
npm run install:local -- start
```

`stop` shuts down this runner's installer, browser/database forwarders, and Kubernetes
containers while retaining data. `start` resumes them.

For another installation test with your latest source changes:

```bash
npm run install:local -- reset --yes
npm run install:local -- start
```

**Reset deletes this runner's Kubernetes cluster, Goblin application data, logs,
and cluster credentials.** It retains the shared repository password verifier,
Docker and cached build images, and does not
remove other host services such as PostgreSQL. `destroy --yes` is an alias for
`reset --yes`.

Choose a different browser port on the first start if necessary:

```bash
npm run install:local -- start --http-port 8888
```

## Password storage

Local setup uses the same `deploy/azure/hash-password.py` helper as Azure:
PBKDF2-SHA256, 600,000 iterations, and a random 16-byte salt. Only the verifier is
saved to `.goblin-secrets/owner-password` in this checkout (file mode `0600`,
directory mode `0700`). Git ignores everything in that folder except the empty
`.gitkeep`; Docker excludes the entire folder. The password is never prefilled.

Both this installer and `npm start` reuse that verifier. The full installer passes
its path to the shared Azure bootstrap, which copies the verifier to its private
installation directory and creates the same Kubernetes Secret and read-only
container mount as Azure. Local bootstrap scripts contain no original password.
Azure obtains its password from the deployment form instead of the local prompt.

You can prepare the verifier separately with `npm run setup:password`.
For unattended first-time setup, supply `GOBLIN_LOCAL_PASSWORD` through the process
environment. It does not override an existing verifier. Starting an older local
installation imports its retained verifier and removes the old
`.goblin-local/login-password` plaintext file without changing its password.
`npm run install:local -- password` now reports the verifier's location; the
original password cannot be displayed.

To change the shared local password, run:

```bash
npm run setup:password -- --replace
```

Restart `npm start` to load it. For a completed full installation, run
`npm run install:local -- start` to update the Secret and restart its application
pod. Finish an in-progress installation before changing its password.

The local source snapshot excludes Git-ignored files and rejects symlinks.
Kubernetes readiness, cert-manager admission, image building, application readiness,
and the browser handoff are real. Azure resource creation, DNS propagation,
and Azure networking still require an Azure deployment test.
