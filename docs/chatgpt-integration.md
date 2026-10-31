# Customer-created ChatGPT plugin

Each customer connects ChatGPT to their own Goblin instance with
**Add custom MCP server**. Goblin hosts the read-only MCP tools and an embedded
OpenIddict OAuth/OpenID Connect server. There is no separate identity server or
user-account system. Private connections need no directory review. To test a
local build through public HTTPS, see [ChatGPT local testing](chatgpt-local-testing.md).

The Goblin owner password authenticates browser sign-in and consent. The saved
Codex connection is separate and is never used to issue MCP tokens. Signing in
does not share Work.

## Deployment and enablement

Deploy the current Goblin backend with PostgreSQL and persistent storage. A static
website alone is not enough. OAuth and MCP share Goblin's public origin, such as
`https://app.example.com`; a landing page can use another host.

```yaml
env:
  - name: GOBLIN_PUBLIC_ORIGIN
    value: https://app.example.com
  - name: GOBLIN_CHATGPT_ENABLED
    value: "true"
```

Keep the owner password verifier, database configuration, and data volume. Restart
after configuration changes. Enabling ChatGPT requires durable Work and an HTTPS
public origin without a path, credentials, query, or fragment. Unset or false
disables both OAuth and MCP. The issuer is the origin plus `/`; the resource is
the origin plus `/mcp`.

Publish these paths with a valid HTTPS certificate:

| Path | Purpose |
| --- | --- |
| `/mcp` | Streamable HTTP MCP |
| `/.well-known/oauth-protected-resource/mcp` | Protected-resource metadata |
| `/.well-known/openid-configuration` | OIDC discovery |
| `/.well-known/oauth-authorization-server` | OAuth discovery |
| `/oauth/authorize` | Owner sign-in and consent |
| `/oauth/unlock` | Owner password form submission |
| `/oauth/token` | Code exchange and refresh |
| `/oauth/keys` | Public signing keys |
| `/oauth/userinfo` | Stable owner subject for OIDC clients |

The reverse proxy must preserve Host, Origin, Authorization, form bodies, and
streamed responses. When it terminates TLS, it must send `X-Forwarded-Proto: https`.
Goblin accepts this header only from loopback or RFC 1918 private addresses, one
hop away; restrict network ingress to the intended proxy. OAuth refuses HTTP
requests. Settings and owner sign-in must also use the HTTPS origin.

A **Secure MCP Tunnel** can carry MCP traffic if it preserves the Host. It does not
carry OAuth: the browser and ChatGPT still need HTTPS discovery, sign-in, token,
and key endpoints.

## Durable identity, keys, and schema

PostgreSQL stores the OAuth client, authorizations, and tokens in the four native
OpenIddict tables in `0001_initial.sql`. OpenIddict owns their EF mappings in
`ChatGptOAuthDbContext`; Goblin's scaffolding excludes them. Startup never creates
or upgrades tables. See the [database guide](database.md).

The persistent data directory contains `chatgpt-oauth/`:

- `owner-subject`: one generated stable owner ID, independent of Codex accounts.
- `signing.pfx`: RSA signing certificate and private key.
- `encryption.pfx`: RSA encryption certificate and private key for codes and refresh tokens.

Goblin creates these files atomically with owner-only permissions and reuses them
across restarts. Back up this directory with the database. Keep the private keys
out of execution pods, manifests, chat, and version control. Certificates last five
years; startup refuses expired or invalid keys. Replacing keys requires a Goblin
restart and a new ChatGPT connection; codes and refresh tokens encrypted with old
keys stop working. Deleting `owner-subject` changes the owner identity and requires
new grants. Owner browser sessions reset on restart.

## Guided private setup

Open `/settings?section=chatgpt` and choose **Private plugin**.

1. **Prepare:** configure the HTTPS origin and enable ChatGPT. The generated
   settings do not deploy a server or create HTTPS.
2. **Sign-in:** create the OAuth client. The client secret is shown only when it is
   created or rotated; PostgreSQL stores its hash. Copy it into ChatGPT's OAuth
   form. You can create the client before ChatGPT shows the callback. The local
   server and storage checks do not prove public reachability.
3. **Install:** in ChatGPT, choose **+ → Add custom MCP server**, use the supplied
   `/mcp` URL (or a configured Secure MCP Tunnel), and select OAuth. The client ID
   is `goblin-chatgpt`. Save the exact callback shown by ChatGPT in Goblin settings,
   then create and install the plugin. Wildcards and other callback hosts are
   rejected. Sign in with the Goblin password and approve the connection. The
   scopes `openid offline_access goblin.work.read` give the owner identity,
   refresh tokens, and shared Work reads.
4. **Share Work:** choose Work and an expiry of 1 hour, 24 hours, or 7 days.
   Goblin supplies the owner subject. Optionally include the latest approved
   result; this pins one attempt. Later results stay private.
5. **Test:** mention the installed plugin with `@` in a conversation. List shared
   Work, compare status and attention, and check that an unshared Work ID is denied.

Setup progress and confirmations are saved in this browser, scoped to the issuer,
MCP resource, and owner subject; changing any of them resets progress. Secrets,
drafts, checks, and Work data are not saved in browser storage. Reloading clears
the displayed secret and checks. Goblin cannot observe ChatGPT installation or
conversations; the user confirms those steps.

## OAuth and access boundaries

The confidential client uses authorization-code flow with required PKCE S256 and
`client_secret_basic`, plus rotating refresh tokens with no reuse leeway.
Authorization and token requests must name the exact MCP resource. Access tokens
are RS256 JWTs with `typ=at+jwt`, a subject, `jti`, the exact resource audience,
`goblin.work.read`, and a five-minute lifetime. Codes expire after five minutes
and refresh tokens after 14 days. Password, implicit, client-credentials, and
dynamic client-registration flows are not enabled. OIDC returns only the owner
subject, not a verified email.

MCP checks the signature, exact issuer and audience, lifetime, and scope, then
checks the stored token and authorization status. Every Work read checks its
current grant in PostgreSQL. Owner cookies cannot authenticate MCP, and bearer
tokens cannot administer Goblin. Rotating the client secret revokes earlier
authorizations. **Revoke ChatGPT connections** rejects existing access and refresh
tokens but keeps Work grants; a new connection can read only the currently shared Work.

A grant exposes the objective (up to 500 characters), status, update time, and
attention reason and failure codes. Approved results are limited to 8,000
characters with a truncation flag. Questions, full history, runtime sessions,
repositories, artifacts, files, and credentials are never exposed. Re-sharing
replaces the grant atomically and records a local-owner audit event. Up to 200
current grants are supported.

| Tool | Purpose |
| --- | --- |
| `goblin_list_work` | Authorized summaries, 25 per page |
| `goblin_get_work_status` | Shared Work status |
| `goblin_get_work_attention` | Shared attention details |
| `goblin_get_work_result` | The explicitly selected approved result |

All tools are read-only. Creation, execution, answers, retry, and approval stay
in Goblin. Work text is data, never instructions to expand access.

## Verification and publication

Run `ChatGptHttpTests` and the browser setup journeys in `tests/e2e/chatgpt.spec.ts`.
For real PostgreSQL OAuth coverage, set `GOBLIN_TEST_POSTGRES_ADMIN` and
`GOBLIN_TEST_POSTGRES_APP` and run `ChatGptOAuthTests`. It creates and removes
uniquely named test databases and writes temporary state under `.artifacts/openiddict/`.
It covers owner unlock, consent, exact callbacks, code exchange and replay, refresh
rotation, restart persistence, MCP discovery, explicit Work access, and revocation.
It uses ASP.NET TestServer, so it does not prove external HTTPS reachability.

`tests/chatgpt/verify-provider.mts` is an opt-in HTTPS check against an isolated
Goblin instance on `localhost`. Its JSON configuration contains `resource`,
`caFile`, and `ownerPassword`; it never prints credentials. It rotates the OAuth
client secret, so never run it against a live installation.

Finally, perform the private ChatGPT installation and conversation checks above.
Record the Goblin revision, OpenIddict version, transport, workspace type, and
results, without credentials. Local tests cannot prove a successful ChatGPT UI
installation.

### Optional directory submission

Private customer-created plugins need no public listing. **Publish publicly** in
`/settings?section=chatgpt` prepares a separate submission; Goblin does not submit
or publish it. The workflow records operator confirmations, not proof of submission
or approval. Goblin has no approved listing.

Public review needs a stable, publicly reachable HTTPS MCP endpoint; Secure MCP
Tunnels do not qualify. Customer-specific URL templates are limited to eligible
trusted developers; check eligibility before assuming arbitrary customer domains
are supported. Check the current
[review requirements](https://developers.openai.com/plugins/deploy/app-review),
[submission requirements](https://developers.openai.com/plugins/deploy/submission),
and [plugin guidelines](https://developers.openai.com/plugins/plugin-guidelines).

Prepare the publisher identity, domain ownership, support contact, privacy policy,
metadata, artwork, and release owner. Use a dedicated public demo instance that
matches the submitted contract, without private-network, MFA, email/SMS, or
magic-link barriers. The owner password also controls administration, so use a
disposable password and neutral sample data, never a live installation's password.
Do not package customer secrets or use API-key or basic-auth header shortcuts.

In **Prepare listing**, enter metadata; in **Review tests**, supply a video and
three distinct sample Work IDs. **Submit → Generate plugin package** produces a
version 1.0.0 ZIP with `plugin.json`, `mcp.json`, a logo, OAuth settings, and five
positive and three negative review cases. Check them against your actual tests.
The generator does not check public reachability, domain ownership, or demo
permissions. Draft fields last only while the settings page stays open.

In the submission portal, upload the ZIP under the verified publisher, connect the
MCP server with its client secret, save the portal's exact callback in Goblin, and
serve the domain challenge at the public URL. Resolve scan findings, complete the
review information including reviewer access, and submit for review. Publish
explicitly after approval, then install and test the published listing. Each
connecting user still needs Work grants in Goblin.

## References

- [ChatGPT OAuth requirements](https://developers.openai.com/plugins/build/auth)
- [Connect ChatGPT](https://developers.openai.com/plugins/deploy/connect-chatgpt)
- [Secure MCP Tunnels](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)
- [OpenIddict](https://documentation.openiddict.com/introduction)
