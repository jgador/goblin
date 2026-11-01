# Chisel on AK3S

One Chisel server in the [website cluster](../../website/production/README.md)
forwards a public HTTPS address to Goblin in WSL for
[ChatGPT local testing](../../docs/chatgpt-local-testing.md). It runs in the shared
`goblin` namespace with two ingress routes:

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
writable root filesystem, capabilities, or a Kubernetes API token. Probes check the
tunnel port, not whether WSL is connected. Keep one replica and the Recreate
strategy: the application listener and its tunnel must be on the same server.

## Administrator bootstrap

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

## GitHub environment and deployment

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

## Connect WSL

Install the matching [Chisel release](https://github.com/jpillora/chisel/releases/tag/v1.12.0)
in WSL and follow the complete
[local testing walkthrough](../../docs/chatgpt-local-testing.md#prepare-wsl).
It covers the WSL database profile, local Goblin startup, private credential
prompt, OAuth plugin installation, and verifying a fresh response ID in logs.
Routine testing against this existing gateway uses credentials and a trusted
fingerprint saved externally; it requires no AK3S kubeconfig.

The client reads its credential from `AUTH` and opens an outbound HTTPS connection,
so WSL needs no public IP, router port, or VPN. The `0.0.0.0` listener is inside
the server pod; public access still goes only through the HTTPS ingress.

Stop the WSL client with Ctrl+C when finished; the public application hostname
then returns an unavailable response.

### Obtain the server fingerprint (administrator)

An administrator can read the server's fingerprint with the intended kubeconfig:

```bash
kubectl --kubeconfig /path/to/ak3s-admin.yaml -n goblin logs deployment/chisel
```

Copy the full SHA-256 `Fingerprint` value; it stays the same while
`CHISEL_SERVER_KEY` stays the same. It is the Base64-encoded SHA-256 hash of the
SSH-encoded server public key, not the username/password or a hash of the PEM
file bytes. Give the verified fingerprint to testers through a trusted channel
and keep it with the saved credential in their password manager.

## Verification

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
signing keys belong to the local instance. Complete a ChatGPT connection, then check
that shared Work is readable, unshared Work is denied, and streamed responses and
terminal WebSockets work. Stop the client and check that the endpoint becomes
unavailable.
