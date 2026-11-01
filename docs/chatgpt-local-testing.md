# Test the ChatGPT plugin with a local build

ChatGPT needs a public HTTPS address. Two gateways forward the whole Goblin
application to a local build in WSL:

- [Chisel on AK3S](../deploy/chisel/README.md) serves `https://dev.goblinboard.com`.
  That guide owns server deployment; the WSL testing steps below use the existing
  gateway.
- The [Azure test VM relay](#prepare-the-test-vm-once) switches a dedicated Azure
  test installation's ingress to WSL through an SSH tunnel. The hostname and TLS
  certificate stay on Azure.

Always open the gateway's HTTPS address. OAuth registrations, keys, and Work grants
belong to the instance that serves the request; a local address does not connect to
the public one.

## Prepare WSL

Run from the repository root in WSL with the
[development prerequisites](repository-layout.md#build-and-test). The Chisel path
needs a [Chisel 1.12.0 client](https://github.com/jpillora/chisel/releases/tag/v1.12.0),
the server username and password, and a trusted full server fingerprint.
Keep the credential and verified fingerprint in a password
manager such as Bitwarden. The client needs no server PEM private key or AK3S
kubeconfig; the administrator owns server provisioning and fingerprint rotation.

After the [local installation](../deploy/local/README.md) and
[PostgreSQL WSL prerequisites](database.md#wsl-and-windows-pgadmin), create the
database profile once from a root WSL shell:

```bash
cargo build --locked -p goblinctl
target/debug/goblinctl db connect wsl --local --forward-port 55432
```

The local installation runs PostgreSQL in k3s and exposes it to WSL on
`localhost:5432`. The optional Windows forwarding port is 55432. The profile and
client certificates live under `~/.config/goblin/postgres/wsl/` by default. With
the profile present, `make dev-start` refreshes access itself. Goblin started by
that command runs as a native WSL .NET process.

## Start the local build

Set the gateway's HTTPS origin. The example uses Chisel; for the relay, use the test
VM's hostname, such as `https://goblin-test.southeastasia.cloudapp.azure.com`.
Use local PostgreSQL; a database-free development host cannot enable the plugin.

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

Goblin listens on HTTP port 8787. The gateway terminates TLS and sends
`X-Forwarded-Proto: https`.

After connecting either gateway, open
`https://<gateway-hostname>/settings?section=chatgpt` and follow the
[private plugin setup](chatgpt-integration.md#guided-private-setup).

Goblin writes logs to this terminal. To save them for response-ID verification,
use this alternative start command:

```bash
(
  umask 077
  mkdir -p .artifacts/chatgpt-local-testing
  set -o pipefail
  GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
  GOBLIN_CHATGPT_ENABLED=true \
    make dev-start DB_PROFILE=wsl 2>&1 |
    tee .artifacts/chatgpt-local-testing/goblin.log
)
```

This log is temporary diagnostic output. Keep the persistent Goblin data
directory and database; they own the
[OAuth identity, keys, and tokens](chatgpt-integration.md#durable-identity-keys-and-schema).

## Connect the existing Chisel gateway

In a second WSL Bash terminal, enter the trusted fingerprint, username, and
password saved outside the repository:

```bash
(
  read -r -p 'Server fingerprint: ' chisel_fingerprint
  read -r -p 'Chisel username: ' chisel_username
  read -r -s -p 'Chisel password: ' chisel_password
  printf '\n'

  AUTH="${chisel_username}:${chisel_password}" \
    chisel client --fingerprint "$chisel_fingerprint" \
    https://dev-tunnel.goblinboard.com \
    R:0.0.0.0:8787:127.0.0.1:8787
)
```

Wait for `Connected` and keep both terminals open. The full fingerprint is
`Base64(SHA256(SSH-encoded server public key))`; it identifies the server key and
is separate from the username/password credential. A saved password is enough
for client authentication once you also have the trusted public fingerprint.

Open `https://dev.goblinboard.com/settings?section=chatgpt` and follow the
[private plugin setup](chatgpt-integration.md#guided-private-setup): create the
OAuth client in Goblin, enter `https://dev.goblinboard.com/mcp` in ChatGPT's
**Add custom MCP server** form with OAuth, and copy the generated client secret
into that form. Save the exact callback displayed by ChatGPT in Goblin, then
create and install the plugin in ChatGPT. Sign in on Goblin's HTTPS page with the
local Goblin owner password and approve the connection.

## Test the connection without Work

After installing and signing into the plugin, you can test the connection without
sharing Work, approving a result, or signing into GitHub:

1. Keep Goblin and the Chisel client running.
2. Refresh the plugin's tools in ChatGPT after rebuilding Goblin, then open a new
   conversation.
3. Type **@** and select your installed Goblin plugin.
4. Ask: **Call goblin_ping now and show every field from the tool response unchanged.**
5. Check the tool result for a fresh `responseId`, `respondedAtUtc`, and the
   server's `operatingSystem`, `runtime`, and `processArchitecture`.
6. In the terminal running Goblin, find the JSON console event whose `Message`
   starts with `goblin_ping response`. Its `State.ResponseId` must match the
   returned `responseId`. This matches the response to an actual authenticated
   invocation on your local Goblin process.
7. Call again. The response ID changes and the UTC timestamp advances.

If you saved the Goblin log above, replace the placeholder with the returned ID:

```bash
rg --fixed-strings 'COPY_RESPONSE_ID' .artifacts/chatgpt-local-testing/goblin.log
```

Without log capture, search the Goblin terminal's scrollback. If that output is
gone, call again while watching the terminal and compare the new ID.

The tool is on the existing `https://dev.goblinboard.com/mcp` endpoint and takes
no arguments. It uses the same OAuth authentication as the Work tools. For example
(IDs, timestamps, and runtime details vary):

```json
{
  "message": "Goblin MCP connection is working.",
  "responseId": "9c664e391b524f8eaa5fe3c2d892ba9d",
  "respondedAtUtc": "2026-11-01T06:00:00+00:00",
  "operatingSystem": "Linux",
  "runtime": ".NET 10.0.12",
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

## Troubleshoot this setup

| Symptom | Check |
| --- | --- |
| `Create the profile with --local or --ssh USER@HOST` | Create the WSL profile in [Prepare WSL](#prepare-wsl), then rerun `make dev-start DB_PROFILE=wsl`. |
| `502 Bad Gateway` on the public address | Confirm Goblin is listening on local port 8787 and the Chisel terminal shows `Connected`. Both processes must remain running. |
| Fingerprint mismatch or authentication failure | Check the full trusted fingerprint separately from the username/password. Obtain a new trusted fingerprint from the administrator if the server key changed. |
| ChatGPT still shows the old fixed ping reply | Rebuild and restart Goblin, refresh the connection's tools in ChatGPT, and start a new conversation with the plugin selected using `@`. |
| Empty Work list or `No Work is shared yet` | Ping requires no grants. To test Work reads, select actual Work and share it in Goblin. Leave **Also share the latest approved result** unchecked unless an approved result exists; requesting a missing approved result prevents the grant. |
| GitHub remains `Connecting` with no device code | GitHub sign-in is a separate flow. It is unnecessary for `goblin_ping`; investigate GitHub CLI/device-code behavior separately. |

For a local listener check:

```bash
ss -ltnp '( sport = :8787 )'
```

The ping checks connectivity and authentication. Work grants, approved-result
access, and GitHub sign-in need their own tests.

## Repeat tests

For a code change, keep the gateway connected, stop the local application, then
build and start it again on the same port. PostgreSQL state and OAuth keys stay.
Reload the public page. If tools or authentication changed, refresh the connection
in ChatGPT and start a new conversation.

For a fresh onboarding test:

1. Remove the test connection in ChatGPT. To test revocation, first revoke the
   connection in local Goblin.
2. Use **Start setup again** in Goblin. This clears only this browser's setup
   progress; saved OAuth settings, tokens, and Work grants stay.
3. For a fresh owner identity, keys, clients, and Work, reset the local instance
   and database with the [local reset](../deploy/local/README.md#stop-resume-or-start-clean)
   and [database](database.md) tools.
4. Start the local build with the same HTTPS origin and port, then recreate the
   ChatGPT connection and sample Work.

Changing the OAuth identity or keys invalidates old connections.

## Prepare the test VM once

Install Goblin manually with the [Azure installation](../deploy/azure/README.md).
Use a dedicated test resource group and DNS label, and save the SSH private key.
Allow SSH only from your public IP or CIDR. Wait for status `ready`, then open the
HTTPS `goblinUrl` and check there is no certificate warning. Keep public ports 80
and 443 open for certificate issuance and renewal.

The relay is developer tooling in `cargo xtask relay` (`make relay-*`). It does not
sign in to Azure or create or delete cloud resources. It opens no public port; SSH
uses a private Unix socket on the VM. Use the same checkout for start, status, and
stop, and do not delete `.artifacts/chatgpt-relay/` while a relay is active.

To test a full local Kubernetes installation instead of `make dev-start`, set the
platform Sandbox's `GOBLIN_PUBLIC_ORIGIN` to the HTTPS origin and
`GOBLIN_CHATGPT_ENABLED=true`, restart the platform pod, and forward its service:

```bash
sudo k3s kubectl -n goblin port-forward service/platform 8877:8787
```

Keep this terminal open and pass `--local-port 8877` to `relay-start`. This
bypasses the local ingress, which would change the forwarded scheme or Host.

## Connect the relay

From another WSL terminal in the checkout:

```bash
make relay-start ARGS="--origin https://goblin-test.southeastasia.cloudapp.azure.com --ssh goblinadmin@goblin-test.southeastasia.cloudapp.azure.com --identity /path/to/saved-key.pem"
make relay-status
```

The SSH hostname must equal the HTTPS hostname, and the SSH user must not be root.
Check the VM's host fingerprint on the first connection. Read it with Azure Portal's
VM Run Command:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Use a short checkout path: the SSH control socket path must stay under 100
characters.

Start creates the NGINX proxy `goblin-dev-relay` on the VM and points the ingress
`/` route to it. Certificate challenge paths stay on Azure. Start fails unless the
public address then serves the local OAuth signing keys. Relay resources carry the
session's `goblin.dev/relay-owner` annotation; another session cannot change them.

## Stop and recover

```bash
make relay-stop
```

Stop restores the `platform` upstream, removes this session's proxy resources, and
closes the SSH connection. The VM then serves its installed Goblin again.

If WSL, SSH, or the local application stops, the public address returns an
unavailable response; it never falls back to Azure's Goblin. Run `relay-stop` to
restore it. After a failed start, check `relay-status`, then stop. If
`.artifacts/chatgpt-relay/session.json` is lost, inspect the ingress
`goblin.dev/relay-owner` annotation and the `goblin-dev-relay` resources on the VM
before changing or deleting anything.

## Manual acceptance

On a test VM:

1. Check HTTPS without bypassing certificate verification, and check that HTTP
   redirects to HTTPS.
2. Start the relay. Check that a local UI change appears at the Azure address.
3. Complete a private ChatGPT connection and read shared Work. Check that unshared
   Work is denied.
4. Interrupt the local host or tunnel and check there is no Azure fallback. Stop
   the relay and check that Azure's own Goblin and OAuth identity return.
5. Check certificate renewal status. Avoid repeated production certificate
   issuance during local resets.
