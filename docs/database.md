# PostgreSQL

Goblin uses PostgreSQL with certificate authentication and database-first EF Core. SQL
owns the schema; EF generates the C# mappings.

## WSL and Windows pgAdmin

### Store a client profile in this checkout

After completing the [WSL k3s installer](../deploy/k3s/README.md), open a **root shell
inside WSL** and run these commands from the repository root. The Linux profile lives
in the Git-ignored `.artifacts/db-client/goblin/postgres/wsl-artifacts/` directory.

```bash
cargo build --locked -p goblinctl
apt install -y postgresql-client openssl openssh-client iproute2
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db connect wsl-artifacts --local --port-forward 55432
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db export wsl-artifacts --client windows --role app
```

The export prints the full Windows paths for both files. For the `wsl-artifacts` profile
and `app` role, look in `%LOCALAPPDATA%\Goblin\postgres\wsl-artifacts\app\`. Import
`pgAdmin.json` into **pgAdmin**. Use `connection.json` to configure other clients.

1. Open **Tools → Import/Export Servers → Import** and select the full path
   printed after **pgAdmin import**.
2. Keep **Remove all the existing servers?** set to **No** and finish the import.
3. Open **Goblin → Goblin - wsl-artifacts (app)**. No database password is needed.

WSL reaches PostgreSQL at `localhost:5432`. Windows uses `localhost:55432` through WSL
localhost forwarding. Keep WSL running. The profile saves the forwarding port, which
can be any port from 1024 to 65535 except the reserved database port 5432.
Connect checks for WSL port conflicts, and export checks for Windows port conflicts.
Export also verifies the database identity and TLS login before writing the Windows files.

### Reconnect or refresh

After restarting WSL or renewing certificates, run this from the repository root in a
root WSL shell and reconnect the saved pgAdmin server. Use the same config directory and
profile name as during setup.

```bash
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db export wsl-artifacts --client windows --role app
```

`export` refreshes certificates at the same paths and starts forwarding. Import once
per profile and role. If you change the forwarding port, export again and update the
saved server's port.

### Export after recreating k3s

An export that reports a changed cluster, database volume, or CA stops before writing
files to Windows. If you expected the original database, restore that installation.
After intentionally recreating k3s, choose a profile name that is unused in WSL and
Windows, such as `wsl-artifacts-new`. Run these commands from the repository root in
a root WSL shell.

```bash
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db connect wsl-artifacts-new --local --port-forward 55432
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db export wsl-artifacts-new --client windows --role app
```

Import the printed `wsl-artifacts-new\app\pgAdmin.json` path and use the new profile name
in future exports.

### Troubleshooting and other clients

- **WSL port is in use.** Connect reports the listener when it can identify it. To
  stop that listener and take over its port, rerun your connect command with `--force`.
  Keep the same config directory, profile name, and explicit port.

  ```bash
  XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
    ./target/debug/goblinctl db connect wsl-artifacts --local --port-forward 55432 --force
  ```

- **Windows port is in use.** Free it in Windows or connect with a different
  `--port-forward` value, export again, and update the port in pgAdmin.
- **Windows export failed.** Rebuild with `cargo build --locked -p goblinctl` and run
  the [export command](#reconnect-or-refresh) from the repository root. Check that WSL
  can launch Windows PowerShell and that your Windows account can write to the export
  folder.
- **Password prompt:** leave it blank, select **Save Password**, and connect.
  Exports include an empty `pgpass.conf` to avoid this prompt.
- **Windows access violation:** set **Properties → Parameters → GSS encmode →
  disable** (add it with **+** if absent). This works around a
  [pgAdmin Windows bug](https://github.com/pgadmin-org/pgadmin4/issues/10445).
  New exports include this setting; edit existing saved servers.
- **Missing root certificate:** use the exported absolute certificate paths and
  keep SSL mode **verify-full**.

For another client, read the export in Windows PowerShell. Use the name of the profile
you exported in place of `wsl-artifacts` if it differs.

```powershell
Get-Content "$env:LOCALAPPDATA\Goblin\postgres\wsl-artifacts\app\connection.json" |
    ConvertFrom-Json |
    Format-List host, port, database, username, sslmode, gssencmode, sslrootcert, sslcert, sslkey
```

Copy those values into the client's connection and SSL fields; clients do not read
`connection.json` themselves. `sslrootcert` is the CA, `sslcert` the client certificate,
and `sslkey` its private key. Leave the password blank.

## Daily development

Use `make dev-start`, `make database-migrate`, `make database-scaffold`, or
`make database-test`. [Makefile.dev](../Makefile.dev) refreshes the profile and selects
the least-privileged role needed. Use the same `XDG_CONFIG_HOME` as during setup and set
`DB_PROFILE` to the profile name. The Make targets default to the name `wsl` when
`DB_PROFILE` is unset.

```bash
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" make dev-start DB_PROFILE=wsl-artifacts
```

Run other commands with the same config directory and profile. Use `--role admin` or
`--role both` only when needed.

```bash
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db run wsl-artifacts -- COMMAND
```

`make dev-start ARGS=--without-database` disables PostgreSQL and Work APIs.

## Azure development

[Install Azure manually](../deploy/azure/README.md), then configure SSH authentication
and trust the VM's host key. The SSH user needs passwordless `sudo` for `k3s kubectl`.
With the CLI and packages from [the WSL setup](#wsl-and-windows-pgadmin), run these
commands from the repository root in WSL with your SSH destination.

```bash
./target/debug/goblinctl db connect azure-dev --ssh developer@your-azure-vm --port-forward 55433
./target/debug/goblinctl db export azure-dev --client windows --role app
```

Import the printed `azure-dev\app\pgAdmin.json` with the
[same pgAdmin steps](#wsl-and-windows-pgadmin). The SSH tunnel runs in WSL on
`localhost:55433` and reaches the VM's `localhost:5432`. Keep WSL running. For later
exports, repeat the export command above.

## Profile management and storage

Use `goblinctl db --help` for commands. Connect/refresh preserve the saved port; change
it with `connect --port-forward PORT`. Disconnect stops only this profile's forwarder,
preserving settings and credentials. It does not stop the installation's loopback
endpoint. Linux refresh does not update Windows exports.

Source certificates come from Kubernetes Secrets, where the CA signing key stays.
Linux profiles live under `$XDG_CONFIG_HOME/goblin/postgres/<profile>/` with private
directory and file modes. When `XDG_CONFIG_HOME` is unset or empty, the base directory
is `~/.config`. Windows exports are under
`%LOCALAPPDATA%\Goblin\postgres\<profile>\`, one directory per role with private ACLs.
Export only the role needed. Profiles pass connection settings to children, not
appsettings files.

A profile refuses a changed source, cluster, database volume, or CA identity. Restore
the original installation or use a new profile name. After deliberately
[recreating development](../deploy/k3s/README.md#stop-resume-or-start-clean), disconnect
and remove that installation's old Linux profile and Windows export before reusing the
name.

Before moving or removing a profile directory, run `db disconnect` with the original
`XDG_CONFIG_HOME` and profile name.

## Installation and certificates

The installer sets up PostgreSQL, applies migrations, and creates host access; it
exports no client credentials. After k3s and cert-manager are ready, you can run these
commands for repair from a root shell on the database's WSL instance or Azure VM:

| Command | Purpose |
| --- | --- |
| `goblinctl db setup` | Configure certificates and PostgreSQL in the selected Kubernetes context, then check TLS |
| `goblinctl db host` | Configure the installation's loopback endpoint on 5432 |
| `goblinctl db migrate IMAGE` | Run the schema migration Job with the given Goblin image |

Setup uses `kubectl`, falling back to `k3s kubectl`, and keeps the database volume, CA,
and certificate identities. Host access needs port 5432 to be free or already owned by
Goblin; find out which service owns the port before stopping it.

Resources, certificate identities, storage, and network policy are defined in
[deploy/postgres](../deploy/postgres/). Read current names and sizes there.

Every network connection needs TLS and a trusted client certificate whose common name
matches the role. Initialization clears the bootstrap password; clients use no password.
Inside the container, Unix socket administration is allowed only for the `postgres` OS
user.

`goblin_admin` owns the schema. `goblin_app` can read and write application tables but
cannot create or drop them; default privileges cover future tables and sequences that
the administrator creates. Network policy allows Goblin and same-namespace pods labeled
`goblin-database-access: "true"`; there is no public database listener.

Back up the CA, the initialization Secret, and database recovery material. Setup refuses
to replace a missing CA while issued TLS Secrets exist, or a missing initialization
Secret while the PVC exists; restore the missing Secrets. Deleting the namespace or PVC,
or losing the VM, can delete the database.

### Certificate renewal

cert-manager renews leaf certificates and rotates their keys according to the
[certificate manifests](../deploy/postgres/). PostgreSQL reloads its TLS files, and new
Npgsql connections read the current client files. No rebuild or connection-string change
is needed. Linux profiles refresh on connect and on development commands; Windows needs
an explicit [export](#reconnect-or-refresh).

Monitor readiness and expiry. The CA keeps its key during leaf renewal. Its issuer does
not provide trust rollover or revocation, so plan overlapping trust before you replace
the CA, and restrict access to Secrets and issuance.

## Schema and EF Core

Goblin tables use singular, unquoted `snake_case` names in the `public` schema and
`PascalCase` C# names with mapping attributes. They use `id bigint` keys and bigint
foreign keys. Identity sequences allocate local IDs; GitHub repository IDs keep their
GitHub values. HTTP exposes bigint IDs as decimal strings. Entity types are singular and
collections plural: `WorkItem` maps to `work_item`, and `WorkItems` is a DbSet. The Work
history sequence `work_event_id_seq` has no table.

The [SQL baseline](../backend/database/migrations/0001_initial.sql) lists every table.
Wolverine tables keep their library names (`wolverine_` prefix) and key types, and the
baseline DDL must match Wolverine's own SQL because startup uses `AutoCreate.None`.
`ChatGptOAuthDbContext` maps OpenIddict's native long-key entities to the singular
`openiddict_application`, `openiddict_authorization`, `openiddict_scope`, and
`openiddict_token` tables with unquoted `snake_case` columns. See
[ChatGPT authentication](chatgpt-integration.md). The `schema_migration` journal is
administrator-only. Wolverine, OpenIddict, and the journal are excluded from product
scaffolding.

### Apply the SQL schema

Use `make database-migrate` from [daily development](#daily-development).
[Goblin.Database](../backend/tools/Goblin.Database/) applies
[SQL files](../backend/database/migrations/) in ordinal filename order, once each. Each
file and its journal entry commit in one transaction; failures roll back. An advisory
lock allows one runner at a time, and SHA-256 checksums reject edits to applied files.
New files must sort after the last applied file.

**Before production:** keep all schema changes in `0001_initial.sql` and recreate
disposable development databases when it changes; the checksum check rejects a database
that has an older baseline. Use isolated test databases for verification. Do not add
rename migrations or compatibility views, and do not bypass the journal. Schema changes
never allow deleting Work PVCs or unrelated installation data. After recreating the
database, review the new volume and CA identities, remove the stale client profiles, and
reconnect. **After production:** keep applied files unchanged and add higher-numbered
migrations.

Use LF line endings and plain SQL without `psql` commands or explicit `BEGIN` /`COMMIT`.
Operations that cannot run in a transaction, such as `CREATE INDEX CONCURRENTLY`, need a
separate administrator procedure. Review destructive SQL and back up before applying it.
Web startup never runs migrations; EF migrations and `EnsureCreated` are not used.

### Reverse engineer the database

Use `make database-scaffold` from [daily development](#daily-development).
[scaffold-database.sh](../backend/scripts/scaffold-database.sh) owns the table list and
EF flags. It restores the pinned local tool and regenerates
[Generated/](../backend/src/Goblin.Persistence/Generated/) with repository formatting,
UTF-8 without BOM, and LF endings.

`Name=ConnectionStrings:Goblin` reads the app connection from the environment;
`--no-onconfiguring` keeps it out of generated code. Do not set `--use-database-names`
(`work_item` must become `WorkItem`), and do not add `--schema public`, which would
include every table.

Keep custom behavior in partial classes **outside** `Generated/`, for example custom
relationship configuration. EF design tools stay in developer tooling; the published
migration runner has no EF dependency.

To change the schema:

1. Edit the baseline (or, after production, add a migration) following
   [the migration rules](#apply-the-sql-schema). New tables need primary keys.
2. Update the scaffolding selection and affected persistence tests/custom mappings.
   Apply SQL to a fresh disposable database with `make database-migrate`, then run
   `make database-scaffold`. Select the profile as in [daily development](#daily-development).
3. Remove generated entities for dropped or renamed tables, then run
   `dotnet build backend/Goblin.slnx` and the [database tests](#application-configuration-and-verification).
4. Review SQL, table list, and generated C# together. A second scaffold against
   the same database must produce no further diff.

### Workspace storage boundary

PostgreSQL stores Work conversations, lifecycle history, Git commit metadata, and
inspection records. Workspace files stay on the Work PVC; `workspace_checkpoint` holds
no file content or archive. Never store workspace archives in PostgreSQL. See
[workspace lifecycle](workspace-lifecycle.md).

## Application configuration and verification

[appsettings.json](../backend/src/Goblin.Web/appsettings.json) owns the deployed
connection and certificate paths. Kubernetes mounts only the application identity,
read-only. Database passwords and TLS keys stay out of the image. Administrator and CA
credentials never reach [repository sandboxes](execution-hosting.md). Rebuild and
redeploy when packaged connection settings change.

Direct development receives the app connection through `ConnectionStrings__Goblin`.
Durable Work needs the migrated schema, including Wolverine tables. The installer runs
`goblinctl db migrate IMAGE` with only the admin certificate and stops on failure before
deploying Goblin. When Work is enabled, `/readyz` checks the database.

Run `make database-test` against a development profile. Select the profile as in
[daily development](#daily-development). The tests create and drop
uniquely named databases. Without the test connections, database tests skip and ordinary
tests need no server. `GOBLIN_TEST_POSTGRES_APP` opts in to durable Work HTTP and browser
journeys; PostgreSQL schema integration suites also need `GOBLIN_TEST_POSTGRES_ADMIN`.
[The environment catalog](../config/environment.mts) defines both connections. The
profile runner uses `--role app` by default; `--usage tests` exposes test connections
for the selected roles. Add `--role both` for schema suites; `make database-test`
selects both roles. Report database-dependent skips separately from passing fixture
tests.

To run HTTP and browser journeys against a migrated, disposable development database:

```bash
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db run wsl-artifacts --usage tests -- make node-test
XDG_CONFIG_HOME="$PWD/.artifacts/db-client" \
  ./target/debug/goblinctl db run wsl-artifacts --usage tests -- make browser-test ARGS=tests/e2e/work.spec.ts
```

These journeys write Work records to the selected database.

### Manual regression after development recreation

After recreating the development database, applying the baseline, and deploying the
changed build, check readiness and repeat these journeys with neutral test data. Keep
Work PVCs.

- **Work:** create an item, rename its title, and reload the page. In PostgreSQL,
  check the updated `work_item.title` and original `work_item.description`.
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
