# Customer-owned Keycloak OAuth compatibility

Reviewed 2026-10-14 UTC. Keycloak is a **conditional self-hosted provider candidate**, not a deployed/tested Goblin integration, official directory listing, OAuth issuer implemented by Goblin, or Sign in with ChatGPT identity integration. This iteration performs no login, registration, Keycloak installation or platform traffic with customer data.

## Current official evidence and restrictions

| Area | Evidence | Goblin gate |
| --- | --- | --- |
| MCP resource binding | Current [Keycloak MCP guide](https://www.keycloak.org/securing-apps/mcp-authz-server) labels RFC 8707 resource indicators experimental and requires `--features=resource-indicators` to process resource values. | Do not assume a stock provider satisfies newer MCP authorization. Review a pinned release/feature policy and verify wrong or mismatched resources are rejected. |
| Registration | The same guide offers pre-registration, DCR and experimental CIMD. [OpenAI auth](https://developers.openai.com/plugins/build/auth) supports predefined clients as well as CIMD/DCR. | Start investigation with a separately customer-registered public client; no shared Goblin secret or anonymous broad registration. CIMD needs its own review. |
| Access token type | [Keycloak 26.2 release notes](https://www.keycloak.org/2025/04/keycloak-2620-released) introduced the opt-in `Use "at+jwt" as access token header type` setting; its documented default was off. | Explicitly configure/check this on the chosen release. The existing Goblin verifier rejects `JWT` identity/access-token headers; do not weaken it to accommodate defaults. |
| PKCE and callbacks | [Keycloak administration](https://www.keycloak.org/docs/latest/server_admin/index.html) distinguishes optional PKCE from required S256 and supports restricted public-client callbacks. OpenAI requires advertised S256. | Force S256 on the actual client, use authorization-code flow and register the exact callback shown by the connection UI. No wildcard callbacks or password/implicit flow shortcuts. |
| Signing keys | Keycloak administration describes active/passive rotation and its JWKS endpoint. | Fixed-key verification remains unchanged. [Isolated rotation prototype](SIGNING-KEY-ROTATION.md) now checks synthetic key sets; actual retrieval/TLS/provider rotation and emergency invalidation remain adoption blockers. Discovery preflight alone implements none of these. |
| Private reachability | [Secure MCP Tunnel OAuth guidance](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) says the authorization server is not automatically tunneled. | Separately verify browser/discovery/token/JWKS reachability and TLS trust. A private MCP tunnel is not proof that OAuth works. |

The Keycloak guide describes an audience-mapper/scope workaround when resource indicators are disabled. This can produce an audience claim, but must not be equated with full `resource` processing, mismatch rejection or newer-MCP conformance. No fallback is enabled here. Its experimental CIMD options include trusted client-metadata domains and a resource allowlist; never accept arbitrary metadata URLs/domains or broad resource audiences to satisfy distribution.

## Concrete customer-owned configuration to evaluate

All provider accounts, realm/client configuration, private signing keys and grant/audit authority belong to the customer's installation. No Goblin-operated auth service or proxy is introduced.

Use an exact realm issuer, for example `https://identity.customer.example/realms/goblin`, and canonical Goblin resource `https://goblin.customer.example/mcp`. Keycloak's realm discovery and `/protocol/openid-connect/{auth,token,certs}` endpoints are documented in its administration guide; fetch only from separately trusted configuration, never token-selected URLs.

For the initial predefined-public-client investigation, configure required S256, exact registered redirects, appropriate consent and the explicit `goblin.work.status.read` client scope. Disable unwanted direct-access, implicit and service-account flows for that client; global discovery advertising a capability does not prove client enforcement. Expose the intended scope in discovery and verify it is included in issued access tokens. Do not auto-grant local Work from provider roles, email or profile claims.

With resource indicators approved for the chosen release, the guide configures a resource-server client with `resource_url` and a requesting client audience mapper. Verify the exact resource in authorization **and** token requests, reject unknown/mismatched targets, and inspect verified access tokens for exact issuer/audience, RS256, `typ=at+jwt`, `exp`/`nbf`, nonempty `sub`/`jti` and exact read scope. OIDC signing-algorithm metadata alone is not evidence of the access-token algorithm/type. No realm import, guessed client attribute names, default passwords or live settings changes are shipped.

The customer owner must still approve each issuer/subject/resource/Work grant locally. Production schema, owner authority, transactional mutation/audit and 14 staged PostgreSQL checks remain unverified. Prefer verified ChatGPT identity only after its separate documented eligibility/registration is approved; Keycloak identity is not ChatGPT identity and an ID token is not a Goblin Work access token.

## Implemented offline preflight

`CustomerOAuthDiscoveryPreflight` reviews caller-supplied discovery JSON for one **pre-registered public-client** policy. Typed attributed POCOs, a 16 KiB UTF-8 bound, depth eight, duplicate-property rejection, exact issuer and advertised authorization-code/code/S256/none/read-scope requirements. Authorization/token/JWKS endpoints must be bounded HTTPS URLs on the configured issuer origin, without userinfo, queries, fragments, controls or backslashes. Errors are fixed and omit raw metadata/parser details.

This is conservative experiment policy, not a universal OAuth conformance validator: same-origin/query-free endpoints and explicit capability/scope advertisements can reject otherwise valid providers. It fetches nothing and resolves no DNS. A passing synthetic document does **not** prove trusted discovery transport, client registration/callbacks, actual resource indicators, PKCE enforcement, token format/claims, signing keys, local grants or reachability. It is unregistered and does not replace `ConnectorReadBoundary` or alter current Web/Slack/stdio behavior.

Twenty-six focused checks use synthetic JSON only, including exact issuer, missing/wrong capabilities, unsafe/foreign endpoints, duplicate/malformed/oversized data and sanitized failures. No real Keycloak export/token/discovery or live OAuth coverage is claimed.

## Distribution and unavoidable exchange

Customer-specific private ChatGPT connections remain the nearest documented path. A compatible provider does not establish one public plugin's eligibility to route to arbitrary customer-hosted URLs; the trusted-developer/template-domain restrictions in [FEASIBILITY.md](FEASIBILITY.md) remain unresolved. Slack routing/app-secret restrictions are unaffected.

Keycloak login/consent and OAuth handling necessarily exchange browser requests, codes and connector tokens with the selected client. OpenAI handles connector access/refresh tokens and requested/returned Work fields if connected; OIDC scopes can also disclose identity attributes. Password verifiers, provider signing keys and authoritative Work/grant history remain local. Never promise all token copies or selected data stay exclusively on-premises. Tunnel runtime credentials are separate from customer-user Work authorization.

The isolated [signing-key rotation prototype](SIGNING-KEY-ROTATION.md) uses established IdentityModel primitives, with explicit cache/deadline/cooldown policies. Its [pinned HTTPS document source](HTTPS-KEY-DOCUMENTS.md) now has mocked transport coverage, not live TLS/provider verification. Neither is wired into HTTP/stdio; the ten-minute key-removal window requires an emergency invalidation decision before adoption. Next small increment: trusted-owner emergency invalidation and in-flight generation/race checks. No token-selected `jku`/`x5u`, trust-on-first-use, unbounded retries or production listener. Live provider compatibility requires a provisioned customer test instance and separately authorized consent; do not attempt installation/login in this sandbox.
