# Codex App Server protocol models

This project contains serialization models for the official Rust `codex app-server`.
It implements no conversation, turn, model, token, or compaction behavior.

The checked-in files in `backend/schemas/codex` are the contract. Regenerate with:

```sh
python3 backend/scripts/generate-protocol.py
python3 backend/scripts/generate-protocol.py --check
dotnet test backend/tests/Goblin.Protocol.Tests/Goblin.Protocol.Tests.csproj
```

The generator verifies that the aggregate and individual schemas agree, resolves
all named definitions, and fails on unsupported constructs. Generated files and a
hash of every input schema are checked in; building requires only the .NET SDK.
Refresh the schemas with the installed Codex CLI before regenerating when adopting
a new protocol version. Changes to the generated files should come from the
generator or schema inputs.

Use `ProtocolJson.Options` for serialization. Required members have `required` and
`JsonRequired`; required nullable members still emit JSON null. Optional members
are nullable and omitted when null. Explicit `false`, `0`, and empty collections
remain present. Primitive schema aliases such as `ThreadId`, absolute paths, and
`ReasoningEffort` remain C# strings; their mappings are recorded in the manifest.

String enums have explicit wire names and reject unknown values. Object unions
have abstract bases, concrete record variants, and converters that support a
discriminator anywhere in the JSON object. The concrete records also serialize
their discriminator, so a generic request method cannot accidentally omit it.
Mixed and untagged unions have explicit typed branches; their converters try the
schema alternatives without constructing an ad-hoc JSON document. Request IDs
preserve the distinction between strings and signed 64-bit integers.

`JsonElement` appears only where the schema allows arbitrary JSON: JSON-RPC
`params`/`result`/error data, configurable schemas, extension values, and similarly
unconstrained fields. Deserialize an envelope's payload into its generated model
at the transport boundary. These are typed serialization models, not a general
JSON Schema validation engine; application orchestration belongs outside this
project and Codex runtime validation remains in the App Server.
