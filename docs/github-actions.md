# GitHub Actions

Use short workflow titles and job IDs so the Actions list and check results are
easy to read. The workflow title gives the component; the job ID names the
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

Keep the repository name out of workflow titles and job IDs. Prefer specific
operations such as `verify`, `seal`, `publish`, and `deploy` over generic names
such as `checks` or `gate`.

Required check names must be unique across workflows, because the workflow title
does not qualify a required status check name. CI's required check is
`test_results`; Website Release's is `validate`. Jobs that are not required checks,
such as `publish` or `deploy`, can reuse IDs in other workflows. Reusable workflow
jobs appear as caller/called paths, such as `installation / prepare`; keep both
IDs short.

## Workflows and operations

| Workflow | File | Jobs | Purpose |
| --- | --- | --- | --- |
| CI | [ci.yml](../.github/workflows/ci.yml) | `changes`, `csharp`, `rust`, `javascript`, `integration`, `installer`, `test_results` | Select affected verification groups and report the required merge result |
| CI Maintenance | [ci-policy.md](../.github/workflows/ci-policy.md) | `prepare` and generated gh-aw jobs | Owner-only CI policy proposals |
| Docs Maintenance | [documentation.md](../.github/workflows/documentation.md) | `prepare` and generated gh-aw jobs | Owner-only documentation drift review on pull requests or manual dispatch |
| Platform Release | [platform-release.yml](../.github/workflows/platform-release.yml) | `prepare`, `verify`, `seal`, `publish`, `installation`, `report` | Publish the verified release pair, then prepare installation assets and deploy the website after approval |
| Installation Assets | [installation-assets.yml](../.github/workflows/installation-assets.yml) | `prepare`, `website` | Prepare the catalog from authenticated published assets, then call Website Release with that snapshot artifact |
| Recommend Release | [recommend-release.yml](../.github/workflows/recommend-release.yml) | `installation` | Recommend an existing release or repair its installation hosting through Website Release |
| Website Release | [website-release.yml](../.github/workflows/website-release.yml) | `validate`, `publish`, `deploy` | Verify the public website, publish its tested image to GHCR, and deploy the approved image to Kubernetes |
| Chisel Deploy | [chisel-deploy.yml](../.github/workflows/chisel-deploy.yml) | `verify`, `deploy` | Manually deploy the self-hosted WSL development tunnel using `ak3s-production` environment secrets |

Platform and goblinctl have separate versions and tags (`goblin-v*` and
`goblinctl-v*`). Azure installation is manual. GitHub's **Review deployments**
button approves both release publication (`goblin-release` environment) and
website deployment (`website-production` environment); neither approval installs
the platform. See [platform releases](releases.md) and
[website production](../website/production/README.md) for procedures.

Chisel Deploy runs only on manual dispatch. Its `deploy` job requires a `release/*`
branch and the `ak3s-production` environment, and uses a dedicated credential for
the website's AK3S cluster in the shared `goblin` namespace. Its `verify` job is
not a required branch check. See [Chisel setup](../deploy/chisel/README.md) for
bootstrap, DNS, Secrets, and manual public acceptance.

## CI selection and required result

[`config/ci-verification.json`](../config/ci-verification.json) defines trigger
lists, verification commands, registered project manifests, and dependencies
between groups. [`scripts/ci.mts`](../scripts/ci.mts) detects changes (`detect`),
runs one group (`run GROUP`), and reports the result (`report`).

Detection matches file extensions and file names anywhere in the repository, so
Rust under `backend/` still selects Rust. Group rules are checked before the shared
rules, so names such as `Cargo.toml`, `package.json`, or `pnpm-lock.yaml` select
only their group. Other JSON and TOML files are unclassified and select every
group. One file can select several groups. Markdown under `.github/workflows/` is
executable gh-aw configuration and selects full verification; other Markdown skips
runtime tests. `tests/fixtures/fake-codex.mts` and the root `package.json` and
`package-lock.json` also select C#, because C# tests use them.

| Changed input | Selected verification |
| --- | --- |
| Markdown or reStructuredText only | `changes` and `test_results` only; no installs, builds, or runtime tests |
| C# source, project, or configuration | C#, integration, and installer |
| Rust source, Cargo, toolchain, or test configuration | Rust formatting, Clippy, tests, and documentation tests, plus integration and installer |
| JavaScript/TypeScript, Node configuration, HTML, or CSS | Script, frontend, release-script, website, and video checks, plus integration and installer |
| Shared build/deployment inputs (such as SQL, shell, YAML, Dockerfiles, Makefiles) or unclassified files | Every group |
| Known unsupported language or unregistered project manifest | Every group; `test_results` fails with registration instructions |

Integration runs generated contract checks, dependency and schema checks, and
frontend, HTTP integration, deployment, release-script, maintenance, and Chisel
tests. Installer runs the release build, `make release-check-installer`, the
setup and install browser journeys, and `make platform-check-image`, so a missing
Docker build input fails CI instead of a later installation. Language-only tests
for an unchanged language skip even when integration builds that language.

PR runs compare the merge base of the payload's base and head commits. Pushes
compare the full before/after range, and merge queues compare the group's base and
head. Manual runs and pushes that create a branch verify every file in the head
snapshot. Checkout fetches full history. An invalid or missing comparison commit
fails detection; it never means "no changes". Rename detection is off, so both
sides of a rename are classified and renaming source to Markdown cannot hide its
removal. Removing unsupported source or a project manifest still selects full
verification, but does not require registering a runner for a file that no longer
exists.

`test_results` always runs and needs `changes` and every group. It passes only
when detection succeeded, the plan is valid and matches the job outputs, every
selected group succeeded, and every unselected group was skipped. Missing jobs,
failed or cancelled groups, unexpected skips, unsupported languages, and
unregistered projects fail it. A documentation-only plan passes with all groups
skipped. Do not add workflow-level path filters: the required check would then not
report.

The report step writes a job summary with the comparison, changed-file count,
selection, reasons, results, and overall result. GitHub publishes the job
conclusion as the check; the workflow needs only `contents: read`. Cancellation or
a runner failure cannot produce a passing check. Before changing the check name,
see [Rename references and roll out required checks](#rename-references-and-roll-out-required-checks).

To add a language or independent project, register its triggers and manifests in
the policy, define commands that actually build and test it, add its toolchain
setup, job, and selection output to `ci.yml`, and add the job to
`test_results.needs`. A configured group without a job cannot pass. Unknown
extensions select full verification, but that does not cover a language with no
runner. Languages under `unsupportedLanguages`, such as Go and Python, fail until
configured. [CI Maintenance](agentic-maintenance.md) proposes inventory updates in
draft PRs.

When changing this policy, run `make ci-test make-test scripts-typecheck`. Check
publication and merge-queue behavior need a real GitHub Actions run.

## Action runtimes

Use JavaScript actions whose `action.yml` declares `runs.using: node24`, including
actions called by composite actions. Setting `setup-node` to Node.js 24 only
selects the runtime for repository commands; each action declares its own runtime.
For artifact actions, use upload v6 and download v7 or newer. Keep SHA pins where
they are used, and change agentic workflow Markdown sources before
[regenerating their lock files](agentic-maintenance.md).

The shared [.NET setup action](../.github/actions/setup-dotnet/action.yml) installs
the Linux x64 SDK **10.0.403** from Microsoft's public
`dotnetcli.blob.core.windows.net` feed, not the `aka.ms` redirect. It checks a
pinned SHA-512 before extraction and keeps files under `.artifacts/dotnet/`. CI
(through `setup-verification`) and Platform Release use it. Change the version and
checksum together; the SDK must satisfy `global.json`.

## Installation commands and artifacts

Installation Assets runs `node scripts/release-github.mts installation-assets VERSION MODE`,
where `MODE` is `recommend` or `preserve`. It authenticates the chosen release and
installer, prepares the catalog, uploads it as `installation-source-<attempt>`, and
calls Website Release with that artifact. It does not commit or push.

When Website Release runs on its own, `validate` runs
`node scripts/installation-assets.mts snapshot OUTPUT --authenticate`. For a local
preview, omit `--authenticate`; GitHub asset digests and manifest checksums are
still checked. [Platform releases](releases.md#installation-defaults-and-recommendation)
describes which releases the snapshot includes and how signatures are checked.

`validate` uploads the snapshot as `website-installation-<attempt>`; `publish` and
`deploy` use that artifact instead of fetching releases again, and reruns use the
artifact from their completed validation job. `source.json` stays outside the
image and records the hash of the public catalog at snapshot time. Before
deploying, `check-baseline OUTPUT` fails if the public catalog changed during
approval, but accepts a public catalog that already equals this candidate after a
completed rollout. Snapshots and downloads stay under `.artifacts/`. The
`installation-assets` concurrency group serializes release preparation and its
website run; `website-production` serializes deployment.

## Rename references and roll out required checks

Update each renamed job's `needs` dependencies and expressions, reusable-workflow
paths, required-check templates, scripts that select checks or signer workflows,
and operating documentation together. Keep triggers, commands, permissions,
approval environments, and concurrency behavior when changing names.

The checked-in [ruleset template](../website/production/github-ruleset.json)
requires `validate` from GitHub Actions on `master`; CI requires `test_results`.
The template does not update the live ruleset. When changing check names, review
the active ruleset and plan its update before merging.

## Release signing identity

The workflow filename `platform-release.yml` and the `master` ref are part of the
release signing identity. Renaming the workflow stops existing installers from
being reused. See [platform releases](releases.md#verify-and-publish-the-candidate)
for the signer rules. Signing, publication, and website deployment need a real
GitHub Actions run; local checks cover only their offline contracts.
