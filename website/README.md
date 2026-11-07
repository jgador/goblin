# Goblin: In the loop

The public website at `goblinboard.com` is released by
[Website Release](../.github/workflows/website-release.yml). The
[Installation Assets workflow](../.github/workflows/installation-assets.yml) prepares
verified release data and calls Website Release to serve the installation page, catalog,
and versioned Azure templates at `goblinboard.com/install/`. See
[production deployment](production/README.md) for hosting, TLS, and approvals.

The static HTML/CSS/JavaScript site shows a scripted demonstration of Goblin working
across channels. It sends no messages and does not run Work. Teams, Discord, and email
show integrations that are not available; all results in the demo are fictional. The
About dialog in `index.html` lists the public sources behind each fictional scenario.

The demo is scripted illustrative content, not a product capability guarantee. Preserve
keyboard operation, reduced motion, and mobile accessibility when changing it.

The Goblin mark is official artwork from `../assets/branding/svg/`, outside the
repository's Apache license grant. Inter keeps its SIL Open Font License in
`assets/OFL.txt`. Slack, Teams, and GitHub artwork are copies of the frontend provider
assets. The Gmail mark in `assets/email.svg` is from the
[gilbarbara/logos collection](https://github.com/gilbarbara/logos). This provider
artwork keeps its notice in `assets/PROVIDER-LICENSE.txt`. Channel names and marks
belong to their owners.

Run `npm --prefix website run check`, and check changed interactions in a browser,
including narrow layouts, keyboard controls, and reduced motion.

## Installation page

`install/` offers Azure deployment; AWS, Google Cloud, and other providers are shown as
planned, with no deployment action. Azure's catalog and templates live under
`install/azure/`.

The page fetches `install/azure/releases.json` from the same origin, selects the
recommended version, and respects `?version=X.Y.Z`. An unavailable requested version
requires an explicit choice. With no releases or a failed request, deployment stays
disabled; a failed request shows Try again. The Deploy button opens Azure Portal with
the selected version's `azuredeploy.portal.json` and `createUiDefinition.json`; Download
template serves its `azuredeploy.json`. Azure collects server choices, the Goblin
password, and the SSH key.

`make website-start` creates a snapshot under `.artifacts/installation-assets/output/`
when one is missing. It reads published `goblin-v*` releases in the current manifest
format from the GitHub Releases API and the recommendation from the deployed catalog,
and verifies GitHub asset digests and manifest checksums. It does not check the release
signer, so it can show releases that production excludes. Opening the tracked HTML
directly uses the empty catalog in `install/azure/releases.json`.

[Release operations](../docs/releases.md#installation-defaults-and-recommendation)
describe the production signer policy, recommendation rules, and frozen snapshot.

The installation route allows same-origin `connect-src`; the home page keeps
`connect-src 'none'`. Files under `install/azure/versions/` allow cross-origin reads so
Azure Portal can load the templates; the page and catalog do not.

## Promo video

`video/` is a development-only [Remotion](https://www.remotion.dev/) project with its
own [package and scripts](video/package.json). It is excluded from the website image.
Use those scripts for studio, rendering, and typechecking. Re-render the tracked videos
when their composition changes. Content shared with the homepage must stay consistent;
timing and camera code are private implementation. Remotion has its own
[license](https://www.remotion.dev/license).

## Develop in Docker

Run from the repository root inside WSL:

```bash
make website-start
```

Open **http://localhost:5173** in your Windows browser. The command builds the
development image, starts Docker Compose detached, waits for its health check, and
returns. Docker must run in this WSL distribution with the native Linux Buildx and
Compose plugins. With Docker's Ubuntu package repository, install them with
`sudo apt-get install docker-buildx-plugin docker-compose-plugin`. Docker Desktop is not
required.

The container mounts `website/` read-only at `/app`, and the installation snapshot
read-only. Vite serves the snapshot under `/install/`, also when you run Vite directly.
The snapshot is reused; to refresh it, stop the website, remove
`.artifacts/installation-assets/output/`, and run `make website-start` again. Vite
reloads the browser when you save. Keep the checkout on WSL's Linux filesystem for
reliable file watching. Dependencies live in the image, and `make website-start`
rebuilds them when the lockfile changes.

```bash
make website-status
make website-logs
make website-logs ARGS=--follow
make website-stop
```

The server binds only to loopback port 5173. The commands use `/var/run/docker.sock` and
a private client configuration under `.artifacts/website/docker/`, without personal
Docker contexts, registry logins, or credential helpers. `make website-stop` removes the
container and network.

## Preview in local k3s

With the existing single-node k3s installation running in this WSL distribution:

```bash
make website-deploy
make website-preview
```

`make website-deploy` builds the production Nginx image with a new tag, imports it into
k3s's containerd, applies the manifests in `k8s/` to namespace `goblin`, and waits for
the rollout. These manifests are separate from Goblin's application installer.

`make website-preview` forwards **http://localhost:5174** to the website Service and
keeps the terminal open; Ctrl+C stops forwarding and leaves the deployment running. The
commands use `/etc/rancher/k3s/k3s.yaml`, request `sudo` when needed, and do not create
or reset a cluster.

The container runs as a non-root user with a read-only filesystem. Asset filenames are
stable and served with `Cache-Control: no-cache`, so new previews do not keep old
assets. Temporary manifests and tool state stay under `.artifacts/website/`.

## Measure website resources

With the website deployed to local k3s, run:

```bash
make website-test-load
```

[load-test.mjs](load-test.mjs) owns visit cadence, durations, and sampling. It models
fresh low-frequency visitors with cache disabled and blocks other origins.

Install Playwright and Chromium first if needed (`npm ci`, then `make browser-install`).
The runner must run on the k3s host and uses `sudo` to read the website container's
cgroup. Traffic goes directly to the website's ClusterIP Service.

CPU and memory measurements come from the website container's cgroup, including health
probes. This measures neither application Work nor public-network latency.

Results go to a dated directory under `.artifacts/website/load-test/`; a failed run
writes its error and partial samples. The run requires exactly one ready website pod and
fails if the container restarts or changes. Do not redeploy or send other traffic during
measurement.

For a shorter check at the same arrival rate:

```bash
make website-test-load ARGS='--visits 3 --idle-seconds 15'
```

Measure again after asset, runtime, traffic, or host changes. The
[Deployment](k8s/deployment.yaml) owns measured resource requests/limits and the
[HPA](k8s/hpa.yaml) owns scaling policy. Deployment leaves replicas unset so the HPA
retains control. Local deployment requires metrics-server; scaling adds pods, not nodes,
on the single WSL host.

The website shares namespace `goblin` with the platform but has its own selector and no
service-account token; see
[Resource and storage ownership](../docs/architecture.md#resource-and-storage-ownership).
To remove the preview, delete only `deployment/website`, `service/website`, and
`hpa/website` in namespace `goblin`. Never delete the shared namespace as website
cleanup. To inspect usage and scaling:

```bash
sudo k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml -n goblin top pod -l app.kubernetes.io/name=website --containers
sudo k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml -n goblin get hpa website
```
