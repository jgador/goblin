# Supported API and distribution review

Reviewed 2026-10-14 UTC. Documentation/source review only: no SDK upgrade, platform enrollment, OAuth exchange or live connection. The previous 340 synthetic/local checks were not rerun for this prose-only increment.

## SDK decision

The official [latest-release endpoint](https://api.github.com/repos/modelcontextprotocol/csharp-sdk/releases/latest) returned [2.2.0](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0), published 2026-08-13. The inspected tag points to commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`. Its release describes hybrid HTTP session serving and a header-decoding fix, not an OAuth descriptor extension.

Inspected tagged [Tool](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/src/ModelContextProtocol.Core/Protocol/Tool.cs), [McpMetaAttribute](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/src/ModelContextProtocol.Core/Server/McpMetaAttribute.cs) and [McpServerToolCreateOptions](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/src/ModelContextProtocol.Core/Server/McpServerToolCreateOptions.cs): Tool remains sealed, with `_meta` but no primary `securitySchemes` member or JSON extension-data member. Attribute and create-options metadata populate Tool.Meta, not a new top-level field. This finding concerns these ordinary descriptor APIs, not a proof that every possible SDK extension is incapable of it.

**Decision:** keep the reviewed 1.4.1 packages/lockfiles. Do not upgrade just to solve this gap: the inspected newer APIs do not solve it. No 2.2.0 package restore, compile or descriptor probe was attempted, and its other protocol/transport behavior is not validated here. A future supported primary-field path needs its own compatibility decision and bounded probe. No serializer fork, response rewriting or undocumented declaration.

## Authentication is not one gate

| Layer | Official evidence | Experiment consequence |
| --- | --- | --- |
| Whole-server authentication | [Authentication](https://developers.openai.com/plugins/build/auth) describes HTTP 401 with protected-resource discovery, customer authorization-server metadata, resource binding and PKCE S256. | Keep bearer-required initialization/discovery and per-request verification; do not allow anonymous Work calls to expose linking UI. |
| Tool-level linking UI | The same guide requires per-tool declarations and runtime authentication challenge metadata. | The compatibility mirror alone does not prove this UI. Local permission/lease denial must not become a reconsent prompt. |
| Provider callbacks | The guide distinguishes exact management-page callback URLs; stable callbacks depend on advertised and actually returned issuer identification. | Offline metadata cannot prove callback behavior. Current preflight does not review this advertisement; extend its advisory summary next, without generating callback allowlists. |

The descriptor gap is not evidence that all whole-server OAuth is impossible. Conversely, HTTP challenge contracts and SDK clients with OAuth disabled are not a ChatGPT linking test. Complete provider, callback, consent, TLS/reachability and platform verification remain required. Customer-local authorization is mandatory even after authentication succeeds.

## One public integration versus independent installations

| Route | Current restriction | Decision |
| --- | --- | --- |
| Public template endpoint | [Remote review](https://developers.openai.com/plugins/deploy/app-review) limits templates to trusted developers with an established relationship; a real reachable review URL must match the template. The example changes a subdomain under one domain. | Goblin eligibility, arbitrary customer-owned domain patterns and independent OAuth configuration remain unresolved. The example does not prove arbitrary origins are accepted. No central proxy. |
| Private custom connection | [Custom MCP](https://developers.openai.com/api/docs/guides/custom-mcp-server) accepts a customer URL or selected tunnel, subject to workspace controls. | Closest documented decentralized path: one maintained Goblin implementation, separately configured by each customer. Not one public directory installation. |
| Private tunnel | [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) uses customer-run outbound forwarding but excludes public plugin distribution. | Customer owns credentials/configuration and local endpoint. OpenAI relays MCP traffic; authorization-server reachability remains separate. No live eligibility or tunnel claim. |
| Local desktop distribution | [Desktop submissions](https://developers.openai.com/plugins/deploy/desktop-only) require approved/allowlisted partners and local desktop threads. | Conditional future route, not a web/mobile solution or current approval. |
| ChatGPT identity | [SIWC quickstart](https://developers.openai.com/siwc/quickstart) separates selected commercial identity partners from open-source plan usage. | No identity integration assumed. OpenAI inference credentials are not Goblin Work grants. |

## Slack recheck

[PKCE](https://docs.slack.dev/authentication/using-pkce/) permits public-client exchange, but enabling it is one-way without Slack support. Custom-scheme/localhost desktop redirects cannot request bot scopes. Custom schemes receive rotating tokens; PKCE refresh tokens expire after 30 days. HTTPS callbacks still need review against registered redirects and the chosen rotation profile. [Conventional OAuth](https://docs.slack.dev/authentication/installing-with-oauth/) requires its confidential-client secret; it must not be shipped to customer installations.

[Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/) is excluded from Marketplace and distributes payloads to any of up to ten app connections, not a guaranteed workspace-specific connection. [HTTP event delivery](https://docs.slack.dev/apis/events-api/using-http-request-urls/) configures the app's Request URL, not an independent receiver selected by each install. PKCE does not change that delivery model. Existing Goblin app/bot tokens, credential storage and local identity confirmation remain unchanged; retain customer-owned apps. No official secret, app-level token or cross-customer events are shared.

## Data and adoption limits

No Goblin-operated central infrastructure. Customer configuration, grants and authoritative Work history stay local. OpenAI necessarily receives descriptors, OAuth code/access-token copies and selected status requests/results; a tunnel relays those through OpenAI. Slack processes its own messages/events, identities and selected replies. Those platform copies are not exclusively on-premises. No local password, owner session or Slack token is a tool result or plugin artifact.

No real owner/audit/durable SQL/provider/platform test is established by this review. PostgreSQL's 14 staged checks remain unrun. Preserve the admitted-read revocation limitation and all production adoption gates; do not enable a listener or auto-grant/retry Work.
