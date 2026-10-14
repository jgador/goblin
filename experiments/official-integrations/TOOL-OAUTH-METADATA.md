# Tool OAuth metadata compatibility gate

Reviewed 2026-10-14 UTC. This is an isolated descriptor increment, not OAuth login, a verified ChatGPT identity integration or public distribution.

Both private HTTP status tools now declare `_meta.securitySchemes` through the pinned SDK's supported `McpMetaAttribute.JsonValue` API. The value is a JSON array containing only `oauth2` and the existing exact `goblin.work.status.read` scope. The constant derives from the verifier's scope, not runtime configuration, incoming claims or tool arguments. The stdio executable is unchanged because its local session/allowlist is not an OAuth connector.

## Restriction proven before implementation

[OpenAI authentication](https://developers.openai.com/plugins/build/auth) documents per-tool top-level `securitySchemes` plus resource metadata and runtime `_meta["mcp/www_authenticate"]` errors for tool-level OAuth UI. The [OpenAI reference](https://developers.openai.com/plugins/reference) separately documents `_meta.securitySchemes` as a compatibility mirror for clients reading metadata. That mirror is the only part added here; it does not establish that current ChatGPT accepts a mirror-only declaration.

The official [SDK 1.4.1 Tool source](https://github.com/modelcontextprotocol/csharp-sdk/blob/v1.4.1/src/ModelContextProtocol.Core/Protocol/Tool.cs) is sealed, has `Meta` but no top-level `securitySchemes` property or JSON extension-data member. Its [metadata attribute source](https://github.com/modelcontextprotocol/csharp-sdk/blob/v1.4.1/src/ModelContextProtocol.Core/Server/McpMetaAttribute.cs) and [API reference](https://csharp.sdk.modelcontextprotocol.io/v1/api/ModelContextProtocol.Server.McpMetaAttribute.html) explicitly support JSON-valued listing metadata, not invocation-result metadata. Actual SDK HTTP serialization tests verify the mirror and the absence of the top-level field. A complete required declaration cannot be represented by the pinned ordinary descriptor API inspected here.

No serializer fork, reflection mutation, response rewriting, undocumented property, dependency upgrade or invented authentication capability is introduced. Primary top-level support remains an adoption gate requiring a separately reviewed supported SDK/API path. OpenAI's two-half OAuth UI requirement is not satisfied by this change.

## Authorization and verification

All MCP requests still need one validated bearer header; anonymous discovery remains HTTP 401 with the existing resource/scope challenge. Public protected-resource metadata remains the only anonymous endpoint. Neither tool advertises `noauth`, receives credentials as arguments, nor grants Work access from descriptor hints. Signature/issuer/audience/expiry/scope and customer-local per-Work authorization remain enforced. The rotating identity retains its lease checks and explicit non-atomic admitted-read limitation.

Local permission denial remains a sanitized MCP tool error, not an OAuth reauthorization challenge. No `_meta["mcp/www_authenticate"]` is added to permission failures: reconnecting cannot approve a revoked/ungranted Work. Mid-request lease denial also remains fail-closed without an automatic login/retry. Adding runtime challenges would require a separate authentication-versus-authorization classification review.

Six new actual TestServer/MCP SDK checks cover fixed and rotating descriptor arrays/exact scope, one Work-ID argument, absence of top-level metadata, no bearer disclosure, and permission denial without OAuth challenges. Existing anonymous/header/Host/Origin/local-grant/lease checks remain in place. Backend/grants/keys/owner remain synthetic; no ChatGPT client, OAuth provider, real TLS, durable SQL or end-to-end platform result is claimed.

Authoritative customer credentials/configuration/history stay on-premises with no Goblin-operated central infrastructure. A connected platform necessarily receives descriptors, OAuth code/token copies and selected Work IDs/status fields; Slack receives selected messages/events. Descriptor metadata is not a promise of exclusively on-premises platform processing or storage. [FEASIBILITY.md](FEASIBILITY.md) retains the independent distribution, tunnel/SIWC and Slack routing gates.
