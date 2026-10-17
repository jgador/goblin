# Goblin: In the loop

The public website at `goblinboard.com` is released by
[Website Release](../.github/workflows/website-release.yml). The separate
[GitHub Pages workflow](../.github/workflows/github-pages.yml) deploys the
installation catalog at `jgador.github.io/goblin/`. See
[workflow naming](../docs/github-actions.md) and
[production deployment](production/README.md).

The page leads with **“Turn team conversations into results.”** and explains the
benefit in neutral international English: “Assign tasks, follow progress, and
review the work. Goblin keeps conversations and results connected, so your team
spends less time coordinating.” The page metadata follows the same message.
“Your self-hosted AI coworker” remains a smaller product description, and
“In the loop” remains part of the branding.

Four captions describe what the team gains:

| Benefit | Supporting copy |
| --- | --- |
| Spend less time switching tools | Follow conversations, progress, and results in one place. |
| Spend less time repeating information | Keep each task connected to the conversation that started it. |
| Control where Goblin runs | Host Goblin on infrastructure you control. |
| Stay involved in decisions | Assign the work, review the results, and decide what happens next. |

The hub and conversation panel follow the introduction directly, without a
separate instruction line, example button, or destination. The demonstration
plays on its own unless reduced motion is preferred, each channel card's
accessible name describes the conversation it plays, and the about dialog
explains how to explore it. A visible “Illustrative example” label and
planned-integration note explain the demonstration's scope alongside the
channels.

A standalone interactive concept showing Goblin at the center of Slack,
Microsoft Teams, GitHub, Discord, and Gmail conversations. The layout follows
`references/goblin-in-the-loop-v2.png` in this directory:
a pale green tool network, central Goblin mark, and a tabbed conversation panel.
The tool network has no enclosing box; the conversation panel retains its border.
Open `index.html` in a modern browser; the page itself requires no build,
dependencies, or account connections. The header links to the Goblin GitHub
project without showing repository metrics. Each channel card names its
platform beside the platform's mark. On phones, five compact channel cards form
a loose ring around Goblin. Each card has one thin, muted fiber-like connector
meeting the card and logo edges. A soft light travels from the card to Goblin and
back, showing both directions without a separate dotted path. Motion pauses with
the demonstration and follows the reduced-motion setting. On phones, the read-only
message preview and benefit descriptions follow the conversation panel, keeping
the hub and example together. On wider screens, the message preview sits inside
the Conversation view and the benefits sit below the hub. Their positions update
when crossing the phone breakpoint.

The central Goblin icon and wordmark scale together when vertical space is tight,
keeping an 18-pixel gap above the Discord connector endpoint in wide, short windows.
On wider layouts, the tool network keeps its own height instead of stretching
with the conversation panel, so arriving replies lengthen only the panel and do
not resize the diagram or hub during the animation.
On wider layouts, every connector starts at the measured center of its card's
dot and ends at its Goblin-side dot. Paths and animation lengths update with
the layout. Endpoint dots, moving dots, and their halos stay circular at a
consistent 9-pixel size, independent of the SVG's aspect ratio. Cards highlight
without shifting on hover, keeping their connectors attached.
Connector lines have no floating icon badges; the cards identify each platform.
Goblin avatars inside the channel cards use 45% opacity to keep the central hub
visually prominent. The hub and conversation panel avatars retain full opacity.

The hub's Goblin mark is a live SVG character rather than a video. On load, a
CSS entrance pops the blocks in, drops the head onto them, and opens the eyes
in about 1.2 seconds; it always completes, even when the demonstration is paused.
Afterward, `website.js` drives the mark from the demonstration: it breathes and
blinks while idle, looks toward the requesting channel card, tilts curiously when
the request arrives, squints while the blocks lift in turn during thinking,
watches the reply leave, and hops with brief arc eyes when the reply arrives.
These reactions follow the scripted exchange and pause when visitors explore
another tab or open a dialog. Reduced motion shows the static mark.

The scenarios are associated with Josh A. (Slack), Anders H. (Microsoft
Teams), Stephen T. (GitHub), Renko K. (Discord), and Linus T. (Gmail). The
channel cards and conversation panel show platforms rather than people: each
request appears under its platform's mark and name, and only the card avatars
keep the participants' initials.

Josh A.’s three Slack scenarios use a fictional log ingestion pipeline:
handle higher traffic without losing accepted logs, protect logs during
deployment, and review whether a wider release is ready. The primary request is
“Help us handle higher traffic without losing accepted logs. Prepare a tested
change and a deployment plan for review.” Goblin's reply explains the intended
work and when a team decision is needed. The conversation uses three plain stages:
understand the traffic problem, prepare and check the changes, and plan the
deployment. Work details and deliverable previews retain the technical checks for
bounded buffering, backpressure, durable enqueue, deduplication, health probes,
graceful shutdown, load testing, and rollback without discarding queued logs.
The illustrated result offers proposed changes, a verification summary, and a
deployment plan for review. The dialogue is invented, not a quotation or a
description of any actual system or work by Josh. No pipeline has been rewritten,
load-tested, or deployed as part of this conversation.

The expanded GitHub card shows Code, Issues, Pull requests, Actions, and Projects
icons, with their names on hover. They illustrate repository navigation; clicking
the card plays the GitHub conversation.

Its three scenarios are based on Stephen T.’s public
[dotnet/runtime issue #96277](https://github.com/dotnet/runtime/issues/96277),
“Investigate optimizing some OrderBy().Take calls to use a PriorityQueue.”
They cover allocation reduction, stable ordering, and benchmarking when a bounded
priority queue is worthwhile. The thread discusses preserving equal-key source
order, reversing the heap comparison and output order, and measuring the actual
System.Linq implementation: lower allocations can still come with slower execution.
It also clarifies that the existing path uses partial sorting, so the example does
not claim that it always sorts the whole source or promise an asymptotic speedup.
The dialogue is fictional, not a quotation or endorsement. Work details and
deliverable previews describe proposed investigation and checks; Goblin has not
run these benchmarks, changed dotnet/runtime, or opened a pull request. The About
dialog and GitHub deliverable previews link to the original issue.

The Teams scenarios are based on Anders H.’s public
[microsoft/TypeScript issue #62606](https://github.com/microsoft/TypeScript/issues/62606),
“Changing property declaration order causes error.” In the reported `DerivedTable`
example, reversing the `schema` and `readonlySchema` declarations removes an
unexpected generic assignment error. The example combines indexed access
intersections with a `Readonly` mapped type. The issue is closed; the fictional
conversation imagines reviewing regression coverage, preserving errors for invalid
assignments, and preparing a compiler team update. Additional invalid-assignment
cases are proposed regression coverage, not findings from the issue. Goblin has not run
the compiler tests or changed TypeScript. Work details, files, and previews follow
each scenario; the About dialog and deliverable previews link to the source.
The dialogue is not a quotation or endorsement by Anders H.

The email scenarios are based on
[Linus T.’s actual September 11, 2024 reply](https://lists.openwall.net/netdev/2024/09/11/354)
to the AtomISP patch discussion and
[torvalds/linux commit 7c6a3a65ace7](https://github.com/torvalds/linux/commit/7c6a3a65ace70f12b27b1a27c9a69cb791dc6e91),
“minmax: reduce min/max macro expansion in atomisp driver.” Lorenzo S.
authored the patch; Linus committed it and explicitly replied that he had taken
this one patch, leaving the other two for their maintainers. It replaces nested
macros with inline helpers and `clamp_t()` to address a reported Slackware 15.0
32-bit build problem, with a focused change suitable for backporting to 5.15.
Linus noted that the reported build problem did not affect the then-current Git
tree after broader min/max cleanups. The example revisits that historical review;
it does not imply that the fix is still pending in current Linux.

Its three fictional asks cover reproducing the build failure, reviewing a
focused backport, and recapping the patch decision. Compiler memory measurements,
boundary checks, and build logs are proposed investigation work, not results from Goblin
or claims of measured speedups. Both Linus’s reply and the accepted commit are
linked from the About dialog and every email deliverable preview. The dialogue
is not a quotation or endorsement; Goblin has not built Linux, applied a backport,
or sent a mailing-list reply.

Renko K.’s Discord scenarios draw on the public
[Circles Arrows company description](https://www.circlesarrows.com/about-us),
which emphasizes graph database tools, business applications, developer
productivity, and generative AI. The linked product pages describe
[Blueprint41](https://www.circlesarrows.com/blueprint41) as a C# object graph
mapper with generated data objects and schema refactoring, and
[Compare41](https://www.circlesarrows.com/compare41) as a tool for comparing
graph models and data and selecting synchronization scope.

The three asks use an invented customer-order-product dataset: outline a
Blueprint41 model, review Compare41 differences, and plan an AI answer flow
that shows its supporting graph records. These are proposed examples, not
reported product issues, actual customer work, or statements made by Renko.
The About page describes the company rather than Renko’s individual role;
the website assigns him no unverified title. Source links appear in the About
dialog and Discord deliverable previews. No graph database has been queried
or synchronized, and no integration with these products is implied.

Choose a channel card to see its primary scripted exchange. Additional
scripted examples remain in the scene data; there is no scenario dropdown. The
demonstration uses shorter requests and replies that describe the desired outcome
before the implementation details. All fifteen stored examples retain their
scenario-specific checks and source attribution where applicable. The initial
Slack response is visible on arrival. Reduced-motion users see a complete
conversation and can choose channel cards without moving message effects.

The animation loops from Gmail back to Slack automatically at its fixed pace.
It pauses in other tabs, while a dialog is open, or when the browser page is hidden,
then resumes when the conversation is visible again. Changing the reduced-motion
preference updates the animation immediately.

Message travel varies by channel. Each one-way trip takes
0.6 seconds for Slack, 1 second for Teams and Discord, 1.6 seconds for Gmail,
and 2.4 seconds for GitHub. The thinking pause also grows from quick chat
exchanges to longer email and repository investigations. Replies appear when
the returning dot arrives. These are illustrative timings, not measured service
latencies. Each exchange has a half-second lead-in and holds its completed reply
for 2–3 seconds before the next channel. Exchange lengths follow their timing:
5 seconds for Slack, 6 for Teams, 11 for GitHub, 6 for Discord, and 8 for Gmail.
The full loop is 36 seconds, derived from the exchange timings.
While a reply is being prepared, the conversation panel shows a gently pulsing
green dot beside a stationary Goblin avatar, with a screen-reader status instead
of visible thinking text.
The pulse pauses with the demonstration and is disabled for reduced motion.

The Conversation, Activity, Work, and Files tabs show related example content.
Tabs support arrow keys, Home, and End. Deliverables open illustrative previews.
The conversation and Files cards display “Ready for review” with an
“Illustrative result” label. Activity connects the request to its proposed result,
Work explains the planned checks, and Files lets visitors explore the deliverables.
No successful execution or verification is claimed by the illustrated review state.
The message composer is a read-only visual preview.
Its attachment, mention, smile, and send icons are decorative. It does not
accept messages or files; choose a channel to explore a scripted conversation.

These conversations are illustrative. The website makes no external requests and
does not execute Work. Teams, Discord, and email depict future possibilities,
not implemented integrations. Proposed changes and draft deliverables remain
for human review. The channel information icon opens the About dialog, which
explains the website's scope.

The Goblin icons are unchanged copies of the official artwork in the repository's
`assets/branding/svg/` directory. The center hub draws the light-background
icon's shapes inline with the same geometry and colors, so its parts can move,
adding brief arc eyes during the hop; it sits directly on the page with a soft
glow. Conversation avatars and the Slack and Teams
card avatars also use the light-background icon without a surrounding tile.
The Discord card and About dialog use the white icon on a green surface.
Brand artwork remains outside the repository's Apache
license grant. Inter fonts retain their SIL Open Font License in `assets/OFL.txt`.
Slack, Teams, and GitHub artwork comes from existing frontend provider assets
with their license notice. The Gmail mark in `assets/email.svg` comes from the
[gilbarbara/logos collection](https://github.com/gilbarbara/logos) under the same
provider license notice. The Discord mark identifies the depicted channel;
channel names and marks belong to their respective owners.

The production application, its asset map, and its security policy are independent
of this website. Verify changes with `npm --prefix website run check`
from the repository root, plus browser checks of channel scenarios, tabs,
previews, composer, automatic animation, narrow layouts, and reduced motion.

The existing `references/goblin-in-the-loop.gif` is an earlier exported design;
it is not regenerated as part of this website update. Use `index.html` for the
current interactive design.

Production domain: <https://goblinboard.com>. See the
[protected production deployment](production/README.md) for AK3S ingress,
GitHub approval, TLS, and the DNS cutover procedure.
The earlier ChatGPT-hosted copy is independent of this deployment.

## Promo video

`video/` is a development-only [Remotion](https://www.remotion.dev/) project. It
renders a 30-second, 1920 × 1080, 30 fps landscape promo for social posts and
launches. React components rebuild the website's hub, channel cards, and Slack
conversation. A scripted camera moves from the headline to the network,
where the Slack card is spotlighted, then through the conversation and review. It
pulls back to the full network, where a shield badge on the Goblin mark and a
“Self-hosted” caption label mark the hosting benefit, and ends on the website and
repository addresses. A second composition, `GoblinLogo`, is a 12-second,
1080 × 1080 seamless loop of the Goblin mark as a character. Its blocks pop in and
the head drops onto them. It then blinks, looks around, tilts curiously, and
thinks while the blocks take turns lifting. Finally it hops with happy eyes and
leaves, so the clip ends on the same empty stage it starts with. The package has
its own lockfile. It is excluded from the website image, release workflow paths,
CI installation, and the development container's dependencies.

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

`video/src/content.ts` mirrors the hero copy in `index.html` and the first Slack
scenario in `website.js`; update it when that wording changes. `video/src/timeline.ts`
holds frame timing and `video/src/camera.ts` holds the camera shots.
`video/src/GoblinLogo.tsx` holds the logo loop's beats and motion. It redraws the
parts of `assets/goblin.svg` in their original shapes and colours, adding only
brief arc eyes for the happy moment; update its geometry if the mark changes. The
project uses `assets/` as its public directory, so the video uses the same Inter
fonts, Goblin artwork, and provider marks as the website, under the notices above.
Like the website, the promo shows the “Illustrative example” notice. Only the Slack
`#log-pipeline` conversation is depicted. The end card states that the Slack
dialogue is fictional and does not describe an actual system or work, and that
Teams, Discord, and email are planned integrations.

Remotion reads `tsconfig.json` through the TypeScript JavaScript API, which the
repository's TypeScript 7 compiler does not expose, so `video/remotion.config.ts`
sets the automatic JSX runtime for its bundler directly.

Remotion is source-available under its own
[license](https://www.remotion.dev/license), not an open-source license. It is
free for individuals, non-profit organizations, and for-profit organizations with
up to three employees; larger for-profit organizations need a company license.

## Develop in Docker

Run these commands from the repository root inside WSL:

```bash
make website-dev
```

Open **http://localhost:5173** in your Windows browser. The command builds the
development image, starts Docker Compose with `--detach`, waits for its health
check, and returns to your terminal. Docker must be running in this WSL
distribution, with the native Linux Buildx and Compose plugins installed.
On systems configured with Docker's Ubuntu package repository, install the
plugins with `sudo apt-get install docker-buildx-plugin docker-compose-plugin`.
Docker Desktop and its Windows Compose command are not required.

The development container bind-mounts this directory read-only at `/app`.
Vite watches the same files you edit in WSL and updates or reloads the browser
when you save. Keep the checkout on WSL's Linux filesystem for reliable file
watching. Node and the locked Vite dependency live at `/opt/website-dev` in the
image, outside the source mount. Re-running `make website-dev` uses Docker's
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
make website-load-test
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
make website-load-test ARGS='--visits 3 --idle-seconds 15'
```

These measurements size the static Nginx container, not Goblin's application
or AI execution. Local HTTP page-load times exclude public-network latency,
TLS termination, and any ingress controller. A one-visitor/minute baseline
does not establish maximum capacity or prove behavior during traffic spikes.

### Measured baseline and per-pod sizing

Measured on 2026-10-11 UTC (2026-10-12 in Manila), using the production
Nginx 1.30.5 image in the local k3s cluster. The ten-minute traffic window
followed a sixty-second idle baseline. The container had been freshly restarted
before measurement so its lifetime memory peak includes that startup.

| Measurement | Idle baseline | One new visitor/minute |
| --- | ---: | ---: |
| Average CPU, vCPU | 0.0000339 | 0.0000925 |
| Peak CPU, approximately one-second window | 0.000269 | 0.004353 |
| Peak CPU, approximately 250 ms sample | 0.001073 | 0.012454 |
| Average memory working set | 2.163 MiB | 2.201 MiB |
| Peak memory working set | 2.594 MiB | 3.539 MiB |
| Peak sampled cgroup memory, including cache | 2.602 MiB | 3.547 MiB |
| Container-lifetime memory peak, including startup | 5.211 MiB | 5.211 MiB |

All ten visits succeeded: 120 HTTP 200 responses, 811.2 KiB transferred per
visit, median local page load 91.6 ms, and p95 107.2 ms. There were no browser
errors, restarts, OOM kills, memory-limit events, or CPU throttling during the
measurement. CPU includes the normal health probes; it is not idle-subtracted.
The initial harness trial rejected its own URL before issuing traffic; that
URL-normalization defect was fixed and the failed trial is excluded here.

Raw evidence from the completed sizing run is retained locally in
[the summary](../.artifacts/website/load-test/2026-10-11T17-32-28.572Z/summary.json)
and [the samples](../.artifacts/website/load-test/2026-10-11T17-32-28.572Z/samples.json).
These generated artifacts are not committed; rerun the command to produce
measurements for another host or a later website version.

| Resource | Request per pod | Limit per pod | Headroom above measured usage |
| --- | ---: | ---: | --- |
| CPU | `25m` (0.025 vCPU) | `100m` (0.1 vCPU) | Request is 3.3× the higher follow-up one-second peak; limit is 4.0× its 250 ms peak |
| Memory | `16Mi` | `32Mi` | Request is 2.6× the higher fresh-pod working-set peak; limit is 4.9× its lifetime peak |

A follow-up cold-browser visit after rollout also passed all twelve requests,
with no CPU throttling or memory-limit events during the visit. That fresh
container had a higher working-set peak of 6.262 MiB and lifetime memory peak
of 6.566 MiB. The final memory settings above use these higher observations,
not just the lower ten-minute baseline. Its
[validation summary](../.artifacts/website/load-test/2026-10-11T17-45-04.446Z/summary.json)
and raw samples are retained alongside the baseline.

A second follow-up under the larger memory allocation passed, with a higher
one-second CPU peak of 0.007494 vCPU and 250 ms peak of 0.024859 vCPU.
The final CPU settings use these higher observations to allow for run-to-run
variation; [that run's summary](../.artifacts/website/load-test/2026-10-11T17-48-03.596Z/summary.json)
retains the evidence. Memory remained below the prior fresh-pod peak.

The raw counters show 0.333–0.445 seconds of CPU throttling before those
follow-ups' idle baselines, including container startup, with the interim
`50m` CPU cap. Serving the tested traffic was not throttled. The final CPU cap
is `100m`. The runner reports pre-measurement counters separately from the
idle and load windows; zero throttling during a visit does not mean zero
throttling over the container's lifetime.

The [final verification](../.artifacts/website/load-test/2026-10-11T17-50-43.487Z/summary.json)
ran another fifteen-second idle baseline and full one-minute visit interval
with the final `25m`/`16Mi` requests and `100m`/`32Mi` limits. All twelve
requests passed; page load was 107.6 ms. CPU peaked at 0.004829 vCPU over one
second and 0.016759 vCPU over 250 ms. The container-lifetime memory peak was
4.719 MiB, below the higher value used for sizing. There were no restarts,
OOM kills, memory-limit events, or CPU throttling during that measurement.
Pre-measurement startup counters recorded 0.168 seconds of throttling across
two periods. The HPA had active CPU metrics, and the Deployment stayed at one
ready replica with the final resource settings.

Requests reserve scheduling capacity; CPU can burst above its request up to
the limit. These are rounded starting values for this workload, not a capacity
guarantee. Remeasure after significant asset, traffic, runtime, or hosting changes.
At three replicas, the total requests are `75m` CPU and `48Mi` memory; summed
limits are `300m` CPU and `96Mi` memory, excluding cluster and ingress overhead.

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
