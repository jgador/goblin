# GitHub Actions

Use concise workflow titles and job IDs so the Actions list and check results are
easy to read. Workflow titles provide the component context; job IDs describe the
operation. Use **platform** for the main product built from `backend/` and
`frontend/`, following [the component naming agreement](kubernetes-names.md).

## Naming convention

| Element | Convention | Examples |
| --- | --- | --- |
| Workflow title (`name`) | One or two words in Title Case; uppercase acronyms and official product capitalization | `CI`, `Platform Release`, `GitHub Pages`, `Website Release` |
| Workflow filename | Short lowercase name; kebab-case between words, with `.yml` | `ci.yml`, `platform-release.yml` |
| Job ID | Short lowercase name with at most one underscore | `tests`, `seal`, `test_results` |
| Job display name | Omit job-level `name` so GitHub displays the job ID | `validate`, `publish`, `deploy` |
| Step name | Descriptive phrase explaining the operation | `Verify the GitHub Pages installation links` |

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
`github_pages / deploy`; keep both IDs short.

## Workflows and operations

| Workflow | File | Jobs | Purpose |
| --- | --- | --- | --- |
| CI | [ci.yml](../.github/workflows/ci.yml) | `tests`, `test_results` | Verify source, release artifacts, and installation contracts; report the required result |
| Platform Release | [platform-release.yml](../.github/workflows/platform-release.yml) | `prepare`, `verify`, `seal`, `publish`, `github_pages`, `report` | Prepare, verify, attest, and publish the coordinated platform/goblinctl releases, then deploy the GitHub Pages installation catalog |
| GitHub Pages | [github-pages.yml](../.github/workflows/github-pages.yml) | `deploy` | Reusable deployment of the installation catalog and versioned assets to GitHub Pages |
| Recommend Release | [recommend-release.yml](../.github/workflows/recommend-release.yml) | `github_pages` | Recommend an existing platform release or repair its GitHub Pages deployment |
| Website Release | [website-release.yml](../.github/workflows/website-release.yml) | `validate`, `publish`, `deploy` | Verify the public website, publish its tested image to GHCR, and deploy the approved image to Kubernetes |

A release makes a tested version and its artifacts available. A deployment puts
selected content or an image into a running environment. The platform and
goblinctl keep independent versions and GitHub Releases; the coordinated workflow
can reuse an authenticated installer instead of publishing another version.
Platform installation in Azure remains manual. Website Release separates image
publication from approved Kubernetes deployment. Use **Website** for the public
website at `goblinboard.com` and **GitHub Pages** for the installation catalog at
`jgador.github.io/goblin/`. GitHub Pages is the official product name and stays
plural even when it hosts a single page.

GitHub's **Review deployments** UI also manages environment approval for the
release publication job. That UI label does not mean the job installs the platform.
See [platform releases](releases.md) and [website production](../website/production/README.md)
for operating procedures.

## GitHub Pages commands and artifacts

The reusable workflow runs `node scripts/release-github.mts github-pages VERSION MODE`
to prepare the catalog and `node scripts/release-github.mts check-github-pages VERSION MODE`
to verify the deployed assets and recommendation. `MODE` is `recommend` or
`preserve`. These are release operations that authenticate published assets and
can update the `gh-pages` branch and installation default.

The generated worktree is `.artifacts/github-pages/worktree`; the Pages artifact
contains only `.artifacts/github-pages/output`, excluding worktree Git metadata.
Temporary release downloads and verification files also stay under
`.artifacts/github-pages/` with `github-pages-*` prefixes. The `github-pages`
concurrency group serializes deployments. Keep the existing `github-pages`
environment, `gh-pages` branch, and published URL.

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
original workflow revision. Actual signing, publication, and GitHub Pages deployment
require verification in GitHub Actions; local checks cover their offline contracts.
