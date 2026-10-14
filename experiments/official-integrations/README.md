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

## Customer authorization boundary experiment

`ConnectorReadBoundary` is an isolated server-side component, not yet wired to a public endpoint or the stdio host. It verifies signed JWT access tokens using Microsoft IdentityModel before invoking `ICustomerWorkStatusSource`, which must authorize and project status within the customer's persistence boundary. It no longer accepts a separate grant checker and unrestricted reader. The existing local stdio cookie bridge is unchanged and still unsuitable for public distribution.

Its current narrow token contract is RS256, `typ=at+jwt`, a configured exact HTTPS issuer/resource audience, required expiry, optional not-before enforcement, and nonempty `sub`, `jti` and exact `goblin.work.status.read` scope. Issuer public keys are provided by trusted customer configuration and defensively copied; token-supplied key locations are never followed. Identity tokens, unsigned tokens and audience trailing-slash variations are rejected. This is not an OAuth issuer, general-purpose identity-provider configuration, or a complete RFC 9068 implementation.

Every call rechecks a customer-held grant using issuer, subject, resource and Work ID. A valid signature or scope alone cannot authorize a read. Revoked/expired local grants and storage failures must deny access without returning Work data. Token identity is verified but per-token denylisting is not implemented. Ordinary read access tokens are reusable until token expiry or local grant revocation; this component does not claim one-time-token replay protection.

Tests generate ephemeral RSA keys and JWTs in memory and exercise the real cryptographic verifier. HTTP/stdio fixtures still use test grants/backend sources. The guarded PostgreSQL source additionally has fake ADO.NET command/projection checks; see [PERSISTENCE.md](PERSISTENCE.md) for its undeployed schema and snapshot/revocation limits. No token issuance flow, JWKS discovery/rotation, verified durable storage, production Web middleware, authenticated database read or live platform connection is covered yet.

## Private Streamable HTTP adapter

`http/` is a library-only ASP.NET Core composition adapter, with no executable, listener, default grant store or default Work source. A caller must supply a configured `ConnectorReadBoundary`. It does not register routes in Goblin.Web or change the existing stdio executable.

- `PrivateMcpHttpAdapter.AddServices` registers SDK Streamable HTTP in explicit stateless mode and the single status tool. Call `Map` before other terminal middleware when composing an isolated host.
- The configured resource must be an exact HTTPS URL ending in `/mcp`. Metadata at `/.well-known/oauth-protected-resource/mcp` advertises this fixed resource, the configured customer issuer and the read scope. Neither metadata nor challenges are derived from incoming Host/forwarded headers.
- Every MCP HTTP request requires one Authorization Bearer header. Missing/invalid credentials return a sanitized HTTP 401 and a metadata/scope challenge. Owner cookies and query tokens are not accepted.
- Host must match the configured resource authority; an Origin, when present, must match its origin. Disallowed Host/Origin returns sanitized HTTP 403. No broad CORS or forwarded-header trust is added.
- A verified, expiry-bounded identity is kept only in the current HTTP context. Work grants are checked again for each tool invocation. Ungranted Work is returned as a standard MCP tool error, not an HTTP 403, because authentication succeeded and the tool-specific decision occurs inside the protocol request.
- Stateless mode does not issue MCP session IDs or expose legacy `/mcp/sse`. There are no cross-request principal caches or owner-session fallback paths.

The focused executable also runs ASP.NET TestServer checks without listening on a network socket. These use the actual middleware and MCP HTTP transport, plus real JWT verification, but still use in-memory grants and a test Work source. They do not prove TLS/reverse-proxy behavior, durable authorization, live OAuth discovery/consent, real Goblin database reads or ChatGPT/tunnel connectivity. The existing formatting blocker is not retried.

## Local-owner approval/revocation policy

`CustomerGrantAdministration` is an unregistered policy requiring a trusted request-scoped local-owner authority and an atomic mutation/audit store. It pins issuer/resource, requires one explicit subject/Work ID and bounded future expiry, rechecks owner authority for every approval/revoke, and permits revoke without connector login. No production authority, store, route, grant writer or MCP mutation tool exists. See [GRANT-ADMINISTRATION.md](GRANT-ADMINISTRATION.md) for composition gates, uncertainty handling and the 24 test-double policy checks. These do not establish real owner authentication or durable audit coverage.

## Opt-in PostgreSQL fixture checks

The focused executable includes 19 offline fixture-safety contracts, eight embedded-schema/typed-Npgsql-parameter contracts and a separate gated live Work/grant query path. Defaults do not read database credentials. Live mode requires the two stdio arguments followed by `--postgres` plus both established, certificate-configured disposable test connections. See [POSTGRES-HARNESS.md](POSTGRES-HARNESS.md) before enabling it: random fixture ownership is checked, unconfirmed creation/changed identity refuses deletion, and cleanup never forces a DROP. Current sandbox live checks remain skipped. Fourteen staged live checks are not counted as passing; future minimal-fixture results would not establish production schema/audit/concurrency, real owner authentication or end-to-end platform coverage.

## Customer OAuth discovery review

`CustomerOAuthDiscoveryPreflight` is an offline, unregistered metadata review for a predefined public-client policy. It fetches no URLs, loads no keys and cannot authorize Work. Twenty-six synthetic checks cover pinned issuer, bounded/duplicate-safe typed JSON, S256/code/none/read-scope advertisements and restricted endpoint origins. See [KEYCLOAK-COMPATIBILITY.md](KEYCLOAK-COMPATIBILITY.md) for the concrete provider candidate, experimental resource-indicator/CIMD gates, token-header configuration, rotation gap and private OAuth reachability. A metadata pass is not live provider or OAuth success.
