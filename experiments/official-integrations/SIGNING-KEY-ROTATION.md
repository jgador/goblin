# Customer signing-key rotation prototype

Reviewed 2026-10-14 UTC. Library-only experiment, not a deployed Keycloak connection or a replacement for customer-owned Work permissions. No production listener, HTTP adapter registration, OAuth issuance, identity mapping or shared service is added. A separate unregistered [pinned HTTPS document source](HTTPS-KEY-DOCUMENTS.md) now has mocked transport coverage, not live provider/TLS verification.

## Evidence and composition

[OpenAI authentication guidance](https://developers.openai.com/plugins/build/auth) requires resource servers to verify signatures, issuer, audience, lifetime and scope using the authorization server's signing keys. [Keycloak administration](https://www.keycloak.org/docs/latest/server_admin/index.html#_realm_keys) distinguishes active keys signing new tokens from passive keys verifying previous signatures. Neither establishes Goblin public-distribution eligibility or actual customer-provider compatibility.

The prototype reuses pinned Microsoft.IdentityModel 8.19.1 `JsonWebKeySet`, `JsonWebKey`, Base64UrlEncoder and the existing `ConnectorReadBoundary`. [Microsoft's validation API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler.validatetokenasync?view=msal-web-dotnet-latest) requires checking the returned validation result; existing verification does so. No signing or OAuth protocol is implemented from scratch.

`CustomerSigningKeyCache` accepts a summary produced by an exact customer-configured issuer preflight and an explicit `ICustomerJwksDocumentSource`. This is caller configuration, not trust inferred from incoming tokens. The source receives only the pinned HTTPS JWKS URI. There is no discovery fetch, automatic host selection, redirect, token-selected `jku`/`x5u`/embedded `jwk` or trust-on-first-use.

`RotatingConnectorReadBoundary` parses bounded input only to reject wrong issuer/algorithm/type and select an exact `kid`. Those unverified fields are **not identity or authorization**. It passes the selected public key to the existing verifier, then checks customer-held Work permission through the existing source on every invocation. It is deliberately not wired to `PrivateMcpHttpAdapter` or stdio. The fixed-key boundary remains available and unchanged.

## Explicit experimental cache policy

These limits are Goblin experiment policy, not platform requirements or universal JWKS compatibility:

- One serialized retrieval at a time. Gate wait and retrieval each have a five-second limit, so waiting plus retrieval can take up to ten seconds. Caller cancellation propagates; internal timeout and provider/parser errors return a fixed denial without tokens, keys or raw exceptions.
- Successful key sets have a ten-minute maximum age measured with monotonic time. No background timer, unbounded retry or expired last-known-good fallback. Requests use a current known key without fetching. A missing key can trigger one retrieval after a global one-minute cooldown, including after failed retrieval. No per-attacker-`kid` cache is allocated.
- Replacement is atomic after the whole document passes validation. Removed keys are not merged into the new set. Failed refresh does not extend the old set's age. Still-fresh known keys can remain usable after an unknown-key refresh failure, but expired sets deny access.
- Strict UTF-8, 32 KiB document limit, depth eight, duplicate-property rejection and at most 16 keys. Exact, unique, nonempty `kid` values of at most 128 UTF-16 characters, without controls. Reject private/symmetric material. Eligible public keys are RSA 2048 to 4096 bits with absent/RS256 algorithm, absent/signature use and absent or exactly `verify` operations. Unsupported public key profiles are not selected; a set without an eligible key is denied. RSA import validates material before publication; returned arrays are copied.

[IdentityModel ConfigurationManager](https://learn.microsoft.com/en-us/dotnet/api/microsoft.identitymodel.protocols.configurationmanager-1?view=msal-web-dotnet-latest) offers automatic/requested refresh and last-known-good configuration. This increment uses a narrow synchronous cache so its deny-on-expired policy is explicit; it does not claim to configure or inherit ConfigurationManager's defaults. Prefer a reviewed established retrieval/composition solution when moving beyond this isolated experiment.

An unverified token can request a missing key and consume the refresh opportunity. The global cooldown bounds upstream load but can delay newly rotated keys for one minute. Future ingress rate/concurrency limits are still needed. There is no same-`kid` signature-failure refresh: change signing-key IDs during rotation. The ten-minute TTL can accept a removed/compromised key until refresh, and this prototype does not consume Keycloak pushed not-before policies. **Do not enable production** without customer-approved emergency invalidation, revocation and cache-age policy.

## Retrieval and data boundaries

Customer-owned sources must enforce TLS/host trust, prohibit redirects, cap bytes while streaming, honor cancellation and release connections/tasks. The cache's post-read size check cannot enforce upstream allocation or terminate a noncooperative adapter. [CustomerHttpsJwksDocumentSource](HTTPS-KEY-DOCUMENTS.md) implements a pinned, credential-free, explicitly bounded streaming policy with mocked HTTP/stream checks; it remains unregistered. No DNS/HTTPS certificate/provider/network behavior is proven. Public signing keys stay in customer memory, not a Goblin central cache. No bearer token, password, private signing key, Work data or credentials are passed to the document source.

If connected later, OpenAI still handles connector codes/access/refresh tokens and requested/returned Work fields; Slack sees selected messages/events under its own integration. Authoritative credentials/configuration/history remain customer-owned, but platform-handled tokens and selected data are necessarily exchanged. Rotation is not Sign in with ChatGPT eligibility or end-user authorization.

## Verification and next gate

Focused checks use ephemeral RSA/JWT material, synthetic JWKS documents and a test Work source. They cover active/passive overlap, replacement/removal, bounded unknown-key storms, failure cooldown/expiry, defensive copies, issuer/algorithm/type separation, wrong audience/scope/signature, ignored URL headers, local denial, malformed/private/duplicate documents, caller cancellation and a real five-second retrieval timeout. These are not Keycloak exports or live provider/ChatGPT/Slack/database tests.

The pinned HTTPS source's mocked transport increment is described in [HTTPS-KEY-DOCUMENTS.md](HTTPS-KEY-DOCUMENTS.md). Next: trusted-owner emergency cache invalidation and bounded in-flight/concurrency behavior. Keep both components unregistered; live TLS/provider tests require provisioned noninteractive setup. PostgreSQL's 14 staged checks stay gated while recorded setup blockers remain.
