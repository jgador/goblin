# Authentication and connection checks

Goblin has one owner password and a Codex connection supporting ChatGPT device-code
login or an OpenAI API key. A connection check verifies model access using the saved
account. Open **Settings → AI connections → Manage connection** on the Codex card.
GitHub uses the official GitHub CLI device flow under **Settings → GitHub**.
See [execution hosting](execution-hosting.md) for repository authority and credential
isolation. Additional runtimes and their sign-in flows remain unimplemented.

## Run locally

Durable Work is enabled by default and requires a migrated profile from the
[PostgreSQL workflow](database.md#wsl-and-windows-pgadmin). The connection-only
launch below disables database access and Work APIs.

Install the .NET 10 SDK, Node.js 24 or newer, and the pinned Rust toolchain, then run from this checkout:

```bash
make install
make dev-start ARGS=--without-database
```

`make dev-start` builds the C# backend and TypeScript browser assets, provisions the
local password if needed, then starts the ASP.NET Core Minimal API host. After
editing sources, restart it to rebuild. To build separately, run `make build`,
then launch with `cargo xtask dev --without-database`.
Browser assets in `frontend/dist/`, test tooling in `dist/`, and .NET `bin/` and
`obj/` output are ignored by Git.

On first start, choose and confirm your password in the terminal; input is hidden.
Open **http://localhost:8787**, enter that password, and click **Open workspace**.
The homepage opens your Work workspace. Settings is a full workspace page with
section navigation; `/settings?section=codex` opens the Codex connection after
unlocking. Section links support refresh and browser Back/Forward. Use **Back to
workspace** to return to the page you came from; unfinished Work drafts remain
available during navigation. First-time timezone setup remains a focused dialog.
Subsequent starts reuse its saved verifier. After locking the workspace or
restarting Goblin, enter the password again. There is no default password or prefill.

Every environment requires `GOBLIN_PASSWORD_HASH_FILE`. The local launcher sets
it to this checkout's `.goblin-secrets/owner-password` unless you explicitly supply
another verifier path. A missing or invalid configured file prevents startup.
Password login creates a private session cookie; restarting the application
invalidates browser sessions.

Goblin installs Codex CLI **0.161.0** through the lockfile. Its C# client starts
the package's official Rust binary with `app-server` and communicates over stdio
using the checked-in JSON schemas. Its `HOME`, working directory, and `CODEX_HOME` are
private to Goblin. Existing machine-level Codex configuration and provider
credentials are not inherited. You can keep using your normal Codex installation.

### Password setup and storage

`make dev-start` and the full local installer share `.goblin-secrets/owner-password`.
They use the shared Rust credentials implementation (PBKDF2-SHA256, 600,000 iterations, random salt).
Only the verifier is written to disk, with file mode `0600` inside a `0700`
directory. Git ignores the folder's contents except for `.gitkeep`, which keeps
the empty folder in the repository. Docker excludes the entire folder.

To prepare the password before starting the application:

```bash
make password-set
make dev-start ARGS=--without-database
```

Use `make password-set ARGS=--replace` to choose a new password, then restart
Goblin to load it. For unattended first-time setup, supply `GOBLIN_LOCAL_PASSWORD`
through the process environment; it never replaces an existing verifier.
The same verifier can be mounted into Docker. Azure creates its verifier during
provisioning. For a direct .NET launch, set
`GOBLIN_PASSWORD_HASH_FILE=.goblin-secrets/owner-password` explicitly.

## Enable ChatGPT device authentication

Device-code login must be enabled before your first ChatGPT sign-in. Check this
setting even if you already use Codex through another sign-in method.

For a personal account, enable device-code login in ChatGPT **Settings → Security**.
For a workspace account, an administrator must enable it in workspace permissions
for the applicable role. The current Goblin UI also describes Codex Local access
and Device Code Authentication. OpenAI may change the labels; follow its
[headless login instructions](https://learn.chatgpt.com/docs/auth#login-on-headless-devices).
Goblin cannot change these workspace permissions.

After enabling the setting, return to OpenAI's sign-in page and try again. If
the code has expired, cancel sign-in in Goblin and start again for a new code.
Goblin includes these steps before sign-in and beside the device code.
Device-code login is currently beta; see [OpenAI's headless login instructions](https://learn.chatgpt.com/docs/auth#login-on-headless-devices).

## What to check

1. Select **Continue with ChatGPT**. Open OpenAI's sign-in page, enter the
   displayed code, and complete sign-in. Goblin should show your account and
   **Verifying connection…**, then **Connected** after a successful background check.
2. Stop and restart `make dev-start`, unlock Goblin, and confirm the connection
   remains. No second ChatGPT login should be needed while the credentials remain valid.
3. Select **Lock workspace** and unlock it again. This locks browser access without
   disconnecting Codex.
4. Select **Disconnect Codex**. The account should disappear, including after a
   restart. Disconnecting removes this workspace's cached credentials; it does
   not revoke an API key in the OpenAI Platform.
5. Open **Use an OpenAI API key instead** and connect a key. Goblin checks
   `GET /v1/models` and then asks Codex to save it. Goblin then automatically checks
   model access with a small request that counts toward API billing.
6. Try canceling ChatGPT sign-in and starting again. Refreshing the browser
   during a pending login should restore its current code.
7. Select **Check again** to verify model access again. While the check runs,
   Goblin shows **Verifying connection…** and disables duplicate checks. A failed
   check replaces any previous success with an explanation and **Retry check**.
   Expired sign-ins, usage limits, access denial, and timeouts have distinct messages.
   The account email and plan remain visible; the background prompt and reply are hidden.

ChatGPT access follows the connected plan/workspace. API-key usage is billed
separately through the OpenAI Platform. Switching methods requires disconnecting
the current account first. Keys rejected with HTTP 401 are not saved. Restricted
keys without permission to list models, or rate-limited requests, are explicitly
marked as unverified when saved. A successful connection check clears that notice.

## Verify the connected account

The connection check uses the existing `/api/prompt` endpoint with a fixed short
prompt, “Reply with only OK.” It uses the same Codex app-server process and stored
login as the authentication UI. It sends `thread/start` with `ephemeral: true`,
followed by `turn/start`, and waits for a completed turn with a nonempty assistant
reply from Codex's default model. Only then does the UI report **Connected**.
Neither the prompt nor the response is displayed.
See the [app-server protocol](https://learn.chatgpt.com/docs/app-server#turns).

Goblin checks once when a signed-in account is loaded after sign-in, opening or
reloading the page, or unlocking the workspace. It also checks when switching
accounts or selecting **Check again** or **Retry check**. Routine status polling
never repeats a successful or failed check. Results are held only in page memory;
locking or losing the account clears them. A status refresh failure replaces any
previous success with **Connection unavailable** until a new check succeeds.

Each check makes a real model request and counts toward the connected ChatGPT
plan's usage limits or OpenAI API billing. The initial API-key validation against
`/v1/models` remains a separate check that does not generate a response.

Prompts are limited to 500 characters, replies to 8,000 characters, and one
check can run at a time. The response wait times out after 90 seconds;
closing or reloading the page also requests cancellation. Each test uses a
fresh temporary conversation; previous test prompts are not conversation
history. Checks never add a visible conversation to the UI.

The runtime disables shell tools, browsing, apps, plugins, image tools,
multi-agent tools, hooks, and memory. The test also uses a read-only sandbox
and rejects approval requests. It does not enable repository operations.
Account changes are serialized with generation so a prompt cannot switch
accounts halfway through. Model failures show safe messages without raw
upstream errors, credentials, or reasoning output.

## Run in Docker

Create a verifier using [Password setup and storage](#password-setup-and-storage), then build
the image and copy the verifier into a private Docker volume. The setup command
sets file ownership for the application's UID 1000:

```bash
docker build -t goblin-platform:0.1.0 .
docker run --rm -i --user 0:0 --entrypoint sh \
  -v goblin-platform-password:/password goblin-platform:0.1.0 \
  -c 'umask 077; cat > /password/owner-password; chown 1000:1000 /password/owner-password' \
  < .goblin-secrets/owner-password
```

For a connection-only check without PostgreSQL, start with persistent application
storage and the password volume mounted read-only. Durable Work additionally
requires the database and execution configuration in [execution hosting](execution-hosting.md):

```bash
docker run --rm --name goblin-platform \
  -p 127.0.0.1:8787:8787 \
  -v goblin-platform-data:/data \
  -v goblin-platform-password:/etc/goblin:ro \
  -e GOBLIN_PASSWORD_HASH_FILE=/etc/goblin/owner-password \
  -e GOBLIN_WORK_ENABLED=false \
  goblin-platform:0.1.0
```

The image builds browser assets and publishes .NET in separate stages. The final
image contains the ASP.NET Core runtime, published C# host, browser assets, and
the official Rust Codex binary with its runtime resources. Node.js and the
TypeScript compiler are build dependencies only.

Open **http://localhost:8787**, enter your password, and follow the same checks.
Recreate the container with the same named volumes to verify persistence.

## Installed Kubernetes environments

The [local installer](../deploy/local/README.md) and [manual Azure installation](../deploy/azure/README.md)
provision PostgreSQL, migrate the current schema, configure repository execution,
and mount the owner password. Open the installation's Goblin address and use the
password chosen during setup. Follow those guides for installation and recovery;
a standalone connection-only manifest is not a replacement for their configuration.

One owner and one application controller may use each data volume. Replacing its
pod retains credentials on the application PVC; deleting that PVC loses them
according to the storage class's reclaim policy. Application control-plane
credentials manage repository sandboxes, while execution pods receive neither
the application database certificate nor its service-account token.

## Configuration and storage

| Setting | Default | Purpose |
| --- | --- | --- |
| `GOBLIN_DATA_DIR` | `.goblin-platform` locally; `/data/platform` in the image | Persistent private application data |
| `GOBLIN_PASSWORD_HASH_FILE` | `.goblin-secrets/owner-password` supplied by the local launcher; `/etc/goblin/owner-password` in the Sandbox manifest | Required password verifier file in every environment |
| `GOBLIN_HOST` | `127.0.0.1` locally; `0.0.0.0` in the image | Listening interface |
| `GOBLIN_PORT` | `8787` | Listening port |
| `GOBLIN_PUBLIC_ORIGIN` | `http://localhost:8787` | Exact browser origin, used for Host and Origin checks |
| `GOBLIN_ALLOW_INSECURE_HTTP` | `false`; explicitly `true` in the Azure overlay | Allows a public HTTP origin before HTTPS is configured; traffic is unencrypted |
| `GOBLIN_CODEX_COMMAND` | Local pinned native binary, otherwise `codex` on `PATH` | Optional path to the official Codex executable |

Remote origins require HTTPS by default. The Azure overlay explicitly enables
public HTTP with `GOBLIN_ALLOW_INSECURE_HTTP=true`, retaining exact Host/Origin
checks and password authentication. Loopback HTTP is supported for local access
and SSH/kubectl forwarding without that setting. After configuring TLS on the
ingress, set `GOBLIN_PUBLIC_ORIGIN` to the HTTPS address, remove the HTTP opt-in,
and recreate the application pod. HTTPS origins use Secure session cookies.

The application always reads the configured password verifier, including on
localhost. Password collection happens during provisioning, outside the app.
The password is compared exactly, including spaces. A missing or invalid
verifier prevents startup. Password attempts use the existing rate limit and
private session cookies. Restart Goblin after changing the verifier to load the
new password and invalidate current browser sessions.

Codex stores credentials in `codex/auth.json` under the data directory, with
`cli_auth_credentials_store="file"`. It owns ChatGPT refresh and logout. The file
contains credentials in plaintext; the parent directory is private, and storage
and backups must be protected accordingly. The HTTP server never serves it.
Don't copy it into images, source control, chat messages, or shared volumes.

The backend doesn't log RPC payloads, API keys, device codes, upstream error
bodies, or configured passwords. It discards raw Codex stderr because it may
contain authentication details. Error messages give safe retry guidance.

## Automated checks

```bash
make typecheck
make test
make codex-test
make browser-install
make browser-test
```

The test commands build first. `make test` uses the .NET generators for deterministic
schema-generation check, then runs .NET serialization/transport tests and the HTTP
integration tests against the C# host. Type checking also covers tests and scripts.
Playwright loads its TypeScript configuration and browser tests directly. The browser module uses
only type imports from `frontend/src/api/contracts.ts`, so it needs no client framework or bundler.

The integration tests exercise the HTTP and JSON-RPC paths with a fake Codex
process: login notifications, cancellation, expiry, persistence, logout,
credential isolation, access control, and key verification errors. The Codex check
initializes the real pinned Codex binary with a temporary home and verifies
local storage, restart, and logout using a synthetic, unusable key. Browser
tests cover the same UI flow with simulated authentication and model replies,
including mobile layout, clearing API-key inputs, automatic connection checks,
retry and error states, session expiry, account changes, and avoiding repeated
model requests during status polling.
The unit tests also cover prompt validation, event ordering, failed and empty
replies, cancellation, timeouts, and overlapping requests. These
checks do not use personal credentials or run a model task. A real ChatGPT
sign-in remains a manual acceptance check.

See [Codex integration](codex-integration.md) for the C# project layout,
protocol regeneration, and the boundary between Goblin orchestration and Codex Core.
