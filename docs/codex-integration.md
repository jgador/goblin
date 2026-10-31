# Codex integration

Goblin runs the official Rust Codex App Server as a child process. The C# adapter
owns the transport and account operations; durable Work uses Goblin-owned execution
contracts. Codex is the only implemented runtime, and its process model and protocol
are not requirements for other runtimes. See [architecture](architecture.md).

## Responsibilities

| Location | Owns |
| --- | --- |
| `backend/src/Goblin.Protocol` | Generated protocol models and JSON converters ([README](../backend/src/Goblin.Protocol/README.md)) |
| `backend/src/Goblin.Integrations.Codex/CodexClient.cs` | Child process, private environment, JSONL framing, initialization, request correlation, notifications, timeouts, restart, and shutdown |
| `backend/src/Goblin.Integrations.Codex/Authentication.cs` | Serialized login and logout, API-key verification, safe account summaries, and connection checks |
| `backend/src/Goblin.Integrations.Codex/PromptRunner.cs` | Connection-check thread and turn, final reply text, interrupt and unsubscribe on cleanup |
| `backend/src/Goblin.Integrations.Codex/CodexWorkRunner.cs` | Durable Work turns: runtime input, progress, session reference, result or question, and safe failures |
| `backend/src/Goblin.Web` | Workspace paths, owner sessions, Host and Origin checks, static assets, and HTTP routes (`Access/`, `Http/Endpoints/`) |
| Rust Codex App Server and Core | Credential storage and refresh, conversation context, turns, tokens, compaction, model metadata, and runtime behavior |

Each data directory has one owner and one application controller. Connection checks
use a restricted ephemeral Codex thread. Durable Work uses separate execution workers
and non-ephemeral native sessions, with Goblin's saved objective, messages, decisions,
artifacts, and results as context. The connection check does not define durable
execution.

`CodexWorkRunner` treats every runtime error notification as a failure, including
errors that Codex says it could retry. Live approvals and native session resume are
not advertised. Turns without repository access run read-only with tools disabled;
repository tools run only in the [execution sandbox](execution-hosting.md).
PostgreSQL owns Work history and decisions. Native session references help
inspection but are not the product transcript.

The transport sends `initialize` once per process and then `initialized`. It
serializes writes, matches out-of-order responses, decodes known notifications into
generated types, and ignores unknown notification methods. Notification handlers
queue authentication work instead of blocking the reader. Server-initiated requests
receive a `-32601` error. Raw Codex stderr and upstream
error bodies are never logged or sent to the browser.

A broken transport or an RPC that exceeds its 20-second timeout fails pending
requests and retires the process. A new process starts only after the old one exits.
Shutdown sends SIGTERM on Unix (closes stdin on Windows) and kills the process tree
after a 2-second grace period. Cancelling a connection check sends `turn/interrupt`
and then `thread/unsubscribe`; if the interrupt fails, the connection is retired.

## Model selection and discovery

**Options** in a Work's Start or Retry panel selects the model and reasoning effort;
conversation composers have no model controls. Unsupported effort levels are
disabled. The picker first shows three models, starting with the selected or default
model; **Show more models** shows up to ten. Before execution, the selection is a
per-Work draft in the browser tab, and a connection change resets it. Start and Retry
store the requested model and effort on the attempt. Execution details show the
requested and the reported model. Later catalog changes do not rewrite history.

The controller pages through App Server `model/list`, skips hidden models, and stores
the visible catalog in `connection_model_catalog`. Each catalog belongs to one
connection authentication generation and one Codex executable stamp. Agent sandboxes
cannot read it. A refresh runs after an account or executable change, when a picker
opens a catalog older than 24 hours, or on **Refresh models**. Concurrent refreshes
for one connection run once. A failed refresh keeps the last catalog, marks it stale,
and waits one minute before an automatic retry; **Refresh models** can retry sooner.
A changed account cannot use the previous catalog. Without a catalog, Work uses the
Codex default model.

If you replace the Codex CLI while Goblin runs, the existing App Server keeps running
and the catalog stays stale; restart Goblin to finish the refresh. Discovery failure
does not create or retry Work. A model rejection follows the normal failure and
attention rules, with no automatic fallback.

## Protocol regeneration

The checked-in schemas in `backend/schemas/codex` and the pinned Codex runtime change
together. [`selection.json`](../backend/schemas/codex/selection.json) lists the parts
of the protocol Goblin uses, and
[`GenerateProtocol.cs`](../backend/scripts/GenerateProtocol.cs) generates only those
types. Generated models are checked in, so a normal .NET build does not need Codex.

```bash
make protocol-check
make protocol-generate
dotnet test backend/tests/Goblin.Protocol.Tests
```

To upgrade Codex, update the runtime pin and lockfile, refresh the schemas with the
local npm binary, regenerate C#, review the schema diff, and rerun the tests in one
change. The [protocol model README](../backend/src/Goblin.Protocol/README.md) gives
the exact steps and the selection and serialization rules.

## Build and verification

See [test selection](repository-layout.md#choosing-verification) for build and test
scope. Transport and protocol tests use fixtures for serialization, concurrent
initialization, request matching, UTF-8 framing, malformed or oversized output,
server requests, timeouts, crashes, restart order, and cleanup. `make codex-test`
uses the pinned official binary with synthetic credentials. Neither establishes
authenticated model coverage.

Node.js builds the browser assets, provides npm's pinned Codex package, and runs test
tooling. The production host is .NET only and can use a separately installed Codex
executable through `GOBLIN_CODEX_COMMAND`. The Docker image includes the native Codex
binary and its resources, without Node.js.
