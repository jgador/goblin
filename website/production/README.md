# goblinboard.com on AK3S

The same cluster can host the [Chisel development gateway](../../deploy/chisel/README.md)
for local ChatGPT testing. It uses its own namespace, subdomains, and
`ak3s-production` GitHub deployment environment.

The production configuration deploys the public website as `Deployment/website` in namespace `goblin` on the
Hetzner AK3S cluster. The `nginx` ingress class routes `goblinboard.com` to
`Service/website`. TLS is stored in `Secret/website-tls`; the existing
`letsencrypt-production` ClusterIssuer renews certificates through HTTP-01.
The ingress uses `acme.cert-manager.io/http01-edit-in-place: "true"`, matching
the AK3S dashboard. NGINX's controller rejects competing Ingress objects for the
same host, so cert-manager must add challenge paths to the existing ingress.
The website's CPU/memory settings and 1–3 replica HPA are shared with the local
manifests. Their measurements were taken in WSL, not on Hetzner.

`../k8s/` remains the local deployment base. This directory overlays registry
pulls, a rolling update with zero unavailable replicas, and the public ingress.
AK3S continues owning F5 NGINX Ingress Controller (`nginx.org/ingress-controller`),
cert-manager, and metrics-server. This is distinct from community `ingress-nginx`. The
website neither installs nor modifies those components or the platform app.

## Protected release

**Website Release** (`.github/workflows/website-release.yml`) uses `validate` to check
source, production configuration, and the container for all PRs and merge-queue candidates.
Workflow and job names follow [the GitHub Actions convention](../../docs/github-actions.md).
A push changing production image inputs on `master`, or a manual run on `master`,
builds and tests the production image, publishes it to
`ghcr.io/jgador/goblin-website`, and waits for approval. GitHub
Actions uses its own temporary package token; no registry password is stored.
The deployed reference is the exact tested image digest, never a mutable tag.
The image also contains the installation page and a frozen catalog with versioned
Azure templates. Validation lists the GitHub Releases API and downloads current
published installation assets directly. It verifies GitHub digests, signed release
manifests, template hashes, and checksums. The verified certificate must identify
`platform-release.yml` on master; releases with valid signatures from other
workflows are excluded, while invalid signatures fail validation. It retains the
exact snapshot as a GitHub Actions artifact. Publish and deploy restore that artifact. The production
image packages `install/azure/releases.json` and `install/azure/versions/` only;
snapshot provenance stays outside the image.

The deployed website catalog owns the recommendation. Before applying an approved
image, deployment checks that this catalog has not changed since snapshot creation,
preventing stale approvals from replacing a newer recommendation. Initial builds
without a supplied snapshot use the tracked empty Azure catalog. There is no
distribution branch or GitHub Pages dependency.
Website snapshots include current-format releases only; older published artifacts
remain intact on GitHub Releases.
Documentation and infrastructure-only changes do not publish an unchanged
image or request another website deployment. Apply infrastructure changes through
the administrator bootstrap procedure; use a manual workflow run when needed.

Configure the `website-production` GitHub environment with:

- Required reviewer: the repository owner, or another trusted maintainer.
- Selected deployment branches: only the `master` branch; no tag patterns.
- Administrator bypass disabled. A sole maintainer must allow self-review.
- Secret `WEBSITE_KUBECONFIG`: credentials for `goblin/website-deployer`.
- Variable `WEBSITE_DEPLOYMENT_UID`: the UID of the bootstrapped Deployment.

The workflow validates these protections before publishing and again after
approval. It rejects a candidate if `master` advanced while approval was pending.
Deployments are serialized. The named Deployment's UID is checked to catch an
incorrect cluster/configuration or recreation; a resourceVersion precondition
rejects concurrent changes. Only the container image is patched, leaving HPA
ownership of replicas intact. Public verification compares website bytes to
the checked-out release and checks security headers, health, and missing files.

The first GHCR package publication is private by default. In GitHub, open the
`goblin-website` package settings and change its visibility to **Public**. The
publish job tests anonymous access and stops with instructions if it cannot pull
the exact digest. Rerun the failed job after updating visibility. Kubernetes
does not receive the short-lived GitHub package token.

The checked-in `github-ruleset.json` requires `validate` from GitHub Actions on
`master` alongside the platform's `test_results` gate and release-branch rules.
Applying or editing this template does not update the live ruleset automatically.
Review active branch protections when changing required check names.

## Administrator bootstrap

Use the Hetzner kubeconfig explicitly. Never rely on an ambient/default cluster.
The local `make website-deploy` still targets only the WSL cluster.

```bash
kubectl --kubeconfig ~/.kube/ak3s-hetzner.yaml -n goblin get namespace goblin
kubectl --kubeconfig ~/.kube/ak3s-hetzner.yaml apply -f website/production/deployer.yaml
```

Create the Deployment, Service, and HPA from the reviewed production overlay
using the first published digest. `node website/release.mjs render IMAGE`
renders a digest-pinned manifest; save it under `.artifacts/website-production/`
and perform server-side dry-run validation before applying it. The placeholder
`pending` image in the overlay is never a release image.

```bash
kubectl --kubeconfig ~/.kube/ak3s-hetzner.yaml -n goblin \
  get deployment website -o jsonpath='{.metadata.uid}{"\n"}'
```

Save that UID as the GitHub environment variable `WEBSITE_DEPLOYMENT_UID`.
Bootstrap and infrastructure changes use administrator access. Normal releases
use `website-deployer`, whose Role permits only get/watch/patch of the existing
`website` Deployment. It cannot create resources, edit RBAC, read Secrets, or
manage other Deployments. Keep this identity out of application pods. Kubernetes
Deployment patch permission includes the pod template: protect the workflow and
environment reviewer just as carefully as the credential itself.

Create its kubeconfig with this cluster's API address and CA, and a dedicated
ServiceAccount credential; never reuse the administrator client certificate.
Keep credential material out of output, artifacts uploaded by CI, and Git.
If using a manually created ServiceAccount token Secret for unattended CI,
rotate it explicitly and revoke it by deleting that token Secret. A TokenRequest
token expires and needs a renewal mechanism; do not mistake a one-hour token
for a permanent CI credential. Kubernetes RBAC tests should confirm that this
identity cannot read Secrets or patch `Deployment/platform`.

The GitHub-hosted runner must be able to reach the API with valid TLS. A working
WSL connection alone does not verify the runner's network path. The environment
secret is written only inside the approved job, with mode 0600, and removed when
the step exits. Do not print it or upload the `.artifacts/` directory.

## Initial DNS and TLS setup

When moving an existing HTTPS origin, obtain a valid certificate before changing
its A record. Clients may retain HSTS from the previous origin. Inspect current
DNS and TLS state before choosing a cutover procedure; this guide does not
establish the current live state.

1. Obtain a one-time Let's Encrypt certificate with manual DNS-01 validation.
   Store the private account/key files under a mode-0700 task directory inside
   `.artifacts/website-production/`; never upload that credential directory.
   Add the requested `_acme-challenge` TXT record in Cloudflare. Leave the
   existing A record and old site serving while validation completes.
2. Import the full certificate chain and private key as `goblin/website-tls`.
   Deploy and inspect the website through the new origin with the Host/SNI name
   `goblinboard.com`. Check the certificate and exact release assets before cutover.
3. Change the apex A record in Cloudflare to the AK3S ingress IPv4 address.
   Keep DNS-only mode for the initial cutover; remove a stale apex AAAA record
   only if it points somewhere that will not serve this website. Preserve mail,
   other hostnames, and unrelated DNS records. The old origin can remain available
   while cached DNS answers expire.
4. Verify public HTTPS serves the new source and cert-manager's Certificate is
   Ready. It may start an HTTP-01 issuance while DNS still targets the old origin;
   the imported valid certificate remains available during that attempt.
5. Remove the one-time TXT record once the certificate has been issued. Ongoing
   renewal uses AK3S's existing HTTP-01 issuer; no Cloudflare API token or manual
   TXT updates are needed. Keep port 80 reachable for ACME challenge paths.

If Cloudflare proxying is enabled later, use **Full (strict)** TLS and preserve
HTTP-01 challenge access. Never use Flexible mode with the HTTPS redirect.
The site currently requests cache revalidation; avoid caching stale HTML/assets
through custom Cloudflare cache rules.

Only `goblinboard.com` is configured. Adding `www` requires its own DNS and
certificate coverage. No existing unrelated DNS record is implicitly changed.

## Failure and rollback

A successful image push is not a successful deployment. Check the approved job's
rollout and HTTPS verification. If DNS still targets an old origin, the public test reaches that origin; complete origin verification and DNS cutover, then rerun
verification rather than reporting that the new public website is already live.

A stalled rollout keeps an existing ready replica serving because
`maxUnavailable` is zero; it does not automatically roll back. Revert the website
source through a PR and approve the resulting release. For an urgent rollback,
an administrator can deploy a previously verified image digest using the same
`release.mjs deploy` command and the correct Deployment UID. A new run is needed
after a concurrent-change precondition fails; do not overwrite unrelated changes.

Never delete namespace `goblin` as website cleanup. Preserve TLS Secrets, the
platform, and other applications unless removal is separately requested.
