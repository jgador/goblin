# ChatGPT plugin testing

Test the Goblin plugin from ChatGPT against either a local build or a deployed
production instance. Choose the setup below, then use the same
[plugin installation](#install-the-plugin) and [connection test](#test-the-connection-without-work).

| Environment | Setup | Goblin public origin |
| --- | --- | --- |
| [Local](#local-testing) | WSL k3s or a direct WSL development build, through the existing Chisel gateway | `https://dev.goblinboard.com` |
| [Live production](#live-production-testing) | Running deployed Goblin with public HTTPS | Your deployment's origin, such as `https://app.example.com` |

Always use the selected instance's public HTTPS address for settings and OAuth.
OAuth clients, keys, tokens, and Work grants belong to that instance. Create a
separate ChatGPT connection for each environment.

## Local testing

The local workflow forwards public HTTPS to Goblin in WSL through the existing
Chisel gateway. [Chisel tunneling](../deploy/chisel/README.md) owns server and
client installation, credentials, fingerprints, connection commands, and tunnel
troubleshooting. This guide owns Goblin startup, plugin installation, and tests.

### Use the installed WSL k3s platform

With the [WSL k3s installation](../deploy/k3s/README.md) ready, deploy your checkout
and configure the same application settings used in production. Then connect the
Chisel client in a second terminal as described below; it provides the public
HTTPS connection.

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make k3s-deploy
```

This command explicitly selects this checkout's owned WSL cluster. It builds and
imports the application image, applies the two settings to the platform Sandbox,
and restarts the platform pod when the image or settings change. PostgreSQL,
logging, credentials, and PVCs stay in k3s. No database profile or `dev-start`
process is needed.

For an HTTPS public origin, the existing WSL browser endpoint on port **8788**
forwards directly to the platform Service. This preserves the Chisel gateway's
Host and HTTPS header for OAuth and follows pod replacements. A different browser
port chosen during installation still applies. Use the public HTTPS address for
Goblin's UI; localhost remains the tunnel destination and health endpoint.
Opening `http://localhost:8788/` directly now returns `invalid_host` because
Goblin accepts the configured public hostname. This is expected after changing
the public origin.

#### Connect the public address

`make k3s-deploy` does not start the Chisel client. The public address returns
`502 Bad Gateway` while the tunnel is disconnected or cannot reach Goblin.

1. Follow [Connect WSL](../deploy/chisel/README.md#connect-wsl) in another terminal,
   choosing **8788** as the local Goblin port. If a previous client targets the
   direct development build on 8787, stop it and reconnect using 8788.
2. Wait for **Connected** and leave that terminal running. Open
   `https://dev.goblinboard.com/settings?section=chatgpt` and continue to
   [Install the plugin](#install-the-plugin).

Sign in and authorize this k3s instance; the direct development instance has a
separate data directory and OAuth keys even when it shares the database.

After further edits, use `make k3s-deploy`. Unset variables retain the configured
origin and ChatGPT enablement in the Sandbox. Keep Chisel running; no manual
Sandbox edit or Kubernetes port-forward is needed. Stop/start retains this
configuration; a reset and reinstall restores the defaults.

To return to the default loopback UI, explicitly configure both settings again:

```bash
GOBLIN_PUBLIC_ORIGIN=http://localhost:8788 \
GOBLIN_CHATGPT_ENABLED=false \
make k3s-deploy
```

Use your installation's actual browser port if it differs from 8788.

### Use the direct WSL development build

Use `dev-start` for quick application edits and tests outside k3s. Choose this
setup instead of the installed platform when that is the behavior you need to test.

#### Prepare WSL

Run from the repository root in WSL with the
[development prerequisites](repository-layout.md#build-and-test).

After the [WSL k3s installation](../deploy/k3s/README.md) and
[PostgreSQL WSL prerequisites](database.md#wsl-and-windows-pgadmin), create the
database profile once from a root WSL shell:

```bash
cargo build --locked -p goblinctl
target/debug/goblinctl db connect wsl --local --forward-port 55432
```

Reuse the PostgreSQL server already running in WSL k3s for the installed Goblin
platform. You do not need another PostgreSQL installation or database. The `wsl`
profile configures client access to that server's existing `goblin` database;
it does not provision a database. The native development build reads and writes
the same database as the installed platform, so testing uses its stored data.

The local installation exposes PostgreSQL to WSL on `localhost:5432`. The optional
Windows forwarding port is 55432. The profile and client certificates live under
`~/.config/goblin/postgres/wsl/` by default. With the profile present,
`make dev-start` refreshes access itself. Keep k3s and the PostgreSQL forwarder
running. Goblin started by that command runs as a native WSL .NET process on port
8787; the installed k3s platform remains available on `http://localhost:8788`
with the default local installation port.

#### Start the local build

Set the Chisel gateway's HTTPS origin.
Use local PostgreSQL; a database-free development host cannot enable the plugin.

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

Goblin listens on HTTP port 8787.

Goblin writes logs to this terminal. To save them for response-ID verification,
use this alternative start command:

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

This log is temporary diagnostic output. Keep the persistent Goblin data
directory and database; they own the
[OAuth identity, keys, and tokens](chatgpt-integration.md#durable-identity-keys-and-schema).

#### Connect the existing Chisel gateway

With Goblin running, use a second WSL terminal to follow
[Connect WSL](../deploy/chisel/README.md#connect-wsl). Install the client first if
needed, as described in that guide. Choose **8787** as the local Goblin port.

Continue to [Install the plugin](#install-the-plugin) using
`https://dev.goblinboard.com`. Keep Goblin and the Chisel client running throughout
the test. When finished, stop Goblin and follow the Chisel guide's
[stop instructions](../deploy/chisel/README.md#stop-the-tunnel).

## Live production testing

Use the running Goblin deployment's public HTTPS origin. Its reverse proxy
connects directly to the deployed backend. The
[Azure installation guide](../deploy/azure/README.md) covers manual deployment;
[plugin deployment and enablement](chatgpt-integration.md#deployment-and-enablement)
defines the HTTPS and proxy requirements.

1. Confirm the deployed build includes `goblin_ping`, uses PostgreSQL and
   persistent Goblin storage, and has `GOBLIN_CHATGPT_ENABLED=true` with
   `GOBLIN_PUBLIC_ORIGIN` set to its actual HTTPS origin.
2. Open that origin's `/settings?section=chatgpt` page and sign in with the
   deployed instance's Goblin owner password.
3. Continue to [Install the plugin](#install-the-plugin) using that origin.
4. Use **Settings → Logs → Open logs** on the same instance for
   [response-ID verification](#verify-the-production-response).

The installation and tool tests below apply to a private plugin connected to a
production instance. Testing a published directory listing is covered by
[directory submission](chatgpt-integration.md#optional-directory-submission).

## Install the plugin

Use the selected environment's public origin throughout these steps. The
[guided private setup](chatgpt-integration.md#guided-private-setup) describes the
saved setup state and OAuth access boundaries.

1. Open `/settings?section=chatgpt` on that origin and choose **Private plugin**.
2. In **Sign-in**, create the OAuth client. Keep the generated secret available
   for the next step; it is shown only when created or rotated. The client ID is
   `goblin-chatgpt`.
3. In [ChatGPT Plugins](https://chatgpt.com/plugins), select **+ → Add custom MCP
   server**. Give the plugin a name that identifies the environment and enter the
   MCP URL supplied by Goblin, such as `https://dev.goblinboard.com/mcp` locally
   or `https://app.example.com/mcp` in production. Select OAuth and enter the
   client ID and secret.
4. Save the exact callback URL displayed by ChatGPT in Goblin's **Install** step.
5. In ChatGPT, select **Create as a plugin**, review the discovered tools, and
   install the plugin. The tools should include `goblin_ping`.
6. Complete sign-in on the selected Goblin HTTPS origin with that instance's
   owner password and approve the connection.

Creating and installing the plugin are both required. After server metadata
changes, use **Refresh** on the connection in ChatGPT Plugins and start a new
conversation. See the official
[connect and test guide](https://developers.openai.com/plugins/deploy/connect-chatgpt).

## Test the connection without Work

After installing and signing into the plugin, you can test the connection without
sharing Work, approving a result, or signing into GitHub:

1. Refresh the plugin's tools in ChatGPT after updating Goblin, then open a new
   conversation.
2. Type **@** and select the installed Goblin plugin for the intended environment.
3. Ask: **Call goblin_ping now and show every field from the tool response unchanged.**
4. Check the tool result for a fresh `responseId`, `respondedAtUtc`, and the
   server's `operatingSystem`, `runtime`, and `processArchitecture`.
5. Match the returned ID to the `goblin_ping response` event in the selected
   instance's logs. Use [local terminal logs](#verify-the-local-response) for the
   direct development build or [deployed logs](#verify-the-production-response)
   for WSL k3s and production.
6. Call again. The response ID changes and the UTC timestamp advances.

The tool is on the selected instance's `/mcp` endpoint and takes
no arguments. It uses the same OAuth authentication as the Work tools. For example
(IDs, timestamps, and runtime details vary):

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

The fixed message or plausible environment details alone do not prove that
ChatGPT called the tool. Match the fresh response ID to Goblin's log. The log
records generation of the reply; displaying the same ID in ChatGPT confirms the
reply reached ChatGPT. Default Information logging includes this event; see
[saved logs](logging.md) if logging levels or destinations have been changed.
Only these public diagnostic fields are returned: no hostname, username, paths,
environment variables, or credentials.

`operatingSystem: Linux` is expected for WSL. `runtime` reports the .NET runtime
of the process that answered; the SDK used to build it and the SDK/runtime pins
in the [Dockerfile](../Dockerfile) are separate values. `respondedAtUtc` uses UTC;
Manila time is eight hours later.

### Verify the local response

In the terminal running Goblin, find the JSON console event whose `Message`
starts with `goblin_ping response`. Its `State.ResponseId` must match the
returned `responseId`.

If you captured the log during local setup, replace the placeholder with the
returned ID:

```bash
rg --fixed-strings 'COPY_RESPONSE_ID' .artifacts/plugin-testing/goblin.log
```

Without log capture, search the Goblin terminal's scrollback. If that output is
gone, call again while watching the terminal and compare the new ID.

### Verify the production response

On the selected production origin, open **Settings → Logs → Open logs** or
`/logs/`. Search for the returned response ID and find the `goblin_ping response`
event. VictoriaLogs exposes the structured ID as `state.ResponseId`; it must
equal the `responseId` returned in ChatGPT. The raw JSON console field is
`State.ResponseId`. See [saved logs](logging.md) for collection and retention.

The returned runtime information describes the deployed process. Compare it to
the expected deployment, then call again and verify the new ID and timestamp.

## Test Work access

These are additional tests after ping succeeds. A successful ping confirms
connectivity and authentication; it does not establish Work or approved-result
access.

1. In Goblin's ChatGPT settings on the selected instance, choose an actual Work
   item and click **Share selected Work**. Leave **Also share the latest approved
   result** unchecked unless that Work has an approved result. Requesting a
   missing approved result rejects the whole sharing request.
2. In ChatGPT, select the plugin with `@` and ask it to call `goblin_list_work`.
   Confirm the selected Work appears and compare its ID with Goblin.
3. Ask for that Work's status and attention using `goblin_get_work_status` and
   `goblin_get_work_attention`. Compare the returned values with Goblin.
4. To test results, explicitly share the latest approved result, then call
   `goblin_get_work_result`. Confirm it matches the selected approved attempt.
5. Remove the Work grant in Goblin and confirm that reading the same Work ID is
   denied. Unshared Work and unshared results must stay inaccessible.

GitHub sign-in is a separate flow and is unnecessary for the ping test. These
Work checks were not established by the successful local ping in this session.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `Create the profile with --local or --ssh USER@HOST` | Create the WSL profile in [Prepare WSL](#prepare-wsl), then rerun `make dev-start DB_PROFILE=wsl`. |
| Local development address is unavailable or the tunnel fails | Follow [Chisel troubleshooting](../deploy/chisel/README.md#troubleshooting). |
| `502 Bad Gateway` on the production address | Check the deployed backend's readiness and reverse proxy using the [deployment guide](../deploy/azure/README.md#verify). |
| ChatGPT still shows the old fixed ping reply | Rebuild and restart Goblin, refresh the connection's tools in ChatGPT, and start a new conversation with the plugin selected using `@`. |
| Empty Work list or `No Work is shared yet` | Ping requires no grants. To test Work reads, select actual Work and share it in Goblin. Leave **Also share the latest approved result** unchecked unless an approved result exists; requesting a missing approved result prevents the grant. |
| GitHub remains `Connecting` with no device code | GitHub sign-in is a separate flow. It is unnecessary for `goblin_ping`; investigate GitHub CLI/device-code behavior separately. |

The ping checks connectivity and authentication. Work grants, approved-result
access, and GitHub sign-in need their own tests.

## Repeat tests

For a local code change, keep Chisel connected, stop the local application, then
build and start it again on the same port. PostgreSQL state and OAuth keys stay.
Reload the public page. If tools or authentication changed, refresh the connection
in ChatGPT and start a new conversation. After updating a production deployment,
refresh that connection's tools and run the same tests against its origin.

For another onboarding test, remove the environment's test connection in ChatGPT
and install it again. To test revocation, first revoke the connection in Goblin.
**Start setup again** clears this browser's setup progress; saved OAuth settings,
tokens, and Work grants stay.

### Reset local test data

For a fresh local owner identity, keys, clients, and Work:

1. Stop the local Goblin process.
2. Reset the local instance and database with the
   [k3s reset](../deploy/k3s/README.md#stop-resume-or-start-clean)
   and [database](database.md) tools.
3. Start the local build with the same HTTPS origin and port, then recreate the
   ChatGPT connection and sample Work.

Changing the OAuth identity or keys invalidates old connections.

## Validation from the local session

The 2026-11-01 session used the local Chisel workflow. The Chisel client reported
`Connected`, and ChatGPT returned a dynamic `goblin_ping` reply with a response ID,
UTC timestamp, Linux, .NET 10.0.9, and X64. The returned ID was not independently
matched to a saved Goblin log; use the verification steps above to complete that
check. Work access, GitHub sign-in, and live production testing were not validated
by that connection test.
