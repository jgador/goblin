# Shared contract values

A closed set of behavior or states needs an owning type. Internal APIs should not accept
arbitrary strings where only named values are meaningful. Keep opaque runtime IDs, model
names, Git refs, and external resource references open-ended. Upstream protocol values
belong to that adapter's generated models.

## Owners and generation

Work lifecycle values belong to [Goblin.Core/Work](../backend/src/Goblin.Core/Work/);
integration values belong to [Goblin.Contracts](../backend/src/Goblin.Contracts/). The
[browser generator](../scripts/generate-contract-values.mts) selects the C# owners and
the [Rust installer values](../tools/goblinctl/src/contract_values.rs). Its source list
is the authoritative generation inventory.

Core remains independent of HTTP, persistence, configuration, and runtime protocols.
UI-only labels and filters may keep local browser types. Runtime integrations are not a
closed list.

## Cross-language changes

Edit the owning C# enum or Rust declaration, then run:

```bash
make contracts-generate
make contracts-check
make contracts-test
```

Add new browser-facing enums to the generator's source selection. Never edit generated
files. The generator and [contract type checks](../tests/contracts/values.typecheck.ts)
reject stale output and accidental widening to unrestricted strings.

Use `GitRepository` for provider-independent concepts and `GitHub` for the implemented
provider. Preserve existing wire, route, environment, and persisted JSON spellings
unless the task explicitly changes their contracts.

## Parsing and compatibility

HTTP and CLI adapters parse strings at their boundaries and reject numeric or unknown
enum inputs. Unknown repository operations grant no authority. Generated EF text columns
do not weaken this rule: parse stored values into owning types and fail on invalid data
instead of substituting a valid state.

Installer actions and state are validated before effects. Setup assets are embedded in
goblinctl, so changing them reaches hosts through an installer release.
