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
npm run contracts:generate
npm run contracts:check
npm run test:contracts
```

The small generator extracts explicit declarations into `frontend/src/api/values.ts`
and `deploy/azure/setup/contract-values.js`. It rejects aliases, assigned numeric
values, duplicate values, or syntax it cannot interpret. Add a source/type entry
to the generator when a new enum crosses a browser boundary. Never edit generated
values directly. `npm test` and `npm run typecheck` reject stale generated files;
compile-time regression checks prevent HTTP types widening back to `string`.

TypeScript uses both the generated named constants and their literal unions.
UI-only labels, filters, and actions can keep their own local types; they do not
belong in product contracts merely because they are strings.

Goblin currently supports GitHub repositories only. Use `GitRepository` for shared
core and contract type and member names across languages, matching C#
(`gitRepository` in TypeScript and `git_repository` in Rust/Bash). These names do
not imply support for other Git hosts or arbitrary Git remotes. Keep `GitHub` for
GitHub-specific connections, API calls, repository selection and authorization
controls, worker commands, and release operations. Local Git checkouts and
container image repositories are separate concepts. Generated TypeScript names
match their owning declarations, including `AttentionReason.GitRepositoryRequired`
and `WorkAction.PrepareGitRepository`, while their adapter JSON values remain
`RepositoryRequired` and `PrepareRepository`. HTTP properties, routes, environment
names, and persisted JSON keys retain their existing spellings. Name configuration
directories by their purpose rather than treating them as Git checkouts.

## Parsing and compatibility

HTTP and CLI adapters parse external strings before calling typed internal APIs.
Repository operations preserve `publish`, `pull-request`, `fetch`, and `checkpoint`
exactly; neither enum numeric values nor unsupported operations grant authority.
Authentication enum metadata preserves `apiKey`, `chatgpt`, `accepted`, `unverified`,
`error`, and `info`. Public JSON fields and other named enum values are unchanged.
Integer enum input is rejected by HTTP and execution JSON serializers.

Reverse-engineered EF entities retain existing text columns and generated files.
Use `nameof` for stored named enum values in EF queries and `ContractValue.Parse<T>`
when materializing typed views. Parsing requires an exact defined name, rejecting
numeric strings and unknown values. Repository operation storage uses its explicit
wire-name mapping. Do not use a permissive fallback that fabricates a valid state.
There is no schema or migration change for this refactor.

Setup CLI actions and optional progress scopes use Rust enums; unknown actions and
step IDs are rejected before changing state. The setup browser validates incoming
state against generated values before rendering either polling or SSE updates.
The setup module is embedded in goblinctl and covered by the existing installer
release-input directory allowlist. These changes require a future goblinctl release
before production installers receive them; this PR does not publish that release.
