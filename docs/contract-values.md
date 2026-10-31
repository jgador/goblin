# Shared contract values

A fixed set of behavior or state values must have an owning type. Do not accept
an unrestricted string in an internal API when only named values are meaningful.
Keep opaque identifiers such as runtime IDs, model names, Git refs, external plan
names, and resource references open-ended. Upstream protocol fields belong to
that adapter's generated models, not to a Goblin-wide list.

## Owners

| Contract | Source of truth | Browser consumer |
| --- | --- | --- |
| Work and attempt statuses, attention, failures, history kinds | `Goblin.Core/Work/WorkTypes.cs` | Work views and rendering |
| Git repository authorization status | `Goblin.Core/Work/GitRepositoryAuthorization.cs` | Repository approval |
| Inspection states and observations | `Goblin.Core/Work/WorkspaceSessionRules.cs` | Workspace inspection |
| Work commands and identity reservation kinds | `Goblin.Application/Work/WorkContracts.cs`, `IdentityStore.cs` | Work commands |
| Notices, verification, authentication methods, GitHub connection, connection availability, repository operation states | `Goblin.Contracts/ContractValues.cs` | Connection and Work settings |
| Slack connection and setup statuses | `Goblin.Contracts/Conversations/ExternalConversations.cs` | Slack settings |
| Installer actions, phases, states, and step IDs | `tools/goblinctl/src/contract_values.rs` | Standalone setup UI |
| Git repository operations and wire names | `Goblin.Core/Work/GitRepositoryOperationKind.cs` | None; internal worker HTTP/CLI boundary, not generated |

Paths in the C# rows are relative to `backend/src/`. Core lifecycle types stay
independent of HTTP, persistence, configuration, and runtime protocols.

`RuntimeIds.Codex` names the current integration without making runtimes a closed
list. Executables and directories named `codex` are separate adapter details.

## Cross-language changes

Edit the owning C# enum or Rust `contract_values!` declaration, then run:

```bash
make contracts-generate
make contracts-check
make contracts-test
```

`scripts/generate-contract-values.mts` writes `frontend/src/api/values.ts` and
`deploy/azure/setup/contract-values.js`. It rejects aliases, assigned numeric
values, duplicate values, and syntax it cannot read. When a new enum crosses a
browser boundary, add it to the generator's source list. Never edit generated
files. `make test` and `make typecheck` reject stale generated files, and
`tests/contracts/values.typecheck.ts` stops HTTP types from widening back to `string`.

TypeScript uses both the generated constants and their literal unions. UI-only
labels, filters, and actions can keep local types; being a string does not make
them product contracts.

Use `GitRepository` for shared types and members (`gitRepository` in TypeScript,
`git_repository` in Rust/Bash). Use `GitHub` for provider-specific connections,
APIs, selection, authorization, worker commands, and releases. Only GitHub
repositories are supported; local checkouts and container registries are separate
concepts. Generated TypeScript names follow their owners, while wire values keep
their existing spellings, such as `RepositoryRequired` and `PrepareRepository`.
Keep existing HTTP, route, environment, and persisted JSON spellings. Name
configuration directories for their purpose.

## Parsing and compatibility

HTTP and CLI adapters parse strings at the boundary. Repository operations accept
exactly `publish`, `pull-request`, `fetch`, and `checkpoint`; numeric or unknown
values grant no authority. Keep existing public JSON and authentication wire
names, and reject integer enum inputs in HTTP and execution serializers.

EF entities keep their text columns and generated mappings. Use `nameof` for
stored enum values in queries and `ContractValue.Parse<T>` for typed views; reject
numeric strings and unknown values instead of inventing a valid state. Repository
operations use their explicit wire mapping.

Rust setup actions and optional progress scopes use enums; reject unknown actions
and step IDs before changing state. The setup page validates polling and SSE
state against the generated values. Setup files are embedded in goblinctl, so
changes reach hosts only through a new installer release.
