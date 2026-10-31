# Authentication and connection checks

Goblin has one owner password and one Codex connection. Codex signs in with
ChatGPT device-code login or an OpenAI API key. Open **Settings → AI connections →
Manage connection** to connect, and a connection check confirms model access for the
saved account. GitHub uses the official GitHub CLI device flow under **Settings →
GitHub**. See [execution hosting](execution-hosting.md) for repository authority and
credential isolation.

The private ChatGPT MCP integration uses the owner password for browser sign-in and
consent through embedded OpenIddict. It has one owner subject. Its OAuth tokens
authorize only explicitly shared Work, not Codex execution or Goblin administration.
See [ChatGPT setup](chatgpt-integration.md).

## Run locally

Durable Work is enabled by default and needs a migrated profile from the
[PostgreSQL workflow](database.md#wsl-and-windows-pgadmin). The connection-only launch
below disables database access and Work APIs.

Install the .NET 10 SDK, Node.js 24 or newer, and the pinned Rust toolchain, then run:

```bash
make install
make dev-start ARGS=--without-database
```

`make dev-start` builds the backend and browser assets, creates the local password
verifier if needed, and starts the ASP.NET Core host. Restart it after editing sources.
To build separately, run `make build`, then `cargo xtask dev --without-database`.

On first start, choose and confirm the password in the terminal. Open
**http://localhost:8787**, enter the password, and select **Open workspace**.
`/settings?section=codex` opens the Codex connection directly. There is no default
password.

Every environment requires `GOBLIN_PASSWORD_HASH_FILE`. The local launcher sets it
to `.goblin-secrets/owner-password` unless you set another verifier path. A missing
or invalid file stops startup. Login creates a private session cookie that lasts up
to 12 hours. **Lock workspace** ends the current session; restarting Goblin ends all
sessions. After five failed attempts within one minute, login is refused for up to
one minute.

Goblin installs Codex CLI **0.161.0** through the lockfile. The C# client starts its
official Rust binary as `codex app-server` over stdio. Its `HOME`, working directory,
and `CODEX_HOME` are private to Goblin, so your machine's Codex configuration and
credentials are not used or changed.

### Password setup and storage

`make dev-start` and the full local installer share `.goblin-secrets/owner-password`.
The Rust credentials code stores only a PBKDF2-SHA256 verifier (600,000 iterations,
random salt) with mode `0600` in a `0700` directory. Git ignores the folder except
`.gitkeep`; Docker excludes the whole folder.

To set the password before starting:

```bash
make password-set
make dev-start ARGS=--without-database
```

Use `make password-set ARGS=--replace` to choose a new password, then restart Goblin.
For unattended setup, set `GOBLIN_LOCAL_PASSWORD` in the process environment; it does
not replace an existing verifier unless you also pass `--replace`. Azure creates its
verifier during provisioning. For a direct .NET launch, set
`GOBLIN_PASSWORD_HASH_FILE=.goblin-secrets/owner-password`.

## Enable ChatGPT device authentication

Enable device-code login before your first ChatGPT sign-in, even if you already use
Codex another way. Goblin cannot change this setting.

- Personal account: enable device-code login in ChatGPT **Settings → Security**.
- Workspace account: in **Workspace settings → Permissions & roles**, select the
  role for your account, then enable **Codex Local access** and **Device Code
  Authentication**. This may need a workspace administrator.

Goblin shows these steps before sign-in and beside the device code. OpenAI may change
the labels; see its
[headless login instructions](https://learn.chatgpt.com/docs/auth#login-on-headless-devices).
After enabling the setting, try again on OpenAI's sign-in page. If the code has
expired, select **Cancel sign-in** in Goblin and start again.

## What to check

1. Select **Continue with ChatGPT**, open OpenAI's sign-in page, and enter the code.
   Goblin shows your account and **Verifying connection…**, then **Connected**.
2. Restart `make dev-start` and unlock Goblin. The connection remains without a
   second sign-in while the credentials are valid.
3. Select **Lock workspace** and unlock again. Codex stays connected.
4. Select **Disconnect Codex**. The account disappears, including after restart.
   This removes the cached credentials; it does not revoke an API key in the OpenAI
   Platform.
5. Open **Use an OpenAI API key instead** and select **Connect API key**. Goblin
   checks the key with `GET /v1/models`, saves it through Codex, and then runs a
   connection check that counts toward API billing.
6. Cancel a ChatGPT sign-in and start again. Refreshing the page during a pending
   sign-in shows the same code.
7. Select **Check again**. A failed check shows an explanation and **Retry check**.
   Expired sign-ins, usage limits, access denial, and timeouts have separate messages.

ChatGPT access follows the connected plan or workspace; API-key usage is billed in the
OpenAI Platform. Disconnect before switching methods. Keys rejected with HTTP 401 are
not saved. Keys that return 403 or 429 are saved with a notice that they are not
verified; a successful connection check clears the notice.

## Verify the connected account

The connection check calls `/api/prompt` with the fixed prompt "Reply with only OK."
It uses the same Codex App Server process and login, starts an ephemeral thread with
`thread/start`, sends `turn/start`, and reports **Connected** only after a completed
turn with a nonempty reply from Codex's default model. The prompt and reply are not
shown.

The page runs one check when it loads a signed-in account: after sign-in, page load,
unlock, or an account change. **Check again** and **Retry check** run another.
Status polling never repeats a check. Results live only in page memory. If a status
refresh fails, the page shows **Connection unavailable** until a check succeeds.

Each check is a real model request and counts toward the ChatGPT plan's limits or
API billing. Limits: prompts up to 500 characters, replies up to 8,000 characters,
one check at a time, and a 90-second timeout. Closing or reloading the page requests
cancellation. Each check uses a new temporary thread.

The check runs with shell tools, web search, apps, plugins, image tools, multi-agent
tools, hooks, and memories disabled, a read-only sandbox, and no approvals. Account
changes and checks run one at a time, so a check cannot switch accounts partway
through. Failures show safe messages without raw upstream errors or credentials.

## Run in Docker

Create a verifier as in [Password setup and storage](#password-setup-and-storage),
build the image, and copy the verifier into a private volume owned by UID 1000:

```bash
docker build -t goblin-platform:0.1.0 .
docker run --rm -i --user 0:0 --entrypoint sh \
  -v goblin-platform-password:/password goblin-platform:0.1.0 \
  -c 'umask 077; cat > /password/owner-password; chown 1000:1000 /password/owner-password' \
  < .goblin-secrets/owner-password
```

For a connection-only check without PostgreSQL, mount persistent storage and the
password volume read-only. Durable Work also needs the database and execution
configuration in [execution hosting](execution-hosting.md).

```bash
docker run --rm --name goblin-platform \
  -p 127.0.0.1:8787:8787 \
  -v goblin-platform-data:/data \
  -v goblin-platform-password:/etc/goblin:ro \
  -e GOBLIN_PASSWORD_HASH_FILE=/etc/goblin/owner-password \
  -e GOBLIN_WORK_ENABLED=false \
  goblin-platform:0.1.0
```

Open **http://localhost:8787** and follow the same checks. Recreate the container
with the same volumes to check that the connection persists.

## Installed Kubernetes environments

The [local installer](../deploy/local/README.md) and
[manual Azure installation](../deploy/azure/README.md) set up PostgreSQL, the schema,
repository execution, and the owner password. Use the password chosen during setup,
and follow those guides for installation and recovery.

Each data volume has one owner and one application controller. Replacing the pod
keeps credentials on the `platform-data` PVC; deleting the PVC can lose them,
depending on the storage class's reclaim policy. Execution pods receive neither the
application database certificate nor its service-account token.

## Configuration and storage

| Setting | Default | Purpose |
| --- | --- | --- |
| `GOBLIN_DATA_DIR` | `.goblin-platform`; `/data/platform` in the image | Private application data |
| `GOBLIN_PASSWORD_HASH_FILE` | None; required | Password verifier; `/etc/goblin/owner-password` in the Sandbox manifest |
| `GOBLIN_HOST` | `127.0.0.1`; `0.0.0.0` in the image | Listening interface |
| `GOBLIN_PORT` | `8787` | Listening port |
| `GOBLIN_PUBLIC_ORIGIN` | `http://localhost:<port>` | Exact browser origin for Host and Origin checks |
| `GOBLIN_ALLOW_INSECURE_HTTP` | `false` | Allows a non-loopback HTTP origin; traffic is unencrypted |
| `GOBLIN_CODEX_COMMAND` | Pinned binary under `node_modules`, otherwise `codex` on `PATH` | Path to the official Codex executable |

Non-loopback origins must use HTTPS unless `GOBLIN_ALLOW_INSECURE_HTTP=true`. Loopback
HTTP works for local access and SSH or kubectl port forwarding. HTTPS origins use
Secure session cookies. This table owns the web host defaults; see
[environment variables](environment-variables.md) for naming and parsing rules.

The password is compared exactly, including spaces. Restart Goblin after changing
the verifier to load the new password and end current sessions.

Codex stores credentials in plaintext in `codex/auth.json` under the data directory
(`cli_auth_credentials_store="file"`) and owns ChatGPT refresh and logout. The
directory is private and the HTTP server never serves it. Protect storage and
backups, and do not copy the file into images, source control, chat, or shared
volumes.

The backend does not log RPC payloads, API keys, device codes, upstream error bodies,
or passwords. It discards Codex stderr because it can contain authentication details.

## Automated checks

```bash
make typecheck
make test
make codex-test
make browser-install
make browser-test
```

`make test` uses a fake Codex process. `make codex-test` runs the real pinned Codex
binary with a temporary home and a synthetic, unusable key. Browser tests simulate
sign-in and model replies. None of these use personal credentials or run a model
task; a real ChatGPT sign-in remains a manual check.

See [Codex integration](codex-integration.md) for the C# project layout, protocol
regeneration, and the boundary between Goblin and Codex.
