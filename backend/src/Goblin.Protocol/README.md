# Codex App Server protocol models

This project contains generated C# classes with explicit `System.Text.Json` attributes
for the official Rust `codex app-server` protocol. It has no conversation, turn, model,
token, or compaction behavior; orchestration belongs in the adapters.

The schemas in `backend/schemas/codex` are the contract. They target the runtime pinned
in the root [package.json](../../../package.json) and use the default export without
`--experimental`. The aggregate schemas keep the full upstream export. The individual
schemas for the unused Gateway OAuth messages and thread prediction updates are removed.

[`selection.json`](../../schemas/codex/selection.json) lists what Goblin uses:

- `roots` are the types the adapter serializes or deserializes directly.
- `types` entries list the fields read or written on each referenced object. A dotted
  path selects a field inside an inline object; a referenced object needs its own
  entry.
- Union `variants` use the wire discriminator, or the schema title for an untagged
  branch. `*` keeps all branches with only their discriminator unless overridden, so
  the adapter can ignore unrelated turn item kinds.

The generator rejects missing selected fields, variants, and referenced object
selections, and unused selections. A schema object marked closed must have all its
fields selected so its class can still read valid Codex responses. Unselected methods
get no generated notification models.

Regenerate and check with:

```sh
make protocol-generate
make protocol-check
dotnet test backend/tests/Goblin.Protocol.Tests/Goblin.Protocol.Tests.csproj
```

`make protocol-check` runs the generator self-tests and fails on stale generated files.
The generator checks that the aggregate and individual schemas agree and fails on
unsupported selected constructs. Generated files and separate hashes of the schema and
selection inputs are checked in; building needs only the .NET SDK.

To adopt a new Codex version, update the runtime pin and lockfile, then refresh the
schemas with the local npm binary:

```sh
make codex-run ARGS="app-server generate-json-schema --out backend/schemas/codex"
make protocol-generate
```

Review the schema diff, remove obsolete schema files, and remove the unused Gateway
OAuth and thread prediction update individual schemas before regenerating. Change
generated files only through the generator, selection, or schemas. When Goblin starts
using another Codex field or message, add it to the selection, regenerate, and update
the protocol and adapter tests.

## Serialization contracts

Use `ProtocolJson.Options`. Required fields stay required, including explicit null for
required nullable fields. Optional nulls are omitted; explicit false, zero, and empty
collections are written. Preserve discriminator handling, string enum validation, and
string-versus-integer request IDs.

Request IDs require value equality for response correlation; other payloads use
reference equality. Arbitrary JSON is allowed only where selected schemas allow it.
Adapters translate unknown runtime errors into safe failures without retries. The
[generator](../../scripts/GenerateProtocol.cs) and
[protocol tests](../../tests/Goblin.Protocol.Tests/) define model shape and converters.

These are typed serialization models, not a JSON Schema validator; the App Server
validates at runtime.
