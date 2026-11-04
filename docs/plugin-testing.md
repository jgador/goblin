# ChatGPT plugin testing

Use this walkthrough to test a private ChatGPT plugin against Goblin in WSL k3s
through the existing Chisel gateway. The whole session is here: start k3s, connect
Chisel, enable public HTTPS, install or reuse the plugin, test it, and return to
localhost. Run commands from the repository root in WSL.

If k3s and Chisel are already installed, go straight to
[Local testing](#local-testing). For other environments, see
[the direct WSL build](#use-the-direct-wsl-development-build) or
[live production](#live-production-testing).

## One-time preparation

Complete these once before the first session:

1. Prepare the [WSL k3s requirements](../deploy/k3s/README.md#requirements).
   On the first `make k3s-start`, choose your Goblin password, open
   `http://localhost:8788`, and wait for installation to finish. Later starts
   resume the same installation with its password and data.
2. [Install the WSL Chisel client](../deploy/chisel/README.md#install-the-wsl-client)
   and obtain the gateway username, password, and trusted server fingerprint
   from its administrator. Keep them in your password manager. The existing
   gateway is already provisioned; testers need no server key, cluster
   kubeconfig, or server deployment.
3. During the first session, follow [Install the plugin](#install-the-plugin).
   Keep the OAuth client secret in your password manager when Goblin displays
   it. Later sessions can reuse that client and the installed ChatGPT plugin.

The [Chisel guide](../deploy/chisel/README.md) contains one-time client installation
and administrator setup. The commands needed for each test session are below.

## Local testing

### Use the installed WSL k3s platform

Use two WSL terminals. Terminal A runs the k3s commands; Terminal B keeps Chisel
connected. These steps use the default k3s browser port **8788**. If your
installation uses another browser port, enter that port when starting Chisel and
use it in the localhost URL when returning to local testing.

#### 1. Start k3s

In Terminal A:

```bash
make k3s-start
```

For an existing installation, this starts or resumes the owned WSL cluster.
If installation is still running, wait for it to finish before deploying.
`make k3s-status` reports `ready` when installation is complete.

#### 2. Start the tunnel

Open Terminal B and paste this Bash command. Enter **8788** at the local port
prompt, followed by the trusted fingerprint and Chisel credentials:

```bash
(
  read -r -p 'Local Goblin port (8787 dev / 8788 k3s): ' chisel_port
  if [[ ! "$chisel_port" =~ ^[0-9]{1,5}$ ]] || (( 10#$chisel_port < 1 || 10#$chisel_port > 65535 )); then
    printf 'Enter a valid local port.\n' >&2
    exit 1
  fi
  read -r -p 'Server fingerprint: ' chisel_fingerprint
  read -r -p 'Chisel username: ' chisel_username
  read -r -s -p 'Chisel password: ' chisel_password
  printf '\n'

  AUTH="${chisel_username}:${chisel_password}" \
    chisel client --fingerprint "$chisel_fingerprint" \
    https://dev-tunnel.goblinboard.com \
    "R:0.0.0.0:8787:127.0.0.1:${chisel_port}"
)
```

Wait for **Connected** and leave Terminal B running throughout the session.
The server-side port is always **8787**; the WSL destination for k3s is **8788**.
`Connected` confirms the tunnel connection. The public Goblin page is ready after
the next step configures its HTTPS origin.

#### 3. Deploy Goblin for the public address

Back in Terminal A:

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make k3s-deploy
```

Wait for deployment to succeed. This builds and imports the current checkout's
application image, applies these settings to its owned WSL platform Sandbox, and
restarts the platform when the image or settings change. PostgreSQL, logging,
credentials, and PVCs stay in place. A separate `dev-start` process or database
profile is unnecessary for this k3s workflow.

The local browser endpoint now forwards directly to the platform Service,
preserving the gateway's Host and HTTPS header through pod replacements.
`http://localhost:8788/` returns `invalid_host` while the public origin is
configured. Use the public HTTPS URL for the rest of the session.

#### 4. Open Goblin and connect the plugin

Open [Goblin's ChatGPT settings](https://dev.goblinboard.com/settings?section=connections&provider=chatgpt)
and enter this installation's Goblin password. For a first installation, follow
[Install the plugin](#install-the-plugin) below. If the plugin is already
installed, continue directly to [the connection test](#test-the-connection-without-work).

All settings, browser sign-in, and consent must use `https://dev.goblinboard.com`.
The tunnel host `dev-tunnel.goblinboard.com` is only for the Chisel client.

## Install the plugin

Do this once per Goblin instance and ChatGPT connection. For the k3s walkthrough,
the origin is `https://dev.goblinboard.com`. A production connection uses that
deployment's HTTPS origin instead. OAuth clients, keys, tokens, and Work grants
belong to the selected instance; create a separate connection for each environment.

1. In Goblin's `/settings?section=connections&provider=chatgpt`, choose **Private
   plugin**. **Prepare** should show that the OAuth server and MCP endpoint are
   enabled. **Continue to Sign-in** opens the authentication setup step.
2. In **Sign-in**, click **Create OAuth client** if no client exists. This step
   creates the client without asking for ChatGPT's callback. Copy and save the
   generated secret; it is shown only when created or rotated. Reuse the saved
   secret for an existing client. Click **Check provider and storage**, then
   continue to **Install**. These local checks verify configuration and storage;
   the ChatGPT connection test below verifies the public connection.
3. Open [ChatGPT Plugins](https://chatgpt.com/plugins) and select
   **+ → Add custom MCP server**. Give the plugin a name that identifies the
   environment and enter Goblin's connection details:

   | Field | WSL k3s value |
   | --- | --- |
   | Server URL | `https://dev.goblinboard.com/mcp` |
   | Authentication | OAuth |
   | OAuth client ID | `goblin-chatgpt` |
   | OAuth client secret | The secret saved from Goblin |
   | OAuth scopes, if requested | `openid offline_access goblin.work.read` |

4. Before starting sign-in from ChatGPT, copy the exact OAuth callback shown by
   its connection form. In Goblin's **Install** step, under **Save ChatGPT's
   callback before sign-in**, paste it into **ChatGPT OAuth callback** and click
   **Save OAuth client**. This preserves your existing secret. The confirmed
   callback for this workflow is:

   ```text
   https://chatgpt.com/connector_platform_oauth_redirect
   ```

   Use the callback displayed for your connection if it differs. Saving it is
   required before sign-in; creating the client with an empty callback does not
   register one. Wildcards are rejected. Installation confirmation stays disabled
   until a callback has been saved.
5. Return to ChatGPT and create the plugin. When the browser opens Goblin's
   HTTPS sign-in page, enter the Goblin owner password and approve the connection.
   Finish **Create as a plugin** and installation in ChatGPT. Confirm the tools
   include `goblin_ping`, `goblin_list_work`, `goblin_get_work_status`,
   `goblin_get_work_attention`, and `goblin_get_work_result`.

Creating and installing the plugin are both required. If an authorization page
already failed before the callback was saved, start the connection again from
ChatGPT to get a fresh sign-in request. Signing in shares no Work; the ping test
needs no Work grants or GitHub sign-in.

## Test the Codex preparation request

This optional acceptance test uses real accounts and model access. Automated tests
use synthetic credentials and a protocol fixture; they do not prove authenticated
Plugin Creator execution, ChatGPT installation, or Chisel connectivity.

1. Complete [Local testing](#local-testing) through opening the public HTTPS
   settings page. In **Sign-in**, create or reuse the OAuth client, save its
   secret, and run **Check provider and storage** as described in
   [Install the plugin](#install-the-plugin). Continue to **Install**. ChatGPT
   supplies the callback later in its connection flow. Keep the tunnel running.
2. In **Connections → Codex**, authenticate Codex with the intended ChatGPT account.
   Install or enable the official Plugin Creator in that same account where
   available. An API-key-only connection should receive an explanation that a
   ChatGPT login is required.
3. Return to **Private plugin → Install**, expand **Prepare with your Codex
   connection**, and choose **Check Plugin Creator access**. Record whether the
   runtime can read the skill and hosted tools, and the source fingerprint if
   returned. Unavailable capabilities must not be presented as successful
   creation. The check starts no model turn or creator action.
4. If this Goblin server is already registered in ChatGPT, enter its
   `plugin_asdk_app_…` technical ID. Otherwise leave it empty. Choose **Prepare
   with Codex**. This is a real request to Codex's default model and counts toward
   the connected plan's limits. Check the configured public `/mcp` URL, issuer,
   OAuth client ID and scopes, the five read-only tools, and the existing ID
   when supplied. The result must still say Goblin has not registered a plugin
   or verified the connection.
5. Use **Copy** beside **Plugin Creator request**. Review the copied text and
   confirm it includes no client secret, owner password, Codex credentials, owner
   subject, or Work data. There is no ZIP download or helper installation in this
   flow; `goblin-plugin-setup` runs internally in Goblin to draft the description.
6. In your authenticated ChatGPT or Codex session with the actual official
   Plugin Creator available, paste, review, and run the copied request. Record the
   supported registration action and any generated package or installation link.
   If the creator cannot register an existing external MCP server, complete
   **Add custom MCP server** manually. A package or link alone does not prove
   registration, installation, or an OAuth grant.
7. Complete the exact callback, secret entry, owner sign-in, consent, and
   Install/Connect steps in [Install the plugin](#install-the-plugin). Then run
   [the ping test](#test-the-connection-without-work) and match its response ID to
   Goblin's log. Only that fresh tool response establishes authenticated
   connectivity through Chisel; test Work access separately.

Also cancel a preparation while it is running, then start a fresh check. Goblin
must stop the pending generation and permit the new operation without retrying
creation. A runtime request for interactive approval should end with a manual
action notice. No approval should be sent automatically. Changing the Codex
account or disconnecting waits for the current operation to finish.

Record the Goblin revision, pinned runtime version, capability fingerprint,
actual creator tools used, registration/package outcome, OAuth outcome, and
matched ping response ID, without credentials. The preparation implementation
deliberately leaves automatic registration unavailable until the real creator's
external-MCP contract can be verified.

## Test the connection without Work

This is step **5** of the k3s session. Once the plugin is installed:

1. After updating Goblin's tool metadata, use **Refresh** on the connection in
   ChatGPT Plugins. Open a new conversation.
2. Type **@** and select the installed Goblin plugin for the intended environment.
3. Ask: **Call goblin_ping now and show every field from the tool response unchanged.**
4. Check the result for a fresh `responseId`, `respondedAtUtc`, and the server's
   `operatingSystem`, `runtime`, and `processArchitecture`.
5. In Goblin, open **Settings → Logs → Open logs** at the same public origin.
   For k3s, this is `https://dev.goblinboard.com/logs/`. Search for the returned
   response ID and find the `goblin_ping response` event. Its `state.ResponseId`
   must match ChatGPT's `responseId`.
6. Call again. The response ID changes and the UTC timestamp advances.

For example (IDs, timestamps, and runtime details vary):

```json
{
  "message": "Goblin MCP connection is working.",
  "responseId": "9c664e391b524f8eaa5fe3c2d892ba9d",
  "respondedAtUtc": "2026-11-01T06:00:00+00:00",
  "operatingSystem": "Linux",
  "runtime": ".NET 10.0.9",
  "processArchitecture": "X64"
}
```

Matching the fresh response ID to Goblin's log confirms that the reply came from
the selected instance. Default Information logging includes this event. The tool
takes no arguments and uses the same OAuth authentication as the Work tools.
Only these public diagnostic fields are returned: no hostname, username, paths,
environment variables, or credentials.

For the direct WSL development build, use
[local terminal logs](#verify-the-local-response) instead of the saved k3s logs.

## Return to local testing

This is step **6** of the k3s session. In Terminal A:

```bash
GOBLIN_PUBLIC_ORIGIN=http://localhost:8788 \
GOBLIN_CHATGPT_ENABLED=false \
make k3s-deploy
```

Use your installation's actual browser port if it differs from 8788. After the
deployment succeeds, stop Chisel with **Ctrl+C** in Terminal B and open
`http://localhost:8788`. Goblin's OAuth and MCP endpoints are disabled, and the
public development address is unavailable. The installed ChatGPT plugin and
Goblin's saved OAuth client, keys, tokens, and Work grants remain for the next
session. Reconnect if ChatGPT requests sign-in or a token has expired.

The k3s instance keeps running. Use `make k3s-stop` only when you also want to
stop the local cluster; it preserves data. Stopping Chisel alone does not restore
Goblin's localhost origin, so run the command above before closing the tunnel.

## Repeat tests

For another k3s session, repeat [steps 1–4](#use-the-installed-wsl-k3s-platform),
run [the ping test](#test-the-connection-without-work), then
[return to localhost](#return-to-local-testing). Reuse the installed Chisel
client, gateway credentials, OAuth client, and ChatGPT plugin.

While the public session is running, keep Chisel connected and deploy further
application edits with:

```bash
make k3s-deploy
```

Unset origin and ChatGPT variables retain the Sandbox's current settings.
Deployment checks the expected running image and readiness. Refresh the ChatGPT
connection after tool metadata changes and test it in a new conversation.
Stop/start also retains the configured origin, so explicitly restore localhost
when finishing the session.

For another onboarding test, remove this environment's connection in ChatGPT and
follow [Install the plugin](#install-the-plugin) again using the saved client
secret. To test revocation, first revoke the connection in Goblin. **Start setup
again** clears browser progress; saved OAuth settings, tokens, and Work grants
stay. If the secret was lost, generate a new one in Goblin and update ChatGPT's
connection; rotation revokes previous connections.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `k3s-deploy` says installation is incomplete | Run `make k3s-start` and wait for `make k3s-status` to report `ready`. For a failed installation, inspect `make k3s-logs` and use `make k3s-retry` after fixing the cause. |
| `502 Bad Gateway` on `dev.goblinboard.com` | Terminal B must show **Connected** and target local port **8788** for k3s. Wait for the public-origin deployment to succeed. A client still targeting the direct build on 8787 must be stopped and reconnected to 8788. |
| `invalid_host` at `http://localhost:8788` | The public HTTPS origin is configured. Use `https://dev.goblinboard.com` during testing, then run both settings in [Return to local testing](#return-to-local-testing). |
| OAuth reports HTTP or an invalid host through Chisel | Run the public-origin `k3s-deploy` command in step 3; it selects the direct platform Service route that preserves Host and HTTPS headers. |
| `invalid_request`, redirect URI is not valid (`ID2043`) | In Goblin's **Install** step, save the exact ChatGPT callback with **Save OAuth client**, then restart sign-in from ChatGPT. The confirmed callback is `https://chatgpt.com/connector_platform_oauth_redirect`. Creating the client in **Sign-in** does not register a callback; saving it in **Install** keeps the existing secret. |
| Fingerprint mismatch or Chisel authentication failure | Check the trusted fingerprint and username/password separately. Ask the gateway administrator for updated verified values if they changed. See [Chisel troubleshooting](../deploy/chisel/README.md#troubleshooting) for server issues. |
| Client closes after gateway deployment | Restart the client. Gateway deployment restarts the Chisel server; use updated credentials or a newly trusted fingerprint if they changed. |
| ChatGPT still shows an old ping reply or tool list | Rebuild and deploy Goblin, refresh the connection's tools in ChatGPT, and start a new conversation with the plugin selected using **@**. |
| Empty Work list or `No Work is shared yet` | Ping requires no grants. To test Work reads, share actual Work in Goblin. Leave **Also share the latest approved result** unchecked unless an approved result exists. |
| `Create the profile with --local or --ssh USER@HOST` | This concerns the optional direct WSL build. Create its database profile in [Prepare WSL](#prepare-wsl). The installed k3s workflow needs no profile. |
| `502 Bad Gateway` on a production origin | Check that deployment's backend and reverse proxy using the [deployment guide](../deploy/azure/README.md#verify). |

## Test Work access

These optional checks follow a successful ping. Ping confirms connectivity and
authentication; Work and approved-result access require separate grants.

1. In Goblin's ChatGPT settings, choose actual Work and click **Share selected
   Work**. Leave **Also share the latest approved result** unchecked unless the
   Work has an approved result. Requesting a missing result rejects the whole grant.
2. In ChatGPT, select the plugin with **@** and call `goblin_list_work`. Compare
   the shared Work ID with Goblin.
3. Call `goblin_get_work_status` and `goblin_get_work_attention` for that Work.
   Compare the returned values with Goblin.
4. To test results, explicitly share the latest approved result, then call
   `goblin_get_work_result`. Confirm it matches the selected approved attempt.
5. Remove the grant in Goblin and confirm that reading the same Work ID is denied.
   Unshared Work and unshared results must stay inaccessible.

GitHub sign-in is separate and unnecessary for ping.

## Other environments

### Use the direct WSL development build

Choose this for application testing outside k3s. It uses local port **8787** and
its own data directory and OAuth keys. Keep a separate ChatGPT connection even
when it shares the installed platform's database.

#### Prepare WSL

With the [development prerequisites](repository-layout.md#build-and-test),
[WSL k3s installation](../deploy/k3s/README.md), and
[PostgreSQL WSL prerequisites](database.md#wsl-and-windows-pgadmin) ready, create
the database profile once from a root WSL shell:

```bash
cargo build --locked -p goblinctl
target/debug/goblinctl db connect wsl --local --forward-port 55432
```

This profile configures access to the existing k3s PostgreSQL `goblin` database;
it does not create a database. The direct build reads and writes the installed
platform's stored data. WSL accesses PostgreSQL at `localhost:5432`; the optional
Windows forwarding port is 55432. Profiles and client certificates default to
`~/.config/goblin/postgres/wsl/`. Keep k3s and its database forwarder running.

#### Start the local build

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

Goblin listens on HTTP port 8787. In a second terminal, use
[Start the tunnel](#2-start-the-tunnel), entering **8787** instead of 8788.
Stop any existing client for k3s before connecting this one. Keep both terminals
running, use `https://dev.goblinboard.com`, and follow the same
[installation](#install-the-plugin) and [ping test](#test-the-connection-without-work).

To capture the direct build's logs, use this alternative start command:

```bash
(
  umask 077
  mkdir -p .artifacts/plugin-testing
  set -o pipefail
  GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
  GOBLIN_CHATGPT_ENABLED=true \
    make dev-start DB_PROFILE=wsl 2>&1 |
    tee .artifacts/plugin-testing/goblin.log
)
```

#### Verify the local response

Find the JSON console event whose `Message` starts with `goblin_ping response`.
Its `State.ResponseId` must match the returned `responseId`. If logs were captured,
replace the placeholder with that ID:

```bash
rg --fixed-strings 'COPY_RESPONSE_ID' .artifacts/plugin-testing/goblin.log
```

Otherwise search the Goblin terminal's scrollback, or call again while watching
the terminal. For further edits, stop and restart `dev-start` on the same port
with the same settings; leave Chisel connected. When finished, stop both Goblin
and Chisel with **Ctrl+C**. Keep the database and persistent data directory so
the connection's identity and keys remain available.

### Live production testing

Use the deployed Goblin instance's public HTTPS origin. Its reverse proxy
connects to that backend directly. The [Azure guide](../deploy/azure/README.md)
covers manual installation, and [deployment and enablement](chatgpt-integration.md#deployment-and-enablement)
defines the HTTPS and proxy requirements.

1. Confirm the build includes `goblin_ping`, has PostgreSQL and persistent
   storage, and uses `GOBLIN_CHATGPT_ENABLED=true` with `GOBLIN_PUBLIC_ORIGIN`
   set to its actual HTTPS origin.
2. Open that origin's `/settings?section=connections&provider=chatgpt` and enter
   its Goblin owner password.
3. Follow [Install the plugin](#install-the-plugin) with that deployment's MCP URL
   and a separate ChatGPT connection.
4. Run [the ping test](#test-the-connection-without-work) and verify its response ID
   in **Settings → Logs → Open logs** on that origin. Refresh the connection and
   repeat after application deployments.

For a published directory listing, see
[directory submission](chatgpt-integration.md#optional-directory-submission).

## Confirmed workflow and verification limits

The user-confirmed WSL k3s workflow starts k3s, connects Chisel to local port 8788,
deploys with the public HTTPS origin and ChatGPT enabled, then installs and tests
the private plugin at `https://dev.goblinboard.com`. Returning to localhost uses
both the loopback origin and `GOBLIN_CHATGPT_ENABLED=false`.

The earlier 2026-11-01 direct-build session returned a dynamic ping reply through
Chisel. Its response ID was not independently matched to a saved log. Work
access, GitHub sign-in, and live production testing were not established by that
ping. Use the response-ID and Work checks above to verify those separate claims.
