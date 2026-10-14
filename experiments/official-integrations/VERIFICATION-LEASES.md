# Generation-bound rotating verification leases

Reviewed 2026-10-14 UTC. Library-only experiment, not a production listener, OAuth issuer, real owner authority or live platform integration. Fixed-key HTTP behavior and the stdio executable are preserved; protection described here applies only to `RotatingConnectorReadBoundary`, including its optional isolated HTTP composition.

## Implemented checks

`CustomerSigningKeyCache.GetLeaseAsync` is internal. A lease binds a defensively copied public key to its exact cache object and opaque current generation. Generation changes on owner invalidation **and** every successful key-set replacement. The current cache age must still be below ten minutes when checked. Public `GetAsync` remains a public-key-copy API for existing callers; that returned key alone has no subsequent lease enforcement.

The rotating boundary retains reject-only bounded parsing, then:

1. Selects a trusted-cache lease and checks its cache/generation/age before signature validation.
2. Calls the existing IdentityModel-based verifier for issuer, audience, RS256, at+jwt, expiry/not-before, subject/token identity and exact read scope.
3. Checks the lease again after verification before constructing `RotatingConnectorIdentity`.
4. Before each new leased Work read, checks exact boundary ownership and current cache generation/age, then invokes the existing expiry/resource checks and customer-local authorization/status source.

No identity is built from unverified claims. The new sealed identity has internal construction, retains no raw bearer token, and exposes only verified issuer/subject/resource/expiry. Internal phased methods let the friend test assembly deterministically invalidate between actual cryptographic verification and identity binding; they are not public authentication or application composition APIs. Changing a cache or boundary object is denied even when issuer/resource/key material match.

[OpenAI authentication guidance](https://developers.openai.com/plugins/build/auth) still requires full resource-server token checks and local policy. [Microsoft's validation API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler.validatetokenasync?view=msal-web-dotnet-latest) requires checking the validation result, which the reused verifier does. A generation lease adds local cache-consistency checks, not OAuth protocol support, provider key revocation or verified ChatGPT identity eligibility.

## Deliberately conservative limits

- Every successful refresh retires old leases, even with identical/overlapping keys. Re-authenticate locally from the still-valid bearer token to obtain a fresh verified lease; this code does not automatically retry reads, redirect to OAuth or reapprove grants. Unknown-key traffic can consume a refresh opportunity and retire existing leased identities; the one-minute global cooldown bounds retrieval but future ingress limits remain necessary.
- Failed refresh does not extend age or publish a generation. Fresh known-key leases may remain usable until their original age limit; an expired or invalidated lease denies before invoking the source. Owner invalidation resets cooldown and cannot be undone by a pre-invalidation fetch.
- The final lease check is an admission check, **not atomic database revocation**. Invalidation can race after that check and before/during source authorization or its database await. No network, crypto or database await is held under the cache state lock. A read already admitted/authorized may complete after invalidation, and a focused test explicitly demonstrates that limit. No zero-latency or retroactive read-revocation guarantee.
- Provider-still-published compromised keys can be trusted again after a fresh fetch. Disable/remove them at the provider before flushing. Default ten-minute cache age, same-kid policy, real owner/audit/Host/Origin composition, durable grants and cross-process invalidation remain adoption gates. No key denylist or Keycloak pushed not-before implementation.

## Actual verification and exchange

Focused tests use real ephemeral RSA/JWT verification with synthetic JWKS, clock, local grants and owner. They cover valid identity without Work access, repeated local grant checks, invalidation after authentication/before verification/after real signature verification, renewed verification of still-published keys, boundary/cache isolation, cache-age expiry, successful-refresh retirement, wrong signature/audience/scope/type/expired token, caller cancellation and the admitted-read limitation. Completion gates are used without sleeps or timing loops. No real login, TLS/provider rotation, SQL/durable revocation or ChatGPT/Slack end-to-end success.

Lease/generation/verified identity remain customer-runtime memory; no Goblin central service or storage exists. If connected later, key retrieval goes directly to the customer identity provider, OpenAI handles connector codes/access/refresh tokens and selected tool fields, and Slack handles its selected messages/events. Authoritative credentials/configuration/Work history remain customer-owned; selected platform data and token copies are not exclusively on-premises. Public distribution and SIWC/Slack restrictions are unaffected.

The [isolated HTTP composition](ROTATING-HTTP.md) now carries the verified leased identity in request context, with actual TestServer/SDK checks for invalidation between middleware authentication and tool access, Host/Origin/challenge behavior and fresh local grants. The fixed-key path is preserved and production listeners remain disabled. Real owner authority, PostgreSQL and provisioned noninteractive TLS/provider checks remain required.
