# C# App Server integration

Goblin runs the official Rust `codex app-server` as a child process. The C# code
replaces the former TypeScript backend; it does not implement Codex Core.
The existing authentication preview, HTTP routes, cookies, browser UI, and prompt
limits remain in place.

## Responsibilities

| Layer | Owns |
| --- | --- |
| `backend/src/Goblin.Protocol` | Generated POCO records, typed unions/enums, request IDs, and `System.Text.Json` converters |
| `backend/src/Goblin.Web/Codex/CodexClient.cs` | Child process, isolated environment, JSONL framing, initialization, request correlation, typed notifications, timeouts, restart and shutdown |
| `backend/src/Goblin.Web/Auth/Authentication.cs` | Serialized login/logout operations, API-key verification, safe account summaries, and prompt admission |
| `backend/src/Goblin.Web/Codex/PromptRunner.cs` | Maps each active Goblin prompt operation to its Codex thread/turn IDs, selects final assistant text, and interrupts/unsubscribes on cleanup |
| `backend/src/Goblin.Web/Access/Workspace.cs` and `GoblinApplication.cs` | Private workspace paths, owner access, browser sessions, HTTP validation, static assets, and Minimal API routes |
| Rust Codex App Server/Core | Credential storage/refresh, conversation history, turns, context windows, tokens, auto-compaction, model metadata, and runtime behavior |

The preview has one owner and one Codex process per data directory. Each prompt
uses a fresh ephemeral thread, as before. Thread/turn IDs live only for that
operation; there is no Goblin transcript store or persistent chat-session model.
Browser unlock sessions remain separate from Codex conversations. Adding durable
Goblin conversations later would require storing a Goblin-session-to-Codex-thread
mapping and using Codex resume/read operations, while leaving conversation state
with Codex.

The transport initializes once per connection and acknowledges with `initialized`.
It serializes writes, correlates out-of-order responses, decodes all known
notifications into generated types, and ignores unknown future notification methods.
Notification callbacks enqueue authentication work rather than blocking the reader.
Server-initiated interactive requests retain the preview's `-32601` rejection.
Raw Codex stderr and upstream error bodies are never logged or sent to the browser.

A broken transport or timed-out RPC rejects pending work and retires the process.
Restart waits for exit before reusing the credential directory. Shutdown sends
SIGTERM on Unix (closes stdin on Windows), then kills the process tree if the grace
period expires. HTTP prompt cancellation requests `turn/interrupt` and then
`thread/unsubscribe`; interruption failure retires the connection.

## Protocol regeneration

The 305 checked-in schema files are authoritative. A separate coding agent
generated models for all 698 named definitions plus their inline variants.
`backend/scripts/generate-protocol.py` is deterministic, verifies the aggregate and
individual schemas agree, and records input hashes. Generated models are checked
in, so an ordinary .NET build does not require Python or a Codex installation.

```bash
python3 backend/scripts/generate-protocol.py --check
python3 backend/scripts/generate-protocol.py
dotnet test backend/tests/Goblin.Protocol.Tests
```

When upgrading Codex, regenerate the schemas with the chosen official binary,
regenerate C#, review the schema diff, and rerun the tests before changing the
runtime pin. Models use explicit JSON property names, required fields, enum wire
names, and converters for discriminated/mixed unions. `JsonElement` is confined
to fields the schema leaves unconstrained, including envelope payloads; protocol
operations and notifications use the generated typed models. See the
[model project documentation](../backend/src/Goblin.Protocol/README.md).

## Build and verification

```bash
npm ci
npm run build                    # browser assets and .NET solution
dotnet run --no-build --project backend/src/Goblin.Web
npm test                         # schema drift, .NET tests, HTTP tests
npm run test:codex                # official pinned Rust binary, synthetic key only
npm run test:browser              # unchanged UI against the C# test host
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish
```

Node.js is used for browser compilation, npm's pinned Codex distribution, and test
tooling. The production host runs entirely in .NET and can use a separately
installed official Codex executable through `GOBLIN_CODEX_COMMAND`. The Docker
image includes the native binary and its adjacent resources, without Node.js.

Migration validation first ran the original 22 TypeScript tests as a baseline.
The HTTP scenarios were redirected to `backend/tests/Goblin.TestHost`; API-key verifier
and origin-validation checks moved to .NET. The fake JSONL peer now includes fields
required by the actual schemas, rather than relying on the old partial TypeScript
types. Additional .NET tests cover serialization, concurrent initialization,
request correlation, UTF-8 framing, malformed/oversized output, server requests,
timeouts, process crashes, restart ordering, and cleanup. The superseded
TypeScript backend was removed after the C# HTTP tests and real-runtime storage
check passed.
