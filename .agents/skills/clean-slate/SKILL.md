---
name: clean-slate
description: Remove this checkout's Goblin installation and test data from WSL so the installation workflow and Goblin UI can be tested from scratch. Use only when the user explicitly invokes $clean-slate, not for general fresh-install requests, stopping services, diagnosing installation, or creating cleanup instructions.
---

# Goblin clean slate

Return this checkout's WSL test environment to an uninstalled, signed-out state.
Remove Goblin's test data as well as its services, including Kubernetes persistent
storage and the shared local password verifier. Leave installation stopped so the
user can watch the next installation from its beginning.

This is a repository-local skill. Resolve the checkout with `git rev-parse
--show-toplevel`; do not assume `/root/repos/goblin` or operate on other checkouts.

## Authorization and boundaries

- Run this skill only when the user explicitly invokes `$clean-slate`. Do not
  select it automatically for general cleanup or fresh-install requests.
  An invocation to execute cleanup authorizes stopping or killing verified Goblin
  test listeners and deleting the verified, in-scope resources below. A request to
  create, edit, explain, or preview this skill does **not** authorize cleanup, even
  if it mentions `$clean-slate`.
- Immediately before mutation, tell the user which installation and data will
  be removed, that sign-in and password setup will be required again, and that
  deleted persistent data needs an existing backup to recover. If destructive
  intent or ownership is unclear, ask before proceeding. Honor narrower requests
  such as keeping a password, and report those exceptions to a full clean slate.
- Preserve source files, Git history, tracked placeholders, uncommitted work,
  this skill, personal Codex configuration, and unrelated services/data. Keep
  Node, npm, .NET, Python, Docker, other developer tools, and shared caches.
- Do not reset/unregister WSL, touch Azure or remote clusters, change Windows
  services, uninstall shared PostgreSQL/Docker, or clear an entire browser profile.
  Cleanup does not authorize a reinstall, Git commit, push, or credential revocation
  with external providers. Do not make new copies of secrets as an automatic backup.

## Inspect before cleanup

Read the current implementations before relying on this inventory:

- `deploy/local/README.md` and `deploy/local/install.py`: ownership, stop/reset,
  preflight, systemd units, and ports.
- `deploy/local/start.py`, `deploy/local/password.py`, and
  `backend/src/Goblin.Web/Program.cs`: direct development startup and data overrides.
- `deploy/azure/install-app.sh`: Docker image naming and build ownership.
- If database artifacts exist, `deploy/postgres/write-appsettings.py` and
  `docs/database.md`: exported credentials and generated configuration.

Record Git status before starting. Inventory only metadata needed for ownership;
never print password verifiers, auth files, kubeconfig credentials, private keys,
full process environments, or secret-bearing connection strings.

1. Confirm the host is the intended WSL environment. Check
   `/var/lib/goblin/local-test/config.json`: `mode` must be `direct` and `repo`
   must match this resolved checkout. Record `http_port` and optional
   `postgres_port`. Use `npm run install:local -- status` when ownership exists.
   A missing installation is a valid repeat-cleanup case, not a reason to start one.
2. Inspect the installation's systemd units, k3s configuration/drop-ins, uninstall
   and killall scripts, storage paths, and mounts. Check for a custom
   `K3S_DATA_DIR`, `--data-dir`, or storage location. Do not execute an uninstaller
   with an unreviewed environment or redirect deletion to an unverified directory.
3. When the owned cluster is reachable, inventory namespaces, workloads, PVCs/PVs,
   and their actual backing paths. Explicitly select the local cluster, for example:

   ```bash
   sudo k3s kubectl --kubeconfig=/etc/rancher/k3s/k3s.yaml get namespaces,pods,pvc -A --request-timeout=10s
   sudo k3s kubectl --kubeconfig=/etc/rancher/k3s/k3s.yaml get pv --request-timeout=10s
   ```

   Inspect unexpected workloads before uninstalling a whole cluster. Do not use
   the ambient kubectl context or start a stopped cluster just for inventory.
   Expected Goblin storage includes `goblin-auth-data` and, when separately set
   up, `goblin-postgres-data`. The default local-path data lives under
   `/var/lib/rancher/k3s/storage`; external/retained volumes need separate review.
4. Identify listeners and their owning PIDs/services, including ports 80, 443,
   8788 (installer), 8787 (direct app), 8798 (browser tests), and 55432 (database
   forwarder), plus configured custom ports. Attribute direct app/test processes
   and their children using executable, working directory, parentage, and data
   paths. A port number or a process named `node`, `dotnet`, or `codex` is not proof.
5. Inventory checkout-local data below and any proven custom `GOBLIN_DATA_DIR`
   or `GOBLIN_PASSWORD_HASH_FILE` paths. Inspect only relevant settings privately.
   Goblin's private Codex home is inside its data directory; the user's personal
   Codex home and the agent performing cleanup are outside scope.
6. Inventory additional local containers, volumes, images, test databases, old
   VM disks, and temporary test directories before removing them. Verify any
   Docker endpoint is the intended WSL daemon, not a remote or Windows engine.
   Names containing `goblin` or `goblin_test_` alone do not establish ownership.

For every additional filesystem deletion, verify the exact absolute target,
tracked-file status, resolved ancestors, symlinks, and mounts. Do not traverse a
symlink or mounted filesystem into unrelated data. Use enumerated, validated
targets, never broad `git clean`, `rm` globs, system-wide prunes, or name-only kills.

## Stop services and remove the owned installation

Freeing occupied Goblin local test ports is a required part of cleanup, even when
k3s is already uninstalled or no installation ownership file exists. Include the
default app/installer/browser-test ports 8787, 8788, and 8798, the database forwarder
on 55432, and any configured custom ports recorded during inventory. Check the
installer's required ports 80 and 443 too.

For direct development servers, browser test runners, their Goblin children, and
manual port-forwards, use this termination procedure before deleting their data:

1. Inspect listening sockets with sufficient privileges to see their owners, for
   example `sudo ss -ltnp`, checking both IPv4 and IPv6. Resolve each occupied
   target port to its current PID and any owning systemd unit, socket, container,
   or supervisor. Do not mistake a lack of permission to see a PID for a free port.
2. Stop a verified Goblin supervisor/container or disable its socket activation
   before terminating the listener, so it cannot immediately respawn. Use the
   built-in reset below for installer-managed services; preserve its stop order.
3. For a standalone verified Goblin process, send `SIGTERM` to its exact PID and
   allow up to 10 seconds to exit. If it still holds the port, recheck its identity
   (including process start time to avoid PID reuse), ownership, and socket, then
   send `SIGKILL` to that same verified process. Do not stop at reporting that a
   Goblin port is occupied. Never use broad `pkill`, `killall`, or blind `fuser -k`.
4. Recheck the ports after termination and after the installation reset. If a
   listener respawns, identify its supervisor instead of repeatedly killing new
   PIDs. If one graceful stop and one verified forced termination do not release
   it, report the remaining blocker and request direction; do not loop endlessly.
5. If a port belongs to an unrelated or unidentified process, explain the conflict
   and ask before stopping it. Never kill the current agent, its controlling
   session, or a shared service to free a port. A Windows-host-only conflict needs
   separate user action; this WSL skill does not kill Windows processes.

An occupied target port is a blocker to declaring a complete clean slate, not a
reason to silently switch to another port. Report any exception the user chooses
to preserve.

For the standard, verified local installation, run from the repository root:

```bash
env -u K3S_DATA_DIR npm run install:local -- reset --yes
```

The runner requests sudo if needed. Its reset stops the installer **before** setup
so the recovery handler cannot restart the UI, disables browser/database sockets,
stops k3s and its containers, runs the official uninstaller, and removes runtime
state and installation logs. Stopping `k3s.service` alone leaves containers alive.
Do not substitute namespace or pod deletion for removing persistent storage.

Do not use this default command for a custom data directory until its deletion
scope and the uninstaller's behavior have been resolved. If the ownership record
is missing but machine-level installation artifacts remain, ownership mismatches,
or reset refuses cleanup, stop the machine-level deletion and explain the blocker.
Never forge ownership metadata, bypass its check, or fall back to blanket removal.
If no installation or machine-level residue exists, skip reset and clean only the
verified remaining test data. Repeated cleanups should succeed without installing
anything.

If reset fails partway, retain its ownership/state for diagnosis and retry. In
particular, `/var/lib/kubelet` can retain an empty self-bind mount on WSL; the
runner handles that case. Nonempty or unexplained mounts require investigation,
not recursive deletion or a lazy/forced unmount.

## Remove remaining Goblin test data

The built-in reset deliberately retains some local data. A full clean slate also
removes these verified artifacts after their writers have stopped:

| Target relative to this checkout | Cleanup scope |
| --- | --- |
| `.goblin-auth/` | Private Codex login/tokens, session databases, history, home, workspace, and other application state. |
| `.goblin-browser-test/` | Fake-login state, browser-test password, and private test workspace. |
| `.goblin-local/` | Remaining snapshots, bundles, logs, screenshots, legacy passwords, and old test-VM archives/disks such as `previous-vm/`. Verify no VM/process still uses a disk first. |
| `.goblin-postgres/` | Exported application/admin certificates and private keys. |
| `.goblin-secrets/` | Ignored test credentials, including `owner-password`; preserve `.gitkeep` and any other tracked files. |
| `test-results/`, `playwright-report/` | Generated reports, traces, screenshots, and browser artifacts, excluding any tracked files. |

Also address these conditional leftovers; report anything that cannot safely be
removed rather than claiming the environment is completely clean:

- **Database settings:** `backend/tools/Goblin.Database/appsettings.json` is an
  ignored generated file. Remove it if it contains only this installation's
  generated settings; otherwise remove only the proven generated `Goblin` and
  `GoblinAdmin` connection entries, preserving unrelated settings. Check generated
  copies beside built executables for stale local credentials/configuration. Do
  not delete or reset the tracked `backend/src/Goblin.Web/appsettings.json`; its
  password-free cluster configuration is source, not test data. Preserve any user
  edits. Report stale connection/data/password environment overrides without
  rewriting the user's shell profiles.
- **Storage outside k3s:** Remove exact local backing directories or volumes
  proven exclusive to this installation if the uninstaller did not remove them.
  Never infer that a retained PV, remote volume, or shared host path is disposable.
- **Docker artifacts:** Remove verified Goblin-only test containers and their
  exclusive volumes/networks. Remove verified installation image tags, normally
  `localhost/goblin-auth:<source-sha256>`, after checking other consumers. Preserve
  unrelated tags, shared base images, Docker's daemon/configuration, and shared
  BuildKit/download caches. Do not use `docker system prune`, global volume prune,
  or force image removal. Report retained shared caches; this is a state reset,
  not a guarantee of cold downloads/builds.
- **Host PostgreSQL:** If Goblin testing used a separate PostgreSQL server in
  this WSL distribution, remove only proven Goblin test databases and exclusively
  owned roles/credentials after checking dependencies and active users. Interrupted
  integration tests can leave `goblin_test_<guid>` databases (see
  `backend/tests/Goblin.Persistence.Tests/PostgresTests.cs`). Verify provenance,
  not just the prefix. Preserve the shared server, other databases, and roles with
  other consumers. Do not connect to or mutate an external database for this task.
- **Other files:** Remove exact verified custom data/password paths, local auth
  backups (such as individually identified `goblin-auth*.tar` files), and abandoned
  test/temp directories tied to this checkout. Enumerate candidates without
  deleting by wildcard. Preserve unrelated `/tmp` entries, active agent work,
  personal credentials, kubeconfig contexts for other clusters, and source work.
  Remove only entries belonging to the removed local cluster from a shared
  kubeconfig, if present. Do not vacuum the system journal to erase Goblin logs.

Do not run tests during cleanup: they can recreate credentials, temporary
databases, browser state, and test servers. Do not restart the app as verification.

## Verify and hand off

Check all of the following; distinguish “absent” from permission/query failures:

- `goblin-local.socket/service`, `goblin-local-postgres.socket/service`,
  `goblin-setup.service`, `goblin-installer.service`, and the owned `k3s.service`
  are inactive and their installation-owned unit files/drop-ins are gone.
- Owned `/var/lib/goblin`, `/opt/goblin`, `/etc/rancher/k3s`,
  `/var/lib/rancher/k3s`, `/var/lib/kubelet`, `/var/lib/cni`, and any verified
  additional storage are gone. Check `/etc/kubernetes` if present; it is also a
  preflight blocker, not permission to remove unrelated Kubernetes configuration.
  The owned k3s executable, containers, and mounts no longer remain.
- All inventoried local test ports are free on IPv4 and IPv6, and no Goblin-owned
  direct app/test processes or forwards remain. If a target port is still occupied,
  follow the termination procedure above or report the unresolved ownership or
  permission blocker; do not declare success. A short HTTP probe to the former
  browser port should fail to connect, not return a cached sign-in page.
- The listed test data and generated credentials are absent, except explicitly
  preserved files. Git status has no unintended source changes/deletions. Do not
  recreate a password verifier merely to test its absence.

After confirming the ownership file is absent, run the non-installing preflight
from the repo root (with sufficient privileges for its port checks):

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -c 'import sys; sys.path.insert(0, "deploy/local"); import install; config = install.preflight(None); print("Fresh installation preflight passed:", install.origin(config))'
```

If a different port was requested, pass that port instead of `None`. Report a
preflight failure as a remaining blocker; do not start installation to work around
it. Leave `/run/goblin-local.lock` alone: it coordinates runner processes and is
not persisted application data.

Summarize what was deleted, which listeners were stopped or forcibly killed and
which ports were freed, recovery limitations, any retained/shared resources or
blockers, and verification results. Give the next command without running it
unless the user separately asked to reinstall:

```bash
npm run install:local -- start
```

The default Windows browser address is `http://localhost:8788`. A new password is
chosen in the terminal after the verifier is removed, unless the user deliberately
supplies `GOBLIN_LOCAL_PASSWORD`. `npm start` launches only the direct application
on port 8787 and does **not** show installation progress. Windows browser site data
is not cleared by WSL cleanup; suggest a private window or clearing only this
localhost site's data if the user also wants a fresh browser session.
