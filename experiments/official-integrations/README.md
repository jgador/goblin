# Official integration experiment

Start with [STATE.md](STATE.md) for the next bounded task and [FEASIBILITY.md](FEASIBILITY.md) for supported routes and restrictions.

## Build and focused checks

Requires the .NET 10 SDK pinned by the repository. Keep temporary outputs in `.artifacts/official-integrations`. No Docker, PostgreSQL, Kubernetes or platform login is needed for the focused checks.

```bash
timeout 120 dotnet build experiments/official-integrations/tests/Goblin.Mcp.Prototype.Tests.csproj \
  -m:1 -nr:false -p:UseSharedCompilation=false \
  --artifacts-path .artifacts/official-integrations/build
timeout 30 dotnet .artifacts/official-integrations/build/bin/Goblin.Mcp.Prototype.Tests/debug/Goblin.Mcp.Prototype.Tests.dll \
  "$(command -v dotnet)" \
  "$PWD/.artifacts/official-integrations/build/bin/Goblin.Mcp.Prototype/debug/Goblin.Mcp.Prototype.dll"
```

The test executable uses mocked backend responses and an actual SDK stdio child process. Its process deadline is 15 seconds; it disposes the client/session after the check. This is not end-to-end ChatGPT or Slack validation.

## Prototype local configuration

These experiment-only variables are read at the stdio process boundary and never forwarded to execution workers:

| Name | Meaning |
| --- | --- |
| `GOBLIN_MCP_ORIGIN` | Fixed customer Goblin HTTPS origin, or loopback HTTP origin. No path, query, fragment or embedded credentials. |
| `GOBLIN_MCP_SESSION` | Already valid local owner-session token. Supply privately at runtime; never put it in plugin manifests, command arguments, Git or prompts. |
| `GOBLIN_MCP_READ_WORK_IDS` | Explicit comma-separated positive Work IDs granted for status reads. Empty/malformed grants prevent startup. |

Launch the built prototype DLL with `dotnet` inside the customer trust boundary. stdout is MCP only. HTTP requests have a five-second deadline, a 128 KiB response limit, no redirects and no automatic cookies. The only tool is `goblin_get_work_status(workId)`; IDs and versions preserve PostgreSQL bigint precision as strings.

Do not publish this session bridge as a remote server. There is no OAuth implementation, identity mapping, plugin submission or installation automation yet. See feasibility notes before connecting any shared service.

## Isolated installation policy

The focused checks do not create a Goblin installation or require an owner password. When a future check does, generate a fresh cryptographic `GOBLIN_LOCAL_PASSWORD` in its runtime environment and pass a private, disposable `GOBLIN_PASSWORD_HASH_FILE` path to existing setup. Never reuse an existing checkout's secret directory, echo secrets, use a shared default, or commit verifiers. Keep secrets outside agent execution and clean up disposable installation files/processes.

Every iteration has a 20-minute maximum, stops implementation near minute 18, records sanitized results and invokes the authorized `$commit-push` workflow for the same experiment branch. Never merge or create a PR automatically.
