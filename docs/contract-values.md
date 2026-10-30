# Shared contract values

A fixed set of behavior or state values must have an owning type. Do not accept
an unrestricted string in an internal API when only named values are meaningful.
Keep opaque identifiers such as runtime IDs, model names, Git refs, external plan
names, and resource references extensible. Upstream protocol fields belong to
that adapter's generated models, not a Goblin-wide list of third-party strings.

## Owners

| Contract | Source of truth | Browser consumer |
| --- | --- | --- |
| Work and attempt statuses, attention, failures, history kinds | `Goblin.Core/Work/WorkTypes.cs` | Work views and rendering |
| Git repository authorization status | `Goblin.Core/Work/GitRepositoryAuthorization.cs` | Repository approval |
| Git repository operations and wire names | `Goblin.Core/Work/GitRepositoryOperationKind.cs` | Internal worker HTTP/CLI boundary |
| Inspection states and observations | `Goblin.Core/Work/WorkspaceSessionRules.cs` | Workspace inspection |
| Work commands and identity reservation kinds | `Goblin.Application/Work/WorkContracts.cs`, `IdentityStore.cs` | Work commands |
| Account, verification, notice, connection, and publication states | `Goblin.Contracts/ContractValues.cs` | Connection and Work settings |
| Installer actions, phases, states, and step IDs | `tools/goblinctl/src/contract_values.rs` | Standalone setup UI |

Paths in the C# rows are relative to `backend/src/`. Core lifecycle types remain
independent of HTTP, persistence, configuration, and runtime protocols.

`RuntimeIds.Codex` names the current integration without restricting the runtime
contract to a permanent vendor list. Executable names and directories that happen
to be called `codex` are separate adapter details.

## Cross-language changes

Edit the owning C# enum or Rust `contract_values!` declaration, then run:

```bash
make contracts-generate
make contracts-check
make contracts-test
```

The small generator extracts explicit declarations into `frontend/src/api/values.ts`
and `deploy/azure/setup/contract-values.js`. It rejects aliases, assigned numeric
values, duplicate values, or syntax it cannot interpret. Add a source/type entry
to the generator when a new enum crosses a browser boundary. Never edit generated
values directly. `make test` and `make typecheck` reject stale generated files;
compile-time regression checks prevent HTTP types widening back to `string`.

TypeScript uses both the generated named constants and their literal unions.
UI-only labels, filters, and actions can keep their own local types; they do not
belong in product contracts merely because they are strings.

Use `GitRepository` for shared types and members (`gitRepository` in TypeScript,
`git_repository` in Rust/Bash). Use `GitHub` for provider-specific connections,
APIs, selection, authorization, worker commands, and releases. Only GitHub
repositories are supported; local checkouts and container registries are distinct.
Generated TypeScript names follow their owners, while adapter JSON retains existing
wire values such as `RepositoryRequired` and `PrepareRepository`. Preserve HTTP,
route, environment, and persisted JSON spellings. Name configuration directories
for their purpose.

## Parsing and compatibility

HTTP and CLI adapters parse strings at the boundary. Repository operations use
exactly `publish`, `pull-request`, `fetch`, and `checkpoint`; numeric or unknown
values grant no authority. Preserve existing public JSON and authentication wire
names, and reject integer enum inputs in HTTP and execution serializers.

EF entities retain their text columns and generated mappings. Use `nameof` for
stored named enum values in queries and `ContractValue.Parse<T>` for typed views;
reject numeric strings and unknown values instead of fabricating a valid state.
Repository operations use their explicit wire mapping.

Rust setup actions and optional progress scopes use enums; reject unknown actions
and step IDs before changing state. The setup browser validates polling and SSE
state against generated values. Setup modules are embedded in goblinctl: changes
require a new installer release to reach hosts, even when source is already merged.
