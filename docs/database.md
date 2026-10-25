# PostgreSQL

Goblin uses PostgreSQL with certificate authentication and database-first EF Core.
SQL owns the schema; EF generates the C# mappings.

## WSL and Windows pgAdmin

After completing the [local installer](../deploy/local/README.md), run these
commands **inside WSL, as root**, from the repository root:

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

PostgreSQL's WSL host endpoint is `localhost:5432`; Windows uses
`localhost:55432` through WSL localhost forwarding. Keep WSL running. Choose a
port free in both WSL and Windows; the profile saves it and never silently changes it.
The commands verify the database identity and TLS login before publishing files.

### Reconnect or refresh

After restarting WSL or renewing certificates, run this in WSL and reconnect the
saved pgAdmin server:

```bash
goblinctl db export wsl --client windows --role app
```

`export` refreshes certificates and starts forwarding, so an existing profile
needs no separate `connect`. Import only once per profile and role; repeated
imports add duplicates. Certificate paths stay the same. If you change the
forwarding port, export again and update the saved server's port.

### Troubleshooting and other clients

- **Password prompt:** leave it blank, select **Save Password**, and connect.
  New exports include an empty `pgpass.conf` to avoid the initial prompt.
- **Windows access violation:** set **Properties → Parameters → GSS encmode →
  disable** (add it with **+** if absent). This works around a
  [pgAdmin Windows bug](https://github.com/pgadmin-org/pgadmin4/issues/10445).
  New exports include this setting; an existing saved server needs the edit.
- **Missing root certificate:** use the exported absolute certificate paths and
  keep SSL mode **verify-full**.

For manual configuration, inspect the export in Windows PowerShell:

```powershell
Get-Content "$env:LOCALAPPDATA\Goblin\postgres\wsl\app\connection.json" |
    ConvertFrom-Json |
    Format-List host, port, database, username, sslmode, gssencmode, sslrootcert, sslcert, sslkey
```

Copy those values into the client's connection and SSL fields; clients do not
load `connection.json` automatically. `sslrootcert` is the CA, `sslcert` the
client certificate, and `sslkey` its private key. Leave the password blank.

## Daily development

These commands refresh the profile and prepare access automatically:

| Command | Purpose / role |
| --- | --- |
| `make dev-start` | Start Goblin with `goblin_app` |
| `make database-migrate` | [Apply SQL](#apply-the-sql-schema) with `goblin_admin` |
| `make database-scaffold` | [Regenerate EF mappings](#reverse-engineer-the-database) with `goblin_app` |
| `make database-test` | [Run database integration tests](#application-configuration-and-verification) with both roles |

`DB_PROFILE` defaults to `wsl`. Append `DB_PROFILE=azure-dev` to use another
development database; there is no global active profile. For connection-only
development, `make dev-start ARGS=--without-database` disables Work APIs.

Wrap other developer commands with `goblinctl db run wsl -- COMMAND`;
add `--role admin` or `--role both` before `--` only when needed.

## Azure development

[Install Azure manually](../deploy/azure/README.md), then configure SSH authentication
and trust the VM's host key. The SSH user needs noninteractive sudo access to k3s.
Using the CLI and packages from [the WSL setup](#wsl-and-windows-pgadmin), run in WSL
with your SSH destination:

```bash
goblinctl db connect azure-dev --ssh developer@your-azure-vm --forward-port 55433
goblinctl db export azure-dev --client windows --role app
```

Import the printed `azure-dev\app\pgAdmin.json` using the
[same pgAdmin steps](#wsl-and-windows-pgadmin). The SSH tunnel runs in WSL on
`localhost:55433` and reaches the VM's `localhost:5432`. Keep WSL running.
For later exports, use `azure-dev` in the [refresh command](#reconnect-or-refresh).

## Profile management and storage

| Command | Effect |
| --- | --- |
| `goblinctl db connect wsl` | Refresh certificates, restore forwarding, and verify TLS |
| `goblinctl db refresh wsl --role admin` | Refresh access for the selected role |
| `goblinctl db disconnect wsl` | Stop this profile's extra listener/tunnel; retain settings and certificates |

Connect and refresh preserve the saved port; change it explicitly with
`connect --forward-port PORT`. Disconnect leaves installation-managed host access
on 5432 running. Linux refreshes do not update Windows exports.

| Location | Purpose |
| --- | --- |
| Kubernetes Secrets | Authoritative certificates; the CA signing key stays here |
| `~/.config/goblin/postgres/<profile>/` | Linux profile and client certificates; honors `XDG_CONFIG_HOME` |
| `%LOCALAPPDATA%\Goblin\postgres\<profile>\` | Explicit Windows exports, separated by role |
| `/var/lib/goblin/postgres/` | Host forwarding state, without client keys |
| Repository `.artifacts/` | Disposable build/test output |

SSH control sockets remain in the profile store's private `.ssh` directory.
The CLI starts and controls SSH from that directory using the socket filename,
so long home or configuration paths do not exceed Unix socket path limits.

Linux directories are 0700 and files 0600. Windows exports resolve the current
Windows account and set private ACLs. Export only the role needed. Profiles pass
connections through child-process environment variables without editing checkout
`appsettings.json` files.

Profiles refuse changes to the source, cluster, database volume, or CA. Restore
the original installation or deliberately use a new profile name. After
[recreating a development installation](../deploy/local/README.md#stop-resume-or-start-clean),
disconnect and remove its named Linux profile and Windows export before reusing
that name.

## Installation and certificates

The installer provisions PostgreSQL, applies migrations, and creates host access;
it exports no client credentials. After k3s and cert-manager are ready, these
commands are available for repair from a root shell on the database's WSL instance
or Azure VM:

| Command | Purpose |
| --- | --- |
| `goblinctl db setup` | Configure certificates and PostgreSQL in the selected Kubernetes context, then verify TLS |
| `goblinctl db host` | Configure the installation's loopback endpoint on 5432 |

Setup uses `kubectl`, falling back to `k3s kubectl`, and preserves the database
volume, CA, and certificate identities. It does not configure host-installed
PostgreSQL. Host access needs port 5432 free or already owned by Goblin; resolve
any other service's ownership before stopping it.

Resources are defined in [deploy/postgres](../deploy/postgres/):

| Resource | Purpose |
| --- | --- |
| `goblin-postgres-ca` Certificate/Secret | Database CA; signing key stays in Kubernetes |
| `goblin-postgres-server` Certificate | Server identity in `goblin-postgres-tls` |
| `goblin-postgres-app` Certificate | `goblin_app` identity in `goblin-postgres-app-tls` |
| `goblin-postgres-admin` Certificate | `goblin_admin` identity in `goblin-postgres-admin-tls` |
| `goblin-postgres-admin` Opaque Secret | Initialization-only password required by the official image |
| `goblin-postgres-data` PVC | Independent 5 GiB database storage |

Every network connection requires TLS and a trusted client certificate whose
common name matches the role. Initialization clears the bootstrap password;
clients use no password. Container-local Unix socket administration uses peer
authentication restricted to the `postgres` OS user.

`goblin_admin` owns the schema; `goblin_app` can read/write application tables but
cannot create/drop them. Default privileges cover future tables and sequences
created by the administrator. Network policy allows Goblin and same-namespace
pods labeled `goblin-database-access: "true"`; no public database listener is created.

Back up the CA, initialization Secret, and database recovery material. Setup
refuses to replace a missing CA while issued TLS Secrets exist, or a missing
initialization Secret while the PVC exists; restore the missing Secrets.
Deleting the namespace/PVC or losing the VM can delete the database.

### Certificate renewal

cert-manager renews 90-day leaf certificates 30 days before expiry and rotates
their keys. Full Secret directory mounts receive updates; PostgreSQL reloads TLS,
and new Npgsql connections load current client files. Existing pooled connections
remain usable. No application rebuild or connection-string change is needed.
Linux profiles refresh on connect and development commands; Windows needs an
explicit [export](#reconnect-or-refresh).

Monitor readiness and expiry. The CA lasts ten years and retains its key during
routine renewal. Its issuer does not provide complete trust rollover or revocation;
plan overlapping trust before replacing the CA and restrict Secret/issuance access.

## Schema and EF Core

Goblin-owned tables use singular, unquoted PostgreSQL `snake_case` names in the
default `public` schema and `PascalCase` C# names with mapping attributes. They use
`id bigint` and bigint foreign keys. Identity sequences allocate local IDs;
GitHub repository IDs retain their upstream values. HTTP exposes bigint IDs as decimal strings.
Inspection and repository-operation requests reserve IDs before submission and
retain them on replay.

Wolverine-owned tables retain the official packages' native names, `wolverine_`
prefix, and library-defined key types:

- `wolverine_agent_restrictions`
- `wolverine_control_queue`
- `wolverine_dead_letters`
- `wolverine_incoming_envelopes`
- `wolverine_node_records`
- `wolverine_nodes`
- `wolverine_node_assignments`
- `wolverine_outgoing_envelopes`

The administrator-only migration journal is `public.schema_migration`; its
identity sequence is also inaccessible to the application role.
Wolverine and the journal are excluded from Goblin EF scaffolding. The catalog has
20 singular product tables, the singular journal, and eight native Wolverine
tables. Primary keys, foreign keys, identity sequences, and table-specific indexes
follow their owner's naming conventions. The independent bigint Work history
sequence is `work_event_id_seq`; it has no table or column owner. Wolverine uses
`wolverine_node_records_id_seq` and
`wolverine_nodes_node_number_seq`. C# entity types remain singular and collections
remain plural: `WorkItem` maps to `work_item`, while `WorkItems` is a DbSet.

Goblin references the official `WolverineFx.EntityFrameworkCore`,
`WolverineFx.Postgresql`, and `WolverineFx.RuntimeCompilation` NuGet packages at
6.39.1. The administrator-applied baseline DDL must match their native SQL
definitions; startup retains `AutoCreate.None`. The application excludes the
integration's transitive EF design-time assets from runtime consumers and pins
EF relational runtime assets to Goblin's existing 10.0.12 version.
EF transactions and the durable inbox preserve atomic state/dispatch persistence.
Failed Work requires attention
and explicit retry after reconciliation of uncertain execution, as described in
the [Work lifecycle](work-lifecycle.md). Optional tenancy, dynamic listeners,
recurring messages, deduplication, partitioning, and database message queues are
not enabled.

### Apply the SQL schema

Use `make database-migrate` from [daily development](#daily-development).
[Goblin.Database](../backend/tools/Goblin.Database/) applies
[SQL files](../backend/database/migrations/) in ordinal filename order, once each.
Each file and its journal entry commit in one transaction; failures roll back.
An advisory lock serializes runners, and recorded SHA-256 checksums reject edits
to applied scripts.

**Before production:** consolidate schema changes into `0001_initial.sql` and
deliberately recreate disposable development databases when it changes.
Previously applied versions of the unreleased baseline cannot be reapplied:
checksum validation must reject them. Use isolated test databases for verification
without resetting the running installation.
Do not add rename migrations, compatibility views, or bypass the journal.
Recreate only the verified owned development database; schema changes do not
authorize deleting Work PVCs or
unrelated installation data. Remove stale named client profiles only after
reviewing the new database volume and CA identities, then reconnect.
Local provisioning does not freeze the baseline or bypass checksum checks. **After production:** keep applied filenames
and contents immutable and add higher-numbered migrations.

Use LF endings and ordinary SQL without `psql` commands or explicit
`BEGIN`/`COMMIT`. Nontransactional operations such as `CREATE INDEX CONCURRENTLY`
need a separate administrative procedure. Review destructive SQL and back up
before applying it. Web startup never runs migrations; EF migrations and
`EnsureCreated` are not used.

### Reverse engineer the database

Use `make database-scaffold` from [daily development](#daily-development).
[scaffold-database.sh](../backend/scripts/scaffold-database.sh) owns the table
selection and EF flags, restores the pinned local tool, and regenerates
[Generated/](../backend/src/Goblin.Persistence/Generated/) with repository
formatting, regular constructors, UTF-8 without BOM, and LF endings.

The app role can inspect the schema. `Name=ConnectionStrings:Goblin` resolves its
connection from the tooling host's environment; `--no-onconfiguring` keeps it out
of generated code. `--data-annotations` requests attributes; remaining mappings
use Fluent API. Leave `--use-database-names` unset (`work_item` becomes `WorkItem`),
and do not combine the explicit table list with `--schema public`, which includes
every table. `public` is implicit in generated attributes.

Keep custom behavior in partial classes **outside** `Generated/`. EF/scaffolding
tools remain developer-only, separate from application and persistence test builds;
the published migration runner has no EF model dependency.

To change the schema:

1. Edit the unreleased baseline, or add a migration after production, following
   [the migration rules](#apply-the-sql-schema). New tracked tables need primary keys.
2. Update the 20 singular `--table` entries and custom candidate names in the
   scaffolding tooling. Apply SQL to a fresh disposable database with
   `make database-migrate DB_PROFILE=<profile>`, then run
   `make database-scaffold DB_PROFILE=<profile>`. Wolverine and the journal remain excluded.
3. Remove obsolete generated entities after table drops/renames, then run
   `dotnet build backend/Goblin.slnx` and the [database tests](#application-configuration-and-verification).
4. Review SQL, table selection, and generated C# together. A second
   scaffold against the unchanged database must produce no additional diff
   relative to the first scaffold. Keep filtered unique relationship corrections
   in `WorkModelConfiguration.cs` outside generated files.

### Workspace storage boundary

PostgreSQL stores Work conversations, lifecycle history, Git commit metadata, and
inspection records. Workspace files stay on the Work PVC; `workspace_checkpoint`
contains no filesystem payload or archive digest. Never store workspace archives
in PostgreSQL. See [workspace lifecycle](workspace-lifecycle.md).

## Application configuration and verification

[appsettings.json](../backend/src/Goblin.Web/appsettings.json) supplies the deployed
Npgsql connection: `goblin-postgres:5432`, database `goblin`, role `goblin_app`,
`SSL Mode=VerifyFull`, and `GSS Encryption Mode=Disable`. CA/client certificate/key
paths are `/etc/goblin-postgres/{ca.crt,tls.crt,tls.key}`. VerifyFull checks the CA
and hostname; disabling GSS avoids Kerberos dependencies.

Kubernetes mounts only the app certificate/key and public CA in a read-only Secret
volume accessible to the app's group. No password or private key is baked into the
image; administrator/CA private keys and database credentials stay out of
[repository sandboxes](execution-hosting.md). Npgsql handles certificate validation
directly. Rebuild/redeploy when packaged connection settings change.

Direct development receives the app connection through `ConnectionStrings__Goblin`.
Durable Work requires committed migrations, including Wolverine tables; automatic
schema creation is disabled. The installer runs
`bash deploy/postgres/migrate.sh IMAGE` with only the admin certificate and stops
on failure before deploying Goblin. `/readyz` checks the database independently
of Codex. Custom EF configuration for the filtered active-attempt relationship
lives outside generated classes.

Run `make database-test` against a development profile. Tests create/drop uniquely
named databases and cover EF CRUD across contexts, role permissions, schema
application/checksums/rollback, messaging storage, sequences, foreign keys, and
attempt cleanup reservations. Certificate checks verify the authenticated role
and reject missing client certificates, admin impersonation, unencrypted access,
an untrusted CA, and a wrong server hostname. Without explicit test configuration,
database tests skip and ordinary solution tests need no server.

To run HTTP/browser journeys against a migrated, disposable development database:

```bash
goblinctl db run wsl --usage tests -- make node-test
goblinctl db run wsl --usage tests -- make browser-test ARGS=tests/e2e/work.spec.ts
```

These journeys write Work records to the selected database.

### Manual regression after development recreation

After deliberately recreating the development database, applying the baseline,
and deploying the changed build, check readiness and repeat these journeys with
neutral test data. Preserve Work PVCs. Previous successful manual checks establish
the starting behavior; they do not verify this build.

- **Slack:** connect or relink the installation, link an external identity, submit
  a message in a linked thread, and confirm Work and replies appear once. Reply
  in that thread and confirm it reaches the existing conversation.
- **GitHub:** connect the account, select an accessible test repository, complete
  repository setup/approval, and confirm a Work repository operation completes
  with its recorded result and provenance.
- **ChatGPT:** connect the ChatGPT/Codex account, select a model, submit Work,
  respond to an attention request, and confirm completion and history. Restart
  Goblin during queued Work and confirm recovery; explicitly reconcile uncertain
  execution before retrying failed Work.
