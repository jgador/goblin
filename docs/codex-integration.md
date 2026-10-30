# Codex integration

Goblin runs the official Rust Codex App Server as a child process. The C# adapter
owns its transport and account operations, while durable Work uses Goblin-owned
execution contracts. Codex is the only implemented runtime; its process model and
protocol are not requirements for future integrations. See [architecture](architecture.md).

## Responsibilities

| Layer | Owns |
| --- | --- |
| `backend/src/Goblin.Protocol` | Generated POCO classes, typed unions/enums, request IDs, and explicit `System.Text.Json` attributes/converters |
| `backend/src/Goblin.Integrations.Codex/CodexClient.cs` | Child process, isolated environment, JSONL framing, initialization, request correlation, typed notifications, timeouts, restart and shutdown |
| `backend/src/Goblin.Integrations.Codex/Authentication.cs` | Serialized login/logout operations, API-key verification, safe account summaries, and prompt admission |
| `backend/src/Goblin.Integrations.Codex/PromptRunner.cs` | Maps each active Goblin prompt operation to its Codex thread/turn IDs, selects final assistant text, and interrupts/unsubscribes on cleanup |
| `backend/src/Goblin.Web/Access/Workspace.cs` and `GoblinApplication.cs` | Private workspace paths, owner access, browser sessions, HTTP validation, static assets, and Minimal API routes |
| Rust Codex App Server/Core | Codex credential storage/refresh, runtime conversation context, turns, context windows, tokens, auto-compaction, model metadata, and runtime behavior |

Goblin has one owner and one application controller per data directory.
Connection verification uses a restricted ephemeral Codex thread. Durable Work
uses independent execution workers and non-ephemeral native sessions, with
Goblin's persisted objective, messages, decisions, artifacts, and prior results
as context. The connection-verification prompt does not define durable execution.

`CodexWorkRunner` translates Work into runtime inputs and maps progress, session
provenance, a final result or input request, and safe failures back to shared
contracts. It treats every reported runtime error as a failure, including errors
that Codex says it could retry. Live approvals and native session resume are not
advertised. Questions have tools disabled; repository tools run only in the
[execution sandbox](execution-hosting.md).

PostgreSQL owns Work history and decisions. Native session references aid
inspection, but are not the product transcript. Future adapters can receive
Goblin context without reading a Codex thread; handoff policy remains deferred.

The transport initializes once per connection and acknowledges with `initialized`.
It serializes writes, correlates out-of-order responses, decodes all known
notifications into generated types, and ignores unknown future notification methods.
Notification callbacks enqueue authentication work rather than blocking the reader.
Server-initiated interactive requests receive a `-32601` rejection.
Raw Codex stderr and upstream error bodies are never logged or sent to the browser.

A broken transport or timed-out RPC rejects pending work and retires the process.
Restart waits for exit before reusing the credential directory. Shutdown sends
SIGTERM on Unix (closes stdin on Windows), then kills the process tree if the grace
period expires. HTTP prompt cancellation requests `turn/interrupt` and then
`thread/unsubscribe`; interruption failure retires the connection.

## Model selection and discovery

**Options** in a Work's Start/Retry panel selects model and reasoning effort;
conversation composers have no model controls. Unsupported effort stops are
disabled. The picker initially shows the default and three visible models;
**Show more** loads up to ten. Before execution, selections are per-Work browser
tab drafts; an incompatible connection change invalidates them. Start/Retry stores
the requested model and effort on the attempt, and execution details show both the
requested and reported model. Later catalog changes do not rewrite history.

The controller paginates App Server `model/list`, excludes hidden models, and
atomically stores the visible catalog in `connection_model_catalog`, scoped to the
connection's authentication generation and Codex executable stamp. Agent sandboxes
cannot access it. Refresh follows account or executable changes, opening a picker
with a catalog older than 24 hours, or **Refresh models**. Concurrent refreshes
coalesce. Failure retains the last catalog as stale with a one-minute automatic
retry cooldown; manual refresh can retry sooner. A changed account cannot use the
previous catalog, and without a catalog Work can use the Codex default.

Replacing the CLI while Goblin is running leaves the existing App Server running
and the catalog stale; restart Goblin to complete refresh. Discovery failure does
not create or retry Work. A model rejection follows the normal failure and
attention policy, without automatic fallback.

## Protocol regeneration

The checked-in schemas and pinned Codex runtime are maintained together.
[`selection.json`](../backend/schemas/codex/selection.json) identifies the requests,
responses, notifications, fields, and variants Goblin uses. The C# file-based
[`GenerateProtocol.cs`](../backend/scripts/GenerateProtocol.cs) generates only
that selection and its referenced types. It verifies that the aggregate and
individual schemas agree and records separate schema and selection hashes. Generated models are checked in; an ordinary .NET build does not run
the generator or need a Codex installation.

```bash
dotnet run --file backend/scripts/GenerateProtocol.cs -- --self-test --check
dotnet run --file backend/scripts/GenerateProtocol.cs
dotnet test backend/tests/Goblin.Protocol.Tests
```

When upgrading Codex, update the runtime pin and lockfile, regenerate the schemas
with that local npm binary, regenerate C#, review the schema diff, and rerun the
tests. Keep the runtime and schema changes together. Models use explicit JSON
property names, required fields, enum wire names, and converters for
discriminated/mixed unions. `JsonElement` is confined
to fields the schema leaves unconstrained, including envelope payloads; protocol
operations and notifications use the generated typed models. See the
[model project documentation](../backend/src/Goblin.Protocol/README.md).

## Build and verification

```bash
make install
make build                    # browser assets and .NET solution
dotnet run --no-build --project backend/src/Goblin.Web
make test                         # schema drift, .NET tests, HTTP tests
make codex-test                # official pinned Rust binary, synthetic key only
make browser-test              # browser journeys against the C# test host
dotnet publish backend/src/Goblin.Web -c Release -o .artifacts/publish
```

Node.js is used for browser compilation, npm's pinned Codex distribution, and test
tooling. The production host runs entirely in .NET and can use a separately
installed official Codex executable through `GOBLIN_CODEX_COMMAND`. The Docker
image includes the native binary and its adjacent resources, without Node.js.

Transport and protocol tests cover serialization, concurrent initialization,
request correlation, UTF-8 framing, malformed/oversized output, server requests,
timeouts, crashes, restart ordering, and cleanup. HTTP/browser fixtures exercise
the connection UI. Real Codex initialization uses a synthetic key and does not
establish authenticated model coverage. Select checks for the changed boundary
using [repository verification](repository-layout.md#choosing-verification).
