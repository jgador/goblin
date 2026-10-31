# goblinboard.com on AK3S

Production runs the public website as `Deployment/website` in namespace `goblin`
on the Hetzner AK3S cluster. The `nginx` ingress class routes `goblinboard.com` to
`Service/website`. TLS is stored in `Secret/website-tls`, and the existing
`letsencrypt-production` ClusterIssuer renews it through HTTP-01. The ingress
uses `acme.cert-manager.io/http01-edit-in-place: "true"` because the controller
rejects a second Ingress for the same host, so cert-manager must add challenge
paths to this one.

`../k8s/` is the base. This overlay adds registry pulls, a rolling update with
zero unavailable replicas, and the public ingress. CPU/memory settings and the
1–3 replica HPA come from the base; they were measured in WSL, not on Hetzner.
AK3S owns F5 NGINX Ingress Controller (`nginx.org/ingress-controller`, not
community `ingress-nginx`), cert-manager, and metrics-server. The website does not
install or change them or the platform. The same cluster can host the
[Chisel development gateway](../../deploy/chisel/README.md), which has its own
resources and GitHub environment.

## Protected release

**Website Release** (`.github/workflows/website-release.yml`) runs `validate` on
every PR and merge-queue candidate to check source, production configuration, and
the container. A push to `master` that changes production image inputs, a manual
run on `master`, or a call from **Installation Assets** builds and tests the image,
publishes it to `ghcr.io/jgador/goblin-website`, and waits for approval. GitHub
Actions uses its temporary package token; no registry password is stored. The
deployed reference is the tested image digest, never a mutable tag.

The image packages the installation page, `install/azure/releases.json`, and
`install/azure/versions/` from the validated snapshot. See
[release operations](../../docs/releases.md#installation-defaults-and-recommendation)
for snapshot verification, signer policy, and recommendation rules. Before applying
an approved image, deployment rejects it if the deployed catalog changed since the
snapshot was made.

Changes to documentation or infrastructure only do not publish an image. Apply
infrastructure changes through the administrator bootstrap below, and use a manual
workflow run when needed.

Configure the `website-production` GitHub environment with:

- Required reviewer: the repository owner or another trusted maintainer.
- Selected deployment branches: only `master`; no tag patterns.
- Administrator bypass disabled. A sole maintainer must allow self-review.
- Secret `WEBSITE_KUBECONFIG`: credentials for `goblin/website-deployer`.
- Variable `WEBSITE_DEPLOYMENT_UID`: the UID of the bootstrapped Deployment.

The workflow checks these protections before publishing and again after approval.
It rejects a candidate if `master` advanced while approval was pending.
Deployments run one at a time. Deployment checks the Deployment UID, uses a
resourceVersion precondition to reject concurrent changes, and patches only the
container image, so the HPA keeps owning replicas. Public verification compares
the served bytes with the release and checks security headers, health, and 404s.

The first GHCR package publication is private. In GitHub, open the
`goblin-website` package settings and set visibility to **Public**. The publish
job checks anonymous pull of the exact digest and stops with instructions if it
fails; rerun the job after changing visibility.

`github-ruleset.json` requires `validate` from GitHub Actions on `master`. It is
separate from the platform's `.github/goblin-ruleset.json`. Editing either file
does not update the live rulesets; check active branch protections when changing
required check names.

## Administrator bootstrap

Use the Hetzner kubeconfig explicitly. Never rely on a default cluster.
`make website-deploy` targets only the WSL cluster.

```bash
kubectl --kubeconfig ~/.kube/ak3s-hetzner.yaml -n goblin get namespace goblin
kubectl --kubeconfig ~/.kube/ak3s-hetzner.yaml apply -f website/production/deployer.yaml
```

Create the Deployment, Service, HPA, and Ingress from the production overlay with
the first published digest. `node website/release.mjs render IMAGE` renders a
digest-pinned manifest; save it under `.artifacts/website-production/` and run a
server-side dry run before applying it. The `pending` image tag in the overlay is
never a release image.

```bash
kubectl --kubeconfig ~/.kube/ak3s-hetzner.yaml -n goblin \
  get deployment website -o jsonpath='{.metadata.uid}{"\n"}'
```

Save that UID as the GitHub environment variable `WEBSITE_DEPLOYMENT_UID`.
Bootstrap and infrastructure changes use administrator access. Normal releases
use `website-deployer`, whose Role allows only get/watch/patch on the `website`
Deployment. It cannot create resources, edit RBAC, read Secrets, or manage other
Deployments. Keep this identity out of application pods. Patch permission covers
the pod template, so protect the workflow and environment reviewer as carefully
as the credential.

Create its kubeconfig with this cluster's API address and CA and a dedicated
ServiceAccount credential; never reuse the administrator client certificate.
Keep credential material out of output, CI artifacts, and Git. A manually created
ServiceAccount token Secret must be rotated explicitly and is revoked by deleting
that Secret. A TokenRequest token expires and needs renewal; a one-hour token is
not a permanent CI credential. Confirm that this identity cannot read Secrets or
patch `Deployment/platform`.

The GitHub-hosted runner must reach the API with valid TLS; a working WSL
connection does not prove that. The approved job writes the secret with mode
0600 and removes it when the step exits. Do not print it or upload `.artifacts/`.

## Initial DNS and TLS setup

When moving an existing HTTPS origin, get a valid certificate before changing its
A record, because clients may keep HSTS from the old origin. Check current DNS and
TLS state before choosing a cutover procedure.

1. Get a one-time Let's Encrypt certificate with manual DNS-01 validation.
   Keep the account and key files in a mode-0700 task directory under
   `.artifacts/website-production/`; never upload it. Add the requested
   `_acme-challenge` TXT record in Cloudflare. Leave the existing A record and
   old site serving during validation.
2. Import the full certificate chain and private key as `goblin/website-tls`.
   Deploy, then check the new origin with Host/SNI `goblinboard.com`, including
   the certificate and release assets, before cutover.
3. Change the apex A record in Cloudflare to the AK3S ingress IPv4 address.
   Keep DNS-only mode for the cutover. Remove a stale apex AAAA record only if it
   points somewhere that will not serve this website. Keep mail, other hostnames,
   and unrelated records unchanged. The old origin can stay up while cached DNS
   answers expire.
4. Check that public HTTPS serves the new source and that cert-manager's
   Certificate is Ready. If cert-manager starts HTTP-01 issuance while DNS still
   points to the old origin, the imported certificate stays in use.
5. Remove the one-time TXT record after issuance. Renewal uses AK3S's HTTP-01
   issuer and needs no Cloudflare API token or TXT updates. Keep port 80 reachable
   for ACME challenge paths.

If Cloudflare proxying is enabled later, use **Full (strict)** TLS and keep
HTTP-01 challenge access. Never use Flexible mode with the HTTPS redirect. Do not
add Cloudflare cache rules that cache HTML or assets; the site asks for
revalidation.

Only `goblinboard.com` is configured. Adding `www` needs its own DNS record and
certificate coverage.

## Failure and rollback

A successful image push is not a successful deployment. Check the approved job's
rollout and HTTPS verification. If DNS still points to an old origin, the public
check reaches that origin; finish origin checks and DNS cutover, then rerun
verification before reporting the new site as live.

A stalled rollout keeps a ready replica serving because `maxUnavailable` is zero;
it does not roll back automatically. Revert the website source through a PR and
approve the resulting release. For an urgent rollback, an administrator can deploy
a previously verified image digest with
`node website/release.mjs deploy IMAGE KUBECONFIG DEPLOYMENT_UID`. If the
concurrent-change precondition fails, start a new run; do not overwrite unrelated
changes.

Never delete namespace `goblin` as website cleanup. Keep TLS Secrets, the platform,
and other applications unless their removal is requested separately.
