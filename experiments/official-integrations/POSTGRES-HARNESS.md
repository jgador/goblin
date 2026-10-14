# Opt-in PostgreSQL fixture harness

Reviewed 2026-10-14 UTC against Goblin's existing disposable PostgreSQL fixtures, connection-variable catalog, certificate configuration and database-first rules. Official references: [Npgsql data sources/parameters](https://www.npgsql.org/doc/basic-usage.html), [CREATE DATABASE](https://www.postgresql.org/docs/current/sql-createdatabase.html), [DROP DATABASE](https://www.postgresql.org/docs/current/sql-dropdatabase.html).

The harness has **test-only fixture safety, a live connectivity/role/cleanup check and a gated minimal Work/grant query suite**. It does not deploy a production grant schema or durable writer. On 2026-10-14 UTC, the sandbox passed 19 fixture safety contracts plus eight embedded-schema/typed-parameter contracts; live PostgreSQL stayed skipped. No database was created or deleted and no SQL was evaluated.

## Explicit configuration boundary

Ordinary focused execution does not read database credentials or open a connection, even if test variables happen to exist. Live mode additionally requires the final `--postgres` argument, both existing `GOBLIN_TEST_POSTGRES_ADMIN` and `GOBLIN_TEST_POSTGRES_APP` values, and:

- The same single network Host/Port, with roles exactly `goblin_admin` and `goblin_app`.
- `SSL Mode=VerifyFull` and explicit root certificate, client certificate and client key paths. Password-bearing, multi-host and Unix-socket configurations are rejected.
- An administrator allowed to create/drop databases and connect to the fixed `postgres` maintenance database. This may require configuring an existing disposable development profile; the harness does not provision roles/certificates or perform login.

Both absent values produce a skip; partial/malformed/unsafe configuration fails with a fixed sanitized error. Normal `ConnectionStrings__Goblin` and application defaults are never read. Test connections are sensitive: supply through the established profile/runtime environment, never command arguments, committed files, logs or prompts.

## Fixture ownership and cleanup

Each plan generates a new `goblin_mcp_test_` name followed by 128 cryptographically random bits in lowercase hex. The configured database names are never used for fixture DDL or application queries. Admin create/drop runs only in `postgres`; app/admin smoke queries run only in the generated fixture. Database identifiers are generated internally, quoted and checked against that exact plan. Catalog lookups use positional parameters and qualified `pg_catalog` objects.

After an acknowledged CREATE, the harness records the database OID and verifies its owner. Before cleanup it rechecks the same name/OID/owner. An uncertain CREATE/OID acknowledgement does not authorize a name-only DROP; a changed/missing identity refuses cleanup. Creation and cleanup each get only one attempt. No `DROP ... FORCE`, global pool clearing, connection termination, production reset or broad filesystem cleanup exists. Active foreign connections make DROP fail.

Connections are unpooled and do not enlist in ambient transactions; connection/command timeouts are five seconds, live work has a 30-second deadline, and cleanup gets its own ten-second deadline even if the caller deadline expired. Commands, readers and data sources are disposed. Raw provider/configuration errors are withheld; the generated nonsecret fixture name is reported on failures so an operator can investigate an orphan explicitly.

Catalog checks and DROP cannot be one PostgreSQL transaction. This is not protection against an administrator concurrently dropping/recreating the same random name between those statements. Use a dedicated disposable test profile, no concurrent privileged fixture DDL, and never share that test server/profile with production. This configuration gate cannot prove the supplied hostname belongs to a safe environment; the operator must provision the correct profile.

## Running after the blocker changes

Build using README's targeted command. Only when an already provisioned disposable profile is available, run the same executable with its two stdio arguments plus `--postgres`, using `goblinctl db run <profile> --usage tests -- ...` to supply the established test variables privately. Leave enough outer timeout for cleanup, for example 120 seconds. Do not run this command unattended in the current sandbox while the recorded setup blocker persists.

The optional check creates an empty `template0` fixture, checks `current_database()`/`current_user` using both roles, then runs `PostgresWorkGrantChecks` before confirmed cleanup. A second exact fixture-name/admin-role check precedes test DDL. `fixtures/work-grants.sql` is embedded only in the test executable, not packaged as a production migration or run at Web startup. It is a reduced Work schema with private objective/JSON-state decoys and the proposed grant constraints; it is not Goblin's full persistence model.

The fixture clears direct/PUBLIC table privileges before granting the app role only four Work columns and the grant predicate columns. It adds no roles, database-wide privileges or approval defaults. This does not negate privileges inherited from other roles or superuser access; live privilege-negative checks must verify the test profile. No production role configuration is changed.

Fourteen staged live checks exercise the actual `PostgresGrantedWorkStatusSource` with Npgsql and ephemeral verified RSA/JWT identities: no grant, exact bigint/UTC projection, ungranted/missing Work, different issuer/subject/resource, hostile subject binding, fresh-connection visibility, private-column and self-approval privilege denial, same-identity revocation, expiry without sleeps, and future approval denial. Fixture administrator writes are explicit synthetic test setup, **not** a production owner-authenticated mutation/audit implementation.

These live checks have **not run in this sandbox**. Even a later live pass would not establish full production migrations/EF compatibility, constraint-negative coverage, server-enforced certificate policy, concurrent snapshot/revocation guarantees, audit transactions, real owner sessions or ChatGPT/Slack connectivity. Existing certificate-negative tests and later concurrency/audit checks remain necessary.

Offline contracts cover disabled-mode zero credential reads, absent/unsafe configuration, random names, connection rebasing/bounds, uncreated/uncertain/cancelled creation, ownership-mismatch cleanup refusal and one-attempt sanitized cleanup failure. They use actual Npgsql connection-string parsing plus fake database control, not SQL execution or real ownership verification.

Eight additional offline checks load the embedded SQL and inspect its marker/constraints/privilege clauses and actual Npgsql seed-command parameters, including hostile subject, bigint, JSONB and UTC timestamp bindings. They do not parse/evaluate SQL or test database policy. Ordinary execution still does not read connection credentials or open a database connection.

Keep the live path skipped until an already provisioned disposable test profile is available. Production baseline/table-list/EF regeneration, audit writer, concurrency and owner-session/Origin integration remain separate adoption gates. Next development can evaluate a concrete customer-owned OAuth provider while those database gates are blocked. No OpenAI/Slack exchange, production endpoint or central infrastructure is introduced here.
