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

Tests generate ephemeral RSA keys and JWTs in memory and exercise the real cryptographic verifier. HTTP/stdio fixtures still use test grants/backend sources. The guarded PostgreSQL source additionally has fake ADO.NET command/projection checks; see [PERSISTENCE.md](PERSISTENCE.md) for its undeployed schema and snapshot/revocation limits. Isolated signing-key rotation now has synthetic document/cache checks, not live retrieval/provider coverage. No token issuance flow, live JWKS discovery/rotation, verified durable storage, production Web middleware, authenticated database read or live platform connection is covered yet.

## Private Streamable HTTP adapter

`http/` is a library-only ASP.NET Core composition adapter, with no executable, listener, default grant store or default Work source. A caller must supply a configured `ConnectorReadBoundary` or `RotatingConnectorReadBoundary` to both `AddServices` and `Map`, using the same boundary instance. Choose one path per isolated host. It does not register routes in Goblin.Web or change the existing stdio executable.

- `PrivateMcpHttpAdapter.AddServices` registers SDK Streamable HTTP in explicit stateless mode and the single status tool. Call `Map` before other terminal middleware when composing an isolated host.
- Registration owns exactly one boundary per host. Duplicate/mixed or pre-registered boundary families are rejected before registry changes. `Map` requires that exact instance and rejects late duplicate/override/replacement/mixed registrations and repeated mapping before adding middleware/routes. Construct a separate service collection per host; do not share the registration marker. See [HTTP-COMPOSITION.md](HTTP-COMPOSITION.md) for startup failure and trusted-host limits.
- The configured resource must be an exact HTTPS URL ending in `/mcp`. Metadata at `/.well-known/oauth-protected-resource/mcp` advertises this fixed resource, the configured customer issuer and the read scope. Neither metadata nor challenges are derived from incoming Host/forwarded headers.
- Every MCP HTTP request requires one Authorization Bearer header. Missing/invalid credentials return a sanitized HTTP 401 and a metadata/scope challenge. Owner cookies and query tokens are not accepted.
- Host must match the configured resource authority; an Origin, when present, must match its origin. Disallowed Host/Origin returns sanitized HTTP 403. No broad CORS or forwarded-header trust is added.
- A verified, expiry-bounded identity is kept only in the current HTTP context. Work grants are checked again for each tool invocation. Ungranted Work is returned as a standard MCP tool error, not an HTTP 403, because authentication succeeded and the tool-specific decision occurs inside the protocol request.
- The rotating overload retains `RotatingConnectorIdentity`, not raw claims or a bearer token. Its tool rechecks exact boundary ownership and the current cache lease before local authorization. Invalidation after middleware authentication but before tool access returns a sanitized MCP tool error without source access; a later request can freshly verify still-published keys. This is not atomic revocation of an already admitted/in-flight read.
- Stateless mode does not issue MCP session IDs or expose legacy `/mcp/sse`. There are no cross-request principal caches or owner-session fallback paths.
- Both HTTP tools now declare the exact read scope in the official `_meta.securitySchemes` compatibility mirror. SDK 1.4.1 cannot represent the primary top-level declaration through its inspected ordinary descriptor API, so OAuth UI compatibility is **not proven**. Bearer-required transport and local permission denial are unchanged. See [TOOL-OAUTH-METADATA.md](TOOL-OAUTH-METADATA.md); no runtime OAuth challenge or stdio OAuth claim is added.

The focused executable also runs ASP.NET TestServer checks without listening on a network socket. These use the actual middleware and MCP HTTP transport, plus real JWT verification, but still use in-memory grants and a test Work source. They do not prove TLS/reverse-proxy behavior, durable authorization, live OAuth discovery/consent, real Goblin database reads or ChatGPT/tunnel connectivity. The existing formatting blocker is not retried.

Actual pinned SDK HTTP-client checks now complement the hand-built JSON-RPC requests: fixed/rotating initialization, tool/scope discovery, safe status projection, fresh local authorization/changed bearer subject, cancellation and rotating lease invalidation through TestServer. OAuth and stream reconnection are disabled and the test handler pins one target; no live platform connection is made. See [SDK-HTTP-CLIENT.md](SDK-HTTP-CLIENT.md) for evidence and limits.

The same fixture now checks missing/malformed/expired bearer failures during initialization and later tool calls: exact safe HTTP 401/challenge, one rejected request, no Work access, no automatic fallback/login/retry and explicit later credential restoration. SDK HTTP exceptions remain distinct from authenticated local-permission MCP errors; no production authentication policy changes.

One additional rotating SDK/TestServer fixture now composes the actual pinned HTTPS key source with a friend-only mocked key transport. Six checks verify a credential-free pinned GET during cold initialization, cached safe reads and local revocation/restoration, response disposal and source-owned handler cleanup. This is not live TLS/provider/OAuth or ChatGPT/Slack evidence; see [SDK-HTTP-CLIENT.md](SDK-HTTP-CLIENT.md).

Six further checks cover cold initialization with mocked key redirects, HTTP 503 and malformed documents: one sanitized MCP 401, no Work access, no source detail/bearer leakage or follow/retry, and failed-response/owned-handler disposal. Authentication failure must have the specific Unauthorized HTTP classification, not a timeout or arbitrary exception counted as a pass.

Three additional checks deliberately create a second SDK transport on each failed host during a deterministically frozen cache cooldown. Each new request stays safely denied without another key GET or Work access. This is an explicit synthetic test action, not automatic reconnect or Work retry; real provider/platform behavior and recovery after cooldown remain unverified.

Controlled synthetic recovery now advances only the fixture's monotonic cache clock by 61 seconds, publishes valid ephemeral public keys and explicitly creates a new SDK client on each failed host. Nine new checks require one additional credential-free pinned GET, initialization without Work access, local permission denial and a separate safe cached read after explicit permission restoration. No sleep, automatic recovery/retry or production change; live provider/platform recovery remains unverified.

## Local-owner approval/revocation policy

`CustomerGrantAdministration` is an unregistered policy requiring a trusted request-scoped local-owner authority and an atomic mutation/audit store. It pins issuer/resource, requires one explicit subject/Work ID and bounded future expiry, rechecks owner authority for every approval/revoke, and permits revoke without connector login. No production authority, store, route, grant writer or MCP mutation tool exists. See [GRANT-ADMINISTRATION.md](GRANT-ADMINISTRATION.md) for composition gates, uncertainty handling and the 24 test-double policy checks. These do not establish real owner authentication or durable audit coverage.

## Opt-in PostgreSQL fixture checks

The focused executable includes 19 offline fixture-safety contracts, eight embedded-schema/typed-Npgsql-parameter contracts and a separate gated live Work/grant query path. Defaults do not read database credentials. Live mode requires the two stdio arguments followed by `--postgres` plus both established, certificate-configured disposable test connections. See [POSTGRES-HARNESS.md](POSTGRES-HARNESS.md) before enabling it: random fixture ownership is checked, unconfirmed creation/changed identity refuses deletion, and cleanup never forces a DROP. Current sandbox live checks remain skipped. Fourteen staged live checks are not counted as passing; future minimal-fixture results would not establish production schema/audit/concurrency, real owner authentication or end-to-end platform coverage.

## Customer OAuth discovery review

[PLATFORM-REVIEW.md](PLATFORM-REVIEW.md) records the 2026-10-14 SDK 2.2.0 source/distribution review and explicit decision to retain 1.4.1. Whole-server discovery is distinct from unproven tool-level linking UI. Public templates, desktop distribution and ChatGPT identity remain restricted; customer-specific private connections are the closest documented route. No production listener or platform authentication was enabled.

`CustomerOAuthDiscoveryPreflight` is an offline, unregistered metadata review for a predefined public-client policy. It fetches no URLs, loads no keys and cannot authorize Work. Twenty-six synthetic checks cover pinned issuer, bounded/duplicate-safe typed JSON, S256/code/none/read-scope advertisements and restricted endpoint origins. See [KEYCLOAK-COMPATIBILITY.md](KEYCLOAK-COMPATIBILITY.md) for the concrete provider candidate, experimental resource-indicator/CIMD gates, token-header configuration, rotation gap and private OAuth reachability. A metadata pass is not live provider or OAuth success.

Seventeen additional synthetic checks cover strict callback-issuer capability parsing. The summary's `AdvertisesAuthorizationResponseIssuerIdentification` reports only caller-supplied metadata, defaults conservatively to false and cannot establish actual callback behavior, select a production redirect URI or authorize Work. Null/non-Boolean/duplicate advertisements fail sanitized; existing provider/security policy remains enforced.

## Isolated signing-key rotation

`CustomerSigningKeyCache` and `RotatingConnectorReadBoundary` compose trusted-endpoint key retrieval with existing RSA/JWT verification and per-Work authorization. Monotonic ten-minute cache age, one-minute global refresh cooldown, atomic bounded key-set replacement and five-second gate/retrieval deadlines are experimental policy. Optional isolated HTTP composition is now available; no stdio or production registration. See [SIGNING-KEY-ROTATION.md](SIGNING-KEY-ROTATION.md) for limits and removal/emergency-invalidation caveats.

`CustomerHttpsJwksDocumentSource` is an unregistered pinned HTTPS implementation with a dedicated credential-free client, redirect refusal, streaming byte/deadline limits and disposal/cancellation controls. Tests replace only its internal transport with mocks; actual TLS/provider traffic remains unverified. See [HTTPS-KEY-DOCUMENTS.md](HTTPS-KEY-DOCUMENTS.md) for policy and restrictions.

Six additional direct source/cache/verifier checks cancel a caller during a mocked key-body read, independently observe owned-resource disposal and prove later cache admission after an explicit test-clock advance. Local permission still controls each separate Work read. No production behavior, SDK HTTP mapping or platform cancellation behavior is changed or claimed; see [HTTPS-KEY-DOCUMENTS.md](HTTPS-KEY-DOCUMENTS.md).

Five further checks cancel only a queued authentication while the original key-body retrieval remains paused and intact. Explicit body release completes the first authentication with one total key GET; later cached authentication and local permission denial/restoration remain independent. Signal-driven mocked behavior only, with bounded cleanup and no retries or production changes.

`CustomerSigningKeyAdministration` requires local-owner authority for each atomic cache flush. A generation check prevents pre-flush refreshes from republishing keys; no production owner authority or public mutation exists. Cache clearing is not a provider key denylist or retroactive read revocation. See [KEY-CACHE-INVALIDATION.md](KEY-CACHE-INVALIDATION.md) for tests and limits.

The isolated rotating boundary now retains a cache/boundary-bound verified identity, checking generation and cache age before/after signature validation and before each new per-Work read. Successful replacement retires previous leases; admitted/in-flight reads remain an explicit limit. Fixed-key HTTP behavior and stdio composition are preserved. See [VERIFICATION-LEASES.md](VERIFICATION-LEASES.md) and [ROTATING-HTTP.md](ROTATING-HTTP.md) for verification and adoption gates.
