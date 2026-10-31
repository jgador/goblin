# Codex App Server protocol models

This project contains generated C# classes with explicit `System.Text.Json`
attributes for the official Rust `codex app-server` protocol. It has no conversation,
turn, model, token, or compaction behavior; orchestration belongs in the adapters.

The schemas in `backend/schemas/codex` are the contract. They target Codex CLI
**0.161.0**, the runtime pinned in the root `package.json`, and use the default export
without `--experimental`. The aggregate schemas keep the full upstream export. The
individual schemas for the unused Gateway OAuth messages and thread prediction updates
are removed.

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

`make protocol-check` runs the generator self-tests and fails on stale generated
files. The generator checks that the aggregate and individual schemas agree and
fails on unsupported selected constructs. Generated files and separate hashes of the
schema and selection inputs are checked in; building needs only the .NET SDK.

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

Generated model rules:

- Names: schema definition names and existing titled-variant names stay stable.
  Inferred names use the property name plus `Details` for anonymous payloads,
  `Variant` where a wrapper would match its payload or base, and numeric suffixes
  for remaining collisions in sorted definition order.
- Serialization: use `ProtocolJson.Options`. Selected required members have
  `required` and `JsonRequired`; required nullable members still write JSON null.
  Optional members are nullable and omitted when null. Explicit `false`, `0`, and
  empty collections are written.
- Scalars: primitive aliases such as `ThreadId`, absolute paths, and `ReasoningEffort`
  stay C# strings; their mappings are recorded in the generated manifest.
- Enums: string enums have explicit wire names and reject unknown values.
- Unions: object unions have abstract base classes, concrete variants, and converters
  that accept the discriminator anywhere in the object. Variants also write their
  discriminator. Mixed and untagged unions have typed branches, and their converters try each
  schema alternative.
  JSON Schema `type` arrays become typed unions of each non-null alternative.
  Scalar and array wrappers use type-level `JsonConverter` attributes, and their
  `Value` properties have `JsonIgnore`.
- Request IDs keep the difference between strings and signed 64-bit integers.
  `RequestId` has value equality so a response ID matches its pending request.
  Other payload classes use reference equality; compare properties explicitly.
- Codex error details include a fallback for unknown strings or objects; the object
  branch keeps arbitrary fields as JSON values. Adapters map unknown errors to safe
  failures and never retry failed Work automatically.
- `JsonElement` appears only where the selected schema allows arbitrary JSON, such as
  JSON-RPC `params` and `result` and the turn output schema. Deserialize an envelope's
  payload into its generated model at the transport boundary.

These are typed serialization models, not a JSON Schema validator; the App Server
validates at runtime.
