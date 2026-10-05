# Goblin release workflow plan

Status: stabilization, backports, and coordinated candidate preparation (steps 1–3)
are implemented. Verification, approval, coordinated publication, and recovery
remain follow-up work (steps 4–6). The [release guide](releases.md)
documents the branch and backport procedure and required repository setup.
Repository automation does not authenticate to Azure, perform live installations,
or manage cloud resources.

## Current rollout

1. **Establish stabilization branches and checks — issue #41.** Develop on
   `master`; cut `release/<major>.<minor>` from a checked master commit when a line
   is ready for stabilization. Run `goblin-checks` for PRs targeting either branch
   family and pushes to both. Apply the same active PR and status-check protections
   to both, without bypass actors. Creating a release branch requires no tag,
   published goblinctl, or dependency-pin update. Keep the existing publication
   workflows unused in this phase.
2. **Stabilize through targeted backports.** Fix master first in the normal case.
   Cherry-pick the relevant fix with provenance onto
   `backport/<major>.<minor>/<issue>-<description>` and open a PR targeting that
   release line. Resolve conflicts and verify the fix against the release branch.
   Include required prerequisites deliberately; do not merge all of master into
   the stabilized line. Document any release-only exception. Apply this process
   equally to application code, goblinctl, and bundled assets.
3. **Prepare a coordinated release — implemented.** Freeze a source SHA
   from the selected release line and choose independent Goblin/goblinctl versions.
   Reuse authenticated matching goblinctl, or build a new version from the selected
   source within the same operation. Changes to bundled assets count. Resolve a
   new goblinctl version without a version-bump PR and restart. Both paths produce
   one candidate whose release record owns the exact pairing; preparation no
   longer creates a dependency-pin PR. The workflow currently stops at candidate
   upload, with full candidate verification pending. It runs tooling from master
   against an isolated source export and requires the exact source's passing
   goblin-checks result. Version suggestions respect the selected release line.
4. **Verify, approve, and publish — planned follow-up.** Test and seal the candidate,
   then obtain one approval for coordinated publication. Publish newly built
   goblinctl first, then Goblin using those same verified assets. Update installation
   delivery afterward and change the recommendation only when requested. Support
   release-branch sources consistently in source validation and approval policies.
5. **Recover partial publication — planned follow-up.** Explicitly retry the
   unfinished publication using the retained, approved candidate after verifying
   existing tags and assets. Never overwrite a published version. Changed source
   or artifacts require fresh preparation and approval. Report site delivery and
   recommendation separately from release publication.
6. **Service each line with the same operation — planned follow-up.** After a
   backport, prepare the next candidate from that release branch. A goblinctl build
   must reflect that branch's shipping inputs even if master has moved ahead.
   Verify both reuse and new builds before enabling publication. Retire the
   standalone installer publication entry point and pin-PR handoff after the
   coordinated path is verified, preserving authentication of older releases.
   Retain source checks, installation-site delivery, and recommendation/recovery.

Steps 1–2 implement [issue #41](https://github.com/jgador/goblin/issues/41). Verify
the source-check triggers and the live protections for both branch families. No
release is published during steps 1–3. Steps 4–6 must be implemented and
verified before publication from release branches is enabled; exercise installer
reuse, changed bundles, backports, version conflicts, and partial-publication
recovery.

## Existing publication implementation

The remaining sections record the master-only implementation introduced on
2026-09-29. Its separate goblinctl publication and dependency-pin handoff describe
the original design. Step 3 has replaced the preparation workflow; the standalone
installer workflow and legacy published-record verification remain until the
later publication and cleanup steps. They are not prerequisites
for stabilization or backports and are not the coordinated release design.

## Outcome

The maintainer merges source changes, runs **Prepare Goblin release**, and reviews
a summary such as:

```text
Goblin 0.1.0-preview.2
Installer: goblinctl 0.1.3 — reused
Deployment checks: passed
Azure installation: manual
Ready to publish
```

Publication happens only after the maintainer acts on that prepared result.
Goblin and goblinctl keep independent versions. The release tooling remembers and
verifies their exact pairing.

When a new installer is needed, preparation suggests its version. The maintainer
explicitly publishes goblinctl, merges the resulting dependency update, and
resumes Goblin preparation. This was the approach implemented for the first
version of this workflow.

## 1. Maintainer experience

### Ordinary development

- Continue developing on `master` through normal PRs and required source checks.
- Installer changes can merge before a corresponding installer is published.
- Installation links select published Goblin releases. They do not install the
  moving development branch.
- The current rollout supersedes the original maintenance-only branch policy:
  cut a release branch for stabilization, then retain it for servicing/backports.

### Prepare Goblin release

Use a manually dispatched GitHub Actions workflow with these inputs:

| Input | Default | Purpose |
| --- | --- | --- |
| Release channel | Preview | Choose preview or stable. |
| Goblin version | Automatic | Override the suggested version when needed. |
| Source revision | Current `master` commit | An optional advanced override must resolve to a merged commit. |
| Make recommended after publication | No | Explicitly choose whether this release should become the installation default. |

Resolve the source to a full commit SHA immediately and display it in the detailed
report. All subsequent steps use that captured revision.

Version calculation considers only `goblin-v*` releases. Start with
`0.1.0-preview.1`, then increment the preview number for the current release base.
The stable version for `0.1.0-preview.N` is `0.1.0`. After a stable release, the
automatic suggestion starts the next patch preview; an override selects a minor
or major release. File changes never determine semantic version significance.
Recheck version availability before publication to handle overlapping candidates.

Goblin's release version belongs to the prepared release record and eventual tag.
It does not require bumping the Rust workspace or every npm/.NET package.

### Resolve the installer

1. Read the exact selected version from `dependencies.toml` and the authenticated
   metadata in `dependencies.lock.json`.
2. Compare the selected source's shipping inputs with that published installer.
3. If they match, verify and reuse the published archive.
4. If they differ, check the version declared by the source's Cargo workspace.
   If that version is already published and matches the inputs, prepare its pin
   update. This covers resuming after a deliberate installer publication.
5. Otherwise report **New goblinctl release required**, list the changed inputs,
   and suggest an unused installer version. Honor an already prepared version
   bump; otherwise suggest the next patch with a maintainer override.

Use these two concrete candidates rather than searching release history for a
semantically compatible installer. Retain conservative exact input matching,
including added/deleted files and required capabilities. The inventory continues
to include embedded assets and shared build inputs outside `tools/`.

The installer path is:

1. Review and merge the suggested Cargo version/lockfile bump if one is needed.
2. Run **Publish goblinctl** for the selected merged source. Keep its native
   packaging, packaged executable tests, checksums, and provenance verification.
3. Have the workflow open a small PR updating the Goblin dependency selection and
   lock. The same pin operation is available from Prepare if publication succeeded
   but PR creation failed.
4. Merge that PR, then run Prepare again.

Pin changes are reviewed through normal source checks. They never change image
versions as a side effect. Use one existing pin PR per installer version. Explicitly
dispatch the source checks for automation-created commits, because pushes/PRs
created with `GITHUB_TOKEN` do not normally trigger another workflow.

Preparation that needs an installer or pin update stops with the required next
step. It does not claim readiness.

### Review and publish

Once preparation passes, show the agreed summary and queue a job named **Publish
Goblin** behind a protected `goblin-release` environment in the same workflow run.

GitHub's button for this is **Review deployments**, followed by approval of the
environment. Explain in the run summary that this approval publishes the Goblin
release to GitHub. Azure installation remains manual. Configure a maintainer as
reviewer and allow the initiating maintainer to approve their own release. Verify
that the repository's GitHub plan supports the environment configuration during setup.

This keeps the approval attached to the exact run and its artifacts. The
maintainer does not copy commit hashes or artifact IDs into a second workflow.
An unapproved candidate remains unpublished. Revalidate artifact identity and
version availability after approval, before writing the release.

Keep **Publish goblinctl** as a separate explicit action. A later **Recommend
Goblin release** action can promote or restore an already published release
without rebuilding or republishing it.

## 2. One dependency selection and one prepared release record

Keep these responsibilities distinct:

| Location | Responsibility |
| --- | --- |
| `dependencies.toml` | Exact selected goblinctl version and the existing service image declarations. |
| `dependencies.lock.json` | Generated installer archive identity, source/provenance metadata and input inventory, plus existing image locks. |
| Cargo manifests/lockfile | Version and dependencies of goblinctl being developed. |
| Prepared `release.json` | Goblin version, frozen source SHA, installer identity, generated asset hashes, and verification evidence. |
| GitHub Release `goblin-v<version>` | Permanent published copy of the prepared assets and record. |

Fold `deploy/goblinctl-release.json` into the dependency lock. Read installer
metadata from that lock when generating Azure assets. A local consistency check
does not substitute for authenticating the published archive during preparation.
Continue accepting existing published goblinctl manifests, including 0.1.3, when
they pass the required checks.

Generate the ARM templates and release-specific Azure UI definition into an
artifact directory. Keep Bicep and the UI source in Git; remove generated ARM
copies from the source tree. Generate once for the frozen source, test those
bytes, and publish those same bytes.

The release record contains:

- Goblin version, channel, and exact source SHA.
- Installer version, source SHA, archive checksum and authenticated manifest
  identity. Its source SHA can be older than Goblin's.
- Checksums of the Azure templates and UI definition.
- Preparation run ID/attempt and local deployment-check results.

Keep the record and checksums permanently on the GitHub Release. Actions artifacts
are temporary candidate storage and can expire. Expired or missing candidate
artifacts require preparation again before publication.

## 3. What the checks mean

### Deployment checks: passed

- The chosen source is merged and passes the required source checks.
- The published installer archive and manifest pass checksum and GitHub provenance
  verification.
- Shipping inputs, capabilities, dependency selection, and generated resources
  agree.
- The actual published executable validates the install request and passes the
  relevant deployment/authentication contract tests.
- The generated Azure template and UI bind the displayed Goblin version to the
  correct source and installer. A different source cannot be substituted through
  the normal release form.

Reuse existing checks and their implementation. Consolidate duplicate runs within
the source CI workflow while retaining checks of the actual packaged/published
executable at the release boundary.

### Azure installation: manual

Generate and validate the Azure templates and installer assets locally. The
release workflow does not require Azure credentials or an installation report,
and does not provision or clean up Azure resources. Remove the live installation
job, its script, and the scheduled cleanup workflow.

After publication, the maintainer deploys the selected release manually through
the [Azure deployment guide](../deploy/azure/README.md). Publication approval
does not initiate that deployment. Summaries and release notes describe Azure
installation as manual and do not claim it passed an automated live check.

Existing opt-in PostgreSQL suites and authenticated runtime checks report their
own coverage and skips. The frozen source, installer, and templates identify the
release; upstream OS and image tags can still affect a later manual installation.

Apply the existing migration rule in `AGENTS.md`: the first real deployment is the
boundary for making SQL migrations immutable. Record that boundary when the first
real Azure deployment occurs; the public announcement is a separate event.

## 4. Publication and installation

### Publish the prepared result

After approval:

1. Revalidate the prepared record, required successful check results, artifact
   hashes, and version availability. Do not resolve `master` again or rebuild.
2. Create `goblin-v<version>` at the verified Goblin commit.
3. Publish the release with its tested ARM/UI assets, release record, checksums,
   notes, and a version-specific installation link. Mark preview releases as
   GitHub prereleases.
4. Verify the publicly served assets and installation links.
5. If requested, make that published Goblin release the recommendation.

Published versions are never overwritten or retagged. A retry may finish an
incomplete publication only after proving its existing tag/assets identify the
same prepared candidate. Publication, install-site delivery, and recommendation
are separate reported outcomes: a recommendation failure must not suggest that a
successfully published version disappeared or needs a new version number.

### Default installation experience

Provide a small static installation page, hosted on GitHub Pages, with:

- **Goblin version**, defaulting to the explicitly recommended version.
- Published Goblin releases as the available choices, with previews labeled.
- A **Deploy to Azure** button for the selected release.

Each choice opens that release's own tested Azure template and UI. Azure displays
the selected Goblin version with its source already resolved. Changing to another
release starts from the installation page so its templates and installer travel
together. This avoids mixing one release's template with another release's source.
The normal form has no commit SHA entry or goblinctl version choice.

Keep versioned assets permanently on GitHub Releases and serve verified copies
for Azure through the static site. Store its generated catalog and recommendation
on the Pages distribution branch. That branch stores installation metadata and
assets; `master` remains the development branch. Publish the versioned site files
before exposing a new catalog entry or recommendation. Preserve older version
directories and serialize catalog/recommendation updates.

The recommendation is Goblin-specific. Do not derive it from the repository's
generic latest GitHub Release, which also includes goblinctl releases. Before an
explicit recommendation exists, show the available releases and require a version
selection. Never fall back to `master`.

During development, deliberately recommend a tested preview. For the public stable
announcement, prepare, verify, and publish the stable version, then recommend it.
Later previews remain available without replacing the stable default. Changing
the recommendation affects new installations; upgrades require separate work.

## 5. Simplification and removal

| Current area | Planned change |
| --- | --- |
| Standalone `goblin-deployment-check.yml` | Fold its verification into Prepare; remove the separate manual readiness workflow. |
| `goblinctl-dependency.yml` and `rust-ci.yml` | Consolidate source checks; retain one required `goblin-checks` gate and remove duplicate Rust test execution within CI. |
| Advisory installer-status job | Move the release decision and changed-input report into Prepare. |
| `deploy/goblinctl-release.json` | Remove after moving its necessary metadata into the lock. |
| Committed `azuredeploy.json` and `azuredeploy.portal.json` | Remove; produce release artifacts from Bicep. |
| Separate manual pin/template regeneration sequence | Replace with one pin operation and its reviewed PR. |
| Existing release command variants and aliases | Consolidate around the shared preparation/build/verification implementation; retire obsolete entry points and documentation. |
| Current README deployment link and SHA-entry instructions | Replace with the recommended-version installation entry point. |
| Historical migration instructions in release docs | Replace with the implemented routine and a short setup guide. |

Keep the shipping-input inventory, archive authentication, compiler coverage for
embedded assets, actual install-request validator, native packaging, image
dependency management, and deployment/security regression coverage. These checks
provide evidence needed for the agreed summary.

Limit this cleanup to release/deployment tooling and its consumers. Keep the Work
lifecycle and product boundaries described in the architecture plan intact.

## 6. Implementation sequence

1. **Consolidate dependency data and asset generation.** Migrate the installer pin
   into the lock; generate ARM/UI artifacts outside the source tree; adapt existing
   tests and local deployment commands. Confirm the existing published installer
   remains usable wherever its inputs still match.
2. **Build preparation and the installer handoff.** Add Goblin version calculation,
   exact source capture, reuse/new-installer decisions, pin PR creation, the
   prepared record, and concise summaries. Make automation-created PR checks run
   explicitly. Keep installer publication deliberate.
3. **Keep Azure installation manual.** Preserve local template and installer
   checks while removing live Azure authentication, deployment, cleanup, and
   installation-evidence requirements from release automation.
4. **Add gated publication.** Configure the release environment, publish the exact
   tested artifacts, and handle duplicate-version/partial-publication recovery.
5. **Add installation defaults and promotion.** Build the static selector,
   release-specific Azure form, versioned asset delivery, and explicit
   recommendation/rollback action. Check generated links and assets locally;
   Azure Portal installation is verified manually.
6. **Remove replaced tooling and finish the migration.** Consolidate CI, remove
   stale commands/files, replace operator docs, and update live GitHub rules only
   after their replacement checks exist. Verify actual repository permissions,
   PR automation settings, reviewers, Pages settings, and required checks.

Use the repository's pinned Rust toolchain and Python-free operational tooling.
Share the existing Rust release logic between local helpers and Actions; keep
GitHub workflow orchestration thin.

## 7. Acceptance criteria

- An application-only change can prepare a preview using the existing installer
  and produce the agreed summary after the local release checks pass.
- An installer input addition/change/deletion produces an accurate required
  release report; publishing and pinning that installer clears it without a loop.
- A corrupt archive, incorrect provenance, missing capability, pin mismatch,
  failed/omitted deployment check, or modified prepared artifact cannot reach
  publication.
- Release preparation succeeds without Azure configuration or installation
  evidence. No repository workflow authenticates to Azure or manages its resources.
- Subsequent movement of `master` does not change a prepared or published release.
- Preview/stable version calculation excludes goblinctl tags and rejects duplicate
  published versions. Existing matching publications can be recovered safely.
- The public installation page defaults to the recommended Goblin version and
  opens the matching assets. An older release remains independently installable.
- Publishing a preview or goblinctl does not change the recommendation implicitly.
- A failed recommendation update is recoverable without republishing Goblin.
- Retired workflows and commands have no remaining active consumers. Required
  source checks still run for ordinary and automation-created PRs.

Verification uses focused release-decision and failure-path tests, existing
deployment/authentication/browser tests, the required Rust checks, and `make test`.
Record manual Azure verification and PostgreSQL/runtime coverage accurately. The
final release exercise must demonstrate both the reused-installer path and the
deliberate new-installer path.

## Implementation verification — 2026-09-29

- `make test` passed: 35 Rust tests, 246 .NET tests, six release orchestration
  checks, and 97 HTTP/deployment checks. The 39 .NET PostgreSQL tests and one Work
  HTTP persistence test were skipped because their live database was unconfigured.
- The final focused `make test-rust CARGO_ARGS="-p xtask"` run passed all 21 tests; Clippy and script
  type checks passed.
- All six setup/installation-selector Playwright checks passed.
- All 20 secret-scanner regression tests passed; the repository scan reported no
  findings in the index or working tree.
- The authenticated published goblinctl 0.1.3 archive matched the installer inputs
  using the consolidated dependency lock. Six focused template/login checks using
  that published executable passed.
- Actionlint 1.7.12 validated the workflow files. The new GitHub workflow runs have
  not been dispatched; they become available after merge to `master`.
- Configured the `goblin-release` environment with maintainer review, self-review
  permitted, and a `master` deployment branch policy. Configured GitHub Pages to
  use Actions. The preflight check passed against these live GitHub settings.
- The existing GitHub source-check ruleset is disabled. Its desired replacement is
  `.github/goblin-ruleset.json`; activate it after merging the new checks workflow.
- No live Azure installation or Azure Portal deployment was performed, as
  explicitly requested. Live Azure automation was subsequently removed in favor
  of manual installation.
  No release was published and no installation-site deployment was run.
