# Goblin releases

Develop on `master`; stabilize and service each release line on
`release/<major>.<minor>`. Goblin and goblinctl keep independent versions.

Stabilization and backports from [issue #41](https://github.com/jgador/goblin/issues/41)
are implemented. Step 3 of the [release workflow plan](release-workflow-plan.md)
automates coordinated candidate preparation. Verification, approval, publication,
and recovery remain follow-up work. Existing published releases remain available.

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
`goblin-checks` passed for that exact commit. Then, for the `0.1` line:

```bash
git switch --create release/0.1 origin/master
git push --set-upstream origin release/0.1
```

Use the appropriate major/minor name for another line. Creating the branch does
not require publishing goblinctl, updating its dependency pin, or creating a tag.
Unreleased goblinctl changes may be present in the source being stabilized.

PRs targeting `master` or `release/*`, and pushes to either, run `goblin-checks`.
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
conflict resolutions and verification. Wait for `goblin-checks` and merge through
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
   **goblin-checks** to pass for the exact commit.
2. Open **Actions → Prepare Goblin release → Run workflow** on `master`.
   The workflow uses the checked-in preparation tooling from this run; **Source
   branch** selects the product source, for example `release/0.1`.
3. Choose **preview** or **stable**. Leave the Goblin version blank to suggest the
   next version within that line. An optional full source SHA must already belong
   to that release branch; otherwise preparation captures its current tip once.
4. Normally leave **goblinctl version** blank. Preparation authenticates published
   installer metadata and compares the frozen source's shipping inputs and
   required capabilities. It prefers the checked-in selection, then the source's
   Cargo version, then newer published versions. An exact match can be reused
   even when its source is older or the checked-in pin has not been updated.
5. If nothing matches, preparation builds goblinctl from the captured source. It
   uses the source's Cargo version when unused, otherwise suggests the next unused
   patch version. An override can select a matching published installer or an
   unused version. Releases, drafts, and orphan tags reserve their versions.
6. Download **goblin-candidate-<attempt>** and review its summary. It contains the
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
versions stop preparation with a failing job and a next action. Legacy manifests
without input fingerprints are never reused.

The workflow has read permissions and stops after candidate upload. It checks the
packaged binary's version, metadata, and installation-request validator. These
checks do not replace the full candidate verification planned in step 4:
`deploymentChecks` remains `pending`. Candidates are neither approved nor
published, and the original seal/publication commands reject their schema.

## Publication during this phase

Coordinated publication remains disabled until steps 4–6 are implemented and
verified. Do not use **Publish goblinctl** as a prerequisite to preparation; its
standalone publication/pin flow remains only for the existing implementation and
will be retired during workflow cleanup. The original installer pin command is
still available for deliberate development dependency updates:

```bash
cargo xtask release pin-installer --version X.Y.Z
```

Creating a release branch, merging a backport, and preparing a candidate do not
publish releases or change the installation recommendation.

## Installation defaults and recommendation

The [installation page](https://jgador.github.io/goblin/) lists published Goblin
versions and selects the recommendation. Each version opens its own Azure
assets. Azure displays **Goblin version**, already bound to the source and installer.

For existing published releases, promotion, rollback, and site recovery use
**Recommend Goblin release** with an already published version. Uncheck its
recommendation option to repair site delivery while preserving the current default.
An older published version remains independently installable.

Before the first recommendation exists, the page requires a version selection.
During development, explicitly recommend a tested preview. For a stable
announcement, publish and recommend the stable release; later previews do not
replace it automatically. A recommendation affects new installations.

Site delivery will follow publication when the coordinated publication phase is implemented.
The `gh-pages` branch stores generated site assets, the published catalog, and its
recommendation. GitHub Pages uses the Actions deployment source. Versioned assets
also remain permanently attached to GitHub Releases. The repository-wide GitHub
“latest release” does not determine Goblin's default.

## Repository setup

Source protection is defined in
[`.github/goblin-ruleset.json`](../.github/goblin-ruleset.json). Apply it as an active
repository ruleset for `master` and release branches. The
[live ruleset](https://github.com/jgador/goblin/rules/24076471) is active for both.
The ruleset uses GitHub's
`refs/heads/release/**/*` pattern to cover both immediate and nested release
branch names; Actions uses `release/**`. Verify the live ruleset as well as the
file. Install `checks.yml` before activating protection in a new repository so
the required `goblin-checks` job exists. Its PR trigger covers all target branches;
its push trigger covers `master` and `release/**`.

Candidate preparation needs read access to source checks, releases, and attestations;
it does not require publication approval or Pages configuration. Existing
publication/recommendation workflows retain their approval and hosting settings.
On 2026-09-29, the `goblin-release` reviewer environment (restricted to `master`)
and GitHub Pages Actions hosting were configured. PR automation was already
enabled. Azure credentials and subscription settings are not required. Keep
publication restrictions unchanged during the stabilization/backport phase.

The retained publication and site workflows use these settings; coordinated
candidate preparation does not require them:

- A `goblin-release` environment with at least one required maintainer reviewer.
  Allow that maintainer to approve their own run. Limit deployment branches to
  `master`. Retain the existing `goblinctl-release` approval.
- GitHub Pages with **Source: GitHub Actions**. The installation-site workflow
  builds the distribution branch and deploys the generated artifact explicitly.
- Actions permission to create pull requests. Pin jobs use scoped write
  permissions and explicitly dispatch `checks.yml` on their generated branch;
  `GITHUB_TOKEN`-created PRs do not trigger ordinary PR workflows themselves.
- Keep `goblin-checks` as the required source check. The old standalone Rust,
  advisory installer-status, and deployment-readiness workflows were consolidated.

### Manual Azure installation

Repository automation does not authenticate to Azure, deploy Goblin, or create or
delete Azure resources. There is no live Azure release check or scheduled cloud
cleanup. Release preparation and publication require only the local checks and
GitHub configuration described above.

After publication, choose a release on the installation page and follow the
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
cargo xtask dependencies check --locked
cargo xtask azure                         # writes .artifacts/azure/
just fmt
just clippy -p xtask -- -D warnings
just test -p xtask
npm test
npx playwright test tests/e2e/setup.spec.ts tests/e2e/install.spec.ts
```

`cargo xtask azure` generates review assets bound to the current SHA and a
`0.0.0-preview.1` development label. These are local test outputs. Preparation
creates the actual versioned release assets and requires clean merged source.

To authenticate the pinned installer and exercise its validator locally:

```bash
bash scripts/build-goblinctl-release.sh target
cargo xtask release verify-installer
```

The public archive is downloaded and verified; this command does not deploy Azure.
`cargo xtask release check-installer` validates local compiler input coverage and
contracts without requiring a published binary. `just release` creates a local
review archive. The standalone legacy workflow uses
`cargo xtask release build-installer` from clean merged source.

Candidate artifacts expire after 30 days. A failed preparation can be rerun using
**Re-run all jobs**, creating a fresh candidate. A blank source SHA captures the
release branch tip again; specify the original SHA to prepare that exact source.
Evidence from different attempts cannot be combined. Publication recovery from
an approved retained candidate remains step 5; never substitute new bytes into
an existing published version.

After changing release tooling or workflows, start a new **Prepare Goblin
release** run from the updated `master`. Rerunning an older run keeps its original
workflow and source, including any former Azure requirements.

If publication succeeds and installation-site delivery fails, rerun delivery using
**Recommend Goblin release**, choosing whether to preserve the recommendation.
The published release remains available. If candidate artifacts have expired,
prepare a new candidate/version rather than substituting new bytes into an
existing release.

Automated release checks do not establish live Azure installation coverage. The
app is built from source on the VM during manual installation; upstream OS and
image tags can affect later installs. Opt-in PostgreSQL suites and real-runtime
journeys report their own coverage and skips.
