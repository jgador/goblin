# Isolated rotating-key HTTP composition

Reviewed 2026-10-14 UTC. This increment connects the existing rotating verification boundary to the library-only private HTTP adapter, not Goblin.Web or a public platform integration.

Pass the same customer-configured `RotatingConnectorReadBoundary` instance to `PrivateMcpHttpAdapter.AddServices` and `Map`. This overload registers only `RotatingPrivateWorkTools`; the fixed-key overload continues registering only `PrivateWorkTools`. Choose one per isolated host. There is no executable, listener, default source/grant, owner authority, administration route or platform registration.

The [composition guard](HTTP-COMPOSITION.md) now enforces that exact registration/mapping instance and refuses duplicate/mixed boundary families, late DI overrides and repeated mapping before new middleware/routes. Independent hosts require independent service collections/markers. This is trusted startup validation, not authentication or permission issuance.

Both overloads share one middleware implementation: fixed resource metadata/challenge, configured Host and optional exact Origin checks, one bearer header, no cookie/query-token fallback and explicit stateless SDK transport. Authentication stores only the sealed verified identity in the current request context. The rotating tool requires `RotatingConnectorIdentity` and calls its original boundary's identity read overload, preserving exact boundary ownership, cache generation/age, token expiry/resource and fresh customer-local per-Work authorization. It never reconstructs identity from claims or downgrades to the fixed verifier.

Invalidation between middleware authentication and tool access produces the normal sanitized MCP tool error before Work source access. It does not trigger a retry, key refresh or owner approval inside the tool. A later HTTP request authenticates independently and can trust a key still published by the customer provider. Remove/disable compromised provider keys before flushing. The final lease admission check remains non-atomic with source/SQL authorization: an already admitted/in-flight read may finish after invalidation. See [VERIFICATION-LEASES.md](VERIFICATION-LEASES.md).

## Official documentation checked

- [OpenAI authentication](https://developers.openai.com/plugins/build/auth): bearer access tokens require resource-server validation and authorization; transport client identity is not end-user identity. This adapter implements neither OpenAI mTLS nor Sign in with ChatGPT eligibility.
- [Official C# SDK v1 HTTP context](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/httpcontext/httpcontext.html): `IHttpContextAccessor` provides current Streamable HTTP context; stateless mode creates a fresh context per request. The pinned 1.4.1 API is compiled and tested, not upgraded to newer SDK defaults. A direct source-tag web lookup returned an internal retrieval error; no tagged-source inspection is claimed.

## Verification limits and data exchange

Tests use actual ASP.NET TestServer, MCP server SDK dispatch and ephemeral real RSA/JWT verification. Deterministic test middleware invalidates the cache or revokes a synthetic grant after authentication and before SDK dispatch. Checks cover metadata/challenge, Host/Origin, malformed/signature/audience/expiry failures, initialization/discovery, repeated grants, pre-tool invalidation, next-request verification, post-auth grant revocation, ID policy, and anonymous/cookie/query/multiple-header isolation. Keys, owner authority and Work authorization/source remain synthetic. No network socket, real TLS, OAuth provider/login, durable SQL, real owner session or live ChatGPT/Slack is tested.

No Goblin-operated central service is added. Authoritative configuration/credentials/history remain on-premises. If connected, OpenAI necessarily handles OAuth codes/token copies and selected tool requests/status fields; Slack similarly handles selected messages/events. No raw bearer is retained in tool context or output, but the incoming transport necessarily receives it. Platform-held copies are not exclusively on-premises.

Public distribution, template endpoint support, private tunnel eligibility/OAuth reachability, SIWC and independent Slack event-routing restrictions remain as documented in [FEASIBILITY.md](FEASIBILITY.md). Production adoption still requires trusted owner authority/audit, durable grants/revocation, rate limits, provider removal/same-kid policy and provisioned noninteractive TLS/OAuth tests. Nothing here proves a single public plugin can route to independent instances.
