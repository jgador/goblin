# Customer-created ChatGPT plugin

Each customer connects ChatGPT directly to their own Goblin instance using
**Add custom MCP server**. Goblin hosts both the read-only MCP tools and an
embedded OpenIddict OAuth/OpenID Connect server. There is no separate identity
server or user-account system. Private connections need no directory review.
An optional development relay forwards Azure HTTPS to a locally running build.

Goblin's owner password authenticates the browser sign-in and consent. The saved
Codex connection authorizes agent execution independently; its credentials are
never used to issue MCP tokens. Signing in does not share Work.

## Deployment and enablement

Deploy the current Goblin backend, PostgreSQL with the current schema, and the
existing persistent storage. A static website alone is insufficient. A deployment
can use `https://app.example.com` for Goblin while its landing page uses another
host. OAuth and MCP share Goblin's canonical public origin.

```yaml
env:
  - name: GOBLIN_PUBLIC_ORIGIN
    value: https://app.example.com
  - name: GOBLIN_CHATGPT_ENABLED
    value: "true"
```

Retain the owner password verifier, database configuration, and persistent data
volume. Restart after configuration changes. Enabling ChatGPT requires durable
Work and an HTTPS public origin without a path, credentials, query, or fragment.
Unset or false disables both OAuth and MCP. The issuer is exactly the origin plus
`/`; the resource is exactly the origin plus `/mcp`.

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

The reverse proxy must preserve Host, Authorization, form bodies, and streamed
responses. When terminating TLS, supply `X-Forwarded-Proto: https`. Goblin accepts
this header only from loopback or RFC 1918 private proxy addresses, one hop away;
restrict network ingress to the intended proxy. OAuth refuses HTTP requests.
Settings and owner sign-in must also be available at the canonical HTTPS origin.

A **Secure MCP Tunnel** is an optional private MCP transport. Preserve the
canonical Host when forwarding. It does not automatically tunnel OAuth: the
browser and ChatGPT still need HTTPS discovery, sign-in, token, and key endpoints.
Public directory submission requires a publicly reachable MCP endpoint.

## Durable identity, keys, and schema

PostgreSQL persists the OAuth client, authorizations, and token records in the four
native OpenIddict tables. These library-owned EF mappings are excluded from Goblin
product scaffolding. Their SQL is consolidated into `0001_initial.sql`; startup
never creates or upgrades tables. Follow the [database workflow](database.md),
using an isolated database for verification and deliberately recreating only
owned preproduction databases when the baseline changes. Preserve Work volumes.

The platform's existing persistent data directory contains `chatgpt-oauth/`:

- `owner-subject`: one generated stable owner ID, independent of Codex accounts.
- `signing.pfx`: RSA signing certificate and private key.
- `encryption.pfx`: RSA encryption certificate and private key for codes and refresh tokens.

Files are created atomically with owner-only permissions on Unix and reused across
restarts. Back up this directory with the database. Treat private keys as secrets;
keep them out of execution pods, manifests, chat, and version control. Certificates
last five years; startup refuses expired or invalid keys. Plan replacement before
expiry. Replacing keys requires restarting Goblin and reconnecting ChatGPT;
existing codes and refresh tokens encrypted with retired keys become unusable.
Deleting the owner subject changes the identity and requires recreating grants.
Browser owner sessions continue to reset on restart.

This is a replacement of the unreleased external-provider configuration, not an
in-place identity migration. Old provider connections must reconnect, and grants
must be recreated for the current issuer and owner subject.

## Guided private setup

Open `/settings?section=chatgpt` and choose **Private plugin**.

1. **Prepare:** configure the HTTPS origin and enable ChatGPT. The generated
   settings do not deploy a server or create HTTPS.
2. **Sign-in:** create the OAuth client. The client secret is returned only when
   created or explicitly rotated; PostgreSQL stores its hash. Copy it into
   ChatGPT's OAuth form. You can create the client before the callback is known.
   The local server/storage checks do not establish public reachability.
3. **Install:** in ChatGPT, choose **+ → Add custom MCP server**, use the supplied
   `/mcp` URL (or configured Tunnel), and select OAuth. Client ID is
   `goblin-chatgpt`. Save the exact callback displayed by ChatGPT in Goblin settings,
   then create/install the plugin. Wildcards and foreign callback hosts are rejected.
   Complete sign-in using the Goblin password, then approve the connection.
   The supplied OAuth scopes are `openid offline_access goblin.work.read`, enabling
   the stable owner identity, refresh tokens, and shared Work reads.
4. **Share Work:** choose Work and an expiry of one hour, 24 hours, or seven days.
   Goblin supplies the owner subject automatically. Optionally include the latest
   already-approved result; this pins one attempt. Later results remain private.
5. **Test:** mention the installed plugin with `@` in a conversation. List shared
   Work, compare status and attention, and verify an unshared Work ID is denied.

Setup progress and manual confirmations are saved in this browser, scoped to the
issuer and MCP resource. Secrets, drafts, checks, and Work data are not stored in
browser storage. Reloading clears the displayed secret and provider checks.
Changing the issuer/resource resets progress. ChatGPT installation and conversation
results are confirmed by the user; Goblin cannot observe them.

## OAuth and access boundaries

The confidential client supports authorization-code flow with required PKCE S256
and `client_secret_basic`, plus rotating refresh tokens with no reuse leeway.
Authorization and token requests must name the exact MCP resource. Access tokens
are signed RS256 JWTs with `typ=at+jwt`, immutable subject, `jti`, exact resource
audience, five-minute expiry, and `goblin.work.read`. Codes expire after five
minutes and refresh tokens after 14 days. Password, implicit, client-credentials,
self-registration, and dynamic client-registration flows are not enabled.

OpenIddict advertises and returns issuer identification in authorization redirects.
OIDC provides the owner subject; this single-owner configuration does not provide
verified email. Workspaces that require verified-email/domain identity need an
additional explicitly designed identity configuration.

MCP validates signature, exact issuer/audience, lifetime and scope, then checks
persisted token and authorization status. Every Work read checks its current grant
in PostgreSQL. Owner cookies cannot authenticate MCP, and bearer tokens cannot
administer Goblin. Client-secret rotation revokes prior authorizations. **Revoke
ChatGPT connections** rejects existing access and refresh tokens without deleting
Work grants; reconnecting still exposes only the currently shared Work.

A grant exposes objective (up to 500 characters), status, update time, and attention
reason/failure codes. Approved results are bounded to 8,000 characters with a
truncation flag. Questions, full history, runtime sessions, repositories, artifacts,
files, and credentials remain excluded. Re-sharing replaces the grant atomically
and records a local-owner audit event. Up to 200 current grants are supported.

| Tool | Purpose |
| --- | --- |
| `goblin_list_work` | Authorized summaries, 25 per page |
| `goblin_get_work_status` | Shared Work status |
| `goblin_get_work_attention` | Shared attention details |
| `goblin_get_work_result` | The explicitly selected approved result |

All tools are read-only. Creation, execution, answers, retry, and approval remain
in Goblin. Work text is data, never instructions to expand access.

## Verification and publication

Run the ChatGPT authentication/HTTP tests and browser setup journeys. For real
PostgreSQL OAuth coverage, supply the opt-in application/admin test connections and
run `ChatGptOAuthTests`; it creates and removes uniquely named test databases and
writes temporary state under `.artifacts/openiddict/`. It exercises owner unlock,
consent, exact callbacks, code exchange, code replay, refresh rotation, restart
persistence, MCP discovery, explicit Work access, and authorization revocation.
It uses ASP.NET TestServer, so it does not establish external HTTPS reachability.

`tests/chatgpt/verify-provider.mts` runs an opt-in HTTPS loopback check against an
isolated Goblin instance. Its configuration contains `resource`, `caFile`, and
`ownerPassword`; credentials are consumed locally and never printed. It creates
or rotates the disposable OAuth client. Never target a live installation with it.

Finally perform the private ChatGPT installation and conversation checks above.
Record the Goblin revision, OpenIddict version, transport, workspace type, and
results without recording credentials. Fixtures and local integration runs cannot
establish a successful ChatGPT UI installation.

**Publish publicly** is a separate workflow. Goblin generates reviewable plugin
JSON, MCP JSON, and a ZIP with branding and review cases. Secrets and reviewer
credentials are excluded. Follow [directory submission](chatgpt-directory-submission.md)
for publisher verification, public HTTPS, package upload, and review. Goblin does
not submit applications or publish automatically.

## References

- [ChatGPT OAuth requirements](https://developers.openai.com/plugins/build/auth)
- [Connect ChatGPT](https://developers.openai.com/plugins/deploy/connect-chatgpt)
- [Secure MCP Tunnels](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)
- [OpenIddict](https://documentation.openiddict.com/introduction)

## Local iterations through Azure HTTPS

Use a dedicated Azure test installation as the HTTPS gateway for your WSL build.
Open the wizard through that HTTPS origin so the browser, OAuth endpoints, and MCP
all reach the local instance. See [ChatGPT local testing](chatgpt-local-testing.md)
for start/status/stop commands, fresh onboarding resets, and manual acceptance.
Production uses the Goblin instance installed on Azure directly.
