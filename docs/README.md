# Documentation

These guides describe the checked-in application and installation formats.
Goblin is preproduction; local development installations can be recreated when
formats change. Follow [AGENTS.md](../AGENTS.md) for contribution boundaries.

## Product and architecture

| Guide | Purpose |
| --- | --- |
| [Architecture](architecture.md) | Four ownership boundaries, durability, isolation, and recovery contracts |
| [Work lifecycle](work-lifecycle.md) | Core transitions, commands, attention, attempts, and persisted history |
| [Work interface](work-centered-ui.md) | Current navigation, controls, integrations, and UI gaps |
| [Execution hosting](execution-hosting.md) | Text workers, repository sandboxes, claims, and cancellation |
| [Workspace lifecycle](workspace-lifecycle.md) | Persistent files, turns, checkpoints, inspection, and retention |
| [Repository authorization](git-repository-intent.md) | GitHub selection, enablement, immutable grants, and publication |
| [Repository setup memory](git-repository-setup-memory.md) | Scoped preparation observations and verified recall |

## Connections and operations

| Guide | Purpose |
| --- | --- |
| [Authentication](authentication.md) | Owner password, Codex account setup, isolated credentials, and checks |
| [Codex integration](codex-integration.md) | Transport/process ownership and generated protocol maintenance |
| [Model catalog](model-catalog.md) | Start/Retry options and connection-scoped discovery |
| [Slack integration](slack-integration.md) | App setup, linked identities, durable ingress, and delivery limits |
| [ChatGPT plugin](chatgpt-integration.md) | Customer-created MCP plugin, OAuth, Work grants, verification, and troubleshooting |
| [Optional ChatGPT directory submission](chatgpt-directory-submission.md) | Separate publisher eligibility and public review requirements |
| [Database](database.md) | SQL baseline, EF generation, TLS access, and local client profiles |
| [Native tooling](goblinctl.md) | CLI commands, setup, packaging, and releases |
| [Kubernetes names](kubernetes-names.md) | Platform/website/execution identity and storage ownership |
| [System readings](system-overview.md), [Cluster](cluster-view.md), [Logs](logging.md), [Timezones](timezones.md) | Monitoring views and their operating contracts |
| [Local installation](../deploy/local/README.md) | WSL installation testing and lifecycle commands |
| [Manual Azure installation](../deploy/azure/README.md), [reference](../deploy/azure/reference.md) | Portal deployment and operator setup/recovery |
| [Website](../website/README.md), [website production](../website/production/README.md) | Independent public site and deployment assets |

## Development and delivery

| Guide | Purpose |
| --- | --- |
| [Repository layout and verification](repository-layout.md) | Source owners and check selection |
| [Formatting](formatting.md), [Rust development](rust-development.md) | Construction/style conventions and native tools |
| [Typed JSON](typed-json-contracts.md), [Contract values](contract-values.md), [Environment variables](environment-variables.md) | Boundary parsing, generated values, and named configuration |
| [Dependencies](dependencies.md), [Dependency inventory](dependency-inventory.md) | Selected deployment pins and authoritative dependency inputs |
| [Releases](releases.md), [GitHub Actions](github-actions.md) | Immutable release candidates, approvals, and required checks |
| [Secret scanning](secret-scanning.md), [Agent workflow](agent-workflow.md) | Contributor security and instruction maintenance |
| [Agentic maintenance](agentic-maintenance.md) | Owner-only CI policy and documentation upkeep, API budgets, and draft PR publication |
| [Brand assets](../assets/branding/README.md), [Third-party notices](../THIRD_PARTY_NOTICES.md) | Asset ownership and license provenance |

## Remaining proposals

[Frontend maintenance](typescript-refactoring.md) describes unfinished ownership
extractions. The [second-runtime experiment](second-runtime-experiment.md) is a
bounded proposal, not implemented support. Follow-up tables in current guides
likewise describe gaps, not available features.

When behavior changes, update its owning guide and callers together. Keep current
behavior separate from proposals. Consolidate completed plans into their lasting
guides, remove redundant documents, and repair incoming links. Dated test results
do not establish current coverage; report checks actually run for each change.
