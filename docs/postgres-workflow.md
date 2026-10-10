# PostgreSQL development workflow

Run client commands **inside WSL, as your development user**. PostgreSQL and the
Linux host endpoint use **5432** in both WSL and Azure. Extra listeners run in
WSL; your Windows host machine reaches them through WSL localhost forwarding.

| Database | Linux host endpoint | Optional WSL listener | Windows client |
| --- | --- | --- | --- |
| WSL | WSL `localhost:5432` | `localhost:55432` | `localhost:55432` |
| Azure | Azure VM `localhost:5432` | SSH tunnel on `localhost:55433` | `localhost:55433` |

55432/55433 are examples. Choose free WSL **and Windows** ports; the profile keeps
your choice. A conflict never silently changes a port.

## First use

After completing the Goblin installer, use the CLI from this checkout:

```bash
cargo build --locked -p goblinctl
export PATH="$PWD/target/debug:$PATH"
sudo apt install -y postgresql-client openssl openssh-client
sudo -v
goblinctl db connect wsl --local --forward-port 55432
```

The installer creates host access automatically. To repair it, run this on the
**WSL instance or Azure VM containing the database**:

```bash
sudo "$(command -v goblinctl)" db host
```

This needs port 5432 to be free or already owned by Goblin. If another PostgreSQL
service occupies it, decide which service to retain before stopping anything.
Database setup, when needed after k3s/cert-manager installation, remains
`sudo goblinctl db setup`; it creates no host client exports.

For Azure, replace the SSH destination with your VM or SSH host alias. Configure
SSH authentication and trust the host key first. The SSH user needs noninteractive
sudo access to k3s. Azure installation and host preparation remain manual.

```bash
goblinctl db connect azure-dev --ssh developer@your-azure-vm --forward-port 55433
```

`connect` verifies the saved cluster, database volume and CA, refreshes the selected
role's certificate, prepares forwarding, and tests a TLS login. It prints the
connection string and expiry. A changed identity requires restoring the original
installation or deliberately creating another profile name.

## Daily development

```bash
make dev DB_PROFILE=wsl
make db-migrate DB_PROFILE=wsl
make db-scaffold DB_PROFILE=wsl
make test-postgres DB_PROFILE=wsl
```

Each command refreshes its profile and prepares access. Replace `wsl` with
`azure-dev` to select that development database. Development/scaffolding use
`goblin_app` (read/write); migrations use `goblin_admin`. Database tests explicitly
fetch both roles and create/drop their own test databases. Use a development
installation. `DB_PROFILE` defaults to `wsl`; there is no global active profile.

For connection-only development:

```bash
make dev ARGS=--without-database
```

To opt the HTTP/browser tests into a migrated, disposable development database:

```bash
goblinctl db run wsl --usage tests -- make test-js
goblinctl db run wsl --usage tests -- make test-browser ARGS=tests/e2e/work.spec.ts
```

Those journeys write Work records to the selected database. Ordinary tests retain
their opt-in behavior. `db run` also wraps other developer commands; add
`--role admin` or `--role both` only when needed.

## Windows database clients

Export only the role your Windows client needs:

```bash
goblinctl db export wsl --client windows --role app
goblinctl db export azure-dev --client windows --role app
```

Exports go to `%LOCALAPPDATA%\Goblin\postgres\<profile>\`. Each role's
`connection.json` supplies Windows paths, database `goblin`, user, forwarding
port, and TLS settings. Configure the client with **verify-full**, the CA, client
certificate and private key. The export resolves the current Windows account and
sets private ACLs. It refuses to overwrite another database's exported identity.

Export again after certificate renewal or changing a forwarding port. Windows
exports are explicit snapshots; unavailable Windows interop does not block WSL
development. Keep WSL running while using its forwarded connections.

## Refresh, reconnect and disconnect

```bash
goblinctl db refresh wsl
goblinctl db refresh azure-dev --role admin
goblinctl db connect azure-dev
goblinctl db disconnect azure-dev
```

Refresh preserves the selected port. Disconnect stops only that profile's extra
listener/tunnel and retains certificates/settings. Reconnect after restarting
WSL or losing an SSH connection. Local PostgreSQL host access remains managed by
the installation. For an expired sudo session, run `sudo -v` again.

## Storage

| Location | Owner/purpose |
| --- | --- |
| Kubernetes Secrets | Authoritative certificates; CA signing key stays here |
| `~/.config/goblin/postgres/<profile>/` | Linux client certificates and profile; honors `XDG_CONFIG_HOME` |
| `%LOCALAPPDATA%\Goblin\postgres\<profile>\` | Explicit Windows client exports |
| `/var/lib/goblin/postgres/` | Host forwarding state, without client keys |
| Repository `.artifacts/` | Disposable build/test output |

Linux profile directories are 0700 and files 0600. Profiles do not write checkout
`appsettings.json` files. Goblin in Kubernetes uses packaged settings and mounted
Secrets independently of these client stores.

Goblin is preproduction. Recreate development installations when the schema or
installation layout changes; see the [local reset commands](../deploy/local/README.md#stop-resume-or-start-clean).
After recreating a cluster, disconnect and remove its named Linux profile and
Windows export before connecting that profile name to the new cluster.

For schema design and migration rules, see [the database guide](database.md).
