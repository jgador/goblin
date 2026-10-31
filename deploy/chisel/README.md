# Chisel on AK3S

Run one Chisel server in the existing HTTPS website cluster and connect its client
from WSL. The server shares namespace `goblin` with the website. Its two HTTPS
ingress routes are:

| Host | Service port | Purpose |
| --- | --- | --- |
| `dev-tunnel.goblinboard.com` | `chisel:8080` | Authenticated WebSocket tunnel connection from WSL |
| `dev.goblinboard.com` | `chisel:8787` | Entire local Goblin application, including browser UI, OAuth, and MCP |

Both routes use the F5 NGINX `nginx` ingress class and the `letsencrypt-production`
ClusterIssuer of the [website cluster](../../website/production/README.md). Add DNS
A records for both subdomains pointing to that cluster's public ingress IP.
Cert-manager issues a certificate for each host and keeps HTTP-01 challenge paths
on the cluster. The website keeps its own ingress and release workflow.

The `chisel` Service is ClusterIP; only the HTTPS ingress exposes its ports. The
`dev.goblinboard.com` route sends `X-Forwarded-Proto: https`, disables response
buffering, and supports WebSockets.

The image is the official Chisel **1.12.0**, pinned by digest. The server runs
without root, a writable root filesystem, Linux capabilities, or a Kubernetes API
token. Credentials come from the `chisel` Secret. Probes check the tunnel port, not
whether WSL is connected. Keep one replica and the Recreate strategy: the
application listener and its tunnel connection must be on the same server.

## Administrator bootstrap

Use the intended AK3S kubeconfig explicitly. Ensure the shared `goblin` namespace
exists and install Chisel's deployer identity once, outside GitHub Actions:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply \
  --server-side --field-manager=chisel-bootstrap -f deploy/chisel/namespace.yaml
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply -f deploy/chisel/deployer.yaml
```

The separate field manager keeps existing namespace labels and admission policy.

Create a kubeconfig for ServiceAccount `goblin/chisel-deployer` with this cluster's
API address, embedded CA, and a dedicated credential. Its Role allows creating and
updating Chisel's Deployment, Service, Secret, and two Ingresses, without RBAC or
namespace administration. Kubernetes cannot limit `create` by object name, so
creation is allowed across the shared namespace; reads and patches are limited to
Chisel's resource names. Keep the deployer credential out of application pods.

The runner must reach the cluster's Kubernetes API with valid TLS. Use a credential
with a planned rotation; a one-hour TokenRequest token expires. A kubeconfig that
needs local files or an interactive exec plugin does not work on the hosted runner.

## GitHub environment and deployment

Create GitHub environment **`ak3s-production`**. Under **Deployment branches and
tags**, choose **Selected branches and tags** and add a **Branch** rule with
pattern **`release/*`**, such as `release/0.1`. Set review protection as needed for
this production cluster. Add these environment secrets:

| Secret | Value |
| --- | --- |
| `AK3S_KUBECONFIG` | Full kubeconfig YAML for `goblin/chisel-deployer`, not its filename or base64 encoding |
| `CHISEL_AUTH` | A tunnel username and strong password in `username:password` form; no control characters |
| `CHISEL_SERVER_KEY` | An unencrypted P-256 PEM private key, reused across deployments |

Generate the server key once and save it privately instead of printing it:

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

GitHub shows the manual dispatch action only after the workflow is on `master`.
Create a release branch that contains it, then run **Chisel Deploy** manually on
that branch, such as `release/0.1`. The deploy job runs only for `release/`
branches in `jgador/goblin`.

The workflow runs `make chisel-check`, then uses the `ak3s-production` secrets to
apply Chisel's resources and wait for the rollout. Deployments run one at a time.
Temporary credentials are written under `.artifacts/chisel/` with private
permissions and removed on success or failure; Secret manifests go to kubectl on
standard input and are never printed. The workflow never applies the administrator
bootstrap or changes website resources.

The generated authfile allows only `^R:0\.0\.0\.0:8787$`. SOCKS, forward tunnels,
other interfaces, and other reverse ports are denied. Each deployment restarts the
server after updating the Secret, so revoked credentials and key changes also close
open connections. This briefly interrupts an active local testing session.

The **Website Release** workflow uses the separate `website-production` environment
and deployment identity for the same cluster.

## Connect WSL and test Goblin

Install the matching [Chisel release](https://github.com/jpillora/chisel/releases/tag/v1.12.0)
in WSL. Read the server's public fingerprint with an administrator kubeconfig:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml -n goblin logs deployment/chisel
```

Copy the full SHA-256 `Fingerprint` value. Keeping the same `CHISEL_SERVER_KEY`
keeps this fingerprint stable. Start the local build with origin
`https://dev.goblinboard.com` as described in
[ChatGPT local testing](../../docs/chatgpt-local-testing.md#start-the-local-build).
In another WSL terminal, enter the tunnel credential privately, then start the
reverse tunnel:

```bash
read -r -s -p 'Chisel username:password: ' CHISEL_AUTH
printf '\n'
export AUTH="$CHISEL_AUTH"
unset CHISEL_AUTH
chisel client --fingerprint 'COPY_FULL_SERVER_FINGERPRINT' \
  https://dev-tunnel.goblinboard.com R:0.0.0.0:8787:127.0.0.1:8787
unset AUTH
```

Chisel's client reads its credential from `AUTH`. The client opens an outbound
HTTPS WebSocket connection, so WSL needs no public IP, router port, or VPN. The
`0.0.0.0` reverse listener lets the cluster Service reach it; public access still
goes only through the HTTPS ingress.

Then test at `https://dev.goblinboard.com/settings?section=chatgpt` and follow
[ChatGPT local testing](../../docs/chatgpt-local-testing.md) for repeat and reset
steps. DNS, the tunnel server, and public certificates survive local resets. Stop
the client with Ctrl+C when finished; the development hostname then returns an
unavailable response.

## Verification

```bash
make chisel-check
```

This renders the resources offline and checks credential validation, the
single-port ACL, explicit cluster selection, and cleanup on deployment failure.
It deploys nothing. After bootstrap, an administrator can check admission and API
support with a server-side dry run against the intended cluster:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply \
  --server-side --dry-run=server -k deploy/chisel
```

After deployment, check the rollout, both certificates, and HTTPS without bypassing
certificate verification. Connect WSL and confirm the public OAuth signing keys
belong to the local instance. Complete a real ChatGPT connection, then check that
shared Work is readable, unshared Work is denied, and streamed responses and
terminal WebSockets work. Stop the client and confirm the development endpoint
becomes unavailable. A server rollout alone does not prove certificate issuance,
WebSocket reachability, or ChatGPT integration.
