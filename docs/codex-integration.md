# Codex integration

Goblin runs the official Rust Codex App Server as a child process. Its adapter owns
transport, account operations, and translation into Goblin execution contracts. Codex is
the only implemented runtime; its process model and protocol are not core requirements.
See [architecture](architecture.md).

## Runtime boundary

Each credential directory has one owner and one application controller. Connection
checks use restricted ephemeral threads; durable Work runs in separate workers with
saved Work context. Native sessions are execution evidence, not the product transcript.

Runtime errors require failure attention even when Codex suggests retrying them. Text
turns have tools disabled. Repository tools run only within the
[execution sandbox](execution-hosting.md). Broken transport retires the old process
before a new one starts; transport recovery cannot relaunch claimed Work. Credentials,
RPC payloads, and upstream error bodies stay private. Raw Codex stderr is drained and
discarded because it can contain authentication details.

[Transport tests](../backend/tests/Goblin.Tests/) and
[protocol tests](../backend/tests/Goblin.Protocol.Tests/) own framing, correlation,
serialization, cancellation, restart, and shutdown expectations. Model-assisted native
session resume and live approval prompts are not supported.

## Model selection and discovery

The Work Start and Retry options select a model and supported reasoning effort. Choices
remain per-Work browser drafts until execution; a connection change resets them.
Attempts preserve requested and reported models independently of later catalog changes.
If Codex rejects the selected model, Work follows normal failure and attention rules;
Goblin never automatically substitutes another model or retries the attempt.

Discovery uses App Server model metadata, scoped to the account generation and
executable identity. A failed refresh keeps a stale catalog for the same account; a
changed account cannot reuse it. Without a catalog, Work uses the Codex default.
Discovery failure neither creates Work nor authorizes retry or model fallback. The
[catalog policy](../backend/src/Goblin.Application/Work/ModelCatalogPolicy.cs) owns
refresh timing. Restart Goblin after replacing the executable so its running process and
catalog agree.

## Protocol regeneration

[The protocol README](../backend/src/Goblin.Protocol/README.md) owns schema export,
selection, regeneration, and serialization rules. Runtime pins and schemas change
together. Generated models are checked in; normal .NET builds do not need Codex.

`make codex-test` checks the pinned binary's storage and transport using synthetic
credentials without submitting a model task. It cannot prove authenticated execution.
See [test selection](repository-layout.md#choosing-verification).

Node builds the browser and provides npm's pinned Codex package. The production .NET
host can use a separately installed executable through `GOBLIN_CODEX_COMMAND`. The
Docker image includes the native runtime and resources without Node.
