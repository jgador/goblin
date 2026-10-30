# Chisel on AK3S

Run one Chisel server in the existing HTTPS cluster and connect its client from
WSL. The server shares namespace `goblin` with the website. Its two HTTPS ingress
routes are:

| Host | Service port | Purpose |
| --- | --- | --- |
| `dev-tunnel.goblinboard.com` | `chisel:8080` | Authenticated WebSocket tunnel connection from WSL |
| `dev.goblinboard.com` | `chisel:8787` | Entire local Goblin application, including browser UI, OAuth, and MCP |

Both routes use the existing F5 NGINX `nginx` ingress class and
`letsencrypt-production` ClusterIssuer documented for the
[website cluster](../../website/production/README.md). Add DNS A records for both
subdomains pointing to that cluster's public ingress IP. Cert-manager issues
separate certificates and keeps its HTTP-01 challenge paths on the cluster.
The existing website continues using its own ingress and release workflow.

The application Service is ClusterIP. Only the HTTPS ingress exposes its ports.
F5 preserves the public Host and Authorization/Origin headers; the development
route supplies `X-Forwarded-Proto: https`, disables response buffering, and supports
WebSockets. All application paths use the same local Goblin instance.

The pinned official image is Chisel **1.12.0**, including its multi-architecture
registry digest. The server runs without root, a writable root filesystem, Linux
capabilities, or a mounted Kubernetes API token. Credentials are mounted from the
`chisel` Kubernetes Secret. Probes check the server's control port, not whether
WSL is connected. Keep one replica and the Recreate strategy: the application
listener and its tunnel connection must belong to the same server.

## Administrator bootstrap

Use the intended AK3S kubeconfig explicitly. Ensure the shared `goblin` namespace
exists and install Chisel's dedicated deployer identity once, outside GitHub
Actions:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply \
  --server-side --field-manager=chisel-bootstrap -f deploy/chisel/namespace.yaml
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply -f deploy/chisel/deployer.yaml
```

The namespace apply uses its own field manager to preserve existing namespace
labels and admission policy.
Chisel's pod security settings belong to its Deployment.

Create a kubeconfig for ServiceAccount `goblin/chisel-deployer` using this
cluster's API address, embedded CA, and a dedicated credential. The Role permits
initial creation and named updates of Chisel's Deployment, Service, credential
Secret, and two Ingresses, without RBAC or namespace administration.
Kubernetes cannot limit `create` by object name, so the Role's creation permissions
apply across the shared namespace. Named read and patch permissions target Chisel's
resources. Keep the deployer credential out of application pods.

The runner must reach the cluster's Kubernetes API with valid TLS. Use a
credential with an intentional renewal/rotation policy; a one-hour TokenRequest
token will expire. A kubeconfig relying on local files or an interactive exec
plugin is unsuitable for the hosted runner.

## GitHub environment and deployment

Create GitHub environment **`ak3s-production`**. Under **Deployment branches and
tags**, choose **Selected branches and tags** and add a **Branch** rule with
pattern **`release/*`**, such as `release/0.1`. Configure its review protection
as appropriate for this production cluster. Add these environment secrets:

| Secret | Value |
| --- | --- |
| `AK3S_KUBECONFIG` | Full kubeconfig YAML for `goblin/chisel-deployer`, not its filename or base64 encoding |
| `CHISEL_AUTH` | A tunnel username and strong password in `username:password` form; no control characters |
| `CHISEL_SERVER_KEY` | An unencrypted P-256 PEM private key, reused across deployments |

Generate the server key once, saving it privately rather than printing it:

```bash
umask 077
mkdir -p .artifacts/chisel/credentials
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:prime256v1 \
  -out .artifacts/chisel/credentials/key.pem
gh secret set CHISEL_SERVER_KEY --env ak3s-production \
  < .artifacts/chisel/credentials/key.pem
gh secret set CHISEL_AUTH --env ak3s-production
gh secret set AK3S_KUBECONFIG --env ak3s-production < /path/to/chisel-deployer.yaml
```

The interactive `gh secret set CHISEL_AUTH` prompt keeps the value out of shell
history. Keep real credentials out of repository files and uploaded artifacts.

Merge the workflow into `master` so GitHub exposes its manual dispatch action,
then create a release branch containing that workflow version. Run **Chisel
Deploy** manually and select the release branch, such as `release/0.1`.
The deploy job accepts only `release/` branches in `jgador/goblin`;
`master` remains the development branch.

The workflow first runs offline checks, then uses `ak3s-production` secrets to
apply Chisel's resources and wait for the server rollout. Deployment is
serialized. It writes temporary credentials
under `.artifacts/chisel/` with private permissions, removes them on completion or
failure, and sends Secret manifests directly to kubectl without printing them.
It never applies the administrator bootstrap or modifies website resources.

The generated authfile grants only `^R:0\.0\.0\.0:8787$`. SOCKS, ordinary forward
tunnels, other interfaces, and other reverse ports are denied. Deployment restarts
the single server after updating the Secret so revocation/key changes also close
established connections. This briefly interrupts an active local testing session.

The existing **Website Release** workflow still uses `website-production`. These
GitHub environments can target the same cluster with separate deployment identities.

## Connect WSL and test Goblin

Install the matching [Chisel release](https://github.com/jpillora/chisel/releases/tag/v1.12.0)
in WSL. Read the server's public fingerprint with an administrator kubeconfig:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml -n goblin logs deployment/chisel
```

Copy the full SHA-256 `Fingerprint` value. Persisting `CHISEL_SERVER_KEY` keeps
this fingerprint stable. Configure the local application:

```bash
GOBLIN_PUBLIC_ORIGIN=https://dev.goblinboard.com \
GOBLIN_CHATGPT_ENABLED=true \
make dev-start DB_PROFILE=wsl
```

Keep its PostgreSQL and persistent data. In another WSL terminal, enter the same
tunnel credential privately, then start the reverse tunnel:

```bash
read -r -s -p 'Chisel username:password: ' CHISEL_AUTH
printf '\n'
export AUTH="$CHISEL_AUTH"
unset CHISEL_AUTH
chisel client --fingerprint 'COPY_FULL_SERVER_FINGERPRINT' \
  https://dev-tunnel.goblinboard.com R:0.0.0.0:8787:127.0.0.1:8787
unset AUTH
```

`AUTH` is Chisel's own client credential input. The client opens an outbound
HTTPS/WebSocket connection; WSL needs no public IP, incoming router port, or VPN.
The explicit `0.0.0.0` reverse listener allows the cluster Service to reach it;
its public exposure is still through the HTTPS ingress.

Open `https://dev.goblinboard.com/settings?section=chatgpt`, complete the private
plugin flow using the local Goblin password, save ChatGPT's exact callback, and
share local sample Work. ChatGPT's server URL is `https://dev.goblinboard.com/mcp`.
See [ChatGPT integration](../../docs/chatgpt-integration.md) for access boundaries.

For local rebuilds, keep the client connected and restart Goblin on the same port.
Preserve PostgreSQL and OAuth keys. Refresh ChatGPT metadata after tool or
authentication changes. A fresh local state reset requires recreating the plugin
connection and local grants. DNS, the tunnel server, and public certificates
survive local resets. Stop the client with Ctrl+C when finished; an unavailable
client/application produces an unavailable response at the development hostname.

## Verification

```bash
make chisel-check
kubectl kustomize deploy/chisel > .artifacts/chisel/resources.yaml
```

These checks render resources offline and verify credential validation, the
single-port ACL, explicit cluster selection, and cleanup on deployment failure.
They do not deploy anything. For administrator review after bootstrap, server-side
dry-run against the intended cluster checks admission and installed API support:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply \
  --server-side --dry-run=server -k deploy/chisel
```

After actual deployment, check the rollout, both certificates, and HTTPS without
bypassing certificate verification. Connect WSL and confirm public OAuth signing
keys belong to the local instance. Complete a real ChatGPT connection and verify
shared Work is readable and unshared Work is denied. Check streamed responses and
terminal WebSockets, rebuild locally, then stop the client and confirm the
development endpoint becomes unavailable. A server rollout alone does not establish
public certificate issuance, WebSocket reachability, or ChatGPT integration.
