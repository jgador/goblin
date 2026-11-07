# Native administration with goblinctl

goblinctl installs and inspects the host, manages owner credentials and PostgreSQL, and
serves the temporary setup UI. The web UI controls Work. The CLI never retries failed
Work or rewrites product history.

The supported installed host is Ubuntu on x86-64, including WSL with systemd. Release
packaging produces a static Linux musl executable. Installed hosts need no Rust, Cargo,
Make, or Python.

## Commands and ownership

Use `goblinctl --help` and subcommand `--help` for supported flags; the
[CLI declarations](../tools/goblinctl/src/main.rs) generate that reference.
`goblinctl metadata --json` exposes the installation schema. Procedures are owned by
[WSL k3s](../deploy/k3s/README.md), [Azure installation](../deploy/azure/README.md),
[Azure repair](../deploy/azure/reference.md), and [PostgreSQL](database.md).

`status` reads live health; `install status` reads installation progress. `doctor` adds
infrastructure and private-directory checks. They are read-only, support versioned JSON,
and fail when unhealthy. They do not prove database authentication or model execution.
Private installation logs require host access.

On an installed host, inspect progress, diagnose infrastructure, and follow private logs:

```bash
sudo goblinctl install status --json
sudo goblinctl doctor --json
sudo goblinctl logs --follow
```

Password replacement is explicit and never takes passwords in command arguments. On an
installed host it requires completed installation, updates the Secret and verifier, and
restarts the application, ending sessions. If replacement fails after updating the
Secret, inspect and finish the pod restart. `password set --path` manages a verifier
file instead of a running installation.

Local installation commands check checkout ownership and explicitly select its cluster.
`stop` preserves data; `reset` deletes the owned installation. Neither a source edit nor
installation failure authorizes reset. PostgreSQL client profiles are per-user and
independent of checkout selection.

## Installation, progress, and recovery

A root worker changes infrastructure; a separate unprivileged setup service reads public
progress. Public Azure setup cannot retry. After fixing an installation failure, use
`sudo goblinctl install retry`. Local setup may request retry with the owner password
through its restricted control socket. JSON-only requests and password throttling
protect that endpoint.

Independent installation steps may run concurrently. On failure, running steps finish
and no new ones start. Failed installations need an explicit retry; reboot resumes
interrupted work but does not retry failed work.

A retry rechecks the saved source, password, manifests, certificates, images, and PVCs.
It never resets the database. WSL `retry --refresh` replaces only a stopped failed
installation's source snapshot and bundled assets, enabling fixes to be tested without
deleting data. See [WSL recovery](../deploy/k3s/README.md#progress-and-logs).

Downloads and image pulls have bounded retries; these transport policies do not apply to
Work. Read their tuning and timeout definitions in the
[embedded installer](../deploy/azure/setup/installer.sh), including
`GOBLIN_IMAGE_PULL_WORKERS` for image-download concurrency. Public progress contains
authored safe events; compiler output, credentials, and upstream errors remain in
private host logs.

Migration Jobs are checked before replacement. A differently owned Job or one using
another image needs manual inspection. The database lock and migration journal still
control schema application.

## Source and release boundaries

[assets.rs](../tools/goblinctl/src/assets.rs) explicitly embeds installer inputs, so
installed hosts do not need a checkout. xtask supplies developer and release operations
and is not shipped. [Shipping inputs](../tools/goblinctl/release-inputs.json) and
[capabilities](../tools/goblinctl/capabilities.json) determine installer reuse.

Changes to embedded assets require an installer release. See
[coordinated releases](releases.md). For a local review archive:

```bash
make release-package-local
```

Packaging alone does not prove a binary came from the checkout. Release preparation
freezes source and verifies inputs before publication. Archive metadata is deterministic
for identical binary bytes; compiler output is not claimed reproducible across machines.

Bootstrap verifies the exact published archive, checksum, contents, and version; it
never falls back to latest. It takes the installer lock before activation. Read
installed paths from [bootstrap](../deploy/azure/bootstrap.sh).

Goblin supports current installation formats only. Recreate superseded disposable
installations deliberately; there is no in-place upgrade tool. Use
[test selection](repository-layout.md#choosing-verification) for Rust, packaging,
browser, and deployment verification. Live systemd, Kubernetes, PostgreSQL, and Azure
checks remain separate integration evidence.
