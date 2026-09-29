# Goblin release workflow plan

Status: implemented in the working tree, 2026-09-29. The [release guide](releases.md)
documents operation and required repository setup. Live Azure verification was
not run, at the maintainer's explicit request; the checks below describe the
implemented workflow, not a claim of live deployment coverage.

## Outcome

The maintainer merges source changes, runs **Prepare Goblin release**, and reviews
a summary such as:

```text
Goblin 0.1.0-preview.2
Installer: goblinctl 0.1.3 — reused
Deployment checks: passed
Azure installation: passed
Ready to publish
```

Publication happens only after the maintainer acts on that prepared result.
Goblin and goblinctl keep independent versions. The release tooling remembers and
verifies their exact pairing.

When a new installer is needed, preparation suggests its version. The maintainer
explicitly publishes goblinctl, merges the resulting dependency update, and
resumes Goblin preparation. This is the selected approach for the first version
of this workflow.

## 1. Maintainer experience

### Ordinary development

- Continue developing on `master` through normal PRs and required source checks.
- Installer changes can merge before a corresponding installer is published.
- Installation links select published Goblin releases. They do not install the
  moving development branch.
- Introduce maintenance branches only when an actual backport is needed.

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
step. It does not claim readiness or start the Azure installation check yet.

### Review and publish

Once preparation passes, show the agreed summary and queue a job named **Publish
Goblin** behind a protected `goblin-release` environment in the same workflow run.

GitHub's button for this is **Review deployments**, followed by approval of the
environment. Explain in the run summary that this approval publishes the Goblin
release; the Azure test has already finished. Configure a maintainer as reviewer
and allow the initiating maintainer to approve their own release. Verify that the
repository's GitHub plan supports the environment configuration during setup.

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
- Preparation run ID/attempt, check results, Azure test configuration, and links
  to diagnostic evidence.

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

### Azure installation: passed

Add a real Azure smoke test for every prepared Goblin candidate, including when
the installer is reused:

1. Authenticate using GitHub OIDC in a dedicated test subscription or suitably
   isolated test scope. Use one documented region and VM configuration initially.
2. Create resources owned by this run, deploy the generated template with a
   disposable password, and use the pinned published installer through the normal
   bootstrap path.
3. Wait for the installation to complete. Azure resource provisioning success
   alone is insufficient because installation continues in the background.
4. Check PostgreSQL initialization and migrations, application readiness, the
   progress-page handoff, successful login, and a small persisted application
   operation. Verify persistence after an application restart.
5. Collect sanitized diagnostics and delete the run's test resources. Bound the
   run duration; include cleanup on failure and an expiry sweep for abandoned
   resources after cancellation or runner loss.

The test owns only its dedicated resources. It does not modify existing user
installations. Missing Azure configuration, an installation failure, or incomplete
required verification prevents **Ready to publish**. Record cleanup failures
explicitly and resolve them before approving the candidate.

The pass describes this tested installation configuration. Existing opt-in
PostgreSQL suites and authenticated runtime checks must report their own coverage
and skips; an installation pass does not imply every external runtime journey was
tested. Retain source-based application builds for this iteration, so the frozen
source/installer/templates identify the release while upstream OS and image tags
can still affect a later installation.

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
3. **Add the real Azure check.** Configure OIDC, test scope/region/VM, timeouts,
   diagnostics, cleanup and expiry handling. Exercise one fresh installation and
   one failing installation to verify readiness and cleanup behavior.
4. **Add gated publication.** Configure the release environment, publish the exact
   tested artifacts, and handle duplicate-version/partial-publication recovery.
5. **Add installation defaults and promotion.** Build the static selector,
   release-specific Azure form, versioned asset delivery, and explicit
   recommendation/rollback action. Verify the actual Azure Portal entry point as
   well as the automated deployment path.
6. **Remove replaced tooling and finish the migration.** Consolidate CI, remove
   stale commands/files, replace operator docs, and update live GitHub rules only
   after their replacement checks exist. Verify actual repository permissions,
   PR automation settings, reviewers, Pages settings, and required checks.

Use the repository's pinned Rust toolchain and Python-free operational tooling.
Share the existing Rust release logic between local helpers and Actions; keep
GitHub workflow orchestration thin.

## 7. Acceptance criteria

- An application-only change can prepare a preview using the existing installer
  and produce the agreed summary after the real Azure test passes.
- An installer input addition/change/deletion produces an accurate required
  release report; publishing and pinning that installer clears it without a loop.
- A corrupt archive, incorrect provenance, missing capability, pin mismatch,
  failed/omitted Azure check, or modified prepared artifact cannot reach publication.
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
deployment/authentication/browser tests, the required Rust checks, and `npm test`.
Record live Azure and PostgreSQL/runtime coverage accurately. The final release
exercise must demonstrate both the reused-installer path and the deliberate
new-installer path.

## Implementation verification — 2026-09-29

- `npm test` passed: 35 Rust tests, 246 .NET tests, six release orchestration
  checks, and 97 HTTP/deployment checks. The 39 .NET PostgreSQL tests and one Work
  HTTP persistence test were skipped because their live database was unconfigured.
- The final focused `just test -p xtask` run passed all 21 tests; Clippy and script
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
  explicitly requested. Azure test settings remain unconfigured and disabled.
  No release was published and no installation-site deployment was run.
