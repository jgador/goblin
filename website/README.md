# Goblin: In the loop

The public website at `goblinboard.com` is released by
[Website Release](../.github/workflows/website-release.yml). The
[Installation Assets workflow](../.github/workflows/installation-assets.yml)
prepares verified release data and calls Website Release to serve the installation
page, catalog, and versioned Azure templates at `goblinboard.com/install/`. See
[workflow naming](../docs/github-actions.md) and
[production deployment](production/README.md).

The static HTML/CSS/JavaScript site presents a scripted interactive demonstration
of Goblin coordinating work across channels. It accepts no messages or files and
does not execute Work. Teams, Discord, and email illustrate future integrations;
results and verification shown in the demo are fictional and remain for human review.

The demo offers channel selection, Conversation, Activity, Work and Files tabs,
illustrative previews, and an About dialog. Playback pauses when manually paused,
in other tabs, with a dialog open, or when the page is hidden. Keyboard selection
and reduced-motion preferences show a complete conversation without moving effects.
The layout adapts to narrow screens and preserves keyboard navigation, touch targets,
zoom, and safe areas. Open `index.html` or use the development commands below.

The fictional scenarios draw on public sources; they are not quotations or
endorsements, and Goblin has not performed the depicted work:

- GitHub: [dotnet/runtime issue #96277](https://github.com/dotnet/runtime/issues/96277).
- Teams: [microsoft/TypeScript issue #62606](https://github.com/microsoft/TypeScript/issues/62606).
- Email: [Linux mailing-list reply](https://lists.openwall.net/netdev/2024/09/11/354)
  and [accepted commit](https://github.com/torvalds/linux/commit/7c6a3a65ace70f12b27b1a27c9a69cb791dc6e91).
- Discord: [Circles Arrows](https://www.circlesarrows.com/about-us),
  [Blueprint41](https://www.circlesarrows.com/blueprint41), and
  [Compare41](https://www.circlesarrows.com/compare41).

The Goblin mark uses official artwork from `assets/branding/svg/`, outside the
repository's Apache license grant. Inter retains its SIL Open Font License in
`assets/OFL.txt`. Provider artwork retains its license notices; the Gmail mark is
from the [gilbarbara/logos collection](https://github.com/gilbarbara/logos).
Channel names and marks belong to their respective owners. The files under
`references/` are historical designs, not the current layout specification.

Run `npm --prefix website run check`, and inspect changed interactions in a browser,
including narrow layouts, keyboard controls, and reduced motion. The website and
its asset map and security policy are independent from the application. See
[production deployment](production/README.md) for hosting, TLS, and approvals.

## Installation page

`install/` offers Azure deployment; AWS, Google Cloud, and other providers are
planned and have no deployment action. Azure owns its catalog and templates under
`install/azure/`; future providers need their own contracts. The release picker
supports keyboard selection, dismissal without changing the selection, reduced
motion, and responsive placement.

The card fetches `install/azure/releases.json` from the same origin, selects the
recommended version, and respects `?version=X.Y.Z`. Unavailable versions require
an explicit selection; no releases and request failures leave deployment disabled.
A failed request offers Try again. The deploy link binds both Azure Portal inputs
to `install/azure/versions/<version>/`; Download template serves the matching CLI ARM
template. Azure collects server choices, the Goblin password, and SSH key.
The page explains subscription billing before the visitor opens the Portal.

`make website-start` downloads a snapshot directly from the GitHub Releases API
when one is missing. It reads only published `goblin-v*` releases with the current
manifest format, verifies GitHub asset digests and manifest checksums, and serves
the snapshot locally. Opening tracked HTML directly uses an empty Azure catalog.

Website Release also verifies signed release manifests and requires the verified
certificate identity to match `platform-release.yml` on master. Valid releases
signed by another workflow are excluded; invalid signatures fail validation.
Local preview verifies release hashes without enforcing this signer policy, so it
can show releases that are ineligible for production. The validated snapshot is
retained as a GitHub Actions artifact. Publish and deploy use that exact artifact,
without re-fetching releases. The Nginx image contains the Azure catalog and five
files per release, with no Git metadata, installer binary, snapshot provenance,
or build tooling. Original release assets remain attached to GitHub Releases.
There is no dependency on a distribution branch or GitHub Pages.

The default comes from the currently deployed website catalog. Explicit Recommend
Release changes it; ordinary website deployments preserve it. On first publication,
the newest supported stable version is preferred, otherwise the newest preview.
An existing null recommendation stays null. Deployment rejects a changed website
catalog while approval was pending. See [release operations](../docs/releases.md).

The installation route permits same-origin connections through its CSP; the
home page retains `connect-src 'none'`. Container and public verification compare
every packaged template and catalog byte against the frozen snapshot. Versioned
public files allow cross-origin reads so Azure Portal can load its templates;
the page and catalog do not grant cross-origin access.

## Promo video

`video/` is a development-only [Remotion](https://www.remotion.dev/) project
with a 30-second 1080p promo and a 12-second square `GoblinLogo` loop. It has
its own lockfile and is excluded from the website image, release paths, and
development container; the JavaScript CI group type-checks it.

Run these commands from the repository root:

```bash
npm --prefix website/video ci
npm --prefix website/video run studio
npm --prefix website/video run render
npm --prefix website/video run render:logo
npm --prefix website/video run check
```

`studio` opens an interactive preview at **http://localhost:3000**. `render`
writes `video/renders/goblin-promo.mp4` and `render:logo` writes
`video/renders/goblin-logo.mp4`. These committed renders sit outside `assets/`,
so they are not served by the website or included in its image and release
paths; re-render and commit them when a composition changes. The
first render downloads Remotion's Chrome Headless Shell into the package's
`node_modules`. `check` type-checks the project.

`video/src/content.ts` mirrors the homepage hero and first Slack scenario;
update it with their wording. `timeline.ts` and `camera.ts` own timing and camera
shots; `GoblinLogo.tsx` owns the logo loop and must track the artwork's geometry.
The project uses the website's `assets/` and associated notices. Its end card
identifies the Slack dialogue as fictional and Teams, Discord, and email as planned.

`video/remotion.config.ts` sets the bundler's automatic JSX runtime for
compatibility with the repository's TypeScript compiler. Remotion is
source-available under its own [license](https://www.remotion.dev/license); check
its current terms before use.

## Develop in Docker

Run these commands from the repository root inside WSL:

```bash
make website-start
```

Open **http://localhost:5173** in your Windows browser. The command builds the
development image, starts Docker Compose with `--detach`, waits for its health
check, and returns to your terminal. Docker must be running in this WSL
distribution, with the native Linux Buildx and Compose plugins installed.
On systems configured with Docker's Ubuntu package repository, install the
plugins with `sudo apt-get install docker-buildx-plugin docker-compose-plugin`.
Docker Desktop and its Windows Compose command are not required.

The development container bind-mounts this directory read-only at `/app`.
It also mounts the validated installation catalog and versioned templates read-only.
Vite serves those files under `/install/`, so `http://localhost:5173/install/`
shows published releases. The same routing works when running Vite directly.
The snapshot is reused across starts; to refresh it, stop the website, remove
`.artifacts/installation-assets/output/`, and run `make website-start` again.
Vite watches the same files you edit in WSL and updates or reloads the browser
when you save. Keep the checkout on WSL's Linux filesystem for reliable file
watching. Node and the locked Vite dependency live at `/opt/website-dev` in the
image, outside the source mount. Re-running `make website-start` uses Docker's
build cache and rebuilds dependencies when the lockfile changes; there is no
dependency volume that can retain an older installation.

```bash
make website-status
make website-logs
make website-logs ARGS=--follow
make website-stop
```

The development server binds only to loopback port 5173 on the WSL host.
The commands explicitly use the native `/var/run/docker.sock` and anonymous
client configuration under `.artifacts/website/docker/`, without loading personal
Docker contexts, registry logins, or credential helpers. Native Linux plugin
directories take precedence over Docker Desktop plugin links. Logs stay in Docker;
stopping the Compose project removes its development container and network.

## Preview in local k3s

With the existing single-node k3s installation running in this WSL distribution:

```bash
make website-deploy
make website-preview
```

The deployment command builds a separate Nginx image containing only the static
website and license notices. It streams the image from Docker into k3s's
containerd, applies the manifests in `k8s/` to the shared `goblin` namespace,
and waits for the rollout. Every invocation supplies a new image tag, including
uncommitted source edits. `imagePullPolicy: Never` uses that imported image.
The local manifests belong to the website and are separate from Goblin's
application installer and deployment dependency catalog.

The preview command forwards **http://localhost:5174** to the website Service.
Unlike the detached Docker development server, this optional port-forward keeps
its terminal open; Ctrl+C stops forwarding and leaves the deployment running.
The commands use `/etc/rancher/k3s/k3s.yaml` explicitly and request `sudo` when
needed. They require an existing local cluster and do not provision or reset one.

The runtime container runs as a non-root user with a read-only filesystem and
a temporary volume for Nginx. Node, Vite, source references, and development
configuration are excluded from the static image. Stable asset filenames are
served with revalidation so new previews do not retain stale browser assets.
Temporary manifests and tool state stay under `.artifacts/website/`.

## Measure website resources

With the static website deployed to local k3s, run:

```bash
make website-test-load
```

The default run takes eleven minutes: one minute idle, followed by ten fresh
Chromium visits starting exactly one minute apart. Each visit loads the HTML,
CSS, JavaScript, images, and fonts, keeps the animated website open for twenty
seconds, then closes. Browser caching is disabled. This models one new visitor
per minute, not one HTTP request per minute or one continuously active visitor.
No external links are followed, and unexpected external requests fail the test.

The runner uses the repository's pinned Playwright dependency and Chromium
(`npm ci`, then `make browser-install` if they are not already installed).
It runs on the same WSL host as k3s and requests sudo when necessary to read
the website container's cgroup. The browser runs outside the measured container;
no test dependencies are installed in the production image. Traffic goes
directly to the website's local ClusterIP Service, without a port-forward.

CPU is calculated from container CPU-time deltas, with samples every 250 ms
and approximately one-second windows. `1.0` vCPU means one fully busy CPU core;
`0.001` vCPU equals Kubernetes `1m`. Memory reports both cgroup usage, which
includes charged cache and kernel memory, and working set (usage minus inactive
file cache). The kernel's container-lifetime memory peak also includes startup.
Normal Kubernetes health probes remain included. WSL, k3s, metrics-server,
the development server, and Chromium resource usage are excluded.

Raw samples, per-visit HTTP results, and a summary are written to a dated
directory under `.artifacts/website/load-test/`. A failed run writes its error
and partial samples without a successful summary. The runner requires exactly
one ready website pod and fails if it restarts, changes, or scales during the
run. Run this baseline while other website traffic is quiet; do not redeploy
the website during measurement.

For a shorter verification at the same arrival rate:

```bash
make website-test-load ARGS='--visits 3 --idle-seconds 15'
```

These measurements size the static Nginx container, not Goblin's application
or AI execution. Local HTTP page-load times exclude public-network latency,
TLS termination, and any ingress controller. A one-visitor/minute baseline
does not establish maximum capacity or prove behavior during traffic spikes.

### Historical sizing evidence

A 2026-10-11 WSL baseline and follow-up informed the initial per-pod requests
of 25m CPU and 16 MiB memory, with limits of 100m and 32 MiB. These are starting
budgets, not capacity guarantees or evidence of current production performance.
Remeasure after asset, runtime, traffic, or host changes using the command above;
local summaries and samples live under `.artifacts/website/load-test/`.

## Website autoscaling

`k8s/hpa.yaml` uses the `autoscaling/v2` API, with one minimum replica and three
maximum replicas. CPU utilization targets 60% of each pod's CPU request. Memory
is not a scaling signal because this static server's cache is not proportional
to traffic. Scale-up can add one pod per minute; scale-down waits for five
minutes of lower recommendations, then removes at most one pod per minute.

New Deployments default to one replica. The Deployment manifest leaves
`spec.replicas` unset so later deployments do not overwrite the HPA's count.
`make website-deploy` checks that metrics-server is available, applies the
autoscaler after the rollout, and waits for its metrics to become active.
The website shares namespace `goblin` with the platform, but uses
`app.kubernetes.io/name=website` for pod selection. Platform access policies use
`app=platform`; website pods have no platform credentials or service-account token.
Deployment applies only website manifests and does not restart the platform.
`make website-stop` stops the Docker development container. To remove the Kubernetes
preview, delete only `deployment/website`, `service/website`, and `hpa/website`
in namespace `goblin`. Never delete the shared namespace as website cleanup.
See [Kubernetes names](../docs/kubernetes-names.md) for the authoritative naming map.
Use the following commands to inspect website-only usage and scaling:

```bash
sudo k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml -n goblin top pod -l app.kubernetes.io/name=website --containers
sudo k3s kubectl --kubeconfig /etc/rancher/k3s/k3s.yaml -n goblin get hpa website
```

All replicas run on the existing single WSL node; the HPA adds pods, not nodes.
With the `25m` CPU request, the target is `15m` (0.015 vCPU) average CPU per
pod. This low-traffic test should remain at one replica; it does not exercise
a scale-out to three or establish capacity at higher arrival rates.
