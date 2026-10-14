# Local-owner grant administration policy

Reviewed 2026-10-14 UTC. This is an isolated policy and composition contract, **not an enabled administration endpoint, owner authentication implementation or durable writer**. No services are registered by default, and no grant mutation is an MCP tool.

## Existing authority and proposed composition

Goblin currently validates the configured Host, the Origin for POST requests, and its local owner session through `Workspace`/`WorkspaceMiddleware`. Existing Slack identity linking requires local confirmation and remains separate. A future request-scoped `ILocalOwnerGrantAuthority` must use that established customer-local authority, not an external email, profile, connector access token or OpenAI identity token. Never register a constant allow-all implementation. Tests deliberately use a fake and do not establish actual session/CSRF coverage.

`CustomerGrantAdministration` requires an owner authority, mutation store and clock. It authorizes the owner on **each** approval or revocation before validating inputs or touching storage. Authority failures deny with a fixed message. There is no owner flag, session token, password or profile in the grant request or stored command.

The operation selects exactly one subject and positive Work ID. Issuer and resource are pinned in trusted customer configuration, not selected by the request. Subject is an exact opaque identifier, never trimmed, case-folded, treated as an email or interpreted as a wildcard. Blank/control-containing/invalid-UTF-8 subjects fail; the 256-byte subject and 512-byte issuer/resource limits match the schema proposal. Future UI must resolve and display the customer IdP account being reviewed and require explicit approval of this exact Work scope. This policy does not itself prove that the entered subject identifies a real customer account.

Approval requires a future expiry within an explicitly configured positive maximum of at most seven days. Seven days is an experimental local-policy ceiling, not a platform restriction or an automatic lifetime. Timestamps are normalized to UTC. No default duration, global Work grant, auto-link, renewal timer or seeded approval exists. A new explicit owner approval may replace an expired/revoked grant only through the future audited transaction.

Revocation requires the local owner but **no currently valid connector token or active connector login**. A customer must be able to revoke when the IdP or connector is unavailable. The store must preserve existing revocations and avoid accidental re-enablement. The read source's statement-snapshot caveat in [PERSISTENCE.md](PERSISTENCE.md) still applies.

## Mutation/audit adoption gate

`ICustomerGrantMutationStore` deliberately exposes only approval-with-audit and revocation-with-audit operations, not unrestricted grant upserts. Its production implementation must:

- Apply the mutation and appropriate customer-local authorization audit atomically under Goblin's short application transaction/advisory-lock convention.
- Check Work existence and the approved customer identity/link policy in that boundary; only the pinned issuer/resource and explicitly selected Work are in scope.
- Record the local-owner actor, exact scope, timestamps and result without passwords, raw sessions, bearer tokens or IdP secrets. Define audit retention independently of grant deletion.
- Require explicit owner action to renew/re-enable a grant; never renew from connector traffic. Define idempotent revoke behavior and uncertain-commit reconciliation before adding automatic retries.
- Remain outside MCP tool registration and preserve Work lifecycle, Wolverine dispatch and existing Slack identities.

No store implementation or audit table is added here. Store errors return `Grant change could not be confirmed.` and are never retried by this policy; an exception may occur after a commit, so it does not prove rollback. Caller cancellation propagates, likewise without a rollback guarantee. Concrete adapters must honor deadlines/cancellation and resolve uncertainty through customer-local state.

Production adoption still requires the database-first SQL/table-selection/generated-mapping workflow, actual certificate-authenticated PostgreSQL tests and existing owner-session/Origin integration tests. Do not enable routes or register mock services to satisfy this gate.

## Verification and data exchange

Twenty-four focused policy checks use test owner authority, mutation store and clock. They cover pinned single-Work scope, exact bigint/subject and UTC timestamps, owner denial/rechecks, UTF-8/control/ID boundaries, expiry limits, revoke without connector credentials, sanitized authority/store failures, one-attempt uncertain mutation and pre-call cancellation. They do **not** prove database atomicity/audit, idempotence/restart behavior, real owner authentication or CSRF protection.

No platform traffic, isolated installation, owner password or database connection is used. Proposed approvals/audits remain customer-local. A later ChatGPT connection still sends selected Work IDs/status to OpenAI and gives its OAuth client connector tokens; the policy does not change that necessary exchange or establish public distribution eligibility.

Next bounded increment: add an opt-in PostgreSQL harness without retrying the recorded missing database setup. Gate all connections on an explicitly provisioned disposable test profile, never a normal production connection. Add safe fixture ownership/cleanup and then validate actual query/grant/audit semantics when a database becomes available. Actual owner-authority composition remains a separate integration gate.
