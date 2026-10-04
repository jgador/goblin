# Goblin releases

Develop on `master`; stabilize and service each release line on
`release/<major>.<minor>`. Goblin and goblinctl keep independent versions.

The current phase establishes stabilization branches and backports, as described
in [issue #41](https://github.com/jgador/goblin/issues/41). It introduces no tagging
or publication requirement. Existing published releases remain available for
installation. Coordinated Goblin/goblinctl publication is a follow-up in the
[release workflow plan](release-workflow-plan.md).

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
update to make the backport possible. In the planned coordinated workflow,
preparation will decide whether the release branch's source can reuse goblinctl
or needs a new build. Neither cutting a branch nor merging a backport publishes a
release.

## Publication during this phase

Leave **Prepare Goblin release** and **Publish goblinctl** unused for the
stabilization/backport phase. They remain master-only, and do not yet implement
coordinated publication from a release branch. The instructions below describe
that existing implementation for reference and recovery of existing releases.
Before publishing from `release/*`, implement the coordinated workflow described
in the [plan](release-workflow-plan.md).

## Prepare and publish Goblin

1. Merge your source changes after **Goblin checks** passes.
2. Open **Actions → Prepare Goblin release → Run workflow** on `master`.
   Leave **Release channel** at **preview** and **Goblin version** blank to suggest
   the next version, starting at `0.1.0-preview.1`. An optional full source SHA
   selects a particular merged commit. **Make recommended** defaults to false.
3. If preparation requests an installer release, follow the installer steps below.
   If it creates a dependency PR, merge that PR and run Prepare again.
4. Preparation verifies the published installer, runs source/deployment/browser
   checks locally on the runner, and generates the release assets. Azure
   installation is performed manually after publication.
5. Review the summary:

   ```text
   Goblin 0.1.0-preview.2
   Installer: goblinctl 0.1.3 — reused
   Deployment checks: passed
   Azure installation: manual
   Ready to publish
   ```

6. Select **Review deployments**, approve `goblin-release`, then choose
   **Approve and deploy**. The job named **Publish Goblin** publishes the prepared
   release to GitHub. Azure deployment is a separate manual action.

The workflow captures the source once. A later merge to `master` cannot change
that candidate. Publication creates `goblin-v<version>` at the captured commit and
uploads the same tested templates, portal UI, verification record and checksums.
Preview versions are GitHub prereleases. Source branches and published versions
are never automatically upgraded on installed machines.

The automatic version suggestion increments the preview number. A stable release
removes the preview suffix; after a stable release, the next suggestion begins the
next patch preview. Enter an explicit version for a minor/major release. Goblin
version suggestions ignore goblinctl releases.

## When a new installer is needed

Preparation compares all shipping inputs against the authenticated published
installer selected by `dependencies.toml`. The inventory includes Rust source,
shared build inputs, and embedded setup/PostgreSQL assets outside `tools/`.
It also includes `LICENSE`, `NOTICE`, and `THIRD_PARTY_NOTICES.md`; changes to
these shipping documents require a new installer release. The archive contains
the executable followed by those three documents, with deterministic metadata.
Installation preserves the documents in `/opt/goblin/share/licenses/goblinctl/`.
Additions and deletions count. Source differences are conservative release
requirements; the tooling does not infer semantic compatibility or bump size.

1. Review the suggested goblinctl version. Update the Cargo workspace version and
   run `cargo check --workspace` to update its lockfile. Merge those two files in
   an ordinary PR. An existing unused version bump can be used as-is.
2. Run **Actions → Publish goblinctl** on `master`. Its optional SHA defaults to
   that workflow run's `master` commit. Review and approve the existing
   `goblinctl-release` environment after source and packaged executable tests pass.
3. Publication creates `goblinctl-vX.Y.Z` and authenticates the archive and manifest
   with GitHub attestations. A following job opens a dependency PR updating only
   `dependencies.toml` and `dependencies.lock.json`.
4. Merge the dependency PR and run **Prepare Goblin release** again.

If the installer was published but the dependency PR failed, run Prepare again.
It checks the Cargo version's published release as well as the existing pin.
Local recovery uses the same authenticated pin operation:

```bash
cargo xtask release pin-installer --version X.Y.Z
```

Review and commit the two dependency files together. This operation does not
refresh service images. Pin updates and generated Azure outputs do not change the
installer fingerprint, so pinning does not cause another installer release.

## Installation defaults and recommendation

The [installation page](https://jgador.github.io/goblin/) lists published Goblin
versions and selects the recommendation. Each version opens its own Azure
assets. Azure displays **Goblin version**, already bound to the source and installer.

Publication updates the installation site. It changes the default only when
**Make recommended** was selected. For later promotion or rollback, run
**Recommend Goblin release** with an already published version. Uncheck its
recommendation option to repair site delivery while preserving the current default.
An older published version remains independently installable.

Before the first recommendation exists, the page requires a version selection.
During development, explicitly recommend a tested preview. For a stable
announcement, publish and recommend the stable release; later previews do not
replace it automatically. A recommendation affects new installations.

The first site deployment accompanies the first published Goblin release.
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

The existing publication workflows also require configured approval and hosting.
On 2026-09-29, the `goblin-release` reviewer environment (restricted to `master`)
and GitHub Pages Actions hosting were configured. PR automation was already
enabled. Azure credentials and subscription settings are not required. Keep
publication restrictions unchanged during the stabilization/backport phase.

For a new repository, configure:

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
review archive; publication uses `cargo xtask release build-installer` from clean
merged source.

Candidate artifacts expire after 30 days. A failed preparation can be rerun using
**Re-run all jobs**; evidence from different attempts cannot be combined. A
publication-only retry can reuse the original approved, sealed candidate while
its artifacts exist. It may fill missing draft assets only when all existing
assets and the tag already match. A published version is never overwritten.

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
