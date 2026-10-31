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
| Job ID | Short lowercase name with at most one underscore | `changes`, `rust`, `test_results` |
| Job display name | Omit job-level `name` so GitHub displays the job ID | `validate`, `publish`, `deploy` |
| Step name | Descriptive phrase explaining the operation | `Verify the website installation assets` |

Keep the repository name out of workflow titles and job IDs when the Actions UI
already supplies that context. Prefer specific operations such as `verify`, `seal`,
`publish`, and `deploy` over generic names such as `checks` or `gate`.

Required check names must be distinct across workflows. Website Release's required
check is `validate` from GitHub Actions, while CI's required check is `test_results`. Workflow titles do
not qualify required status-check names. Reusing operation IDs such as `publish`
or `deploy` in independent workflows is fine when those jobs are not required
branch checks.

Reusable workflows can appear with caller/called job paths, such as
`installation / prepare`; keep both IDs short.

## Workflows and operations

| Workflow | File | Jobs | Purpose |
| --- | --- | --- | --- |
| CI | [ci.yml](../.github/workflows/ci.yml) | `changes`, `csharp`, `rust`, `javascript`, `integration`, `installer`, `test_results` | Select affected verification groups and report the required merge result |
| CI Maintenance | [ci-policy.md](../.github/workflows/ci-policy.md) | `prepare` and generated gh-aw jobs | Owner-only proposed CI policy updates |
| Docs Maintenance | [documentation.md](../.github/workflows/documentation.md) | `prepare` and generated gh-aw jobs | Owner-only documentation drift review on pull requests or manual dispatch |
| Platform Release | [platform-release.yml](../.github/workflows/platform-release.yml) | `prepare`, `verify`, `seal`, `publish`, `installation`, `report` | Publish the verified release pair, then prepare installation assets and deploy the website through its production approval |
| Installation Assets | [installation-assets.yml](../.github/workflows/installation-assets.yml) | `prepare`, `website` | Prepare the catalog from authenticated published assets, then call Website Release with the exact snapshot artifact |
| Recommend Release | [recommend-release.yml](../.github/workflows/recommend-release.yml) | `installation` | Recommend an existing release or repair its installation hosting through Website Release |
| Website Release | [website-release.yml](../.github/workflows/website-release.yml) | `validate`, `publish`, `deploy` | Verify the public website, publish its tested image to GHCR, and deploy the approved image to Kubernetes |
| Chisel Deploy | [chisel-deploy.yml](../.github/workflows/chisel-deploy.yml) | `verify`, `deploy` | Manually deploy the self-hosted WSL development tunnel using `ak3s-production` environment secrets |

Platform and goblinctl have independent releases; Azure installation is manual.
**Installation Assets** prepares authenticated release data. **Website Release**
publishes the website image and deploys it after production approval. GitHub's
**Review deployments** label also covers release publication approval; it does
not mean the platform is installed. See [platform releases](releases.md) and
[website production](../website/production/README.md) for operating procedures.

Chisel Deploy targets the website's existing AK3S cluster in the shared `goblin`
namespace using a dedicated credential. It runs only on manual dispatch;
deployment requires a `release/*` branch and the `ak3s-production` environment.
Its `verify` job is not a new required branch check. See
[Chisel setup](../deploy/chisel/README.md) for bootstrap,
DNS, Secrets, and manual public acceptance.

## CI selection and required result

[`config/ci-verification.json`](../config/ci-verification.json) owns trigger lists,
verification commands, registered project manifests, and cross-group requirements.
[`scripts/ci.mts`](../scripts/ci.mts) detects changes, runs a selected group, and
reports results. Detection uses extensions and recognized filenames anywhere in
the repository: Rust under `backend/` still selects Rust. Specific Cargo, NuGet,
Node, toolchain, and compiler configuration names take precedence over generic
JSON/TOML extensions. Multiple matches select multiple groups.
Markdown under `.github/workflows/` is executable agentic configuration and selects
full verification; ordinary documentation Markdown still skips runtime tests.
The shared Codex Node fixture and root Node manifests (which pin the runtime used
by C# tests) also select their C# consumers through explicit file dependencies;
directory names do not determine a language's ownership.

| Changed input | Selected verification |
| --- | --- |
| Markdown or reStructuredText only | Detection and `test_results`; no dependency installation, builds, or runtime tests |
| C# source/project/configuration | C# plus shared integration and installer checks |
| Rust source/Cargo/toolchain/test configuration | Rust formatting, Clippy, tests, and documentation tests, plus integration and installer checks |
| JavaScript/TypeScript, Node configuration, HTML, or CSS | Browser/module, release-script, website, and video checks, plus integration and installer checks |
| Shared build/deployment inputs or unclassified files | Every configured group |
| Known unsupported language or unregistered project manifest | Existing groups run; the required result fails with registration instructions |

Integration checks retain generated contract values, dependency/schema checks,
frontend, release-script, HTTP, and offline deployment tests. Installer checks retain the musl release build,
compiler-input verification, and the existing setup/install browser journeys.
They also build the platform Docker image (`make platform-check-image`), so a
missing build input fails CI rather than a later installation.
Their inclusion for each language is conservative: C# HTTP contracts, Rust
embedded assets, and browser setup code cross those boundaries. Separate runners
prevent concurrent build/cleanup commands from sharing output. Pure language
tests for an unchanged language can skip even when its build is required by an
integration group. The full local `make check` remains available.

PR detection uses the merge base of the payload's base/head commits; pushes compare
the complete before/after range. Merge queues compare the group's base/head.
Manual runs and new-branch pushes select full verification from the tracked head
snapshot. Checkout fetches complete history. Invalid or unavailable comparisons
fail detection rather than assuming no changes. Additions, deletions, and both
sides of renames are classified; renaming source to Markdown cannot hide removal.
Removed unsupported source or project manifests still select full verification,
but do not require registration of a runner for code absent from the head snapshot.

`test_results` always runs and needs detection plus every verification group
directly. It accepts only successful detection, a valid/consistent selection plan,
success for every selected group, and `skipped` for every unselected group.
Missing jobs, failed/cancelled selected jobs, unexpected skips, unsupported
languages, and unregistered projects fail it. A documentation-only plan passes
with explicitly skipped runtime groups. Keep the workflow enabled for all changes;
workflow-level path filtering would prevent the required result from reporting.

The result step writes an Actions job summary with comparison, changed-file count,
group selection, representative reasons, outcomes, and the overall merge result.
GitHub Actions publishes the job's check conclusion automatically. No Commit
Status API call or additional write permission is used. The required check remains
`test_results` from GitHub Actions, so the existing ruleset name/source is preserved.
Workflow cancellation or runner/setup failure still prevents a passing check.

To add a language or independent project, register triggers and manifests, define
commands that actually include that project, add its toolchain setup/job and
selection output, and include it in `test_results.needs`. A configured group with
no reported job cannot pass. Unknown extensions select existing full verification;
that fallback does not establish coverage for a language with no configured runner.
Go and other recognized unsupported source types fail until explicitly configured.
The [owner-only maintenance workflows](agentic-maintenance.md) discover manifest
drift and propose policy updates in draft PRs using the repository's OpenAI key.

Run `make ci-test`, `make make-test`, `make scripts-typecheck`, and workflow
lint when changing this policy. Tests exercise real Git ranges/renames, output
transport, documentation/mixed/configuration changes, registration, command
failure, summary escaping, and required-result failures. Actual check publication
and merge-queue behavior require a GitHub Actions run. Website Release is an
independent workflow and retains its own `validate` check and triggers.

## Action runtimes

Use JavaScript actions whose `action.yml` declares `runs.using: node24`, including
actions called by composite actions. Setting `setup-node` to Node.js 24 only
selects the runtime for repository commands; each action declares its own runtime.
For artifact actions, upload v6 and download v7 are the first releases that
default to Node.js 24. Preserve SHA pins where used, and update agentic workflow
Markdown sources before [regenerating their lock files](agentic-maintenance.md).
These actions require Actions Runner v2.327.1 or newer; the workflows use
GitHub-hosted Ubuntu 24.04 runners.

The shared [.NET setup action](../.github/actions/setup-dotnet/action.yml) installs
Linux x64 SDK **10.0.403**, including its ASP.NET Core and .NET runtimes, from
Microsoft's public `dotnetcli.blob.core.windows.net` feed. Its SHA-512 is pinned
from Microsoft's release metadata and checked before extraction. Both CI and
Platform Release use this action. Update its version and checksum together when
changing the CI SDK; `global.json` retains the application's SDK compatibility
policy, which the selected SDK must satisfy. This avoids the `aka.ms` redirect to
the unavailable origin feed and an additional floating LTS runtime download.
Downloads and extracted SDK files stay under `.artifacts/dotnet/`.

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

The checked-in website ruleset requires `validate` from GitHub Actions; CI uses
`test_results`. The [ruleset template](../website/production/github-ruleset.json)
does not update the live ruleset. When changing check names, review the active
ruleset and arrange its update before merging.

## Release signing identity

The filename is part of the workflow's attestation identity. Candidate
authentication requires `jgador/goblin/.github/workflows/platform-release.yml`,
the recorded workflow revision, and `refs/heads/master`. Installer authentication
accepts only the same workflow path on `master`, scoped to the repository, and
rejects self-hosted runners.

Installers signed by superseded workflow paths cannot be reused by the current
release tooling. Publish a new installer version through **Platform Release**
before reuse; preserve existing tags and published bytes. After changing the signing workflow, start a fresh release run from updated
`master`, since reruns retain the original workflow revision. Actual signing, publication, and website installation deployment
require verification in GitHub Actions; local checks cover their offline contracts.
