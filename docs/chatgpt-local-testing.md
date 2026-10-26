# Test the ChatGPT plugin with a local build

Use a dedicated Azure test VM as the HTTPS gateway while Goblin runs in WSL.
Production installations route directly to their own Goblin instance. Development
relay mode explicitly changes the test VM's default ingress upstream to a private
SSH tunnel. Its hostname, HTTPS certificate, and certificate renewal remain on Azure.

| Mode | Request flow |
| --- | --- |
| Production | Browser/ChatGPT → Azure HTTPS → F5 NGINX → Azure Goblin |
| Development relay | Browser/ChatGPT → Azure HTTPS → F5 NGINX → relay proxy → SSH → WSL Goblin |

The development relay forwards the entire application: settings, owner login,
OAuth discovery, authorization, consent, tokens, signing keys, and MCP.
Terminal connections use WebSocket forwarding, and MCP/events remain streamed.
Open the
Azure HTTPS address in your browser to see the locally built UI. Entering that
address into settings opened at `http://localhost:8788` does not connect the two
instances; registrations and Work sharing belong to the instance handling the request.

## Prepare the test VM once

Manually install the current Goblin/installer bundle using the
[Azure installation](../deploy/azure/README.md). Use a dedicated test resource group,
choose a distinct DNS label, supply the certificate contact email, and save the SSH
private key. Enable SSH only from your current public IP/CIDR. Wait for installation
status `ready`, then verify the HTTPS `goblinUrl` in a browser without a certificate warning.

Azure's generated `cloudapp.azure.com` hostname already points to its static public
IP. No custom DNS record is required. Keep public ports 80 and 443 available for
HTTP-01 issuance and renewal. The relay adds no public port and requires no SSH
`GatewayPorts` setting: OpenSSH listens on a private Unix socket on the VM.

The commands below are developer tooling in `xtask`, not production goblinctl
commands. They connect to an already installed VM; they do not sign in to Azure,
provision resources, or delete cloud resources. Use the same checkout for relay
start/status/stop, and keep `.artifacts/chatgpt-relay/session.json` until stopping.

## Start the local build

Use the selected test VM's actual hostname in place of the example. Keep local
PostgreSQL, owner credentials, and persistent application storage. A database-free
development host cannot enable the plugin.

```bash
GOBLIN_PUBLIC_ORIGIN=https://goblin-test.southeastasia.cloudapp.azure.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

This starts the application on its normal direct HTTP port 8787. The relay terminates
public TLS on Azure and preserves the canonical Host, HTTPS scheme, Origin, and
Authorization headers. Goblin retains its existing cookie, CSRF, OAuth, and Work
sharing checks. Use the public HTTPS address for browser testing.

If testing a full local Kubernetes installation instead, configure its platform
Sandbox with this HTTPS public origin and `GOBLIN_CHATGPT_ENABLED=true`, restart
the platform pod, and forward directly to its service:

```bash
sudo k3s kubectl -n goblin port-forward service/platform 8877:8787
```

Keep this terminal open and pass `--local-port 8877` when starting the relay. Bypass
the local HTTP ingress so it cannot overwrite the forwarded HTTPS scheme or route
using a different Host. For quick code iterations, the direct development host
avoids rebuilding the whole Kubernetes installation.

## Connect the relay

From another WSL terminal in the checkout:

```bash
make relay-start ARGS="--origin https://goblin-test.southeastasia.cloudapp.azure.com --ssh goblinadmin@goblin-test.southeastasia.cloudapp.azure.com --identity /path/to/saved-key.pem"
make relay-status
```

The SSH hostname must equal the HTTPS hostname. The first connection asks you to
verify the VM's SSH host fingerprint; subsequent connections use the relay's own
known-hosts file. Private key contents are handled by OpenSSH and are never read
by the relay tool. A short checkout path is needed for OpenSSH's Unix control socket.
Use Azure Portal's VM Run Command to read the public host fingerprint before
accepting the first SSH connection:

```bash
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Start checks the local OAuth signing keys, the VM's configured HTTPS origin and
certificate, and the current ingress upstream. It creates a small NGINX proxy only
on the test VM, opens the SSH reverse socket, and changes the default `/` route.
It then verifies that public HTTPS returns the local signing keys. Certificate
challenge paths stay on Azure. Resources are marked with a session owner so another
relay cannot silently replace or remove them.
The ingress's WebSocket service changes atomically with the default route, so
F5 accepts the selected backend during both relay start and restoration.

Open `https://<test-hostname>/settings?section=chatgpt`. Follow the private plugin
flow there, save ChatGPT's exact callback, unlock with the **local Goblin password**,
approve the connection, and explicitly share local sample Work. The Azure instance's
accounts, OAuth registrations, and Work do not become part of the local instance.

## Repeat tests

For a routine code change, stop the local application, build/start it again, and
keep its PostgreSQL state and OAuth keys. The existing SSH tunnel reconnects to
the application when the same local port becomes available. Reload the public
HTTPS page. If tools or authentication changed, refresh the connection in ChatGPT
and start a new conversation.

For a fresh onboarding test:

1. Remove the test connection in ChatGPT. Revoke the local connection in Goblin
   first if you want to exercise revocation.
2. Use **Start setup again** in Goblin to clear this browser's setup confirmations
   and drafts. This action keeps persisted OAuth settings and Work sharing.
3. Stop and deliberately reset only the disposable local instance/database when
   the test needs fresh owner identity, keys, clients, and Work. Existing
   [local reset](../deploy/local/README.md) and [database](database.md) tools keep
   their ownership and destructive-scope checks.
4. Start the local build with the same HTTPS origin and port. Recreate the ChatGPT
   connection and local sample Work after a complete state reset.

Keep the Azure test VM, hostname, and certificate between these iterations.
The existing WSL reset keeps the relay's session directory; keep it for explicit
restoration after testing. Do not clear all of `.artifacts/` while a relay is active.
Changing OAuth identity/keys invalidates old connections; resetting wizard progress
alone does not revoke tokens or recreate server state.

## Stop and recover

```bash
make relay-stop
```

Stop explicitly restores the Azure platform upstream, removes only the session's
proxy resources/socket directory, and closes its SSH master. It preserves Azure's
Goblin data and certificate. The test VM resumes serving its installed Goblin.

If WSL stops, SSH disconnects, or the local application is unavailable, the selected
development upstream returns an unavailable response. It never falls back to Azure
Goblin automatically. Restore production routing with `relay-stop`. A failed start
may retain session state for this cleanup; inspect `relay-status` and then stop.
Ownership mismatches or concurrent ingress edits fail without overwriting another
route. If session state is lost, inspect the ingress ownership annotation and relay
resources manually on the dedicated test VM before changing or deleting anything.

## Manual acceptance

After offline checks pass, run this on a manually deployed test VM:

1. Verify HTTPS without bypassing certificate verification. Verify public HTTP
   redirects to HTTPS after completion, and the certificate reports `Ready`.
2. Start a relay to the local build. Confirm a visible local UI change appears at
   the Azure HTTPS address and public OAuth signing keys match the local instance.
3. Complete a private ChatGPT connection, exact callback, owner unlock, consent,
   and shared Work read. Confirm unshared Work is denied.
4. Rebuild locally and repeat. Perform a deliberate local state reset and reconnect
   to verify onboarding from scratch. Confirm the Azure certificate survives.
5. Interrupt the local host/tunnel and confirm there is no Azure fallback. Stop
   the relay and confirm direct Azure routing and its own OAuth identity return.
6. Verify certificate challenge routing remains valid in both modes. Inspect
   cert-manager renewal status; avoid repeated production issuance during local resets.

Offline tests do not establish Azure network reachability, actual public issuance,
or a completed ChatGPT conversation. VM allocation still depends on regional
availability and Dsv6 quota.
