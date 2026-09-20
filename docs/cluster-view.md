# Cluster view

The Kubernetes installation includes Headlamp at **`/headlamp/`** on the same
address as Goblin. Open **Settings → Cluster → Open cluster**, or visit:

- Local WSL installation: `http://localhost:8788/headlamp/` (or your chosen port).
- Azure VM: `http://<Azure-assigned-hostname>/headlamp/`.
- A Goblin installation configured for HTTPS: `https://<hostname>/headlamp/`.

Use the normal Goblin password. Opening Headlamp while locked takes you to
Goblin's login and returns you to the cluster view afterward. No Kubernetes token,
extra password, DNS record, or public port is required. The Settings link opens a
new tab to preserve unfinished Work drafts. Existing deep links and refreshes keep
the `/headlamp` prefix.

## Access and deployment

Headlamp is a separate Deployment, `goblin-headlamp`, in the `goblin` namespace.
The image is pinned to version 0.45.0 and its multi-architecture digest in
[`deploy/auth/headlamp.yaml`](../deploy/auth/headlamp.yaml). It runs without root
or a writable root filesystem and has its own temporary volume and service account.

The existing Traefik route sends `/headlamp` to Goblin. The C# backend checks the
Goblin session and forwards permitted requests to the private Headlamp Service.
Local forwarding and Azure's assigned hostname therefore use the same route.
Headlamp has no public Ingress, NodePort, or LoadBalancer. Its NetworkPolicy allows
only the main Goblin pods to reach it; agent sandbox pods cannot access it.

The `goblin-cluster-viewer` account can read workloads, pod logs, events, namespaces,
nodes, networking, storage, metrics, RBAC metadata, and Goblin Sandbox resources.
It cannot read Secrets, start a shell, forward ports, or change cluster resources.
Authorization review requests are allowed so Headlamp can display the correct
available actions. These permissions are enforced by Kubernetes, separately from
Goblin's HTTP route restrictions. Other custom resource types need explicit RBAC
additions before their instances can be inspected.

Headlamp's service-account authentication mode is enabled **behind Goblin's
authenticated proxy**. The service-account token stays in the Headlamp pod and
rotates through Kubernetes' projected volume. Goblin strips browser cookies,
authorization, impersonation, and proxy headers before forwarding. The browser
cannot supply an alternate account. Headlamp cannot set Goblin cookies. The
proxy excludes Headlamp's external proxy, cluster import, plugin installation,
and administrative operations. It supports Kubernetes watches and log streams;
connections are bounded to five minutes before they need fresh authorization.

Headlamp shares Goblin's browser origin, so it is a trusted part of this
installation. Its own route permits the inline styles required by its UI and
only the hashes of its two pinned inline bootstrap scripts. Goblin's existing
pages retain their stricter Content Security Policy. When upgrading the image,
verify those hashes and run the real browser check below. The current Azure
installer uses HTTP; Headlamp follows the installation's existing HTTP/HTTPS
configuration.

Headlamp failure does not make Work unavailable: `/readyz` still checks Goblin's
own dependencies, and the cluster route returns an unavailable response if its
upstream cannot be reached. The installer waits for both Goblin and Headlamp
before declaring the installation complete.

## Development and checks

A standalone `npm start` has no cluster viewer by default. `GOBLIN_HEADLAMP_URL`
optionally points the backend at a trusted private Headlamp origin configured
with `-base-url=/headlamp`. Kubernetes manifests set it automatically. Do not
point it at an arbitrary third-party service.

`npm test` includes authenticated proxy, local/Azure Host and Origin, streaming,
credential filtering, route restrictions, and installer tests. For the real
Headlamp/browser check against an existing cluster, temporarily forward its
private Service in one terminal:

```bash
kubectl port-forward -n goblin service/goblin-headlamp 14466:4466 --address 127.0.0.1
```

Then run:

```bash
GOBLIN_TEST_HEADLAMP_URL=http://127.0.0.1:14466 npm run test:headlamp
```

This uses isolated Goblin authentication and real Kubernetes reads, without saved
GitHub or Codex credentials. It tests localhost and an Azure-shaped hostname
resolved locally in Chromium, checks forbidden Secret access and denied mutation
permissions, streams pod logs, refreshes a deep link, and verifies CSP and logout. Actual Azure DNS,
network rules, and TLS still require deployment verification on that VM. Stop the
temporary port-forward afterward; ordinary users always access the Goblin URL.
