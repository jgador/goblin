# Chisel tunneling

This guide covers **one-time Chisel installation** and administrator gateway
setup. The existing gateway connects `https://dev.goblinboard.com` to Goblin in
WSL through `https://dev-tunnel.goblinboard.com`.

For a tester, complete [Install the WSL client](#install-the-wsl-client) once and
save the supplied credentials and fingerprint. Then use
[Plugin testing](../../docs/plugin-testing.md#local-testing) for every session;
that single walkthrough includes k3s startup, the Chisel command, public-origin
deployment, plugin installation, tests, and restoring localhost.

If the client is already installed, go straight to that walkthrough. Gateway
provisioning and deployment are [administrator tasks](#install-the-server-on-ak3s)
and are unnecessary when testing against the existing server.

## Install the WSL client

Run this once in WSL from the repository root. Check whether Chisel is already
installed:

```bash
chisel --version
```

If it reports **1.12.0**, continue to
[Client credentials and fingerprint](#client-credentials-and-fingerprint).
Otherwise install the [Chisel 1.12.0 release](https://github.com/jpillora/chisel/releases/tag/v1.12.0).
The standard x86-64 WSL k3s installation uses the Linux **amd64** binary. This
command downloads it, verifies the release checksum, and installs it on `PATH`:

```bash
(
  set -e
  umask 077
  mkdir -p .artifacts/chisel/client
  cd .artifacts/chisel/client
  curl --fail --location --remote-name \
    https://github.com/jpillora/chisel/releases/download/v1.12.0/chisel_1.12.0_linux_amd64.gz
  curl --fail --location --remote-name \
    https://github.com/jpillora/chisel/releases/download/v1.12.0/chisel_1.12.0_checksums.txt
  sha256sum --check --ignore-missing chisel_1.12.0_checksums.txt
  gzip -dc chisel_1.12.0_linux_amd64.gz > chisel
  sudo install -m 0755 chisel /usr/local/bin/chisel
  chisel --version
)
```

Expect the archive checksum to report **OK** and the installed version to be
**1.12.0**. For a different WSL architecture, choose the matching Linux archive
from the release page. Downloaded files under `.artifacts/chisel/client/` are
temporary; the installed executable is `/usr/local/bin/chisel`.

### Client credentials and fingerprint

Keep the server username, password, and trusted full server fingerprint in a
password manager such as Bitwarden, outside the repository and `.artifacts/`.
Routine connection to the existing gateway requires no AK3S kubeconfig or server
PEM private key. The administrator owns provisioning and key rotation.

The fingerprint is `Base64(SHA256(SSH-encoded server public key))`. It identifies
the server key and is separate from the username/password credential and the
hash of the PEM file bytes. If only the password was saved, obtain the trusted
public fingerprint from the administrator before connecting.

## Connect WSL

Follow the complete [local plugin testing sequence](../../docs/plugin-testing.md#local-testing).
Its [Start the tunnel](../../docs/plugin-testing.md#2-start-the-tunnel) step includes
the connection command. Enter **8788** for installed k3s, **8787** for
`dev-start`, or the browser port chosen for your k3s installation. The remote
listener stays **8787** for all destinations.

The client reads credentials from `AUTH` and connects outbound over HTTPS. WSL
needs no public IP, router port, or VPN. The `0.0.0.0` listener is inside the
server pod; public access goes through the HTTPS ingress.

### Stop the tunnel

For k3s, first follow [Return to local testing](../../docs/plugin-testing.md#return-to-local-testing)
to restore the localhost origin and disable ChatGPT. Then stop the WSL Chisel
client with **Ctrl+C**. The public development address becomes unavailable;
the installed k3s instance keeps running until `make k3s-stop`.

For `dev-start`, stop Goblin and the Chisel client in their terminals.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `502 Bad Gateway` on `dev.goblinboard.com` | Confirm Goblin is reachable on the selected WSL port and the Chisel terminal shows `Connected`. Use 8787 for dev or the configured k3s browser port, normally 8788. |
| OAuth reports HTTP or an invalid host when targeting k3s | Configure the HTTPS public origin with `make k3s-deploy` before tunneling to its browser port. This selects the direct platform Service route. |
| Fingerprint mismatch | Compare the full trusted fingerprint. Obtain a newly verified fingerprint from the administrator if the server key changed. |
| Authentication failure | Check the username and password separately from the fingerprint. The credential is `username:password`. |
| Connection closes after server deployment | Deployment restarts Chisel. Restart the client, using updated credentials or a newly trusted fingerprint if they changed. |

Check the local Goblin listener with:

```bash
ss -ltnp '( sport = :8787 or sport = :8788 )'
```

## Install the server on AK3S

The following administrator steps provision the gateway. Testers using the
existing server only need [Install the WSL client](#install-the-wsl-client), then
[Plugin testing](../../docs/plugin-testing.md#local-testing).

### Gateway

One Chisel server in the [website cluster](../../website/production/README.md)
forwards a public HTTPS address to Goblin in WSL. It runs in the shared `goblin`
namespace with two ingress routes:

| Host | Service port | Purpose |
| --- | --- | --- |
| `dev-tunnel.goblinboard.com` | `chisel:8080` | Authenticated tunnel connection from WSL |
| `dev.goblinboard.com` | `chisel:8787` | The whole local Goblin application |

Both use the `nginx` ingress class and the `letsencrypt-production` ClusterIssuer.
Add DNS A records for both hosts pointing to the cluster's public ingress IP. The
`chisel` Service is ClusterIP; only the HTTPS ingress exposes it. The
`dev.goblinboard.com` route sends `X-Forwarded-Proto: https`, disables response
buffering, and supports WebSockets.

The image is Chisel 1.12.0, pinned by digest. The server runs without root, a
writable root filesystem, capabilities, or a Kubernetes API token. Probes check
the tunnel port, not whether WSL is connected. Keep one replica and the Recreate
strategy: the application listener and its tunnel must be on the same server.

### Administrator bootstrap

Run once, outside GitHub Actions, with the intended AK3S kubeconfig:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply \
  --server-side --field-manager=chisel-bootstrap -f deploy/chisel/namespace.yaml
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply -f deploy/chisel/deployer.yaml
```

The separate field manager keeps existing namespace labels and admission policy.

Create a kubeconfig for ServiceAccount `goblin/chisel-deployer` with the cluster's
API address, embedded CA, and a dedicated credential. Its Role can create resources
in the shared namespace (Kubernetes cannot limit `create` by name), but reads and
patches only Chisel's Deployment, Service, Secret, and Ingresses. It has no RBAC or
namespace rights. Keep the deployer credential out of application pods.

The hosted runner must reach the Kubernetes API with valid TLS. The kubeconfig must
not need local files or an interactive exec plugin. A one-hour TokenRequest token
expires; use a credential with a planned rotation.

### GitHub environment and deployment

Create GitHub environment `ak3s-production`. Under **Deployment branches and tags**,
choose **Selected branches and tags** and add a **Branch** rule `release/*`. Set
review protection as needed. Add these environment secrets:

| Secret | Value |
| --- | --- |
| `AK3S_KUBECONFIG` | Full kubeconfig YAML for `goblin/chisel-deployer`, not a filename or base64 |
| `CHISEL_AUTH` | `username:password` with a strong password and no control characters |
| `CHISEL_SERVER_KEY` | An unencrypted P-256 PEM private key, reused across deployments |

Generate the key once and save it privately:

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

The interactive `CHISEL_AUTH` prompt keeps the value out of shell history. Keep
real credentials out of repository files and uploaded artifacts.

The **Chisel Deploy** workflow is manual and appears only after it is on `master`.
Run it on a `release/` branch, such as `release/0.1`; deployment runs only in
`jgador/goblin`. It runs `make chisel-check`, then `scripts/deploy-chisel.mts`
applies the resources and waits for the rollout. It never applies the
administrator bootstrap or changes website resources.

The authfile allows only the reverse tunnel `R:0.0.0.0:8787`. Each deployment
restarts the server, so changed credentials and keys close open connections and
briefly interrupt local testing.

`CHISEL_AUTH` and `CHISEL_SERVER_KEY` are saved in the GitHub environment. The
deployment script writes `users.json` and `key.pem` into Kubernetes Secret
`goblin/chisel`, which the server mounts read-only. Files under `.artifacts/` are
temporary staging files; keep a private backup of the server key outside that
directory. The WSL client needs only the username/password and trusted public
fingerprint, not the PEM key or cluster configuration.

### Obtain the server fingerprint (administrator)

An administrator can read the server's fingerprint with the intended kubeconfig:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml -n goblin logs deployment/chisel
```

Copy the full SHA-256 `Fingerprint` value; it stays the same while
`CHISEL_SERVER_KEY` stays the same. Give the verified fingerprint to testers
through a trusted channel as described in
[client credentials and fingerprint](#client-credentials-and-fingerprint).

### Verify the server installation

```bash
make chisel-check
```

This checks the manifests and deployment script offline and deploys nothing. After
bootstrap, an administrator can run a server-side dry run against the cluster:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml apply \
  --server-side --dry-run=server -k deploy/chisel
```

After deployment, check the rollout, both certificates, and HTTPS without
bypassing certificate verification. Connect WSL and check that the public OAuth
signing keys belong to the local instance. Follow the
[plugin connection test](../../docs/plugin-testing.md#test-the-connection-without-work)
and optional [Work checks](../../docs/plugin-testing.md#test-work-access).
Check streamed responses and terminal WebSockets, then stop the client and check
that the endpoint becomes unavailable.
