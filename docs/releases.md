# Goblin releases

Develop on `master`; stabilize and service each release line on
`release/<major>.<minor>`. Goblin and goblinctl keep independent versions. The
main product component is **platform** ([component names](kubernetes-names.md)).
The **Platform Release** workflow prepares, verifies, and publishes one Goblin and
goblinctl pair after a single approval. See [GitHub Actions](github-actions.md)
for workflow and job names and installation asset commands.

## Development and stabilization

| Branch | Purpose |
| --- | --- |
| `master` | Ongoing development and the normal first destination for fixes. |
| `release/0.1` | Stabilization and later servicing for the `0.1.x` line. |
| `backport/0.1/<issue>-<description>` | A temporary branch for a targeted fix, merged by PR into `release/0.1`. |

Use PRs targeting `master` for ordinary work. Cut a release branch when its source
is ready for stabilization. Keep the release branch limited to fixes for that line,
and do not merge all of `master` into it.

To cut a line, start with a clean working tree, fetch, and confirm that
`test_results` passed for the exact `origin/master` commit you want. For `0.1`:

```bash
git fetch origin
git switch --create release/0.1 origin/master
git push --set-upstream origin release/0.1
```

After the branch exists, change it only through PRs. Cutting a branch or merging a
backport does not publish anything, create a tag, or require a new goblinctl.

## Backport a fix

Merge the fix into `master` first. Make a separate backport PR for each release
line that needs it. Replace `MASTER_FIX_COMMIT` with the commit that landed on
`master`:

```bash
git fetch origin
git switch --create backport/0.1/123-fix-description origin/release/0.1
git cherry-pick -x MASTER_FIX_COMMIT
```

Cherry-pick the squash commit or the relevant ordinary commits, not a merge commit.
Include required prerequisite fixes explicitly. Resolve conflicts for the release
branch's code, then run `git cherry-pick --continue` (or `--abort` to stop). Keep
the `-x` reference to the original commit in the final message.

Run the checks relevant to the fix, then push:

```bash
git push --set-upstream origin backport/0.1/123-fix-description
```

Open a PR with **base `release/0.1`**. Link the original issue, PR, and commit;
explain why the line needs the fix; note conflict resolutions and verification.
Merge after `test_results` passes, and confirm the checks on the resulting
release-branch commit before using it as a candidate.

A fix that applies only to an older line may start on that line; document why it
does not apply to `master`, or track the forward fix. Backport application code,
goblinctl code, and bundled assets the same way. Do not substitute a newer `master`
installer or merge an unrelated dependency-pin update to make a backport work.

## Prepare a coordinated candidate

1. Merge the source changes or backports into the release line. Wait for
   `test_results` to pass for the exact commit.
2. Open **Actions → Platform Release → Run workflow** and run it on `master`.
   Preparation refuses other refs. Set **Stabilization branch** to the line, for example
   `release/0.1`.
3. Choose **preview** or **stable**. Leave the Goblin version blank to use the
   next version in that line. A version must belong to the line, match the
   channel, and be unused. The optional source SHA must be a full lowercase commit
   on that branch; blank uses the branch tip at preparation time.
4. Normally leave **Optional goblinctl version** blank. Preparation reuses the first
   published installer whose shipping inputs and capabilities match the source. It
   checks the checked-in pin first, then the source's Cargo workspace version, then
   other published versions, newest first. Published manifests without the current
   `schemaVersion` are skipped. Before reuse it authenticates the archive and
   manifest and checks that the tag points to the recorded source commit.
5. If no installer matches, preparation builds goblinctl from the frozen source.
   It uses the Cargo workspace version if unused, otherwise the next unused patch
   version. An explicit goblinctl version reuses a matching published installer or
   builds an unused version. Releases, drafts, and tags all reserve versions.
6. Preparation uploads **goblin-candidate-<attempt>** and shows a summary. The
   candidate contains the installer archive, its manifest and checksums, the
   generated Azure assets, and `release.json` with checksums for every asset.

Shipping inputs are listed in
[`tools/goblinctl/release-inputs.json`](../tools/goblinctl/release-inputs.json).
The source's required installer capabilities are in
[`deploy/goblinctl-requirements.json`](../deploy/goblinctl-requirements.json); the
installer declares its own in
[`tools/goblinctl/capabilities.json`](../tools/goblinctl/capabilities.json).
Preparation does not change Cargo files, the development pin, or service image
locks, and creates no PR.

Preparation stops with a next action when the source is not on the branch, its
`test_results` has not passed, its dependency files are inconsistent, it lacks a
required capability (backport the implementation and capability, then prepare
again), a requested version is reserved or does not match, or the selected
installer fails authentication. For a failed installer, start a new preparation
with an unused **Optional goblinctl version** (`--installer-version`). Keep the
existing release's tag and assets unchanged.

## Verify and publish the candidate

The `verify` job checks out the frozen source separately. It checks every
candidate hash, the installer inputs and capabilities, the executable's version,
metadata and install-request validator, and reproduces the Azure assets
byte-for-byte. It then runs `make install`, `make check`, and the setup and install
browser journeys using the packaged goblinctl, and fails if tests change tracked
files. PostgreSQL tests remain opt-in and report skips.

The `seal` job marks the candidate `deploymentChecks: passed`, attests it and any
newly built installer, and uploads **goblin-ready-<attempt>**. Review the sealed
candidate and the **Ready to publish** summary.

Use **Review deployments** to approve the `publish` job once for the pair; the
`goblin-release` environment controls this approval. Publication checks again the
candidate digest, run and attempt, workflow revision and attestation, the source's
`test_results` check, and branch membership, then authenticates the installer. It
checks both version slots before writing either. It publishes a newly built
goblinctl first, then Goblin; a reused installer must already be fully published.
Publication never marks a release as GitHub's latest release.

The `installation` job then updates the website installation assets.
**Make this release the installation default** is unchecked by default; only an
explicit selection changes the recommendation. The `report` job shows release
publication and website installation results separately.

Signing and authentication accept only
`jgador/goblin/.github/workflows/platform-release.yml` on `refs/heads/master`
from GitHub-hosted runners. Candidate and website checks also require the
release's recorded workflow revision. A published installer signed by another
workflow, including a renamed one, cannot be reused; publish a new installer
version through **Platform Release** and keep existing tags and assets. After
changing the signing workflow, start a new run from updated `master`, because
reruns keep the original workflow revision.

To update the development installer pin after a publication (the release must
match the checkout's installer inputs and capabilities):

```bash
cargo xtask release pin-installer --version X.Y.Z
```

## Installation defaults and recommendation

The [website installation page](https://goblinboard.com/install/) lists published
Goblin versions and the recommended one. Each version opens its own Azure assets,
already bound to its source and installer. Only current-format releases signed by
`platform-release.yml` are listed. Older formats and releases signed by other
workflows stay on GitHub Releases but are excluded. An invalid signature fails the
run.

To recommend an existing release, roll back, or repair installation hosting, run
**Recommend Release** from `master` with a published version. Its recommendation
option is checked by default; uncheck it to redeploy the assets and keep the
current default.

If no catalog is deployed yet, the snapshot recommends the newest stable release,
otherwise the newest preview. Otherwise the deployed recommendation is kept,
including no recommendation, until a run explicitly recommends a version. A
deployed empty catalog has no recommendation, so recommend the first release
explicitly. A recommendation affects new installations only. GitHub's "latest release" never sets Goblin's
default.

The installation snapshot passes as an Actions artifact into **Website Release**,
which deploys through the `website-production` approval. Deployment fails if
`master` advanced or the deployed catalog changed while approval was pending, and
the snapshot fails if a previously hosted release changed or disappeared.
Versioned assets stay attached to GitHub Releases.

## Repository setup

Apply [`.github/goblin-ruleset.json`](../.github/goblin-ruleset.json) as an active
ruleset for `master` and `refs/heads/release/**/*`. It requires PRs, an
up-to-date passing `test_results` check, and resolved conversations, and blocks
deletion and force pushes. It has no bypass actors and requires no approving
review, so a sole maintainer can merge a passing PR. Editing the JSON does not
change GitHub; check that the
[live ruleset](https://github.com/jgador/goblin/rules/24076471) matches it. In a
new repository, add `ci.yml` before activating protection so `test_results` exists.

- Configure the **goblin-release** environment with at least one required
  reviewer, allow that maintainer to approve their own run, and allow the
  **master** deployment branch. Preparation checks the reviewer and branch
  settings before it starts.
- Configure the **website-production** environment and GHCR package as described
  in [website production](../website/production/README.md). This affects only the
  `installation` job, not release publication.

No GitHub Pages configuration, pin-PR permissions, or Azure credentials are needed.

### Manual Azure installation

Repository automation does not sign in to Azure, deploy Goblin, or create or
delete Azure resources. After publication, choose a release on the website
installation page and follow the [manual Azure deployment guide](../deploy/azure/README.md).
The release record contains the source and installer identities, asset checksums,
and the deployment-check result; it does not claim a live Azure installation.

The first production deployment, not an announcement, makes SQL migrations
immutable (see `AGENTS.md`). Record that deployment separately.

## Local verification and recovery

```bash
make check
make browser-test ARGS="tests/e2e/setup.spec.ts tests/e2e/install.spec.ts"
```

`make check` includes `make deploy-generate`, which writes local review assets to
`.artifacts/azure/` with the current SHA and a `0.0.0-preview.1` label. Only
preparation creates real release assets.

To authenticate the pinned published installer and run its validator locally:

```bash
make release-build
cargo xtask release verify-installer
```

This downloads the public archive and does not deploy to Azure. After
`make release-build`, `make release-check-installer` checks compiler-input
coverage and contracts without a published binary. `make release-package-local`
creates a local review archive.

## Recover a failed operation

Candidate artifacts expire after 30 days. For a failed verification, seal,
publication, or installation job, use **Re-run failed jobs** on the original run.
Reruns keep the original artifacts, preparation attempt, and workflow revision. A
rerun publication job may need approval again.

Publication first checks existing tags, drafts, and assets against the candidate.
It uploads only missing draft assets, verifies every uploaded byte before
publishing the draft, and skips completed components. If goblinctl was published
but Goblin failed, the rerun verifies goblinctl and finishes Goblin. Conflicting
tags, drafts from another candidate, changed assets, or incomplete published
releases stop with an error; the workflow never replaces them.

Use **Re-run all jobs** only to prepare a fresh candidate; a blank source SHA
takes the branch tip again. After changing release tooling, start a new run from
updated `master`. Missing or expired artifacts need fresh preparation and
approval, with unused versions where a tag or draft reserved the old ones. Do not
change the bytes of a completed publication.

If publication succeeded and only the `installation` job failed, rerun that job or
use **Recommend Release** for the published version with recommendation unchecked.
The releases stay published.
