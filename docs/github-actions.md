# GitHub Actions

Use concise workflow titles and job IDs so the Actions list and check results are
easy to read. Workflow titles provide the component context; job IDs describe the
operation. Use **platform** for the main product built from `backend/` and
`frontend/`, following [the component naming agreement](kubernetes-names.md).

## Naming convention

| Element | Convention | Examples |
| --- | --- | --- |
| Workflow title (`name`) | One or two words in Title Case; uppercase acronyms and official product capitalization | `CI`, `Platform Release`, `Installation Assets`, `Website Release` |
| Workflow filename | Short lowercase name; kebab-case between words, with `.yml` | `ci.yml`, `platform-release.yml` |
| Job ID | Short lowercase name with at most one underscore | `tests`, `seal`, `test_results` |
| Job display name | Omit job-level `name` so GitHub displays the job ID | `validate`, `publish`, `deploy` |
| Step name | Descriptive phrase explaining the operation | `Verify the website installation assets` |

Keep the repository name out of workflow titles and job IDs when the Actions UI
already supplies that context. Prefer specific operations such as `verify`, `seal`,
`publish`, and `deploy` over generic names such as `checks` or `gate`.

Required check names must be distinct across workflows. CI's `tests` job already
uses that name, so Website Release uses `validate`. Its required check is
`validate` from GitHub Actions, while CI's required check is `test_results`. Workflow titles do
not qualify required status-check names. Reusing operation IDs such as `publish`
or `deploy` in independent workflows is fine when those jobs are not required
branch checks.

Reusable workflows can appear with caller/called job paths, such as
`installation / prepare`; keep both IDs short.

## Workflows and operations

| Workflow | File | Jobs | Purpose |
| --- | --- | --- | --- |
| CI | [ci.yml](../.github/workflows/ci.yml) | `tests`, `test_results` | Verify source, release artifacts, and installation contracts; report the required result |
| Platform Release | [platform-release.yml](../.github/workflows/platform-release.yml) | `prepare`, `verify`, `seal`, `publish`, `installation`, `report` | Publish the verified release pair, then prepare installation assets and deploy the website through its production approval |
| Installation Assets | [installation-assets.yml](../.github/workflows/installation-assets.yml) | `prepare`, `website` | Prepare the catalog from authenticated published assets, then call Website Release with the exact snapshot artifact |
| Recommend Release | [recommend-release.yml](../.github/workflows/recommend-release.yml) | `installation` | Recommend an existing release or repair its installation hosting through Website Release |
| Website Release | [website-release.yml](../.github/workflows/website-release.yml) | `validate`, `publish`, `deploy` | Verify the public website, publish its tested image to GHCR, and deploy the approved image to Kubernetes |

A release makes a tested version and its artifacts available. A deployment puts
selected content or an image into a running environment. The platform and
goblinctl keep independent versions and GitHub Releases; the coordinated workflow
can reuse an authenticated installer instead of publishing another version.
Platform installation in Azure remains manual. Website Release separates image
publication from approved Kubernetes deployment. Use **Website** for the public
website at `goblinboard.com`, including the installation page, catalog, and Azure
templates at `goblinboard.com/install/`. **Installation Assets** prepares release
data; **Website Release** serves it through the existing website container.

GitHub's **Review deployments** UI also manages environment approval for the
release publication job. That UI label does not mean the job installs the platform.
See [platform releases](releases.md) and [website production](../website/production/README.md)
for operating procedures.

## Installation commands and artifacts

The reusable workflow runs `node scripts/release-github.mts installation-assets VERSION MODE`
to authenticate the chosen release/installer and prepare the catalog. `MODE` is
`recommend` or `preserve`. It retains a read-only snapshot artifact and calls
Website Release. It does not commit, push, or write a distribution branch.

Ordinary Website Release validation runs
`node scripts/installation-assets.mts snapshot OUTPUT --authenticate`.
Local preview uses the same command without `--authenticate`; public GitHub
asset digests and manifest checksums are still checked. The script lists GitHub
Releases, excludes drafts, unrelated products, and superseded manifest formats,
and downloads current assets. CI verifies signatures against the recorded workflow
revision, master ref, and GitHub-hosted runner requirement, then checks the verified
certificate for the current `platform-release.yml` signer. Valid signatures from
other workflows are excluded; invalid signatures fail the build. Only
`azure/releases.json` and the five files per version enter the website image.

Publish and deploy restore the validated `website-installation-<attempt>` artifact rather
than fetching releases again. `source.json` records the deployed website catalog
hash outside the image. `check-baseline OUTPUT` rejects a catalog changed during
approval, while allowing an exact candidate to be rechecked after a completed rollout.
Artifact names include the producing attempt; rerun jobs consume the retained
artifact from their completed validation job. The website build snapshot stays under
`.artifacts/installation-assets/output`; release downloads and test artifacts
stay under `.artifacts/`. The `installation-assets` group serializes release
preparation and its website run; `website-production` serializes deployment.
No GitHub Pages configuration, permissions, or branch is required.

## Rename references and roll out required checks

Update each renamed job's `needs` dependencies and expressions, reusable-workflow
paths, required-check templates, scripts that select checks or signer workflows,
and operating documentation together. Preserve triggers, commands, permissions,
approval environments, and concurrency behavior when changing names.

For the Website Release rename, wait for `validate` to pass on the rename PR.
Then replace the active website ruleset's `website-checks` requirement with `validate`, selecting
**GitHub Actions** as the source, before merging. The local
[website ruleset template](../website/production/github-ruleset.json) does not
update the live ruleset. Keep CI's existing `test_results` requirement.

## Release signing identity

The filename is part of the workflow's attestation identity. Candidate
authentication requires `jgador/goblin/.github/workflows/platform-release.yml`,
the recorded workflow revision, and `refs/heads/master`. Installer authentication
accepts only the same workflow path on `master`, scoped to the repository, and
rejects self-hosted runners.

Installers signed by superseded workflow paths cannot be reused by the current
release tooling. Publish a new installer version through **Platform Release**
before reuse; preserve existing tags and published bytes. After this rename is
merged, start a fresh release run from updated `master`, since reruns retain the
original workflow revision. Actual signing, publication, and website installation deployment
require verification in GitHub Actions; local checks cover their offline contracts.
