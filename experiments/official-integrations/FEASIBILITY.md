# Official integrations feasibility

Reviewed 2026-10-13 UTC. This is a prototype and documentation assessment, not a published integration or a successful live customer installation.

## ChatGPT

| Route | Evidence and restriction | Decision |
| --- | --- | --- |
| One public remote plugin with per-customer URLs | [Remote MCP review](https://developers.openai.com/plugins/deploy/app-review) supports template URLs for workspace-specific endpoints, limited to trusted developers with an established OpenAI relationship. A real public HTTPS review endpoint is required. | Conditionally documented, not proven available to Goblin. Confirm eligibility and whether arbitrary customer-owned domains fit the accepted template grammar. No universal Goblin proxy. |
| Secure MCP Tunnel | [Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) supports private custom MCP connections through a customer-run stdio or HTTP client. It explicitly excludes public plugin submission/distribution. Platform roles, runtime API key and workspace association are required. | Closest private cloud connection. Each customer owns its tunnel and configuration. Live tests need preprovisioned credentials and permissions, unavailable in this run. |
| Desktop-only public plugin | [Desktop submission](https://developers.openai.com/plugins/deploy/desktop-only) supports local stdio MCP servers, only for approved/allowlisted partners; installation and use are local desktop only. | A decentralized public route conditional on approval, not a general web/mobile solution. |
| Direct custom MCP connection | [Connection guide](https://developers.openai.com/plugins/deploy/connect-chatgpt) supports customer HTTPS URLs or a selected tunnel. Workspace policies apply. | Customer-specific connection using the same official Goblin server implementation; not one public directory installation. |
| Customer-owned OAuth | [Authentication](https://developers.openai.com/plugins/build/auth) requires protected-resource and authorization-server metadata, resource-bound tokens, PKCE S256, validated audience/scopes and supported client registration. | Planned customer-owned boundary. Do not pass ChatGPT/OpenAI tokens through as Goblin authorization or equate tunnel credentials with user grants. Private authorization-server reachability must be checked separately. |
| Sign in with ChatGPT | [Quickstart](https://developers.openai.com/siwc/quickstart) distinguishes partner identity sign-in from open-source ChatGPT plan usage. Identity sign-in is a limited commercial-partner trial. Goblin still owns local accounts, sessions and permissions. | Prefer verified ChatGPT identity if access is approved. Open-source plan usage does not establish identity-sign-in eligibility for arbitrary on-premises deployments. Never fabricate a client ID, callback support or identity claims. |

**Conclusion:** current documentation provides conditional template and desktop distribution paths. Unrestricted public distribution to arbitrary independent on-premises instances is not established for Goblin. Private custom MCP connections are the closest documented route to prototype now. The experiment uses the official C# SDK over stdio, usable by a local client and potentially by a customer-owned tunnel client; tunnel connectivity itself has not been tested.

## Existing Goblin boundaries and first increment

`Goblin.Web/Http/Endpoints/WorkEndpoints.cs` exposes Work reads backed by `Goblin.Application.Work.WorkStore`. `WorkspaceMiddleware` checks the configured Host, validates Origin on POST, and requires a local session for private APIs. Owner sessions expire after 12 hours. `OwnerPassword` reads a private PBKDF2 verifier. Native setup supports `GOBLIN_LOCAL_PASSWORD`; it is not a Web-server login API.

The isolated `mcp/` prototype reuses the existing authenticated GET Work API. It does not bypass middleware, add public HTTP endpoints, modify the Work lifecycle, launch execution, or change PostgreSQL/Wolverine behavior. A local process owner supplies an already valid Goblin session and explicit Work read grants. Unauthorized IDs are denied before checking existence. Only ID, version, status and update time leave the adapter. No list/history/command tools are exposed.

This is a local service-session prototype, **not per-user OAuth authorization**. Its session represents the local owner and is broader than the adapter grants. Restrict the process and environment to the customer trust boundary. Do not use this cookie bridge as a published remote connector, share one session across customer identities, or send the session in model prompts/plugin files. Next replace it with customer-enforced resource-bound connector tokens and permissions. An expired session fails closed; there is no auto-login or password prompt.

## Slack

Existing `Goblin.Integrations.Slack` uses a customer app manifest, app-level and bot tokens, Socket Mode, and encrypted credential files inside the installation. Identity linking requires local confirmation through the existing conversation adapter. Preserve this path.

| Question | Verified restriction or unresolved issue |
| --- | --- |
| Marketplace and Socket Mode | [Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/) excludes public Marketplace listing. [Transport comparison](https://docs.slack.dev/apis/events-api/comparing-http-socket-mode/) permits distributed Socket Mode apps outside Marketplace. Distribution does not establish independent customer event routing. |
| Shared official Socket Mode app | The same Socket Mode documentation allows up to 10 connections and says a payload may reach any connection. App-level credentials cannot safely be copied to independent customers expecting workspace-exclusive delivery. Current Goblin filters messages but must not receive another customer's events at all. |
| HTTP event delivery | A shared app's configured event Request URL is an app setting, not a customer-specific URL selected by the OAuth install. A universal event receiver would introduce the forbidden Goblin data proxy. Do not implement it. |
| Distributed OAuth | [OAuth install](https://docs.slack.dev/authentication/installing-with-oauth/) requires redirect URI registration and bot scopes for the existing feature set. Conventional confidential-client exchange requires the app secret. Do not distribute that secret. Arbitrary customer redirects are not automatically trusted or registered. |
| Public-client PKCE | [PKCE](https://docs.slack.dev/authentication/using-pkce/) now supports secretless exchanges. Enabling it is a one-way app change. Desktop/localhost redirects cannot request bot scopes. PKCE alone does not resolve event delivery, app-level Socket Mode tokens or arbitrary customer callback registration. Evaluate HTTPS customer callbacks and token rotation before ruling out narrower flows. |

**Conclusion:** one Marketplace bot using current mentions/DM behavior, with fully independent customer event delivery and no central data plane, is not proven feasible. Retain customer-owned Slack apps as the closest decentralized implementation. Do not enable PKCE, change official app configuration or perform Slack authentication during unattended development.

## Necessary platform data exchange

- Goblin remains the authoritative on-premises store; no Goblin-operated cloud service is introduced.
- ChatGPT/OpenAI sees tool definitions, requested Work IDs and returned status fields; those may become part of ChatGPT conversation history. The tunnel also relays requests/results through OpenAI and has its own credential/control-plane requirements. On-premises storage does not imply zero platform processing or storage.
- OAuth clients/platforms necessarily handle authorization codes and connector access tokens. Do not promise all credential copies remain exclusively on-premises. Local verifier passwords, Slack tokens and the prototype Goblin session are never tool outputs.
- Slack holds Slack messages, identity/workspace/channel metadata, replies and any published result excerpts. Existing integration delivers that content directly between the customer installation and Slack.
- Model/runtime provider calls during Work execution remain governed by existing Goblin configuration, separately from these adapters.

No live platform account was authenticated, no customer data used, and no provider API inference requested in this iteration.

## OI-002 authorization evidence

On 2026-10-13 UTC, the [OpenAI token-verification requirements](https://developers.openai.com/plugins/build/auth) were rechecked: resource servers verify signatures, issuer, audience, token validity and scopes, then apply local policy. Microsoft documents that [ValidateTokenAsync](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler.validatetokenasync?view=msal-web-dotnet-latest) returns an `IsValid` result that must be checked; [ValidAlgorithms](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.tokens.tokenvalidationparameters.validalgorithms?view=msal-web-dotnet-latest) restricts accepted algorithms. The [JWT access-token profile](https://www.rfc-editor.org/rfc/rfc9068.html) distinguishes access tokens from identity tokens with the `at+jwt` type.

`ConnectorReadBoundary` now prototypes those cryptographic checks plus a customer-grant contract. It remains disconnected from production Web routes and durable grants. Existing Slack identity policy was inspected: local confirmation enables an identity scoped to installation/workspace/user, and authorization is rechecked before processing. MCP grants must preserve that local-authority separation without reusing Slack-specific identity records or auto-granting owner access from email/profile claims.

Tests establish only the boundary's behavior with real ephemeral RSA-signed JWTs, in-memory grants and a test Work source. Public distribution feasibility, partner eligibility and live OAuth/tunnel/Slack status are unchanged.

## OI-003 private HTTP adapter

On 2026-10-13 UTC, [OpenAI resource metadata and authentication challenges](https://developers.openai.com/plugins/build/auth) were rechecked. The isolated adapter advertises a configured resource/authorization server and includes a fixed metadata URL in 401 responses. It does not implement the authorization server, client registration or consent flow.

The pinned C# SDK's [HTTP context guidance](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/httpcontext/httpcontext.html) identifies stale-context risks with legacy SSE and guarantees fresh request context in stateless mode. Its [transport guidance](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/transports/transports.html) documents explicit stateless mode and restrictive Host/Origin handling. The experiment therefore uses stateless Streamable HTTP, checks bearer authentication on each request, and verifies local grants inside each status call. No public plugin eligibility or live platform capability changed.

## OI-004 guarded persistence boundary

On 2026-10-14 UTC, the [OpenAI authentication requirements](https://developers.openai.com/plugins/build/auth) and [Npgsql data-source/parameter documentation](https://www.npgsql.org/doc/basic-usage.html) were rechecked. Token verification does not replace application authorization. The new source contract therefore requires local grant authorization and minimal Work projection together; see [PERSISTENCE.md](PERSISTENCE.md) for the single-query prototype, undeployed schema and revocation limits. PostgreSQL credentials/tools are absent, so persistence is not verified and no production migration or listener is enabled. This does not resolve or establish public distribution eligibility, ChatGPT identity access or independent Slack event routing.

## OI-008 customer-owned OAuth candidate

Reviewed 2026-10-14 UTC: [Keycloak's official MCP guide](https://www.keycloak.org/securing-apps/mcp-authz-server) identifies experimental resource-indicator/CIMD support, while OpenAI documents predefined public OAuth clients as an alternative registration path. [KEYCLOAK-COMPATIBILITY.md](KEYCLOAK-COMPATIBILITY.md) records the conditional candidate, strict token-profile/rotation gates and separately reachable authorization server. The new offline metadata preflight does not establish live provider operation, public distribution or ChatGPT identity eligibility. No provider deployment, login, central service or Slack change.
