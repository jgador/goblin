# Goblin

Your self-hosted AI coworker.

Goblin turns conversations into durable, reviewable Work. It saves context, attempts,
results, and approvals in PostgreSQL and runs Codex in isolated Kubernetes workspaces
for GitHub tasks. Use the browser, Slack, or a private ChatGPT plugin to work with it.
Results need approval before Work completes.

Goblin is preproduction. See [the guides](docs/README.md) for supported usage,
architecture, and operations.

## Install

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)
](https://goblinboard.com/install/)

Choose a version on the [installation page](https://goblinboard.com/install/) and follow
the [manual Azure installation guide](deploy/azure/README.md).

To test the full installation in WSL/Ubuntu with systemd:

```bash
make k3s-start
```

Open **http://localhost:8788** in Windows. The first start asks for a Goblin password in
the terminal. The [WSL k3s guide](deploy/k3s/README.md) covers prerequisites,
application deployment, logs, recovery, and reset.

## Develop

Follow the [development prerequisites](docs/repository-layout.md#build-and-test), then
run:

```bash
make rust-setup
source "$HOME/.cargo/env"
make install
make dev-start ARGS=--without-database
```

This starts the application at **http://localhost:8787** with Work disabled. For Work
development, create a migrated PostgreSQL profile with the
[database workflow](docs/database.md#wsl-and-windows-pgadmin), then run
`make dev-start DB_PROFILE=wsl`.

`make help` lists tasks; choose checks with the
[repository guide](docs/repository-layout.md#choosing-verification).
[AGENTS.md](AGENTS.md) defines contribution rules.

The public [website](website/README.md) has its own development workflow.

Goblin's original code and documentation use [Apache 2.0](LICENSE).
[Third-party notices](THIRD_PARTY_NOTICES.md) describe dependency licenses and the
separate terms for [fonts and brand artwork](assets/branding/README.md).
