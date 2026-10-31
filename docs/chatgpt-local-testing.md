# Test the ChatGPT plugin with a local build

ChatGPT needs a public HTTPS address. Two gateways forward the whole application
(browser UI, login, OAuth, MCP, streaming, and terminal WebSockets) to Goblin
running in WSL:

- [Chisel on AK3S](../deploy/chisel/README.md) gives a stable
  `https://dev.goblinboard.com` on the website cluster. That guide owns its server
  deployment and client connection.
- The Azure test VM relay below switches a dedicated Azure test installation's
  ingress to WSL through a private SSH tunnel. Its hostname, TLS certificate, and
  renewal stay on Azure.

Open the gateway's HTTPS address to use the local build. Entering that address into
settings on a local address does not connect the instances. OAuth registrations,
keys, and Work grants belong to the instance that serves the request. Production
routes directly to the installed Goblin instance.

## Start the local build

Set the gateway's HTTPS origin. The example uses Chisel; for the relay, use the test
VM's hostname, such as `https://goblin-test.southeastasia.cloudapp.azure.com`.
Keep local PostgreSQL, owner credentials, and persistent application storage. A
database-free development host cannot enable the plugin.

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

This starts Goblin on direct HTTP port 8787. The gateway terminates TLS, preserves
Host, Origin, and Authorization, and sends `X-Forwarded-Proto: https`.

After the gateway is connected, open `https://<gateway-hostname>/settings?section=chatgpt`
and follow the [private plugin setup](chatgpt-integration.md#guided-private-setup).
Save ChatGPT's exact callback, unlock with the **local Goblin password**, approve
the connection, and share local sample Work. The ChatGPT server URL is the origin
plus `/mcp`.

## Repeat tests

For a routine code change, keep the gateway connected, stop the local application,
then build and start it again on the same port. Keep its PostgreSQL state and OAuth
keys. Reload the public HTTPS page. If tools or authentication changed, refresh the
connection in ChatGPT and start a new conversation.

For a fresh onboarding test:

1. Remove the test connection in ChatGPT. To test revocation, first revoke the
   connection in local Goblin.
2. Use **Start setup again** in Goblin to clear this browser's setup confirmations
   and drafts. Saved OAuth settings and Work grants stay.
3. When the test needs a fresh owner identity, keys, clients, and Work, stop and
   reset only the disposable local instance and database with the
   [local reset](../deploy/local/README.md) and [database](database.md) tools.
4. Start the local build with the same HTTPS origin and port. After a full reset,
   recreate the ChatGPT connection and local sample Work.

Keep the gateway, hostname, and certificate between iterations. Changing the OAuth
identity or keys invalidates old connections. Resetting setup progress alone does
not revoke tokens or change server state.

## Prepare the test VM once

Manually install the current Goblin installer bundle with the
[Azure installation](../deploy/azure/README.md). Use a dedicated test resource group
and a distinct DNS label, supply the certificate contact email, and save the SSH
private key. Allow SSH only from your current public IP or CIDR. Wait for status
`ready`, then open the HTTPS `goblinUrl` and check there is no certificate warning.

The generated `cloudapp.azure.com` hostname already points to the VM's static
public IP; no custom DNS record is needed. Keep public ports 80 and 443 open for
HTTP-01 issuance and renewal. The relay opens no public port and needs no SSH
`GatewayPorts` setting: OpenSSH listens on a private Unix socket on the VM.

The relay commands are developer tooling in `xtask`, not goblinctl. They do not
sign in to Azure or create or delete cloud resources. Use the same checkout for
start, status, and stop, and keep
`.artifacts/chatgpt-relay/session.json` until you stop the relay.
`make dev-reset` keeps this directory; do not delete it while a relay is active.

To test a full local Kubernetes installation instead of the development host, set
the platform Sandbox's `GOBLIN_PUBLIC_ORIGIN` to the HTTPS origin and
`GOBLIN_CHATGPT_ENABLED=true`, restart the platform pod, and forward directly to
its service:

```bash
sudo k3s kubectl -n goblin port-forward service/platform 8877:8787
```

Keep this terminal open and pass `--local-port 8877` to `relay-start`. This bypasses
the local HTTP ingress, which would otherwise overwrite the forwarded HTTPS scheme
or route by a different Host.

## Connect the relay

From another WSL terminal in the checkout:

```bash
make relay-start ARGS="--origin https://goblin-test.southeastasia.cloudapp.azure.com --ssh goblinadmin@goblin-test.southeastasia.cloudapp.azure.com --identity /path/to/saved-key.pem"
make relay-status
```

The SSH hostname must equal the HTTPS hostname, and the SSH user must not be root.
On the first connection, OpenSSH asks you to check the VM's host fingerprint; later
connections use the relay's own known-hosts file. Read the fingerprint first with
Azure Portal's VM Run Command:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

The relay passes only the key path to OpenSSH. Use a short checkout path: the
OpenSSH control socket path must stay under 100 characters.

Start creates a small NGINX proxy, `goblin-dev-relay`, on the VM and switches the
ingress `/` route to it. Certificate challenge paths stay on Azure. Start fails
unless the public HTTPS address then serves the local OAuth signing keys. Relay
resources carry a session owner annotation, so another relay cannot replace or
remove them.

## Stop and recover

```bash
make relay-stop
```

Stop restores the `platform` upstream, removes only this session's proxy resources
and socket directory, and closes the SSH connection. Azure's Goblin data and
certificate stay, and the VM serves its installed Goblin again.

If WSL stops, SSH disconnects, or the local application is down, the public address
returns an unavailable response. It never falls back to Azure's Goblin; run
`relay-stop` to restore it. A failed start can keep session state for this cleanup:
check `relay-status`, then stop. Owner mismatches or concurrent ingress edits fail
without changing another route. If session state is lost, inspect the ingress
`goblin.dev/relay-owner` annotation and the `goblin-dev-relay` resources on the test
VM before changing or deleting anything.

## Manual acceptance

After offline checks pass, check the relay on a manually deployed test VM:

1. Check HTTPS without bypassing certificate verification. Check that public HTTP
   redirects to HTTPS and the certificate reports `Ready`.
2. Start the relay. Confirm a visible local UI change appears at the Azure HTTPS
   address and the public OAuth signing keys match the local instance.
3. Complete a private ChatGPT connection, exact callback, owner unlock, consent,
   and shared Work read. Confirm unshared Work is denied.
4. Rebuild locally and repeat. Reset local state and reconnect to test onboarding
   from scratch. Confirm the Azure certificate survives.
5. Interrupt the local host or tunnel and confirm there is no Azure fallback. Stop
   the relay and confirm direct Azure routing and its own OAuth identity return.
6. Check that certificate challenge routing works in both modes. Check cert-manager
   renewal status, and avoid repeated production issuance during local resets.

Offline tests do not prove Azure network reachability, public certificate issuance,
or a completed ChatGPT conversation. VM allocation depends on regional availability
and Dsv6 quota.
