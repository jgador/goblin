# Documentation

These guides describe the current application and installation formats. Goblin is
preproduction; recreate local installations when formats change. Follow
[AGENTS.md](../AGENTS.md) for contribution rules.

## Product and architecture

| Guide | Purpose |
| --- | --- |
| [Architecture](architecture.md) | Boundaries, durability, isolation, and recovery |
| [Work lifecycle](work-lifecycle.md) | Work states, commands, attention, and attempts |
| [Work interface](work-centered-ui.md) | Browser navigation, controls, and known gaps |
| [Execution hosting](execution-hosting.md) | Workers, sandboxes, claims, and cancellation |
| [Workspace lifecycle](workspace-lifecycle.md) | Work PVC, turns, checkpoints, inspection, and retention |
| [Repository authorization](git-repository-intent.md) | GitHub selection, enablement, grants, and publication |
| [Repository setup memory](git-repository-setup-memory.md) | Saved setup observations and recall |

## Connections and operations

| Guide | Purpose |
| --- | --- |
| [Authentication](authentication.md) | Owner password and Codex account setup and checks |
| [Codex integration](codex-integration.md) | Codex process, model selection, and protocol generation |
| [Slack integration](slack-integration.md) | Slack app setup, linked users, and limits |
| [ChatGPT plugin](chatgpt-integration.md) | Plugin setup, OAuth, Work access, and directory submission |
| [ChatGPT local testing](chatgpt-local-testing.md) | Test the plugin against a local build through a relay |
| [Database](database.md) | SQL baseline, EF generation, and client access |
| [goblinctl](goblinctl.md) | Native CLI commands, installation, and packaging |
| [Kubernetes names](kubernetes-names.md) | Resource names, identities, and storage ownership |
| [System and cluster](cluster-view.md), [Logs](logging.md), [Timezones](timezones.md) | Monitoring views and workspace time |
| [Local installation](../deploy/local/README.md) | Install and test from WSL |
| [Azure installation](../deploy/azure/README.md), [reference](../deploy/azure/reference.md) | Portal deployment, setup, and recovery |
| [Website](../website/README.md), [website production](../website/production/README.md) | Public site and its deployment |

## Development and delivery

| Guide | Purpose |
| --- | --- |
| [Repository layout and verification](repository-layout.md) | Source locations and which checks to run |
| [Formatting](formatting.md), [Rust development](rust-development.md) | Code conventions and Rust tools |
| [Contract values](contract-values.md), [Environment variables](environment-variables.md) | Shared values, boundary parsing, and configuration names |
| [Dependencies](dependencies.md) | Deployment pins and dependency updates |
| [Releases](releases.md), [GitHub Actions](github-actions.md) | Release candidates, approvals, and required checks |
| [Secret scanning](secret-scanning.md), [Agent workflow](agent-workflow.md) | Local secret checks and agent instructions |
| [Agentic maintenance](agentic-maintenance.md) | Automated CI and documentation upkeep |
| [Brand assets](../assets/branding/README.md), [Third-party notices](../THIRD_PARTY_NOTICES.md) | Asset sources and licenses |

## Remaining proposals

[TypeScript refactoring](typescript-refactoring.md) lists unfinished frontend
extractions. A [second runtime](architecture.md#connections-and-runtime-adapters),
such as a GitHub Copilot CLI adapter, is a proposal, not supported. Gap sections in
other guides describe missing features, not available ones.

When behavior changes, update its owning guide in the same change. Keep proposals
separate from current behavior, and move completed plans into the lasting guide.
