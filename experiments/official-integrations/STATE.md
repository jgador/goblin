# Official integrations state

## Current objective and status

Develop one official ChatGPT and Slack integration for independent self-hosted Goblin installations without Goblin-operated centralized auth, customer-data storage or a data proxy. Preserve existing architecture and integrations. Prefer verified ChatGPT identity only with documented access; customer authorization remains mandatory.

Branch: `experiments/official-integrations`, created once from master `f9fb7522ee10fe5ec6fce9a57c553c25dde72a38`.

Iteration OI-001, 2026-10-13 UTC: a .NET 10 **local stdio prototype** and documented feasibility assessment. Not a published official plugin, OAuth server, live ChatGPT connection, Slack app release or verified tunnel installation.

## Next incremental task

Replace the prototype's broad owner-session bridge with a customer-enforced read-only connector authorization boundary. First inspect existing local identity/conversation permissions and choose a small adapter/application contract for a verified principal, resource binding and explicit Work read grants. Add focused rejection tests for incorrect audience/resource, missing scope, expired/revoked grants and unauthorized Work IDs. Do not invent ChatGPT identity or implement an ad hoc production OAuth server. Keep remote hosting/public distribution gated until platform eligibility is established.

Then evaluate a standards-based customer-owned OAuth provider with PKCE S256 and discovery, and a private MCP transport/tunnel configuration. Only test live platforms if noninteractive credentials and permissions are already provisioned. Further Slack implementation waits for an independent event-delivery proof.

## Completed changes and commit references

- OI-001: `feat(mcp): prototype permission-controlled local Work status reads`. The implementation and this state are in the same commit. Resolve its exact SHA with `git log -1 --format=%H -- experiments/official-integrations/STATE.md`; a commit cannot contain its own SHA. On the next iteration append the resolved previous SHA here.
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

## Publication transport

Git CLI push failed once with terminal prompts disabled because no HTTPS username/credentials are configured. Use the connected GitHub plugin's Git-data tools to create the tree/commit and update the experiment ref with force disabled and an expected head. Do not retry CLI authentication or request interactive login. The reviewed local snapshot was committed as 5372134ddafefa9e980aa50a735ef2113a661815; the remotely published Git-data commit has the same implementation plus this sanitized publication note and a different commit identity. Resolve the published SHA from the experiment branch before continuing.
