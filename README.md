# Goblin

Your self-hosted AI coworker.

Goblin turns conversations into durable, reviewable Work. PostgreSQL records
context, decisions, attempts, results, and approvals; Codex runs the attempts.
New Work gets the default Goblin agent and waits for an explicit start. Every
failure needs attention and an explicit recovery. Work completes only when its
result is approved, not when the runtime succeeds.

The .NET application serves a TypeScript browser UI. Repository tasks run in
isolated Kubernetes sandboxes, use GitHub through Goblin's broker, and keep one
persistent workspace per Work on its PVC. Slack supports customer-owned apps,
linked owner identities, and DM and mention conversations. Codex is the only
implemented runtime; Teams and Knowledge indexing are not implemented.

The [documentation index](docs/README.md) lists the guides for architecture,
behavior, connections, operations, and limits.

## Install

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://goblinboard.com/install/)

Choose a version on the [installation page](https://goblinboard.com/install/), then
deploy it manually in Azure Portal. The installer sets up the application,
PostgreSQL, Kubernetes execution, Headlamp, and saved logs, with Let's Encrypt
HTTPS for Azure's generated hostname. Open the deployment's `setupUrl` for
read-only progress, then open `goblinUrl` and use the Goblin password you entered in the
Portal. See the [Azure guide](deploy/azure/README.md).

To test the full installation in WSL/Ubuntu with systemd:

```bash
make local-start
```

Open **http://localhost:8788** in Windows. The first start asks for a Goblin
password in the terminal. The [local installer guide](deploy/local/README.md)
covers prerequisites, logs, stop, and reset. Goblin is preproduction; recreate
development installations when formats change.

## Develop

Install GNU Make, .NET 10, Node.js 24+, and the Ubuntu build prerequisites in
[Rust development](docs/rust-development.md), then run:

```bash
make rust-setup
source "$HOME/.cargo/env"
make install
make dev-start ARGS=--without-database
```

This starts the application at **http://localhost:8787** with Work disabled.
For Work development, create a migrated PostgreSQL profile with the
[database workflow](docs/database.md#wsl-and-windows-pgadmin), then run
`make dev-start DB_PROFILE=wsl`.

`make help` lists tasks. Common commands are `make build`, `make test`,
`make check`, and `make format`; choose checks with the
[repository guide](docs/repository-layout.md#choosing-verification).
[AGENTS.md](AGENTS.md) defines contribution rules.

The public [website](website/README.md) is separate from the application. Run
`make website-start` to develop it at **http://localhost:5173**, and
`make website-stop` to stop it.

Goblin's original code and documentation use [Apache 2.0](LICENSE).
[Third-party notices](THIRD_PARTY_NOTICES.md) describe dependency licenses and the
separate terms for [fonts and brand artwork](assets/branding/README.md).
