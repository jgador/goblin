# Codex App Server protocol models

This project contains generated C# classes with explicit `System.Text.Json`
attributes for the official Rust `codex app-server` protocol.
It implements no conversation, turn, model, token, or compaction behavior.

The schemas target Codex CLI **0.155.1**, matching the runtime pinned in the root
`package.json`. They use the default API export without `--experimental`.

The checked-in files in `backend/schemas/codex` are the contract.
[`selection.json`](../../schemas/codex/selection.json) lists the requests,
responses, envelopes, and notifications Goblin uses. Its `roots` are the types
the adapter serializes or deserializes directly. Its `types` entries list the
fields read or written on each referenced object. A dotted path selects a field
inside an inline object. A referenced object has its own entry. Union `variants`
use the wire discriminator, or the schema title for an untagged branch; `*`
retains all branches with only their discriminator unless overridden. This lets
the adapter ignore unrelated turn item kinds without generating their payloads.
The generator rejects missing selected fields, variants, and referenced object
selections. A schema object marked closed must retain all its declared fields
so its generated class can still read valid Codex responses. Unselected Codex
methods do not get generated notification models.

Regenerate with:

```sh
dotnet run --file backend/scripts/GenerateProtocol.cs
dotnet run --file backend/scripts/GenerateProtocol.cs -- --self-test --check
dotnet test backend/tests/Goblin.Protocol.Tests/Goblin.Protocol.Tests.csproj
```

The generator verifies that the aggregate and individual schemas agree, resolves
the selected definitions, and fails on unsupported selected constructs. Generated
files and separate hashes of the schema and selection inputs are checked in;
building requires only the .NET SDK.
When adopting a new protocol version, update the runtime pin and lockfile, then
refresh the schemas with the local npm binary before regenerating:

```sh
npm exec -- codex app-server generate-json-schema --out backend/schemas/codex
dotnet run --file backend/scripts/GenerateProtocol.cs
```

Review the schema diff and remove any obsolete schema files. Changes to the
generated files should come from the generator or selection and schema inputs.
When Goblin starts using another Codex field or message, add it to the selection,
regenerate, and update the protocol and adapter tests.

Schema definition names and existing titled-variant names remain stable. Anonymous
payloads use the property name plus `Details` when the parent already contains that
descriptive phrase. Other inferred names share common prefixes and suffixes only
once; wrappers add `Variant` when their name would otherwise match the payload or
base. Numeric suffixes resolve remaining collisions in sorted definition order.
Variants with different base classes remain distinct even when their fields match.

Use `ProtocolJson.Options` for serialization. Selected required members have `required` and
`JsonRequired`; required nullable members still emit JSON null. Optional members
are nullable and omitted when null. Explicit `false`, `0`, and empty collections
remain present. Primitive schema aliases such as `ThreadId`, absolute paths, and
`ReasoningEffort` remain C# strings; their mappings are recorded in the manifest.

String enums have explicit wire names and reject unknown values. Object unions
have abstract class bases, concrete class variants, and converters that support a
discriminator anywhere in the JSON object. The concrete classes also serialize
their discriminator, so a generic request method cannot accidentally omit it.
Mixed and untagged unions have explicit typed branches; their converters try the
schema alternatives without constructing an ad-hoc JSON document. Request IDs
preserve the distinction between strings and signed 64-bit integers. Scalar and
array wrappers use type-level `JsonConverter` attributes; their `Value` properties
have `JsonIgnore` because the converter writes the underlying value directly.

Protocol payload classes use reference equality. Compare their relevant properties
or enum values explicitly. `RequestId` implements value equality and hashing so a
deserialized response ID can match a pending request's dictionary key.

`JsonElement` appears only where the selected schema allows arbitrary JSON, such
as JSON-RPC `params` and `result`, and the turn output schema. Deserialize an
envelope's payload into its generated model at the transport boundary. These are
typed serialization models, not a general JSON Schema validation engine;
application orchestration belongs outside this project and Codex runtime
validation remains in the App Server.
