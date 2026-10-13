# Official integrations state

## Current objective and status

Develop one official ChatGPT and Slack integration for independent self-hosted Goblin installations without Goblin-operated centralized auth, customer-data storage or a data proxy. Preserve existing architecture and integrations. Prefer verified ChatGPT identity only with documented access; customer authorization remains mandatory.

Branch: `experiments/official-integrations`, created once from master `f9fb7522ee10fe5ec6fce9a57c553c25dde72a38`.

Iteration OI-003, 2026-10-13 UTC: added a private, library-only Streamable HTTP/MCP adapter with protected-resource metadata, bearer checks and per-call local Work authorization. Tests exercise its actual ASP.NET/SDK pipeline in-process. The existing .NET 10 **local stdio prototype** remains separate and still uses an owner-session bridge. Not a published official plugin, OAuth server, live ChatGPT connection, Slack app release or verified tunnel installation.

## Next incremental task

Implement the next small customer-owned persistence/application increment: durable, explicitly approved connector Work read grants scoped to issuer/subject/resource, with revocation and expiry, plus a minimal Work status projection. Inspect current schema/mappings and existing identity policy first. Keep grant authorization and Work selection within the appropriate database boundary, instead of injecting an unguarded WorkStore owner read. Verify with targeted policy/contracts and opt-in live PostgreSQL tests only if a test database is available. Preserve Work lifecycle and existing Slack-specific identity records. Do not ship the in-memory test grants/source or enable a production MCP listener. Later compose the private HTTP adapter with this durable source and an established customer IdP.

Then evaluate a standards-based customer-owned OAuth provider with PKCE S256 and discovery, and a private MCP transport/tunnel configuration. Only test live platforms if noninteractive credentials and permissions are already provisioned. Further Slack implementation waits for an independent event-delivery proof.

## Completed changes and commit references

- OI-001: published commit `e0331f140f347da51a8ff0935e54786013678660`, `feat(mcp): prototype permission-controlled local Work status reads`.
- OI-002: published commit `b0beb0a0707caf184eca32b04aa065a2125a2e03`, `feat(mcp): verify connector tokens before local Work grants`.
- OI-003: `feat(mcp): add a private stateless HTTP adapter`. This state and increment share a commit; resolve its SHA from the experiment branch and record it next time.
- `FEASIBILITY.md`: official-docs assessment for template URLs, private tunnels, desktop plugins, customer OAuth, Sign in with ChatGPT, Slack OAuth/PKCE, Socket Mode, Marketplace and necessary platform data exchange.
- `mcp/`: official C# MCP SDK 1.4.1, pinned with a NuGet lockfile; .NET 10 SDK 10.0.100 used. One read-only `goblin_get_work_status` tool. Explicit positive Work ID grants, fixed HTTPS/loopback origin, no redirect following, five-second HTTP timeout, 128 KiB response limit, safe errors, no HTTP logging, status validation against Goblin.Core.Work.WorkStatus, bigint IDs/versions returned as strings.
- `tests/`: executable focused contract checks using mocked HTTP responses plus a real SDK stdio client/server subprocess. Synthetic random session created only in memory; child environment inheritance disabled. No passwords or real customer credentials used.
- Existing Work, PostgreSQL, Wolverine, Slack integration, public HTTP middleware and installation behavior unchanged. No production endpoint enabled.
- Hourly automation created and enabled, starting 2026-10-14 06:00 Asia/Manila. Carries the 20-minute budget, implementation stop near minute 18, state continuity and explicit `$commit-push` authorization.

## Verification ledger

| Check | Result |
| --- | --- |
| Main prototype serial build | Passed, zero warnings/errors; NuGet restore completed in approximately 53 seconds. |
| Focused test-project build | Initially failed: CS8410, StdioClientTransport is not IAsyncDisposable. One corrective change removed transport async-using; McpClient owns/disposes its session. Corrective build passed. |
| Final serial build after WorkStatus/required-field validation changes | Passed, zero warnings/errors. |
| Focused executable | 22 checks passed: granted status projection/exact bigint, pre-fetch unauthorized denial, invalid IDs, sanitized 401/403/500/redirect failures, wrong returned ID, malformed response, unknown/missing status, malformed/empty grant configuration, unsafe origins, real stdio initialization/discovery/denial and read-only metadata. |
| Backend data source | Mocked HTTP handler. No live authenticated Goblin/PostgreSQL integration claimed. |
| Formatting | Failed before analysis: cached SDK missing Microsoft.CodeAnalysis.Features 5.0.0.0, ReflectionTypeLoadException. No retry; check spacing manually and run formatter only when a complete SDK is available. |
| Full backend suite/core suite/contracts generation | Not run: isolated experimental adapter, no production/core/owning C#/Rust contract modifications. |
| Docker/Kubernetes/PostgreSQL installation | Not attempted; unnecessary for this increment. Availability is not established by this run. |
| Live ChatGPT, SIWC, tunnel, Slack, OAuth consent/Marketplace submission | Sandbox-blocked for this unattended run: no preprovisioned credentials/partner eligibility and interactive authentication is forbidden. Do not retry live login. |

Reproduction: follow README's targeted serial build and test commands. All subprocess commands used timeouts; the stdio contract uses a 15-second cancellation deadline and disposes the SDK client. Temporary build/log outputs stay under ignored `.artifacts/official-integrations/` and are not committed.

## Architectural decisions and unresolved restrictions

1. No central Goblin service. Authoritative Work history stays in the customer database. Data explicitly selected for ChatGPT/Slack is necessarily processed by those platforms and may be stored there; never claim zero off-premises copies.
2. Public ChatGPT template endpoints are documented but restricted to trusted developers. Need confirmation for Goblin eligibility, arbitrary customer domains, per-instance OAuth settings and review setup. No unrestricted distribution claim.
3. Secure MCP Tunnel supports customer-owned private connections and stdio, but not public plugin distribution. It requires customer Platform runtime credentials, roles and workspace association; OAuth authorization-server reachability remains a separate issue.
4. Desktop-only local MCP public submissions require approved partners and only work in local desktop threads.
5. Sign in with ChatGPT identity is partner-gated. Open-source ChatGPT-plan usage does not prove identity integration access or grant permission to Goblin Work. Keep connector tokens separate from OpenAI identity tokens.
6. Current stdio adapter uses an existing local owner session and a process-level Work allowlist. It is deliberately not suitable for a public remote server or independent user identities. Session expiry fails closed; no auto-login. Customer-issued resource-bound connector credentials are next.
7. Existing Slack customer apps remain the viable baseline. A shared Socket Mode app has app-level credentials, a connection limit and nondeterministic delivery across connections; do not share it across independent customer installations. Socket Mode is excluded from Marketplace. Conventional OAuth has app-secret/callback constraints. Current PKCE supports secretless exchange but desktop/localhost redirects cannot request bot scopes; PKCE does not solve independent event delivery.

## Do not retry without changed evidence

- Interactive platform authentication/consent, partner enrollment, Marketplace submissions or live tunnels without provisioned credentials and permissions.
- Cached-SDK formatter while Microsoft.CodeAnalysis.Features is missing.
- Do not mark Docker/Kubernetes/PostgreSQL as permanently unavailable from this run: they were not probed. If a later task needs them, probe once, record the actual error and use mocks/contracts when blocked.
- No isolated installation was started. For future installation checks, generate cryptographic temporary GOBLIN_LOCAL_PASSWORD only in runtime environment; use a private disposable GOBLIN_PASSWORD_HASH_FILE path, never print/commit credentials/verifiers, and clean up.

## Iteration budget and continuation

One small implementation per run. Read this state first. Reuse this branch, preserve remote commits and never force-push, merge to master or auto-create PRs. Maximum 20 minutes; stop implementing near 18 minutes, reserve remaining time for sanitized state and `$commit-push`. Bound every command and terminate child processes. Limit repeated failures to one corrective attempt.

## OI-002 results, 2026-10-13 UTC

- Inspected existing `ExternalConversationStore`: local identity confirmation scopes access to installation/workspace/user; authorization is rechecked before processing. MCP permission claims must never implicitly become local owner access.
- Added isolated `ConnectorReadBoundary` and `ICustomerWorkReadGrants`. Trusted customer issuer/public key and exact resource audience; RS256 signature, access-token type, expiry/not-before, exact scope and subject/token identity validation. Public key material is defensively copied. No token-issued URLs are fetched. Every call checks local grants before Work source access; grant-store failures are closed and sanitized.
- Added Microsoft.IdentityModel.JsonWebTokens 8.19.1 and updated the NuGet lockfile. No production/core/Slack lifecycle changes, new public routes, OAuth issuance, network discovery or environment variables.
- Current narrow JWT profile requires RS256/at+jwt plus nonempty sub/jti/scope and expiry. It is not claimed to implement all RFC 9068 requirements or all identity-provider token formats. Bearer status reads can repeat until expiry/revocation; no one-time replay guarantee.
- Baseline build passed. First increment build failed once with CS8602 on possibly-null scope; one corrective change added an explicit empty/null scope rejection. Corrective and final serial builds passed with zero warnings/errors.
- **43 focused checks passed:** previous 22 plus 21 connector checks. New cases cover valid signed/granted read, wrong issuer/audience/trailing slash, wrong signature/algorithm, unsigned/missing-expiry/identity/future/expired tokens, missing exact scope/sub/jti, malformed/oversized token, unauthorized Work/unlinked actor, local grant revocation/expiry and unavailable grant storage. Denied reads never invoke the test Work source. Real RSA/JWT verifier and SDK stdio transport; in-memory grants and mocked backend/source only.
- Test commands use 40-second outer timeout; stdio client uses a 15-second deadline and disposal. Ephemeral signing keys/tokens live only in memory; no isolated installation, password, customer data or live authentication was used.
- Formatter skipped because the previously recorded cached-SDK Roslyn assembly blocker is unchanged. Manual spacing review and whitespace checks apply. No full backend/core/contracts-generation suite needed for unchanged production/core/contracts.
- Live customer IdP/JWKS rotation, durable grant revocation, authenticated database reads, HTTP MCP hosting and ChatGPT/Slack OAuth/tunnel/Marketplace remain unverified. Missing noninteractive credentials and partner eligibility remain recorded blockers; no login retry.
- Publish through connected GitHub Git-data tools with the observed branch head as the non-forced expected parent. Do not retry known Git CLI credential failure. Continuing branch baseline is `e0331f140f347da51a8ff0935e54786013678660`.

## OI-003 results, 2026-10-13 UTC

- Added `http/`, a library-only private ASP.NET Core/MCP composition adapter. No executable/listener, production routes, default grants or Work source are introduced. Existing stdio cookie bridge and Slack remain unchanged.
- Split cryptographic authentication from per-Work authorization so the HTTP middleware stores a verified identity only in the current request context. Identity includes expiry; the read boundary rejects expired or resource/issuer-mismatched identities and still checks local grants for every invocation. `WorkReadFailure` became an extensible experiment exception so authentication failures can be identified without parsing error messages.
- Fixed resource metadata at `/.well-known/oauth-protected-resource/mcp`; all MCP requests require a single Bearer header. Missing/invalid tokens return sanitized 401 with a fixed metadata/scope challenge. Host/Origin denial returns sanitized 403. Local Work denial is a protocol tool error, not HTTP 403. Cookies, query credentials and multiple Authorization fields cannot acquire connector access.
- Explicit SDK stateless transport avoids cross-request identity/session caches and has no legacy SSE endpoint. Added ModelContextProtocol.AspNetCore 1.4.1 and Microsoft.AspNetCore.TestHost 10.0.0; HTTP adapter dependency lockfile is committed.
- Baseline serial build passed. First adapter build failed with MCP9004 because explicitly assigning the obsolete `EnableLegacySse` option is treated as an error. One corrective change removed that assignment; explicit stateless mode disables the endpoint, confirmed by a 404 check. Corrective and final serial builds passed with zero warnings/errors.
- **60 focused checks passed:** prior 43 plus 17 new HTTP/MCP checks using actual ASP.NET TestServer and SDK Streamable HTTP, ephemeral RSA JWTs, test local grants and Work source. Covered fixed discovery, challenges, malformed/wrong-audience/expired credentials, Host/Origin rejection, initialization without a session ID, one read-only tool, granted status read, ungranted Work denial, next-request revocation, anonymous isolation, absent legacy SSE, cookie/query rejection and multiple Authorization fields. No real network listener or live customer database/platform coverage.
- Commands used 60/90/100-second build limits and a 50-second test-process limit. HTTP test fixture has a 20-second deadline and five-second client timeout; it stops/disposes TestServer. Existing stdio test child uses its 15-second deadline/disposal. No isolated installation, owner password, customer data or interactive authentication was used.
- Formatter skipped: unchanged cached SDK Roslyn assembly blocker. Manual formatting/spacing review and `git diff --check` apply. No full backend/core/contracts-generation suite because owning production code/contracts are unchanged.
- Live IdP discovery/client registration/consent, JWKS rotation, durable grants, authenticated PostgreSQL projection, HTTPS ingress, Secure MCP Tunnel and official platform distribution remain unverified. Do not retry recorded live-auth or formatter blockers. Add durable customer approval/revocation and guarded Work projection before enabling any production listener.
- Continued from published commit `b0beb0a0707caf184eca32b04aa065a2125a2e03`; publication uses connected Git-data tools with force disabled and the observed expected head, not Git CLI login.

## Publication transport

Git CLI push failed once with terminal prompts disabled because no HTTPS username/credentials are configured. Use the connected GitHub plugin's Git-data tools to create the tree/commit and update the experiment ref with force disabled and an expected head. Do not retry CLI authentication or request interactive login. The reviewed local snapshot was committed as 5372134ddafefa9e980aa50a735ef2113a661815; the remotely published Git-data commit has the same implementation plus this sanitized publication note and a different commit identity. Resolve the published SHA from the experiment branch before continuing.
