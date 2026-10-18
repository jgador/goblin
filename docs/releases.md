# Goblin releases

Develop on `master`; stabilize and service each release line on
`release/<major>.<minor>`. Goblin and goblinctl keep independent versions.
The main product component is **platform**, following
[the component naming agreement](kubernetes-names.md). **Platform Release**
coordinates its publication with goblinctl. See [GitHub Actions](github-actions.md)
for workflow and job naming.

Stabilization and backports from [issue #41](https://github.com/jgador/goblin/issues/41)
are implemented. Step 3 of the [release workflow plan](release-workflow-plan.md)
automates coordinated candidate preparation. Steps 4–6 add verification, one
approval, coordinated publication and recovery. Existing published releases retain
their original assets and attestations; current installer authentication requires
the **Platform Release** signing identity described below.

## Development and stabilization

| Branch | Purpose |
| --- | --- |
| `master` | Ongoing development and the normal first destination for fixes. |
| `release/0.1` | Stabilization and later servicing for the `0.1.x` line. |
| `backport/0.1/<issue>-<description>` | A temporary branch for a targeted fix, merged by PR into `release/0.1`. |

Use feature/fix PRs targeting `master` for ordinary work. Cut a release branch
when its source is ready for stabilization, before a backport is needed. Continue
new development on `master`; keep the release branch focused on fixes needed for
that line. Do not merge all of `master` into an existing release branch.

To cut a line, start with a clean working tree and fetch the current source:

```bash
git fetch origin
```

Confirm that `origin/master` is the intended stabilization point and that
`test_results` passed for that exact commit. Then, for the `0.1` line:

```bash
git switch --create release/0.1 origin/master
git push --set-upstream origin release/0.1
```

Use the appropriate major/minor name for another line. Creating the branch does
not require publishing goblinctl, updating its dependency pin, or creating a tag.
Unreleased goblinctl changes may be present in the source being stabilized.

PRs targeting `master` or `release/*`, and pushes to either, run **CI**.
The `tests` job builds and verifies the source, release artifacts, and installation
contracts. The required `test_results` job passes only when `tests` succeeds and
reports failure when `tests` fails, is cancelled, or is skipped. Job IDs use short
lowercase names with at most one underscore; GitHub displays those IDs directly.
The same active ruleset requires PRs, an up-to-date passing check, and resolved
review conversations; it blocks deletion and force pushes. It has no bypass
actors and requires no second reviewer, so a sole maintainer can merge a passing
PR. Once a release branch exists, make changes through PRs rather than direct
pushes. Branch creation uses an already checked commit from `master`.

## Backport a fix

Normally merge the fix into `master` first. Decide which existing release lines
need it, and make a separate backport PR for each affected line.

For example, replace `MASTER_FIX_COMMIT` with the commit that landed on `master`:

```bash
git fetch origin
git switch --create backport/0.1/123-fix-description origin/release/0.1
git cherry-pick -x MASTER_FIX_COMMIT
```

Use the resulting squash commit or the relevant ordinary commits. Do not blindly
cherry-pick a merge commit. The `-x` option records the original commit so the fix
can be traced across branches. Resolve conflicts for the release branch's code,
then use `git cherry-pick --continue`; use `git cherry-pick --abort` to abandon the
attempt. After resolving conflicts, confirm that the final commit message still
identifies the original commit. Include any necessary prerequisite fixes explicitly.

Run the checks relevant to the fix on the backport, then push its branch:

```bash
git push --set-upstream origin backport/0.1/123-fix-description
```

Open a PR with **base `release/0.1`** and this backport branch as the head. Link the
original issue/PR and commit, explain why the release line needs the fix, and note
conflict resolutions and verification. Wait for `test_results` and merge through
the protected PR flow. Confirm the checks on the resulting release-branch commit
before using it as a later candidate.

A fix that applies only to an older line may start there; document why it does
not apply to `master`, or track the corresponding forward fix. Keep this an
explicit exception to the master-first approach.

Backport application code, goblinctl code, and bundled assets by the same process.
Do not substitute a newer `master` installer or merge an unrelated dependency-pin
update to make the backport possible. Coordinated preparation decides whether the release branch's source can reuse
goblinctl or needs a new build. Neither cutting a branch nor merging a backport publishes a
release.

## Prepare a coordinated candidate

1. Merge source changes or backports into the intended release line. Wait for
   **test_results** to pass for the exact commit.
2. Open **Actions → Platform Release → Run workflow** on `master`.
   The workflow uses the checked-in preparation tooling from this run; **Source
   branch** selects the product source, for example `release/0.1`.
3. Choose **preview** or **stable**. Leave the Goblin version blank to suggest the
   next version within that line. An optional full source SHA must already belong
   to that release branch; otherwise preparation captures its current tip once.
4. Normally leave **goblinctl version** blank. Preparation reads published
   installer metadata and compares the frozen source's shipping inputs and
   required capabilities. It prefers the checked-in selection, then the source's
   Cargo version, then newer published versions. An exact match can be reused
   even when its source is older or the checked-in pin has not been updated.
   Before reuse, preparation verifies the selected installer's archive,
   authenticates its archive and manifest, and checks its tag's source commit.
5. If nothing matches, preparation builds goblinctl from the captured source. It
   uses the source's Cargo version when unused, otherwise suggests the next unused
   patch version. An override can select a matching published installer or an
   unused version. Releases, drafts, and orphan tags reserve their versions.
6. Preparation uploads **goblin-candidate-<attempt>** and displays its summary. It contains the
   selected installer archive, installer manifest/checksums, generated Azure
   assets, and the coordinated `release.json` with checksums for every asset.

Goblin and goblinctl retain independent versions. The chosen goblinctl version is
an explicit compile-time input, recorded in its manifest and reported by the
binary, metadata, and local installer paths. Cargo manifests and lockfiles are
unchanged. A release branch cut before this build support existed needs the
support backported if it must build a version different from its Cargo version.

Source is exported from Git into an isolated tree under `.artifacts/`; build
outputs cannot change the checkout. Bicep reads a candidate-specific installer
selection in another isolated rendering tree, so both the bootstrap and displayed
metadata use the recorded installer. Development pins and service image locks
are unchanged, and preparation creates no dependency PR.

Shipping inputs include Rust source/build configuration, setup and PostgreSQL
bundles, branding, and licensing files. Additions and deletions count. Changed
inputs require a matching publication or a new build. Missing capabilities in the
source, failed authentication, failed source checks, and conflicting requested
versions stop preparation with a failing job and a next action. Installer manifests
must contain the current schema and input fingerprints.

A nonmatching installer's signatures or tag cannot block a new
build. Its metadata is used only to rule it out; preparation does not execute or
include its archive in the candidate. Failed verification of a selected installer
stops preparation. Investigate its provenance or start a new preparation with an
unused **Optional goblinctl version** (`--installer-version` in the preparation
CLI). Keep the existing release's tag and assets unchanged.

## Verify and publish the candidate

Preparation and verification have read permissions. Verification uses a separate
checkout of the captured source. It checks every candidate hash, the matching
installer inputs and capabilities, the actual executable's version, metadata and
installation validator, and reproduces the selected Azure assets byte-for-byte.
It runs formatting, Clippy, `make test` with the candidate executable, and the setup
and installation browser journeys. Source-check CI and candidate verification are
both required. Database-dependent tests remain opt-in and report skips.

After verification succeeds, a separate job downloads the original artifact by ID,
seals its record with deploymentChecks: passed, attests the pair and any newly built installer, and
uploads **goblin-ready-<attempt>**. Before sealing, deploymentChecks: pending is
expected. Review the sealed candidate and the **Ready to publish** summary.

Use **Review deployments** to approve the `publish` job in **Platform Release**
once for the pair. The goblin-release environment controls this approval. The
publication job checks the original artifact, manifest digest, run/attempt,
source and tooling revision, source checks, branch membership and provenance again.
It checks both version slots before writing either one. It publishes a newly built
goblinctl first, then publishes Goblin using the same verified assets; a reused installer is verified
without creating another installer release. Tags and published assets are immutable
under this workflow.

Website installation deployment follows successful publication. The workflow reports
release publication separately from website installation deployment. **Make this release
the installation default** is unchecked by default; only an explicit selection updates
the recommendation.

To update the development installer dependency:

```bash
cargo xtask release pin-installer --version X.Y.Z
```

Creating a release branch or merging a backport does not publish anything. A manual
preparation run now proceeds to verification and then waits for publication approval.
An unapproved candidate stays unpublished. Installer signing requires the
`platform-release.yml` workflow on master. Candidate authentication checks this
signer path, the recorded workflow revision, and `refs/heads/master`. Installer
authentication accepts only that workflow path on master. Installers signed by
superseded workflow paths need a new version published through **Platform Release**
before reuse; existing tags and assets remain unchanged. After merging the rename,
start a fresh run from updated master rather than rerunning an older workflow
revision. Only the current release record schema is supported.

## Installation defaults and recommendation

The [website installation page](https://goblinboard.com/install/) lists
published Goblin versions and selects the recommendation. Each version opens its own Azure
assets. Azure displays **Goblin version**, already bound to the source and installer.

For existing published releases, promotion, rollback, and installation hosting recovery use
**Recommend Release** with an already published version. Uncheck its
recommendation option to repair the website installation deployment while preserving the
current default.
An older published version in the current installation format remains
independently installable. Superseded preproduction manifest formats stay on
GitHub Releases as historical artifacts, and are excluded from the website catalog.
A recommendation must use the current format.

An empty catalog requires a published version before installation is enabled.
A deployed catalog with no recommendation requires an explicit version selection.
On first publication, the newest stable version is selected, otherwise the newest
preview. During development, explicitly recommend a tested preview. For a stable
announcement, publish and recommend the stable release; later previews do not
replace it automatically. A recommendation affects new installations.

Installation asset preparation follows coordinated publication through the reusable
**Installation Assets** workflow (`installation-assets.yml`). It authenticates the
selected release and installer, then lists the GitHub Releases API to download all
current-format Goblin assets. Signed manifests, GitHub asset digests, template hashes,
and checksums must agree. Historical preproduction formats stay on GitHub Releases.

The frozen snapshot is a GitHub Actions artifact passed into **Website Release**.
The tested Nginx image serves `goblinboard.com/install/azure/`, through the existing
`website-production` approval. No distribution branch or GitHub Pages is involved.
The currently deployed website catalog preserves the recommendation; explicit
recommendation changes take effect when the approved website image deploys.
For the first publication, the newest supported stable release is preferred,
otherwise the newest preview. A null deployed recommendation remains null.
The repository-wide GitHub “latest release” never determines Goblin's default.
Deployment rejects an advanced source or a changed deployed catalog while approval
was pending. Versioned assets remain attached to GitHub Releases.

The installation page presents Azure as available now. AWS and Google Cloud
Platform are planned and have no deployment action or templates. Provider assets
and catalogs belong under their own website directory; Azure remains the only
implemented provider and all cloud installation remains manual.

## Repository setup

Source protection is defined in
[`.github/goblin-ruleset.json`](../.github/goblin-ruleset.json). Apply it as an active
repository ruleset for `master` and release branches. The
[live ruleset](https://github.com/jgador/goblin/rules/24076471) is active for both.
The ruleset uses GitHub's
`refs/heads/release/**/*` pattern to cover both immediate and nested release
branch names; Actions uses `release/**`. Verify the live ruleset as well as the
file. Install `ci.yml` before activating protection in a new repository so
the required `test_results` job exists. Its PR trigger covers all target branches;
its push trigger covers `master` and `release/**`.

When adopting the CI rename in an existing repository, wait for `test_results` to
pass on the rename PR, then replace the active ruleset's `goblin-checks` requirement
with `test_results` from GitHub Actions before merging. Editing the JSON template
does not update the active GitHub ruleset. Release preparation requires a source
commit with a passing `test_results` check.

Preparation and verification need read access to source checks, releases and
attestations. Signing has separate OIDC/attestation permissions; the protected
publication job has release write permissions. The workflow checks that approval
is configured before starting preparation. Website production configuration affects the
later `installation` job and does not block recovery of release publication.

- Configure **goblin-release** with at least one required maintainer reviewer.
  Allow that maintainer to approve their own run. Keep its deployment branch policy
  on **master**: this identifies the workflow, while the selected product source is
  independently checked against release/<major>.<minor>.
- Configure the existing **website-production** environment and GHCR package as
  described in [website production](../website/production/README.md). The
  **Installation Assets** workflow calls Website Release, including its production
  approval. No GitHub Pages configuration or Azure credentials are required.
- Keep **test_results** as the required source check. No pin-PR permissions or
  separate goblinctl-release approval are needed by the coordinated workflow.

Verify the active required check, master approval policy and website installation hosting
against these settings. Azure credentials and subscription settings are not required.

### Manual Azure installation

Repository automation does not authenticate to Azure, deploy Goblin, or create or
delete Azure resources. There is no live Azure release check or scheduled cloud
cleanup. Release preparation and publication require only the local checks and
GitHub configuration described above.

After publication, choose a release on the website installation page and follow
[manual Azure deployment guide](../deploy/azure/README.md). The templates and
installer remain available for that process. The maintainer chooses the Azure
subscription and manages the installation and its resources directly.

The release record contains the source and installer identities, asset checksums,
and deployment-check result. It does not claim live Azure installation coverage.

`AGENTS.md` makes the first real deployment the SQL migration immutability
boundary. Record the first real manual deployment; an announcement is a separate
event. Local provisioning does not freeze the baseline.

## Local verification and recovery

```bash
make dependencies-check
make deploy-generate                      # writes .artifacts/azure/
make format-rust
make lint-rust CARGO_ARGS="-p xtask"
make test-rust CARGO_ARGS="-p xtask"
make test
make test-browser ARGS="tests/e2e/setup.spec.ts tests/e2e/install.spec.ts"
```

`make deploy-generate` generates review assets bound to the current SHA and a
`0.0.0-preview.1` development label. These are local test outputs. Preparation
creates the actual versioned release assets and requires clean merged source.

To authenticate the pinned installer and exercise its validator locally:

```bash
make release-build
cargo xtask release verify-installer
```

The public archive is downloaded and verified; this command does not deploy Azure.
`cargo xtask release check-installer` validates local compiler input coverage and
contracts without requiring a published binary. `make release-local` creates a local
review archive. Current provenance verification requires the **Platform Release**
signing identity; installers signed by superseded workflows require a fresh
publication under a new version before reuse.

## Recover a failed operation

Candidate artifacts expire after 30 days. Use the original run's **Re-run failed
jobs** action for failed verification, signing, publication or website installation
deployment. The workflow retains successful upstream outputs, including the artifact IDs and the
original preparation attempt. A retried publication job may require environment
approval again; there is still one publication job for the pair.

Publication first verifies existing tags, drafts and assets against the retained
candidate. Drafts are discovered through GitHub's paginated release list because
the release-by-tag endpoint only returns published releases. Publication adds
only missing draft assets, verifies every uploaded byte before
publishing the draft, and skips completed components. If goblinctl was published
but Goblin failed, the retry verifies goblinctl and finishes Goblin. Conflicting
tags, foreign drafts, changed assets or incomplete already-published releases stop
with an error; the workflow never replaces them.

Use **Re-run all jobs** only when intentionally preparing a fresh candidate. A
blank source SHA captures the release branch tip again. After changing release
tooling, dispatch a new run from updated master: reruns use the original workflow
revision. Missing/expired artifacts require fresh preparation and approval, with
unused versions where a prior tag or draft reserved the old version. Preserve
completed publications; do not substitute new bytes into them.

If release publication succeeded and only website installation deployment failed, rerun
the failed `installation` job or use **Recommend Release** for that published version.
Uncheck recommendation to repair installation hosting without changing the installation default.
The releases remain published even while website installation deployment needs attention.

Automated release checks do not establish live Azure installation coverage. The
app is built from source on the VM during manual installation; upstream OS and
image tags can affect later installs. Opt-in PostgreSQL suites and real-runtime
journeys report their own coverage and skips.
