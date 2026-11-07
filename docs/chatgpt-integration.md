# Private ChatGPT plugin

Each owner connects ChatGPT to one Goblin instance with **Add custom MCP server**.
Goblin hosts the MCP tools and an embedded OpenIddict OAuth server. The connection can
read and manage **all existing and future Work** after owner sign-in and consent. No
Work picker, grant renewal, or separate result sharing is needed. This authority does
not include unrelated instance administration, credentials, or raw upstream errors. For
a WSL k3s session, see [plugin testing](plugin-testing.md#local-testing).

## Deployment

Enable durable Work, PostgreSQL, persistent storage, and an HTTPS public origin. For
example:

```yaml
env:
  - name: GOBLIN_PUBLIC_ORIGIN
    value: https://app.example.com
  - name: GOBLIN_CHATGPT_ENABLED
    value: "true"
```

OAuth and MCP share the public origin. The issuer is the origin plus `/`; the resource
is the origin plus `/mcp`. The origin must have no path, credentials, query, or
fragment. Disabling ChatGPT disables both endpoints. A Secure MCP Tunnel can carry MCP
traffic but OAuth discovery, sign-in, token, and key endpoints still need public HTTPS.

| Path | Purpose |
| --- | --- |
| `/mcp` | Streamable HTTP MCP |
| `/.well-known/oauth-protected-resource/mcp` | Protected resource and scopes |
| `/.well-known/openid-configuration` | OIDC discovery |
| `/.well-known/oauth-authorization-server` | OAuth discovery |
| `/oauth/authorize`, `/oauth/unlock` | Owner sign-in and consent |
| `/oauth/token`, `/oauth/keys`, `/oauth/userinfo` | Tokens, public keys, owner subject |

The reverse proxy must preserve Host, Origin, Authorization, form bodies, and streamed
responses. If it terminates TLS, send `X-Forwarded-Proto: https` from the one trusted
private hop. OAuth rejects HTTP. Settings and owner sign-in must use the same HTTPS
origin.

The persistent data directory's `chatgpt-oauth/` contains the stable owner subject, RSA
signing certificate, and encryption certificate. Keep the private keys with the database
backup and out of execution pods and version control. Startup reuses them and refuses
expired or invalid keys. Replacing keys requires a new ChatGPT connection. Owner browser
sessions reset on restart. PostgreSQL stores native OpenIddict client, authorization,
and token records; it owns Work history and command receipts. Startup never creates or
upgrades tables.

## Connect

Open `/settings?section=connections&provider=chatgpt` on the public origin. The four
setup steps are **Prepare**, **Connection setup**, **Install**, and **Test**.

1. Prepare the HTTPS origin and enable the server.
2. In **Connection setup**, create the OAuth client and save its secret. It is
   shown only on creation or explicit rotation. Run the local provider and
   storage check. Owner sign-in happens later, during **Install**.
3. In ChatGPT Plugins, choose **+ → Add custom MCP server**. Use the displayed
   `/mcp` URL, OAuth, client ID `goblin-chatgpt`, the saved secret, and
   `client_secret_basic`. If ChatGPT shows Base scopes, enter
   `openid offline_access goblin.work.read goblin.work.write`. In Goblin's
   **Install** step, select **Download Goblin logo** and upload the 512 × 512
   light-background PNG as the plugin logo in ChatGPT. The download is below
   ChatGPT's 10 KB file-size limit.
4. Copy the exact OAuth callback shown by ChatGPT into Goblin's **Install**
   step and click **Save callback**. Wait for **Callback saved**, then return to
   ChatGPT and connect. The confirmed callback for the current form is
   `https://chatgpt.com/connector_platform_oauth_redirect`; use the displayed
   value if it differs. Wildcards and foreign callback hosts are rejected.
5. Enter the Goblin owner password and approve access to all Work. Refresh the
   plugin's tools in ChatGPT and test it in a new conversation.

Entering a callback does not save it. An unsaved edit blocks installation confirmation;
failed saves preserve the draft. Saving a different callback clears the previous
installation confirmation. If authorization already failed, save the callback and
restart the connection from ChatGPT. Setup progress is stored in this browser, scoped to
issuer, resource, and owner subject. Secrets, drafts, and Work data are not stored
there. Goblin cannot observe installation or conversations, so the owner confirms those
steps.

Scope changes require renewed consent; refreshing cannot expand authorization. Refresh
the custom plugin's tool list after updating Goblin; ChatGPT may need a new
conversation. Rotating the client secret revokes prior connections. **Revoke ChatGPT
connections** revokes access and refresh tokens; subsequent reads and writes are denied.
An operation accepted before revocation or a disconnect may still complete through
Goblin's durable Work path.

## OAuth and security

Goblin uses a confidential client, authorization code with required PKCE S256,
`client_secret_basic`, and rotating refresh tokens without reuse leeway. Requests must
name the exact MCP resource. Access tokens are RS256 `at+jwt` tokens with the
installation's owner subject, a `jti`, exact issuer and audience, both Work scopes, and
a five-minute lifetime. Codes expire in five minutes; refresh tokens expire in 14 days.
Password, implicit, client credentials, and dynamic client registration flows are
disabled. OIDC exposes only the stable owner subject.

MCP verifies signature, issuer, audience, lifetime, subject, both scopes, and the stored
token and authorization state. It derives the acting identity from that authentication,
never from a tool argument. Owner cookies cannot authenticate MCP; MCP bearer tokens
cannot administer Goblin. Work text remains data and cannot expand authority. The public
responses exclude credentials and raw upstream errors.

## Work tools

The MCP tool list generates names, descriptions, input schemas, and annotations from
[ChatGptTools.cs](../backend/src/Goblin.Web/ChatGpt/ChatGptTools.cs). Refresh it in
ChatGPT after a tool change. Use `goblin_ping` for connection verification, then
discover Work and submit explicit actions through those schemas. Each uses the same
application path as the Work HTTP API. Creation requires reserved Work and command IDs;
later commands require a fresh expected version. Keep the command ID and exact input so
a repeated delivery returns the same receipt without creating another Work item or
attempt. A reused ID with different input or a stale version returns an actionable
conflict. After acceptance, the Work transaction and outbox survive HTTP or MCP
disconnects. Execution, failure attention, repository authorization, uncertain execution
reconciliation, and explicit retry keep their existing lifecycle rules. A full
permission grant does not bypass those rules or start automatic failure retries.

Section reads return `contentJson` in chunks with `nextOffset`; concatenate chunks in
order, then parse the JSON array. The expected Work version prevents mixing chunks from
different states. Result text has its own continuation. The artifact section returns
supported artifact metadata and references; it does not read arbitrary files or expose
credentials.

All tools declare OAuth security and truthful MCP annotations. Read tools are read-only.
ID reservation is a state change. Command tools that can start or cancel execution or
authorize repository work declare their side effects. The command tools are idempotent
because identical calls with the same command ID return one receipt. Goblin adds no
per-action plugin approval gate. According to
[OpenAI's custom MCP server guide](https://developers.openai.com/api/docs/guides/custom-mcp-server#how-to-use),
ChatGPT writes require confirmation by default; an owner can remember a choice for a
tool within a conversation, and workspace restrictions still apply. The plugin details
page can toggle tools and refresh their descriptions. Goblin cannot override those
ChatGPT controls.
[OpenAI's annotation guidance](https://developers.openai.com/plugins/plugin-guidelines#correct-annotation)
also says annotations inform safeguards and do not replace authorization.

## Verification

Run `ChatGptHttpTests`, `ChatGptAuthenticationTests`, browser setup journeys, and opt-in
`ChatGptOAuthTests` against disposable real PostgreSQL databases. The OAuth tests cover
consent, callback registration, PKCE, refresh, Work access, and revocation. TestServer
does not prove external HTTPS reachability. The opt-in
`tests/chatgpt/verify-provider.mts` script is for an isolated localhost instance; it
rotates the OAuth client secret, so never run it against a live installation. Complete a
private ChatGPT installation and conversation test for actual ChatGPT behavior. Public
directory submission is not supported.
