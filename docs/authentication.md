# Owner access and connection checks

Goblin has one owner password and one Codex connection. Open **Settings → Connections →
Codex** to sign in with a ChatGPT account or OpenAI API key.
[GitHub repository access](git-repository-intent.md), [Slack](slack-integration.md), and
the [private ChatGPT plugin](chatgpt-integration.md) have separate authorization.

## Run locally

Follow [development setup](repository-layout.md#build-and-test), then use
`make dev-start ARGS=--without-database` for connection-only development or a
[migrated database profile](database.md#wsl-and-windows-pgadmin) for Work. Choose and
confirm the owner password in the terminal on first start, then open
**http://localhost:8787**. There is no default password.

### Password setup and storage

Every host requires a valid `GOBLIN_PASSWORD_HASH_FILE`; missing or invalid verifiers
stop startup. Local development and the WSL installer share
`.goblin-secrets/owner-password`. The file contains a salted PBKDF2-SHA256 verifier with
owner-only permissions, not the original password. Git and Docker exclude private
credential storage.

```bash
make password-set
make password-set ARGS=--replace
```

The first command preserves an existing verifier; replacement is explicit. For
unattended setup, supply `GOBLIN_LOCAL_PASSWORD` in the process environment. It does not
replace an existing verifier without `--replace`. Never put passwords or verifiers in
command arguments.

Restart direct development after replacement to load the verifier and end current
sessions. For an installed host, use its
[password procedure](../deploy/k3s/README.md#password-storage) or
[Azure procedure](../deploy/azure/reference.md#goblin-password). Password comparison
preserves spaces exactly.

Login creates a private, HttpOnly, SameSite session cookie. **Lock workspace** ends the
current session; restarting ends all sessions. Login is throttled after repeated
failures. [Workspace.cs](../backend/src/Goblin.Web/Access/Workspace.cs) owns session and
throttle limits.

## Codex sign-in

Before ChatGPT device sign-in, enable device-code authentication in the personal
account's security settings or the workspace role's Codex permissions. A workspace
administrator may be needed. Use the provider instructions shown by Goblin; labels can
change.

Select **Continue with ChatGPT**, open the provider page, and enter the device code.
Cancel and start again if it expires. A pending sign-in survives a page refresh.
**Disconnect Codex** removes cached credentials; it does not revoke an OpenAI API key
remotely.

For API-key sign-in, Goblin checks model-list access before saving through Codex.
Rejected authentication is not saved; an access or rate-limit response can retain the
key with an unverified notice. A model connection check resolves that notice.

ChatGPT requests count toward plan limits; API-key requests are billed in the OpenAI
Platform. Disconnect before switching authentication methods.

## Connection checks

A check makes a real model request on the saved account using a restricted temporary
thread. Successful model output establishes connection status; it does not complete or
create Work. Checks have no repository tools, web access, apps, plugins, hooks, or
memories.

Opening a signed-in connection page or choosing **Check again** can run a check. Polling
does not repeat it. Results are page-local; closing or reloading requests cancellation.
Checks and account changes are serialized, and account changes are rejected while an
attempt holds the connection. Safe failures exclude credentials and raw provider errors.
See [Codex integration](codex-integration.md).

## Run in Docker

Create a local verifier, then build an image and place the verifier in a private volume
owned by the image's application user:

```bash
docker build -t goblin-platform:local .
docker run --rm -i --user 0:0 --entrypoint sh \
  -v goblin-platform-password:/password goblin-platform:local \
  -c 'umask 077; cat > /password/owner-password; chown 1000:1000 /password/owner-password' \
  < .goblin-secrets/owner-password
docker run --rm --name goblin-platform \
  -p 127.0.0.1:8787:8787 \
  -v goblin-platform-data:/data \
  -v goblin-platform-password:/etc/goblin:ro \
  -e GOBLIN_PASSWORD_HASH_FILE=/etc/goblin/owner-password \
  -e GOBLIN_WORK_ENABLED=false \
  goblin-platform:local
```

This connection-only launch disables Work. Keep the same volumes on recreation to
preserve sign-in. Full installations need the
[database and execution setup](execution-hosting.md#configuration-and-deployment).

## Configuration and storage

The [environment catalogs](environment-variables.md) describe supported settings;
[host parsing](../backend/src/Goblin.Web/Hosting/ApplicationOptionsConfiguration.cs) and
[Dockerfile](../Dockerfile) own defaults and image overrides.

The exact public origin controls Host and Origin checks. Non-loopback HTTP requires
explicit insecure-HTTP enablement; installed public access uses HTTPS and Secure
cookies. Local forwarding uses a loopback origin. A private proxy must preserve the
configured browser authority.

Each persistent data directory has one owner and one application controller. Codex runs
with a private home and working directory rather than inherited machine credentials. Its
plaintext credential file is private runtime storage. Protect the directory and backups;
never copy credentials into images, chat, version control, or execution PVCs. Pod
replacement preserves the application volume; deleting it can lose credentials.

The backend excludes passwords, device codes, keys, RPC payloads, and upstream errors
from logs and public views. Formatting output as JSON does not redact sensitive data.

## Workspace timezone

After unlock, confirm a suggested timezone or choose one under **Settings → Time &
date**. Detection alone never saves a preference. The saved choice applies across
devices to absolute display times; stored instants remain UTC. Refresh open pages and
log views after a change.

IP detection calls **ipwho.is** directly from the browser, exposing its public IP and
normal request metadata to that provider without credentials or a referrer. A VPN may
affect the suggestion. Failed detection falls back to the browser timezone, then UTC.
Goblin stores only the chosen IANA zone, not the visitor's IP.

The preference is private application-volume state independent of PostgreSQL. Saves are
atomic and reject stale edits; the file is not a multi-controller configuration store.
Bundled timezone label data is generated by
[update-timezone-places.mts](../scripts/update-timezone-places.mts). Provider changes
require an actual HTTPS/CORS browser check, and logging UI upgrades need
[timezone integration checks](logging.md#verification).

## Verification

Use [test selection](repository-layout.md#choosing-verification). Fixture sign-in and
`make codex-test` use synthetic credentials and submit no authenticated model task.
Manual acceptance must check sign-in, persistence across restart, disconnect, expired
codes, billing notices, and connection-check cancellation with the intended provider
account.
