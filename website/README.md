# Goblin: In the loop

The public website at `goblinboard.com` is released by
[Website Release](../.github/workflows/website-release.yml). The
[Installation Assets workflow](../.github/workflows/installation-assets.yml)
prepares verified release data and calls Website Release to serve the installation
page, catalog, and versioned Azure templates at `goblinboard.com/install/`. See
[production deployment](production/README.md) for hosting, TLS, and approvals.

The static HTML/CSS/JavaScript site shows a scripted demonstration of Goblin
working across channels. It sends no messages and does not run Work. Teams,
Discord, and email show integrations that are not available; all results in the
demo are fictional. The About dialog in `index.html` lists the public sources
behind each fictional scenario.

The demo has channel selection, Conversation, Activity, Work, and Files tabs,
previews, and an About dialog. Playback pauses when paused by the user, on other
tabs, while a dialog is open, or when the page is hidden. Keyboard channel
selection and reduced motion show the complete conversation without animation.
The layout adapts to narrow screens and keeps keyboard navigation, touch targets,
zoom, and safe areas.

The Goblin mark is official artwork from `../assets/branding/svg/`, outside the
repository's Apache license grant. Inter keeps its SIL Open Font License in
`assets/OFL.txt`. Slack, Teams, and GitHub artwork are copies of the frontend
provider assets. The Gmail mark in `assets/email.svg` is from the
[gilbarbara/logos collection](https://github.com/gilbarbara/logos). This provider
artwork keeps its notice in `assets/PROVIDER-LICENSE.txt`. Channel names and marks
belong to their owners. Files under `references/` are old designs, not the current
layout.

Run `npm --prefix website run check`, and check changed interactions in a browser,
including narrow layouts, keyboard controls, and reduced motion.

## Installation page

`install/` offers Azure deployment; AWS, Google Cloud, and other providers are
shown as planned, with no deployment action. Azure's catalog and templates live
under `install/azure/`.

The page fetches `install/azure/releases.json` from the same origin, selects the
recommended version, and respects `?version=X.Y.Z`. An unavailable requested
version requires an explicit choice. With no releases or a failed request,
deployment stays disabled; a failed request shows Try again. The Deploy button
opens Azure Portal with the selected version's `azuredeploy.portal.json` and
`createUiDefinition.json`; Download template serves its `azuredeploy.json`.
Azure collects server choices, the Goblin password, and the SSH key.

`make website-start` creates a snapshot under `.artifacts/installation-assets/output/`
when one is missing. It reads published `goblin-v*` releases in the current
manifest format from the GitHub Releases API and the recommendation from the
deployed catalog, and verifies GitHub asset digests and manifest checksums. It
does not check the release signer, so it can show releases that production
excludes. Opening the tracked HTML directly uses the empty catalog in
`install/azure/releases.json`.

[Release operations](../docs/releases.md#installation-defaults-and-recommendation)
describe the production signer policy, recommendation rules, and frozen snapshot.

The installation route allows same-origin `connect-src`; the home page keeps
`connect-src 'none'`. Files under `install/azure/versions/` allow cross-origin
reads so Azure Portal can load the templates; the page and catalog do not.

## Promo video

`video/` is a development-only [Remotion](https://www.remotion.dev/) project
with a 30-second 1920×1080 promo (`GoblinPromo`) and a 12-second square logo
loop (`GoblinLogo`). It has its own lockfile and is not part of the website
image. The JavaScript CI group type-checks it.

Run these commands from the repository root:

```bash
npm --prefix website/video ci
npm --prefix website/video run studio
npm --prefix website/video run render
npm --prefix website/video run render:logo
npm --prefix website/video run check
```

`studio` opens a preview at **http://localhost:3000**. `render` writes
`video/renders/goblin-promo.mp4` and `render:logo` writes
`video/renders/goblin-logo.mp4`. Re-render and commit these files when a
composition changes. The first render downloads Remotion's Chrome Headless Shell
into `node_modules`.

`video/src/content.ts` copies the homepage hero and the Slack scenario; update it
when their wording changes. `timeline.ts` and `camera.ts` own timing and camera
shots. `GoblinLogo.tsx` must follow the artwork's geometry. The project uses
`website/assets/` and its notices. Remotion is source-available under its own
[license](https://www.remotion.dev/license); check its current terms before use.

## Develop in Docker

Run from the repository root inside WSL:

```bash
make website-start
```

Open **http://localhost:5173** in your Windows browser. The command builds the
development image, starts Docker Compose detached, waits for its health check,
and returns. Docker must run in this WSL distribution with the native Linux
Buildx and Compose plugins. With Docker's Ubuntu package repository, install them
with `sudo apt-get install docker-buildx-plugin docker-compose-plugin`.
Docker Desktop is not required.

The container mounts `website/` read-only at `/app`, and the installation snapshot
read-only. Vite serves the snapshot under `/install/`, also when you run Vite
directly. The snapshot is reused; to refresh it, stop the website, remove
`.artifacts/installation-assets/output/`, and run `make website-start` again.
Vite reloads the browser when you save. Keep the checkout on WSL's Linux
filesystem for reliable file watching. Dependencies live in the image, and
`make website-start` rebuilds them when the lockfile changes.

```bash
make website-status
make website-logs
make website-logs ARGS=--follow
make website-stop
```

The server binds only to loopback port 5173. The commands use
`/var/run/docker.sock` and a private client configuration under
`.artifacts/website/docker/`, without personal Docker contexts, registry logins,
or credential helpers. `make website-stop` removes the container and network.

## Preview in local k3s

With the existing single-node k3s installation running in this WSL distribution:

```bash
make website-deploy
make website-preview
```

`make website-deploy` builds the production Nginx image with a new tag, imports it
into k3s's containerd, applies the manifests in `k8s/` to namespace `goblin`, and
waits for the rollout. These manifests are separate from Goblin's application
installer.

`make website-preview` forwards **http://localhost:5174** to the website Service
and keeps the terminal open; Ctrl+C stops forwarding and leaves the deployment
running. The commands use `/etc/rancher/k3s/k3s.yaml`, request `sudo` when
needed, and do not create or reset a cluster.

The container runs as a non-root user with a read-only filesystem. Asset filenames
are stable and served with `Cache-Control: no-cache`, so new previews do not keep
old assets. Temporary manifests and tool state stay under `.artifacts/website/`.

## Measure website resources

With the website deployed to local k3s, run:

```bash
make website-test-load
```

The default run takes eleven minutes: one minute idle, then ten fresh Chromium
visits one minute apart. Each visit loads the page, stays open for twenty seconds,
and closes, with the browser cache disabled. This models one new visitor per
minute. Requests to other origins fail the test.

Install Playwright and Chromium first if needed (`npm ci`, then
`make browser-install`). The runner must run on the k3s host and uses `sudo` to
read the website container's cgroup. Traffic goes directly to the website's
ClusterIP Service.

CPU comes from container CPU-time samples every 250 ms, summarized over about
one-second windows; `0.001` vCPU equals Kubernetes `1m`. Memory reports cgroup
usage and working set (usage minus inactive file cache). Only the website
container is measured, including Kubernetes health probes.

Results go to a dated directory under `.artifacts/website/load-test/`; a failed
run writes its error and partial samples. The run requires exactly one ready
website pod and fails if the container restarts or changes. Do not redeploy or
send other traffic during measurement.

For a shorter check at the same arrival rate:

```bash
make website-test-load ARGS='--visits 3 --idle-seconds 15'
```

This measures the static Nginx container only, not Goblin or public-network
latency, and does not establish maximum capacity. The requests of 25m CPU and
16 MiB memory and limits of 100m and 32 MiB in `k8s/deployment.yaml` came from
this test in WSL. Measure again after asset, runtime, traffic, or host changes.

## Website autoscaling

`k8s/hpa.yaml` scales between one and three replicas on CPU, targeting 60% of
the CPU request (15m per pod). It adds at most one pod per minute; scale-down
waits five minutes, then removes at most one pod per minute. The Deployment leaves
`spec.replicas` unset so deployments do not overwrite the HPA's count.
`make website-deploy` requires metrics-server, applies the HPA after the rollout,
and waits for it to become active. On the single WSL node, the HPA adds pods, not
nodes, and the low-traffic test should stay at one replica.

The website shares namespace `goblin` with the platform but has its own selector
and no service-account token; see [Kubernetes names](../docs/kubernetes-names.md).
To remove the preview, delete only `deployment/website`, `service/website`, and
`hpa/website` in namespace `goblin`. Never delete the shared namespace as website
cleanup. To inspect usage and scaling:

```bash
sudo k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml -n goblin top pod -l app.kubernetes.io/name=website --containers
sudo k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml -n goblin get hpa website
```
