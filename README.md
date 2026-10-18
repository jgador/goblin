# Goblin

Your self-hosted AI coworker.

Goblin turns conversations into durable, reviewable Work. PostgreSQL records
context, decisions, attempts, outputs, and approvals; Codex executes the attempts.
New Work receives the default Goblin agent and waits for an explicit start.
Failures require attention and explicit recovery. Runtime success alone does
not complete Work.

The C#/.NET application serves a TypeScript browser. Repository tasks use isolated
Kubernetes sandboxes, GitHub access through Goblin's broker, and one persistent
workspace per Work. Files and unpublished commits stay on its PVC. Slack supports
customer-owned apps, linked owner identities, and durable DM/mention conversations.
Codex is the only implemented runtime; additional runtimes, Teams, and Knowledge
indexing remain future work.

See [the documentation index](docs/README.md), [architecture](docs/architecture.md),
and [Work interface](docs/work-centered-ui.md) for current behavior and limits.

## Install

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://goblinboard.com/install/)

Choose a version on the [installation page](https://goblinboard.com/install/), then
deploy manually through Azure Portal. The installer provisions the application,
PostgreSQL, Kubernetes execution, Headlamp, and saved logs. Open the deployment's
`goblinUrl` to follow progress and use the password chosen during setup.
The default Azure origin uses HTTP until an operator configures HTTPS.
See the [Azure guide](deploy/azure/README.md) and [reference](deploy/azure/reference.md).

To test the full installation in a dedicated WSL/Ubuntu environment with systemd:

```bash
make local-start
```

Open **http://localhost:8788** in Windows. First start asks for a password in the
terminal. The [local installer guide](deploy/local/README.md) covers prerequisites,
logs, stop/resume, and destructive reset. Goblin is preproduction and supports
current installation/database formats; development installations can be recreated
when those formats change.

## Develop

Install GNU Make, .NET 10, Node.js 24+, and the Ubuntu build prerequisites described
in [Rust development](docs/rust-development.md), then run:

```bash
make setup-rust
source "$HOME/.cargo/env"
make install
make dev ARGS=--without-database
```

This starts connection management at **http://localhost:8787** without durable
Work. For Work development, create a migrated PostgreSQL profile using the
[database workflow](docs/database.md#wsl-and-windows-pgadmin), then run
`make dev DB_PROFILE=wsl`. Both paths share the Rust password verifier.
Use [authentication](docs/authentication.md) for Codex sign-in and connection checks,
and [execution hosting](docs/execution-hosting.md) for repository configuration.

`make help` lists tasks. Common commands are `make build`, `make test`,
`make check`, and `make format`; choose verification using the
[repository guide](docs/repository-layout.md#choosing-verification). PostgreSQL tests
are opt-in, and fixtures do not establish authenticated model coverage.
[AGENTS.md](AGENTS.md) defines contribution rules; [agent workflow](docs/agent-workflow.md),
[formatting](docs/formatting.md), and [secret scanning](docs/secret-scanning.md) cover
contributor tooling. The native [goblinctl CLI](docs/goblinctl.md) ships separately
and needs no Rust compiler or Python on installed hosts.

The public [website](website/README.md) is independent of the application. Run
`make website-dev` for Docker development at **http://localhost:5173**,
and `make website-stop` to stop it. Delivery procedures live in
[releases](docs/releases.md), [GitHub Actions](docs/github-actions.md), and
[website production](website/production/README.md).

Goblin's original code and documentation use [Apache 2.0](LICENSE).
[Third-party notices](THIRD_PARTY_NOTICES.md) describe dependency licenses and the
separate treatment of [fonts and brand artwork](assets/branding/README.md).
