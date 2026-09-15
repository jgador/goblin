# PostgreSQL and database-first EF Core

SQL files define Goblin's database. EF Core reverse engineers that database into
checked-in C# classes. PostgreSQL uses unquoted `snake_case` names; C# uses
`PascalCase`, with `[Table]` and `[Column]` attributes preserving the mapping.

The initial table is deliberately small: `goblin.work_items` has a UUID `id` and
required text `objective`. Callers assign the UUID. This establishes persistence;
the Work UI still uses its existing preview data.

## Files and tools

| Location | Purpose |
| --- | --- |
| `deploy/postgres/` | PostgreSQL 16.15, persistent volume, internal Service, initialization, and setup script |
| `backend/database/migrations/` | Ordered, immutable SQL schema changes |
| `backend/src/Goblin.Persistence/Generated/` | Reverse-engineered context and entities; regenerated, not hand-edited |
| `backend/src/Goblin.Persistence/PersistenceServices.cs` | Runtime `DbContext` registration |
| `backend/tools/Goblin.Database/` | SQL migration runner and isolated startup host for `dotnet ef` |
| `backend/src/Goblin.Web/appsettings.json` | Actual backend configuration, using the Kubernetes Service address |
| `backend/tools/Goblin.Database/appsettings.json` | Actual tooling configuration, including administrator access through a port-forward |
| `deploy/postgres/write-appsettings.py` | Writes matching connection strings from the database initialization credentials |
| `backend/scripts/scaffold-database.sh` | Repeatable reverse-engineering command |
| `.config/dotnet-tools.json` | Repository-local `dotnet-ef` version |

Use the repository's .NET 10 SDK, Bash, Python 3, and a configured `kubectl` (or
`k3s kubectl`). EF Core and `dotnet-ef` are pinned to 10.0.12; the Npgsql EF provider
is pinned to 10.0.3. Run the commands below from the repository root.

## Set up PostgreSQL in k3s

```bash
bash deploy/postgres/setup.sh
```

On a VM whose kubeconfig requires root access, use `sudo bash` instead. The script
uses the selected kubectl context, falling back to `k3s kubectl` when needed. It
creates the `goblin` namespace and two Secrets, then applies the PostgreSQL
resources and waits for readiness:

- `goblin-postgres-admin`: initialization and schema administrator password.
- `goblin-postgres-app`: application role's initialization password.

For a new database, setup uses the application password already present in
`backend/src/Goblin.Web/appsettings.json` and generates a separate administrator
password. Both are sent directly to Kubernetes. Setup then creates or updates
the two real `appsettings.json` files with those passwords. It writes
the web connection using `goblin-postgres:5432` and the tooling connections using
`localhost:5432` for port-forwarding. Other JSON settings are preserved. No
example files or manual placeholder replacement are needed.

Rerunning setup preserves both Secrets and the PVC and refreshes the connection
strings from the existing credentials. If a Secret is missing while the PVC
exists, setup stops so that an unrelated replacement password cannot hide the
problem. These Secrets initialize PostgreSQL. The backend's database configuration
is packaged in its own `appsettings.json`.

The first initialization creates database `goblin`, schema `goblin`, and two
roles. `goblin_admin` owns the database and applies schema changes. `goblin_app`
can connect and read/write application tables, but cannot create or drop tables.
Default privileges give it access to future tables and sequences created by
`goblin_admin` in the `goblin` schema. Apply migrations using that administrator
so the ownership and default privileges remain consistent.

PostgreSQL has an internal headless Service and an independently declared 5 GiB
PVC, `goblin-postgres-data`. A network policy permits the Goblin application and
same-namespace pods labeled `goblin-database-access: "true"` to connect. There is
no public database listener. Namespace/PVC deletion still deletes storage; keep
backups for VM or disk loss.

The image's initialization script runs only for an empty data directory. Changing
a Secret or the initialization script does **not** update existing PostgreSQL
passwords or roles. Coordinate password rotation with `ALTER ROLE` and update
the initialization Secret's `password` and the connection string in your private
`appsettings.json`. Rebuild and redeploy the backend image to use the new password.
Keep the administrator and application credentials in backups alongside the
database recovery procedure.

This is an explicit setup step for existing or new Goblin installations; the
Azure installer does not automatically provision PostgreSQL yet.

## Configure appsettings.json

There is one `appsettings.json` format, without environment-specific variants.
The web app reads `ConnectionStrings:Goblin`; the database tooling also reads
`ConnectionStrings:GoblinAdmin` when applying SQL. The administrator connection
belongs only in the tooling configuration.

The backend file is `backend/src/Goblin.Web/appsettings.json`. Its complete
connection string includes `Host=goblin-postgres;Port=5432;Database=goblin;Username=goblin_app`
and a concrete initialization password. Setup uses that password when creating
the application role's Secret for a new database. For an existing installation,
the existing database credentials take precedence and setup refreshes the file
from `goblin-postgres-app`; it does not rotate a running database's password.

The build copies this file into the backend image at `/app/appsettings.json`.
The backend reads it through standard .NET configuration; it needs no database
Secret mount. The web file is eligible for Git and includes the application
password. The database tooling's file, including administrator credentials,
remains ignored and is populated by setup. Repository and image access therefore
also grants access to the recorded application credential.

The [secret scanner](secret-scanning.md#handling-a-finding) allows this specific
connection entry. It continues checking other values in the file and all
administrator and provider credentials.

`localhost` refers to the pod making the connection, even when both pods run on
one VM. Use the Service name `goblin-postgres` from the same namespace, or
`goblin-postgres.goblin.svc.cluster.local` from another namespace. A client in
another namespace also needs an explicit network-policy allowance. The Service
name remains stable when PostgreSQL's pod IP changes.

For commands run on your machine or on the VM host, `localhost` works through a
port-forward to that same deployed database. This does not require a second
environment or a second PostgreSQL instance.

### Connect from outside the cluster

Keep this running in a separate terminal with access to the cluster:

```bash
kubectl -n goblin port-forward service/goblin-postgres 5432:5432
```

Setup already configured the tooling file for this port-forward. To refresh
only the configuration files from an existing cluster:

```bash
kubectl -n goblin get secret goblin-postgres-app goblin-postgres-admin -o json | \
  python3 deploy/postgres/write-appsettings.py
```

The web file is included in publish output and Docker builds; the tooling file
containing administrator credentials stays outside the backend image and is
excluded from its own publish output. Keep the configured image and image archive
private, since they contain the application password.

Local builds copy each file beside its executable so `dotnet run`, direct DLL
execution, and `dotnet ef` resolve them independently of the shell's working
directory. If running the web app outside Kubernetes, change its host to
`localhost` while the port-forward is active. Use `goblin-postgres` when building
the image for k3s. Rebuild after editing a file.

No connection-string environment variables are needed. If you previously set
`ConnectionStrings__Goblin` or `ConnectionStrings__GoblinAdmin`, unset them so
.NET's environment-variable provider does not override your local JSON values.

### Deploy the configured backend

Build from the checkout containing your web `appsettings.json`, then import and
deploy that image using the [Agent Sandbox deployment steps](authentication-preview.md#run-in-the-provisioned-agent-sandbox-cluster).
Use a new image tag for each build and update the Sandbox's container image to
that tag before replacing its pod. The file is part of the image, so changing
the connection string requires another build and deployment.

The Azure installer includes the web `appsettings.json` from its selected source
revision. PostgreSQL setup is still a separate step. If setup refreshes the web
file to match an existing installation's credentials, rebuild and deploy from
that updated checkout before using the database.

## Apply the SQL schema

```bash
dotnet run --project backend/tools/Goblin.Database -- apply backend/database/migrations
```

The runner applies `.sql` files in ordinal filename order, once each. Every file
and its journal entry commit in one transaction. Failed files roll back and can
be retried. A PostgreSQL advisory lock serializes concurrent runners.

The journal lives in `goblin_meta.schema_migrations`, outside the application
schema, so it is not included in reverse engineering. Recorded SHA-256 checksums
reject changes to previously applied scripts. Keep their filenames and contents
unchanged; add a new, higher-numbered file for every change. SQL files use LF line
endings so checksums remain consistent across operating systems.

Scripts contain ordinary PostgreSQL SQL, without `psql` commands or explicit
`BEGIN`/`COMMIT`. Operations that cannot run in a transaction, such as
`CREATE INDEX CONCURRENTLY`, need a separately planned administrative procedure.
The runner never applies schema changes during web application startup.

## Reverse engineer the database

The recommended command is:

```bash
bash backend/scripts/scaffold-database.sh
```

It restores the local tool, regenerates the whole `goblin` schema, and normalizes
generated C# files to UTF-8 without a BOM and LF line endings. It reads
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
  --schema goblin \
  --data-annotations \
  --no-onconfiguring \
  --force
```

`Name=ConnectionStrings:Goblin` resolves the JSON configuration in the tooling
host, keeping the password out of command-line arguments.
`--data-annotations` requests mapping attributes. Leave `--use-database-names`
unset so that `work_items` becomes `WorkItem` and `objective` becomes `Objective`.
`--no-onconfiguring` keeps the connection string out of generated source.

For example, the generated entity contains:

```csharp
[Table("work_items", Schema = "goblin")]
public partial class WorkItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("objective")]
    public string Objective { get; set; } = null!;
}
```

EF retains Fluent API configuration for features attributes cannot fully express,
including database key constraint names and value-generation behavior. Both the
context and entities are generated. Put custom behavior in partial classes
**outside** `Generated/`; do not add it to files overwritten by `--force`.

## Add another table later

1. Add the next SQL file, for example
   `backend/database/migrations/0002_work_notes.sql`:

   ```sql
   CREATE TABLE goblin.work_notes (
       id uuid PRIMARY KEY,
       work_item_id uuid NOT NULL REFERENCES goblin.work_items (id),
       body text NOT NULL
   );
   ```

2. Apply the SQL and reverse engineer again:

   ```bash
   dotnet run --project backend/tools/Goblin.Database -- apply backend/database/migrations
   bash backend/scripts/scaffold-database.sh
   dotnet build backend/Goblin.slnx
   ```

3. Review the new `WorkNote` entity, the context, and relationship changes to
   `WorkItem`. Commit the SQL and generated C# together. The command selects the
   entire `goblin` schema, so adding a table needs no scaffolding-script change.

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

`Goblin.Web` registers `GoblinDbContext` when `ConnectionStrings:Goblin` is set.
It reads the JSON copied from `backend/src/Goblin.Web/appsettings.json`, next to
the executable both locally and in the published image. Rebuild and redeploy
the backend after changing this file. Only application credentials belong in
the web configuration. Codex still runs in that same pod and OS user, so this file
is not an isolation boundary from the
agent process; separating execution into its own Sandbox remains future work.

The authentication and Work previews continue to start without a configured
database. There are no new Work API endpoints in this increment.

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
Without these explicit test variables, database integration tests are skipped
and the normal solution tests need no PostgreSQL server.

See [EF Core reverse engineering](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/)
for the underlying CLI options.
