# ChatGPT plugin testing

Use an isolated Goblin instance to test private ChatGPT access. A successful provider
check is not proof that ChatGPT can reach it. Installation and consent are defined once
in [ChatGPT setup](chatgpt-integration.md#connect).

## One-time preparation

Complete the [WSL k3s requirements](../deploy/k3s/README.md#requirements) and
[Chisel client installation](../deploy/chisel/README.md#install-the-wsl-client). The
gateway administrator supplies credentials and a trusted fingerprint; testers need no
cluster kubeconfig or server private key. Keep credentials and the OAuth client secret
in a password manager.

## Local testing

### Use the installed WSL k3s platform

Use two WSL terminals from the repository root. The default installed browser port is
**8788**; use your actual port throughout if it differs.

1. In Terminal A, start or resume the owned installation with `make k3s-start`.
   Wait for installation to complete; `make k3s-status` reports `ready`.
2. In Terminal B, start Chisel with the command below. Enter **8788** for k3s.
   Wait for **Connected** and keep the terminal running.
3. In Terminal A, apply the public origin and ChatGPT setting with the deployment
   command below. Wait for success before opening the public URL.
4. Open [ChatGPT settings](https://dev.goblinboard.com/settings?section=connections&provider=chatgpt),
   unlock with this instance's password, and
   [connect the plugin](chatgpt-integration.md#connect).
5. Run the [connection test](#test-the-connection-without-work), then restore
   [local access](#return-to-local-testing).

### Start the tunnel

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

The remote listener stays **8787**; the local destination is your browser port.
`Connected` proves only tunnel connectivity.

### Configure the public origin

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make k3s-deploy
```

Deployment builds the checkout, retains database and PVCs, and configures the loopback
forwarder to preserve the gateway's Host and HTTPS header. Localhost browser access
returns `invalid_host` while this public origin is set. Use
`https://dev.goblinboard.com` for settings, sign-in, and consent; the tunnel hostname is
for the Chisel client only.

## Test the connection without Work

1. Refresh the plugin tools in ChatGPT after metadata changes and open a new
   conversation with the intended plugin selected using **@**.
2. Ask: **Call goblin_ping now and show every field from the tool response unchanged.**
3. Match its `responseId` to the `goblin_ping response` event in Goblin's
   **Settings → Tools → Logs**, using `state.ResponseId`.
4. Call again and confirm a different response ID and advancing UTC timestamp.

This establishes a fresh invocation of the selected instance. Ping needs no Work, GitHub
account, or Codex sign-in. For direct development, match `State.ResponseId` in console
logs. Capture logs privately under `.artifacts/plugin-testing/` when needed; do not
upload credentials or private runtime data.

## Test Work access

Use an isolated instance because these checks write durable data.

1. Create Work before consent, then confirm the plugin can list and read it.
   Compare details with Goblin and follow continuations to the end.
2. Reserve IDs and create Work through the plugin. Repeat the same command ID
   and exact input; confirm one Work item and the original receipt.
3. Add context with a fresh command ID and version. Confirm updates in both
   interfaces; an old expected version must conflict.
4. Read an available attempt result through all continuations.
5. Revoke ChatGPT connections in Goblin and confirm existing tokens can neither
   read nor write. Reconnect and approve both Work scopes to resume.

ChatGPT may confirm write calls according to conversation and workspace settings. These
checks do not establish authenticated model execution or GitHub publication.

## Return to local testing

Before closing the tunnel, run in Terminal A:

```bash
GOBLIN_PUBLIC_ORIGIN=http://localhost:8788 \
GOBLIN_CHATGPT_ENABLED=false \
make k3s-deploy
```

Use your actual browser port. After success, stop Chisel with **Ctrl+C** and open
localhost. OAuth/MCP are disabled; saved client credentials, keys, and tokens stay for
the next session. `make k3s-stop` also stops the cluster while preserving data. Stopping
the tunnel alone does not restore the localhost origin.

For later sessions, repeat the local sequence with the same installation and plugin.
Plain `make k3s-deploy` retains installed origin and enablement settings; stop/start
does too. Always restore both settings when finishing. Use
[ChatGPT setup](chatgpt-integration.md#connect) for callback changes, lost secrets,
rotation, and installation confirmation.

## Other environments

### Use the direct WSL development build

Prepare a [PostgreSQL profile](database.md#wsl-and-windows-pgadmin), then:

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

Connect Chisel to **8787**. Keep a separate ChatGPT connection: this process has its own
owner subject and OAuth keys even when it shares the installed database. It writes to
that selected database. Stop Goblin and Chisel after testing.

### Live production testing

Use the deployment's actual HTTPS origin and
[proxy requirements](chatgpt-integration.md#deployment). Complete consent and the same
response-ID test. Work writes require the intended test scope; fixture tests do not
prove public HTTPS or production behavior.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Installation incomplete | Wait for `k3s-status`; inspect `k3s-logs` and use explicit installation retry after fixing a failure. |
| Public 502 | Chisel must be connected to the actual browser port; wait for public-origin deployment. |
| Local `invalid_host` | Use the configured public URL, or restore both localhost settings. |
| OAuth HTTP/host error | Apply the public-origin deployment so the forwarder preserves HTTPS headers. |
| Invalid callback | Save the exact ChatGPT callback in Goblin, wait for confirmation, and restart sign-in from ChatGPT. |
| Fingerprint/authentication failure | Verify fingerprint and credentials separately with the administrator; see [Chisel troubleshooting](../deploy/chisel/README.md#troubleshooting). |
| Old tools or replies | Refresh tools and start a new ChatGPT conversation with the intended plugin. |

Report actual checks separately: provider/storage readiness, public ping matched to a
log, Work reads/writes and revocation, authenticated execution, and publication. Do not
carry historical test results forward as current verification.
