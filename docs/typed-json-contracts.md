# Typed JSON boundaries

Known production payloads are read directly into purpose-specific contracts with
explicit `JsonPropertyName` attributes. Models select only fields Goblin consumes;
unrecognized upstream fields are ignored rather than requiring entire API models.

- HTTP POSTs retain bounded buffering and object/syntax validation. Endpoints bind
  the buffered bytes to their own request type without a DOM or serialization round trip.
- Kubernetes monitoring uses node, pod, and kubelet summary projections. Missing or
  unusable numeric readings remain unknown. The generic Kubernetes API is the only
  resource reader; its former dynamic overload is removed.
- GitHub uses user, repository/permissions, and pull-request projections.
- Slack uses Web API responses, Socket Mode envelopes/events, and typed CLI setup
  files. Workspace-keyed CLI maps retain dictionaries with typed values. Bot/subtype
  field presence, including explicit null, still suppresses unsupported messages.
  Form requests are encoded directly without traversing serialized JSON.
- Repository workers read a typed operation response and retain exact state parsing.
- Codex results and setup observations use adapter contracts that map to core types;
  optional setup presence and workspace release defaults remain explicit.
- Workspace inspection shares typed listing/file responses with the in-pod reader;
  absent fields are omitted so the existing response shapes do not gain a discriminator.

Intentional dynamic boundaries remain: `CodexClient` JSON-RPC envelopes and payloads,
generated Codex models with arbitrary schema values, protocol/Kubernetes/custom
converters, and JSON/schema generators (including the Codex output-schema builder).
Test DOM usage remains appropriate for constructing fixtures and verifying raw wire
structure. Core lifecycle types do not acquire JSON or runtime dependencies.
