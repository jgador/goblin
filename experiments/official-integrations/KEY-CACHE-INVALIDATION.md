# Owner-authorized signing-key cache invalidation

Reviewed 2026-10-14 UTC. Isolated, volatile cache-administration prototype. No production owner-session adapter, public route, connector mutation tool, durable revocation/audit store or external authentication is added.

## Authority and atomic state

`CustomerSigningKeyAdministration` targets one configured customer cache and requires the existing `ILocalOwnerGrantAuthority` on **every** call. The future authority must validate the real request-scoped local-owner session and Host/Origin for that same installation; connector/profile/email claims are not owner authority. Test authorities are not authentication evidence.

Owner authorization has a linked five-second deadline, caller cancellation and fixed sanitized denial on other authority failures/timeouts. It is rechecked for cancellation before mutation. The cache mutation is synchronous and atomic, with no post-mutation cancellation check that could obscure a successful flush. A production owner adapter must cooperate with cancellation; the bounded wrapper cannot terminate arbitrary noncooperative code.

The cache has a dedicated short-lived state lock separate from its existing serialized retrieval gate. Keys, successful-age timestamp and cooldown are cleared, and an opaque generation object is replaced together. Refresh records the generation before awaiting its source, then checks it under the same state lock before publishing keys. A pre-invalidation refresh therefore denies instead of repopulating the cache, including after repeated invalidations. Caller cancellation is checked inside state access/publication. No HTTP, parsing, owner callbacks or await happens while the state lock is held.

[Microsoft's lock guidance](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock) supports a dedicated lock for exclusive access and short critical sections. [CancellationTokenSource documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync?view=net-10.0) explains that cancellation callbacks can execute asynchronously and throw. This design does not cancel sources or invoke callbacks under the state lock. Invalidation does not wait for an old fetch; the fetch retains its existing five-second deadline, and new requests retain the five-second retrieval-gate wait. Cooperative caller cancellation and cleanup are separately tested.

## What a flush does not revoke

**This is cache invalidation, not a signing-key denylist or complete emergency revocation.** A compromised key must first be disabled/removed at the trusted identity provider. If the authoritative provider still publishes that key, a new successful fetch can trust it again. Clearing the cooldown permits immediate fresh retrieval once the older operation leaves the gate; it does not automatically trust a replacement, skip verification or authorize Work.

Already returned public-key copies, signatures/identities already verified, and already authorized/in-flight Work reads are not retroactively revoked. The rotating read prototype currently retains no generation lease through signature verification or status access. There is no zero-latency revocation claim. A cleared cache denies if fresh retrieval fails, but default ten-minute cache age still applies when the owner does not invalidate it. No Keycloak pushed not-before policy, durable key quarantine/audit, cross-process invalidation or HTTP/stdio administration wiring exists.

The [OpenAI resource-server requirements](https://developers.openai.com/plugins/build/auth) still require full signature, issuer, audience, lifetime and scope checks plus local policy. A flush cannot establish ChatGPT identity/public-plugin eligibility or bypass customer-held per-Work grants. Public distribution, SIWC and Slack restrictions are unchanged.

## Verification, exchange and next gate

Focused checks use ephemeral RSA public sets, fake owner/source authorities, completion gates and cooperative cancellation. They cover owner denial/failure without mutation, repeated owner rechecks, pre/post-authority caller cancellation, fresh retrieval without stale fallback, independent caches, repeated invalidation of an in-flight refresh, queued-reader cancellation/source cleanup and a real five-second authority timeout. No sleeps/poll loops, real owner login, provider key removal, TLS/database or end-to-end platform coverage.

Invalidation itself sends no request and stores no customer data centrally. If a subsequent key fetch succeeds it goes directly to the customer's pinned identity provider. When integrated later, OpenAI necessarily handles connector codes/access/refresh tokens and selected Work fields; Slack handles selected events/messages separately. Authoritative credentials, configuration and Work/grant history stay customer-owned, but selected platform data and token copies are not exclusively on-premises.

Next: generation-bound verification leases with tests rejecting a key/identity invalidated between retrieval, signature verification and a new local Work read. Preserve explicit limits for already authorized reads and keep all components unregistered. Real owner authority, audit, durable grants and provisioned noninteractive provider/TLS validation remain adoption gates; do not retry recorded sandbox blockers.
