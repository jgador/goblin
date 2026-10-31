# Test the ChatGPT plugin with a local build

ChatGPT needs a public HTTPS address. Two gateways forward the whole Goblin
application to a local build in WSL:

- [Chisel on AK3S](../deploy/chisel/README.md) serves `https://dev.goblinboard.com`.
  That guide owns the server deployment and the WSL client connection.
- The [Azure test VM relay](#prepare-the-test-vm-once) switches a dedicated Azure
  test installation's ingress to WSL through an SSH tunnel. The hostname and TLS
  certificate stay on Azure.

Always open the gateway's HTTPS address. OAuth registrations, keys, and Work grants
belong to the instance that serves the request; a local address does not connect to
the public one.

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

After the gateway is connected, open `https://<gateway-hostname>/settings?section=chatgpt`
and follow the [private plugin setup](chatgpt-integration.md#guided-private-setup).
Sign in with the local Goblin password. The ChatGPT server URL is the origin plus
`/mcp`.

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
