# PostgreSQL

Goblin uses PostgreSQL with certificate authentication and database-first EF Core.
SQL owns the schema; EF generates the C# mappings.

## WSL and Windows pgAdmin

After completing the [local installer](../deploy/local/README.md), run these
commands **inside WSL, as root** (a root shell, not `sudo`), from the repository root:

```bash
cargo build --locked -p goblinctl
export PATH="$PWD/target/debug:$PATH"
apt install -y postgresql-client openssl openssh-client
goblinctl db connect wsl --local --forward-port 55432
goblinctl db export wsl --client windows --role app
```

On Windows, import the generated connection into **pgAdmin**:

1. Open **Tools → Import/Export Servers → Import** and select the full path
   printed after **pgAdmin import**, ending in `wsl\app\pgAdmin.json`.
2. Keep **Remove all the existing servers?** set to **No** and finish the import.
3. Open **Goblin → Goblin - wsl (app)**. No database password is needed.

WSL reaches PostgreSQL at `localhost:5432`; Windows uses `localhost:55432` through
WSL localhost forwarding. Keep WSL running. Choose a forwarding port from 1024 to
65535, other than 5432, that is free in both WSL and Windows. The profile saves the
port and does not change it on its own. The commands check the database identity
and TLS login before writing any files.

### Reconnect or refresh

After restarting WSL or renewing certificates, run this in WSL and reconnect the
saved pgAdmin server:

```bash
goblinctl db export wsl --client windows --role app
```

`export` refreshes certificates and starts forwarding, so an existing profile
does not need a separate `connect`. Import only once per profile and role; each
import adds another server. Certificate paths stay the same. If you change the
forwarding port, export again and update the saved server's port.

### Troubleshooting and other clients

- **Password prompt:** leave it blank, select **Save Password**, and connect.
  Exports include an empty `pgpass.conf` to avoid this prompt.
- **Windows access violation:** set **Properties → Parameters → GSS encmode →
  disable** (add it with **+** if absent). This works around a
  [pgAdmin Windows bug](https://github.com/pgadmin-org/pgadmin4/issues/10445).
  Exports include this setting.
- **Missing root certificate:** use the exported absolute certificate paths and
  keep SSL mode **verify-full**.

For another client, read the export in Windows PowerShell:

```powershell
Get-Content "$env:LOCALAPPDATA\Goblin\postgres\wsl\app\connection.json" |
    ConvertFrom-Json |
    Format-List host, port, database, username, sslmode, gssencmode, sslrootcert, sslcert, sslkey
```

Copy those values into the client's connection and SSL fields; clients do not
read `connection.json` themselves. `sslrootcert` is the CA, `sslcert` the client
certificate, and `sslkey` its private key. Leave the password blank.

## Daily development

These commands refresh the existing profile and its access before they run:

| Command | Purpose / role |
| --- | --- |
| `make dev-start` | Start Goblin with `goblin_app` |
| `make database-migrate` | [Apply SQL](#apply-the-sql-schema) with `goblin_admin` |
| `make database-scaffold` | [Regenerate EF mappings](#reverse-engineer-the-database) with `goblin_app` |
| `make database-test` | [Run database integration tests](#application-configuration-and-verification) with both roles |

`DB_PROFILE` defaults to `wsl`; append `DB_PROFILE=azure-dev` to use another
profile. `make dev-start ARGS=--without-database` starts Goblin without
PostgreSQL and disables Work APIs.

Run other developer commands with `goblinctl db run wsl -- COMMAND`. Add
`--role admin` or `--role both` before `--` only when needed.

## Azure development

[Install Azure manually](../deploy/azure/README.md), then configure SSH authentication
and trust the VM's host key. The SSH user needs passwordless `sudo` for `k3s kubectl`.
With the CLI and packages from [the WSL setup](#wsl-and-windows-pgadmin), run in WSL
with your SSH destination:

```bash
goblinctl db connect azure-dev --ssh developer@your-azure-vm --forward-port 55433
goblinctl db export azure-dev --client windows --role app
```

Import the printed `azure-dev\app\pgAdmin.json` with the
[same pgAdmin steps](#wsl-and-windows-pgadmin). The SSH tunnel runs in WSL on
`localhost:55433` and reaches the VM's `localhost:5432`. Keep WSL running.
For later exports, use `azure-dev` in the [refresh command](#reconnect-or-refresh).

## Profile management and storage

| Command | Effect |
| --- | --- |
| `goblinctl db connect wsl` | Refresh certificates, restore forwarding, and check TLS |
| `goblinctl db refresh wsl --role admin` | Refresh access for the selected role |
| `goblinctl db disconnect wsl` | Stop this profile's listener or SSH tunnel; keep settings and certificates |

Connect and refresh keep the saved port; change it with
`connect --forward-port PORT`. Disconnect does not stop the installation's host
endpoint on 5432. Linux refreshes do not update Windows exports.

| Location | Purpose |
| --- | --- |
| Kubernetes Secrets | Source certificates; the CA signing key stays here |
| `~/.config/goblin/postgres/<profile>/` | Linux profile and client certificates; uses `XDG_CONFIG_HOME` when set |
| `%LOCALAPPDATA%\Goblin\postgres\<profile>\` | Windows exports, one directory per role |
| `/var/lib/goblin/postgres/` | Host forwarding state, without client keys |

Linux directories are 0700 and files 0600; Windows exports get private ACLs for
the current Windows account. Export only the role you need. Profiles pass
connections to child processes as environment variables and do not edit
`appsettings.json`.

A profile refuses to continue if the source, cluster, database volume, or CA
changes. Restore the original installation or use a new profile name. After
[recreating a development installation](../deploy/local/README.md#stop-resume-or-start-clean),
disconnect and remove the old Linux profile and Windows export before reusing
that name.

## Installation and certificates

The installer sets up PostgreSQL, applies migrations, and creates host access;
it exports no client credentials. After k3s and cert-manager are ready, you can
run these commands for repair from a root shell on the database's WSL instance
or Azure VM:

| Command | Purpose |
| --- | --- |
| `goblinctl db setup` | Configure certificates and PostgreSQL in the selected Kubernetes context, then check TLS |
| `goblinctl db host` | Configure the installation's loopback endpoint on 5432 |
| `goblinctl db migrate IMAGE` | Run the schema migration Job with the given Goblin image |

Setup uses `kubectl`, falling back to `k3s kubectl`, and keeps the database
volume, CA, and certificate identities. Host access needs port 5432 to be free or
already owned by Goblin; find out which service owns the port before stopping it.

Resources are defined in [deploy/postgres](../deploy/postgres/):

| Resource | Purpose |
| --- | --- |
| `goblin-postgres-ca` Certificate/Secret | Database CA; signing key stays in Kubernetes |
| `goblin-postgres-server` Certificate | Server identity in `goblin-postgres-tls` |
| `goblin-postgres-app` Certificate | `goblin_app` identity in `goblin-postgres-app-tls` |
| `goblin-postgres-admin` Certificate | `goblin_admin` identity in `goblin-postgres-admin-tls` |
| `goblin-postgres-admin` Opaque Secret | Initialization-only password required by the official image |
| `goblin-postgres-data` PVC | 5 GiB database storage |

Every network connection needs TLS and a trusted client certificate whose common
name matches the role. Initialization clears the bootstrap password; clients use
no password. Inside the container, Unix socket administration is allowed only for
the `postgres` OS user.

`goblin_admin` owns the schema. `goblin_app` can read and write application tables
but cannot create or drop them; default privileges cover future tables and
sequences that the administrator creates. Network policy allows Goblin and
same-namespace pods labeled `goblin-database-access: "true"`; there is no public
database listener.

Back up the CA, the initialization Secret, and database recovery material. Setup
refuses to replace a missing CA while issued TLS Secrets exist, or a missing
initialization Secret while the PVC exists; restore the missing Secrets.
Deleting the namespace or PVC, or losing the VM, can delete the database.

### Certificate renewal

cert-manager renews the 90-day leaf certificates 30 days before expiry and rotates
their keys. PostgreSQL reloads its TLS files, and new Npgsql connections read the
current client files. No rebuild or connection-string change is needed.
Linux profiles refresh on connect and on development commands; Windows needs an
explicit [export](#reconnect-or-refresh).

Monitor readiness and expiry. The CA lasts ten years and keeps its key during
leaf renewal. Its issuer does not provide trust rollover or revocation, so plan
overlapping trust before you replace the CA, and restrict access to Secrets and issuance.

## Schema and EF Core

Goblin tables use singular, unquoted `snake_case` names in the `public` schema and
`PascalCase` C# names with mapping attributes. They use `id bigint` keys and bigint
foreign keys. Identity sequences allocate local IDs; GitHub repository IDs keep their
GitHub values. HTTP exposes bigint IDs as decimal strings. Entity types are singular
and collections plural: `WorkItem` maps to `work_item`, and `WorkItems` is a DbSet.
The Work history sequence `work_event_id_seq` has no table.

The [SQL baseline](../backend/database/migrations/0001_initial.sql) lists every table.
Wolverine tables keep their library names (`wolverine_` prefix) and key types, and
the baseline DDL must match Wolverine's own SQL because startup uses `AutoCreate.None`.
The four OpenIddict tables keep their quoted names and are mapped by
`ChatGptOAuthDbContext`; see [ChatGPT authentication](chatgpt-integration.md). The
`schema_migration` journal is administrator-only. Wolverine, OpenIddict, and the
journal are excluded from product scaffolding.

### Apply the SQL schema

Use `make database-migrate` from [daily development](#daily-development).
[Goblin.Database](../backend/tools/Goblin.Database/) applies
[SQL files](../backend/database/migrations/) in ordinal filename order, once each.
Each file and its journal entry commit in one transaction; failures roll back.
An advisory lock allows one runner at a time, and SHA-256 checksums reject edits
to applied files. New files must sort after the last applied file.

**Before production:** keep all schema changes in `0001_initial.sql` and recreate
disposable development databases when it changes; the checksum check rejects a
database that has an older baseline. Use isolated test databases for verification.
Do not add rename migrations or compatibility views, and do not bypass the journal.
Schema changes never allow deleting Work PVCs or unrelated installation data. After
recreating the database, review the new volume and CA identities, remove the stale
client profiles, and reconnect. **After production:** keep applied files unchanged
and add higher-numbered migrations.

Use LF line endings and plain SQL without `psql` commands or explicit
`BEGIN`/`COMMIT`. Operations that cannot run in a transaction, such as
`CREATE INDEX CONCURRENTLY`, need a separate administrator procedure. Review
destructive SQL and back up before applying it. Web startup never runs migrations;
EF migrations and `EnsureCreated` are not used.

### Reverse engineer the database

Use `make database-scaffold` from [daily development](#daily-development).
[scaffold-database.sh](../backend/scripts/scaffold-database.sh) owns the table
list and EF flags. It restores the pinned local tool and regenerates
[Generated/](../backend/src/Goblin.Persistence/Generated/) with repository
formatting, UTF-8 without BOM, and LF endings.

`Name=ConnectionStrings:Goblin` reads the app connection from the environment;
`--no-onconfiguring` keeps it out of generated code. Do not set
`--use-database-names` (`work_item` must become `WorkItem`), and do not add
`--schema public`, which would include every table.

Keep custom behavior in partial classes **outside** `Generated/`, for example the
filtered unique relationship fixes in `WorkModelConfiguration.cs`. EF design tools
stay in developer tooling; the published migration runner has no EF dependency.

To change the schema:

1. Edit the baseline (or, after production, add a migration) following
   [the migration rules](#apply-the-sql-schema). New tables need primary keys.
2. Update the `--table` list in `scaffold-database.sh` and, if needed, the custom
   names in `backend/tools/Goblin.Database.Scaffolding/GitRepositoryDesignTimeServices.cs`.
   Apply SQL to a fresh disposable database with
   `make database-migrate DB_PROFILE=<profile>`, then run
   `make database-scaffold DB_PROFILE=<profile>`.
3. Remove generated entities for dropped or renamed tables, then run
   `dotnet build backend/Goblin.slnx` and the [database tests](#application-configuration-and-verification).
4. Review SQL, table list, and generated C# together. A second scaffold against
   the same database must produce no further diff.

### Workspace storage boundary

PostgreSQL stores Work conversations, lifecycle history, Git commit metadata, and
inspection records. Workspace files stay on the Work PVC; `workspace_checkpoint`
holds no file content or archive. Never store workspace archives in PostgreSQL.
See [workspace lifecycle](workspace-lifecycle.md).

## Application configuration and verification

[appsettings.json](../backend/src/Goblin.Web/appsettings.json) holds the deployed
connection: `goblin-postgres:5432`, database `goblin`, role `goblin_app`,
`SSL Mode=VerifyFull`, and `GSS Encryption Mode=Disable`, with certificate files in
`/etc/goblin-postgres/{ca.crt,tls.crt,tls.key}`. Kubernetes mounts only the
`goblin-postgres-app-tls` Secret there, read-only. No password or private key is in
the image, and administrator and CA keys stay out of
[repository sandboxes](execution-hosting.md). Rebuild and redeploy when the packaged
connection settings change.

Direct development receives the app connection through `ConnectionStrings__Goblin`.
Durable Work needs the migrated schema, including Wolverine tables. The installer
runs `goblinctl db migrate IMAGE` with only the admin certificate and stops on
failure before deploying Goblin. When Work is enabled, `/readyz` checks the database.

Run `make database-test` against a development profile. The tests create and drop
uniquely named databases and cover EF access, role permissions, migrations and
checksums, messaging storage, and TLS certificate rules. Without the test
connections, database tests skip and ordinary tests need no server.

To run HTTP and browser journeys against a migrated, disposable development database:

```bash
goblinctl db run wsl --usage tests -- make node-test
goblinctl db run wsl --usage tests -- make browser-test ARGS=tests/e2e/work.spec.ts
```

These journeys write Work records to the selected database.

### Manual regression after development recreation

After recreating the development database, applying the baseline, and deploying
the changed build, check readiness and repeat these journeys with neutral test
data. Keep Work PVCs.

- **Slack:** connect or relink the installation, link an external identity, submit
  a message in a linked thread, and confirm Work and replies appear once. Reply
  in that thread and confirm it reaches the existing conversation.
- **GitHub:** connect the account, select an accessible test repository, complete
  repository setup and approval, and confirm a Work repository operation completes
  with its recorded result.
- **ChatGPT:** connect the ChatGPT/Codex account, select a model, submit Work,
  answer an attention request, and confirm completion and history. Restart
  Goblin during queued Work and confirm recovery; reconcile uncertain execution
  before retrying failed Work.
