# PostgreSQL and database-first EF Core

SQL files define Goblin's database. EF Core reverse engineers that database into
checked-in C# classes. PostgreSQL uses unquoted `snake_case` names; C# uses
`PascalCase`, with attributes preserving the mapping. Work, conversations, connections, attempts, and command receipts are durable.
All Goblin-owned tables use `id bigint`, including the migration journal,
repository operations, workspace checkpoints, inspection sessions, and setup
memories. Their foreign keys use `bigint` too. PostgreSQL identity sequences
allocate local IDs; GitHub repository IDs retain their upstream bigint values.
Public HTTP represents bigint IDs as decimal strings to preserve JavaScript
precision. Inspection requests reserve their ID before posting, and isolated
workers reserve repository-operation IDs through the broker before submission.
Replaying a request retains its original ID.

All tables use PostgreSQL's default `public` schema. Wolverine's inbox/outbox
tables keep their `wolverine_` prefix, and `public.schema_migrations` records
schema history. EF scaffolding selects application tables explicitly.
Wolverine-owned key types are dictated by the pinned messaging library and are
the exception to Goblin's bigint convention.

For daily WSL/Azure connections, Windows exports and development commands, use
[the PostgreSQL workflow](postgres-workflow.md).

## Set up certificate authentication in k3s

After the Goblin bundle has installed k3s and cert-manager, run from this checkout:

```bash
bash deploy/postgres/setup.sh
```

Use `sudo bash` if the kubeconfig requires root. The script uses the selected
`kubectl` context, falling back to `k3s kubectl`. It issues certificates,
configures PostgreSQL, and verifies a real TLS client login. The application
deployment mounts its client certificate and uses packaged connection settings.
Rerunning setup preserves the database volume, CA, and existing certificate identities.

The application installer now invokes this setup and the migration job before
deploying Goblin. Running setup directly remains useful for development and
repair. The same setup works in the local WSL k3s cluster and Azure.
It does not configure a PostgreSQL instance installed directly on the host.

Setup creates the following namespaced resources:

| Resource | Purpose |
| --- | --- |
| `goblin-postgres-ca` Certificate/Secret | Private CA for this database; its key stays in Kubernetes |
| `goblin-postgres-server` Certificate | Server identity, stored in `goblin-postgres-tls` |
| `goblin-postgres-app` Certificate | Client identity `goblin_app`, stored in `goblin-postgres-app-tls` |
| `goblin-postgres-admin` Certificate | Client identity `goblin_admin`, stored in `goblin-postgres-admin-tls` |
| `goblin-postgres-admin` Opaque Secret | Initialization-only password required by the official image; never used in client connection strings |
| `goblin-postgres-data` PVC | Independently declared 5 GiB database storage |

The official image requires an initialization password before running its init
scripts. Setup generates this privately once; initialization clears the database
role's password and creates the application role without one. Neither the app nor
the operator needs to know or supply this bootstrap value.

The PostgreSQL configuration requires TLS and a trusted client certificate for
every network connection. The certificate's common name must match the requested
database role. Password-only and unencrypted connections are rejected. Local Unix
socket administration is restricted to the `postgres` OS user inside the database
container through peer authentication.

`goblin_admin` owns the database/schema and applies schema changes.
`goblin_app` can read/write application tables, but cannot create or drop them.
Default privileges cover future tables and sequences created by `goblin_admin`.
The internal Service and network policy permit Goblin and same-namespace pods
labeled `goblin-database-access: "true"`; no public database listener is created.

### Development installation lifecycle

Goblin is preproduction. Recreate development installations when the database
schema or installation format changes. Support only the current certificate-based
configuration; new formats do not require an in-place conversion path.

Setup refuses to replace a missing CA when issued TLS Secrets exist, or a missing
initialization Secret when the PVC exists. Restore those Secrets from backup.
Keep the CA and database recovery material backed up; deleting the namespace/PVC
or losing the VM can delete the database.

## Password-free connection strings

The checked-in `backend/src/Goblin.Web/appsettings.json` contains this Npgsql
connection string (shown on separate lines for readability):

```text
Host=goblin-postgres;Port=5432;Database=goblin;Username=goblin_app;
SSL Mode=VerifyFull;
GSS Encryption Mode=Disable;
Root Certificate=/etc/goblin-postgres/ca.crt;
SSL Certificate=/etc/goblin-postgres/tls.crt;
SSL Key=/etc/goblin-postgres/tls.key
```

`VerifyFull` checks both the server's CA and its hostname. GSS encryption is
disabled so this connection uses TLS without requiring Kerberos libraries.
Kubernetes mounts only
the application certificate/key into Goblin at those stable paths, using a
read-only Secret volume accessible to the application's group. The administrator
and CA private keys are never mounted into the application.

The connection string has no password, and no private key is copied into the
image. Npgsql supports these settings directly; no custom certificate validation
or authentication callback is used by the application. The application certificate mount is required by the deployed durable Work
configuration; repository agent sandboxes never receive this mount.

Images carry the certificate configuration in `appsettings.json`. Rebuild and
redeploy the application when that packaged configuration changes.

## Database tooling outside Kubernetes

Named user profiles hold client certificates outside the checkout. Development,
migrations, scaffolding and opt-in tests receive the selected connection through
child-process environment variables. The installer exports no client credentials.
WSL and Azure host endpoints use port 5432; additional forwarding runs inside WSL.
See [the workflow guide](postgres-workflow.md) for commands and Windows paths.

## Certificate renewal

Leaf certificates last 90 days and cert-manager renews them 30 days before expiry,
rotating their keys. Full Secret directory mounts receive the renewed files.
PostgreSQL watches the projected Secret and reloads TLS configuration; new Npgsql
connections load the current client files. Existing pooled connections remain
usable. No connection-string change or application rebuild is required.

Linux profiles refresh on connect and before development commands. Windows exports
are snapshots: rerun `goblinctl db export PROFILE --client windows --role app`
after renewal. Monitor Certificate readiness and expiry. The CA has a ten-year
lifetime and retains its key during routine renewal. A CA issuer does not manage
a complete trust rollover or certificate revocation workflow; plan CA replacement
and distribution of overlapping trust before expiry, and protect permissions
to issue certificates and read Secrets in the `goblin` namespace.

## Files and tools

| Location | Purpose |
| --- | --- |
| `deploy/postgres/` | PostgreSQL 18.6, certificate resources, TLS/auth configuration, setup and verification scripts |
| `backend/database/migrations/` | Unreleased initial baseline; ordered, immutable migrations after production deployment |
| `backend/src/Goblin.Persistence/Generated/` | Reverse-engineered context and entities; regenerated, not hand-edited |
| `backend/src/Goblin.Persistence/PersistenceServices.cs` | Runtime `IDbContextFactory<GoblinDbContext>` registration |
| `backend/tools/Goblin.Database/` | SQL migration runner using Npgsql and administrator configuration |
| `backend/tools/Goblin.Database.Scaffolding/` | Developer-only host and naming services for dotnet ef |
| `backend/scripts/scaffold-database.sh` | Repeatable reverse-engineering command |

Use .NET 10, Bash, goblinctl, and a configured kubectl. EF Core and dotnet-ef are
pinned to 10.0.12; the Npgsql EF provider is pinned to 10.0.3.

## Apply the SQL schema

The baseline `0001_initial.sql` creates application and Wolverine tables directly
in `public` on an empty database. Database initialization configures application
permissions and default privileges; the runner creates the protected migration
journal in `public` before applying the baseline.

```bash
make db-migrate DB_PROFILE=wsl
```

The runner applies `.sql` files in ordinal filename order, once each. Every file
and its journal entry commit in one transaction. Failed files roll back and can
be retried. A PostgreSQL advisory lock serializes concurrent runners.

The journal lives in `public.schema_migrations`. Only the schema administrator
can access it, and it is excluded from reverse engineering. Recorded SHA-256 checksums
reject changes to previously applied scripts. Before the first production deployment,
consolidate schema changes into `0001_initial.sql`; local development provisioning
does not freeze the baseline. Recreate the development database when that baseline
changes. The runner's checksum checks still apply.

After production deployment, keep applied filenames and contents unchanged and add a new,
higher-numbered file for every change. SQL files use LF line endings so checksums
remain consistent across operating systems.

Scripts contain ordinary PostgreSQL SQL, without `psql` commands or explicit
`BEGIN`/`COMMIT`. Operations that cannot run in a transaction, such as
`CREATE INDEX CONCURRENTLY`, need a separately planned administrative procedure.
The runner never applies schema changes during web application startup.

## Reverse engineer the database

The recommended command is:

```bash
make db-scaffold DB_PROFILE=wsl
```

It restores the local tool, regenerates the selected application tables, normalizes
generated C# files to UTF-8 without a BOM and LF line endings, and applies the
repository's whitespace and style rules, including regular constructors. The selected
profile supplies the app connection through the environment; ordinary application
access is enough to inspect the mapped schema. Docker publishes only the migration
runner, which has no EF or application-model dependency.
Scaffolding services and their tests live in separate projects so the scaffolding
host's dependency graph does not enter application and persistence test builds.

The script owns the complete table selection and EF flags. Its
`Name=ConnectionStrings:Goblin` argument resolves the profile connection from the
tooling host's environment, keeping it out of the command-line arguments.
`--data-annotations` requests mapping attributes. Leave `--use-database-names`
unset so that `work_items` becomes `WorkItem` and `objective` becomes `Objective`.
`--no-onconfiguring` keeps the connection string out of generated source.
The table list excludes Wolverine and the migration journal. Do not combine it
with `--schema public`: EF includes every table in any selected schema.

For example, the generated entity contains:

```csharp
[Table("work_items")]
public partial class WorkItem
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("objective")]
    public string Objective { get; set; } = null!;
}
```

Npgsql treats `public` as the default schema, so generated attributes omit the
schema name. EF retains Fluent API configuration for features attributes cannot fully express,
including database key constraint names and value-generation behavior. Both the
context and entities are generated. Put custom behavior in partial classes
**outside** `Generated/`; do not add it to files overwritten by `--force`.

## Add another table later

1. Before the first production deployment, add the table to `0001_initial.sql`.
   After production deployment, put it in the next migration, for example
   `backend/database/migrations/0002_work_notes.sql`:

   ```sql
   CREATE TABLE public.work_notes (
       id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
       work_item_id bigint NOT NULL REFERENCES public.work_items (id),
       body text NOT NULL
   );
   ```

2. Add `--table public.work_notes` to `backend/scripts/scaffold-database.sh`, then
   apply the SQL and reverse engineer again:

   ```bash
   make db-migrate DB_PROFILE=wsl
   make db-scaffold DB_PROFILE=wsl
   dotnet build backend/Goblin.slnx
   ```

3. Review the new `WorkNote` entity, the context, and relationship changes to
   `WorkItem`. Commit the SQL, scaffolding table list, and generated C# together.

Applying SQL changes the selected profile's database immediately; scaffolding
only reads its schema. Review SQL before
applying it and keep a backup before destructive changes. EF migrations and
`EnsureCreated` are not part of this workflow.

Scaffolding overwrites matching files but does not remove old entity files after
a table is dropped or renamed. Remove those obsolete generated files explicitly
and build again. New tables need primary keys for normal tracked EF writes.

To check reproducibility, scaffold twice against an unchanged database. The
second pass should produce no additional diff in `Generated/`.

## Workspace storage boundary

PostgreSQL stores Work conversations, lifecycle history, Git commit metadata, and
inspection records. `workspace_checkpoints` contains no filesystem payload or
archive digest. Workspace files stay on the Work PVC, and inspection reads that
volume. Never add workspace archive bytes to PostgreSQL. See
[workspace lifecycle](workspace-lifecycle.md).

## Application configuration and verification

`Goblin.Web` uses the application connection from its published configuration or
`ConnectionStrings__Goblin`. Only application credentials belong there. Durable
Work requires PostgreSQL and the committed migrations, including Wolverine's
tables in `public`. Automatic schema creation is disabled.

The installer runs `bash deploy/postgres/migrate.sh IMAGE` before deploying a new
application image. That job mounts only the schema administrator's client
certificate and stops on failure. Direct development uses the migration command
above. Database-first EF mappings include a separate partial configuration for
the filtered active-attempt relationship; do not edit generated classes manually.

`/readyz` checks the enabled database dependency independently of Codex. For a
connection-only development session without PostgreSQL, use
`make dev ARGS=--without-database` (or set `GOBLIN_WORK_ENABLED=false`). This disables Work APIs. Repository execution uses
[separate sandboxes](execution-hosting.md) without database credentials.

Run the real PostgreSQL integration tests explicitly using a development profile:

```bash
make test-postgres DB_PROFILE=wsl
```

The tests create and drop uniquely named databases. They cover EF insert/read/
update/delete across contexts, application permission boundaries, repeatable
schema application, edited-script rejection, and rollback after failed DDL.
The initial-schema checks cover messaging storage and sequences, foreign keys,
and the connection reservation held by an attempt awaiting cleanup.
With a certificate connection, they also verify the authenticated identity and
reject missing client certificates, administrator impersonation, unencrypted
connections, an untrusted server CA, and an incorrect server hostname.
Profile forwarding uses the stable host endpoint and SSH for Azure.
Without explicit test configuration, database integration tests are skipped
and the normal solution tests need no PostgreSQL server.

See [EF Core reverse engineering](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/)
for the underlying CLI options.
