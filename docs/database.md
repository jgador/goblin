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

## Set up certificate authentication in k3s

After the Goblin bundle has installed k3s and cert-manager, run from this checkout:

```bash
bash deploy/postgres/setup.sh
```

Use `sudo bash` if the kubeconfig requires root. The script uses the selected
`kubectl` context, falling back to `k3s kubectl`. It issues certificates,
configures PostgreSQL, verifies a real TLS client login, and connects an existing
Goblin Sandbox. If the Sandbox
configuration changes, setup recreates its pod to load the certificate mount and
connection string; its image, public origin, login, and storage are preserved.
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

### Existing password-based installations

Setup retains the existing PVC and initialization Secret, installs certificate
authentication, then restarts PostgreSQL with the new configuration. Existing
data and role privileges are preserved. Network password authentication is
disabled even if old password hashes and Secrets still exist. Update any other
database clients to certificates before this restart.

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

For an existing image, setup sets the same certificate connection through
`ConnectionStrings__Goblin` in the Sandbox so it takes effect without rebuilding.
New images carry the certificate configuration in `appsettings.json`. If you
change that file later, rerun setup to refresh the deployed override.

## Database tooling outside Kubernetes

Setup exports only the two client certificates and their public CA to
`.goblin-postgres/app/` and `.goblin-postgres/admin/`. Directories are mode 0700
and files mode 0600, excluded from Git and Docker. These private keys are login
credentials. The CA signing key is not exported.

It writes `backend/tools/Goblin.Database/appsettings.json` with the local
certificate paths and `Host=localhost`. The server certificate includes
`localhost` and loopback IPs for verified local connections.

For the [local WSL runner](../deploy/local/README.md), setup automatically provides
`localhost:55432` through a persistent loopback TCP proxy. It does not require a
terminal or `kubectl port-forward`, and it resumes with the local installation.
For an existing database, enable or repair it with:

```bash
npm run install:local -- database
```

In VS Code's WSL window, use:

```text
postgresql://goblin_app@localhost:55432/goblin?sslmode=verify-full&sslrootcert=/root/repos/goblin/.goblin-postgres/app/ca.crt&sslcert=/root/repos/goblin/.goblin-postgres/app/tls.crt&sslkey=/root/repos/goblin/.goblin-postgres/app/tls.key
```

Adjust the certificate paths if your checkout is elsewhere. Use `goblin_admin`
and the `admin` certificate directory for schema administration.

For other clusters, establish a tunnel from your machine. A temporary connection
can use this command in a separate terminal:

```bash
kubectl -n goblin port-forward service/goblin-postgres 5432:5432
```

For another local port, use matching setup/tunnel ports:

```bash
bash deploy/postgres/setup.sh --port 55432
kubectl -n goblin port-forward service/goblin-postgres 55432:5432
```

To refresh local certificates/configuration without restarting any workloads:

```bash
kubectl -n goblin get secret goblin-postgres-app-tls goblin-postgres-admin-tls -o json | \
  python3 deploy/postgres/write-appsettings.py --port 55432
```

Local builds copy settings beside the executable. For a web process outside
Kubernetes, use the tooling's `Goblin` connection (local host/port and absolute
client certificate paths); `goblin-postgres` resolves inside Kubernetes.
Remove stale connection environment overrides before running tooling, since
.NET environment configuration takes precedence over JSON.

## Certificate renewal

Leaf certificates last 90 days and cert-manager renews them 30 days before expiry,
rotating their keys. Full Secret directory mounts receive the renewed files.
PostgreSQL watches the projected Secret and reloads TLS configuration; new Npgsql
connections load the current client files. Existing pooled connections remain
usable. No connection-string change or application rebuild is required.

Exported host-side files are snapshots: refresh them with the command above
after renewal. Monitor Certificate readiness and expiry. The CA has a ten-year
lifetime and retains its key during routine renewal. A CA issuer does not manage
a complete trust rollover or certificate revocation workflow; plan CA replacement
and distribution of overlapping trust before expiry, and protect permissions
to issue certificates and read Secrets in the `goblin` namespace.

## Files and tools

| Location | Purpose |
| --- | --- |
| `deploy/postgres/` | PostgreSQL 16.15, certificate resources, TLS/auth configuration, setup/export scripts |
| `backend/database/migrations/` | Unreleased initial baseline; ordered, immutable migrations after deployment |
| `backend/src/Goblin.Persistence/Generated/` | Reverse-engineered context and entities; regenerated, not hand-edited |
| `backend/src/Goblin.Persistence/PersistenceServices.cs` | Runtime `IDbContextFactory<GoblinDbContext>` registration |
| `backend/tools/Goblin.Database/` | SQL migration runner and isolated host for dotnet ef |
| `backend/scripts/scaffold-database.sh` | Repeatable reverse-engineering command |

Use .NET 10, Bash, Python 3, and a configured kubectl. EF Core and dotnet-ef are
pinned to 10.0.12; the Npgsql EF provider is pinned to 10.0.3.

## Apply the SQL schema

The baseline `0001_initial.sql` creates application and Wolverine tables directly
in `public` on an empty database. Database initialization configures application
permissions and default privileges; the runner creates the protected migration
journal in `public` before applying the baseline.

```bash
dotnet run --project backend/tools/Goblin.Database -- apply backend/database/migrations
```

The runner applies `.sql` files in ordinal filename order, once each. Every file
and its journal entry commit in one transaction. Failed files roll back and can
be retried. A PostgreSQL advisory lock serializes concurrent runners.

The journal lives in `public.schema_migrations`. Only the schema administrator
can access it, and it is excluded from reverse engineering. Recorded SHA-256 checksums
reject changes to previously applied scripts. Before the first real deployment,
consolidate schema changes into `0001_initial.sql`; local development provisioning
does not freeze the baseline. An existing development database must be recreated
if disposable, or its schema must be verified against the consolidated baseline
before reconciling its migration journal. The runner's checksum checks still apply.

After deployment, keep applied filenames and contents unchanged and add a new,
higher-numbered file for every change. SQL files use LF line endings so checksums
remain consistent across operating systems.

Scripts contain ordinary PostgreSQL SQL, without `psql` commands or explicit
`BEGIN`/`COMMIT`. Operations that cannot run in a transaction, such as
`CREATE INDEX CONCURRENTLY`, need a separately planned administrative procedure.
The runner never applies schema changes during web application startup.

## Reverse engineer the database

The recommended command is:

```bash
bash backend/scripts/scaffold-database.sh
```

It restores the local tool, regenerates the selected application tables, normalizes
generated C# files to UTF-8 without a BOM and LF line endings, and applies the
repository's whitespace and style rules, including regular constructors. It reads
`ConnectionStrings:Goblin` from `backend/tools/Goblin.Database/appsettings.json`;
ordinary application access is enough to inspect the mapped schema. It starts
neither the web server nor Codex.

The equivalent `dotnet ef` invocation is:

```bash
dotnet tool restore
dotnet ef dbcontext scaffold Name=ConnectionStrings:Goblin Npgsql.EntityFrameworkCore.PostgreSQL \
  --project backend/src/Goblin.Persistence \
  --startup-project backend/tools/Goblin.Database \
  --context GoblinDbContext \
  --context-dir Generated \
  --output-dir Generated/Entities \
  --context-namespace Goblin.Persistence \
  --namespace Goblin.Persistence.Entities \
  --table public.agents \
  --table public.connections \
  --table public.conversation_messages \
  --table public.conversations \
  --table public.execution_attempts \
  --table public.work_commands \
  --table public.work_items \
  --data-annotations \
  --no-onconfiguring \
  --force
```

`Name=ConnectionStrings:Goblin` resolves the JSON configuration in the tooling
host, keeping connection configuration out of command-line arguments.
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

1. Add the next SQL file, for example
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
   dotnet run --project backend/tools/Goblin.Database -- apply backend/database/migrations
   bash backend/scripts/scaffold-database.sh
   dotnet build backend/Goblin.slnx
   ```

3. Review the new `WorkNote` entity, the context, and relationship changes to
   `WorkItem`. Commit the SQL, scaffolding table list, and generated C# together.

With one deployed database, applying SQL through the port-forward changes that
database immediately; scaffolding only reads its schema. Review SQL before
applying it and keep a backup before destructive changes. EF migrations and
`EnsureCreated` are not part of this workflow.

Scaffolding overwrites matching files but does not remove old entity files after
a table is dropped or renamed. Remove those obsolete generated files explicitly
and build again. New tables need primary keys for normal tracked EF writes.

To check reproducibility, scaffold twice against an unchanged database. The
second pass should produce no additional diff in `Generated/`.

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
connection-only development session without PostgreSQL, explicitly set
`GOBLIN_WORK_ENABLED=false`. This disables Work APIs. Repository execution uses
[separate sandboxes](execution-hosting.md) without database credentials.

Run the real PostgreSQL integration tests explicitly using the tooling's JSON
configuration (keep the port-forward running):

```bash
python3 - <<'PY'
import json
import os
from pathlib import Path
import subprocess

settings = json.loads(Path("backend/tools/Goblin.Database/appsettings.json").read_text())
connections = settings["ConnectionStrings"]
env = {**os.environ, "GOBLIN_TEST_POSTGRES_ADMIN": connections["GoblinAdmin"],
       "GOBLIN_TEST_POSTGRES_APP": connections["Goblin"]}
raise SystemExit(subprocess.call(["dotnet", "test", "backend/tests/Goblin.Persistence.Tests"], env=env))
PY
```

The tests create and drop uniquely named databases. They cover EF insert/read/
update/delete across contexts, application permission boundaries, repeatable
schema application, edited-script rejection, and rollback after failed DDL.
The initial-schema checks cover messaging storage and sequences, foreign keys,
and the connection reservation held by an attempt awaiting cleanup.
With a certificate connection, they also verify the authenticated identity and
reject missing client certificates, administrator impersonation, unencrypted
connections, an untrusted server CA, and an incorrect server hostname.
Some client resets can cause this k3s version's `kubectl port-forward` to exit.
The local runner's persistent endpoint avoids that path; use it for VS Code and
these tests, or use a stable tunnel to another cluster.
Without these explicit test variables, database integration tests are skipped
and the normal solution tests need no PostgreSQL server.

See [EF Core reverse engineering](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/)
for the underlying CLI options.
