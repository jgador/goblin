# Goblin releases

Goblin and goblinctl have independent versions. Develop on `master`; install a
published Goblin release. Source checks allow unreleased installer changes to
merge. Release preparation decides whether the selected installer can be reused.

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

The workflows require configured approval and hosting. On 2026-09-29, the
`goblin-release` reviewer environment (restricted to `master`) and GitHub Pages
Actions hosting were configured. PR automation was already enabled. Azure
credentials and subscription settings are not required. The existing source-check
ruleset is disabled; activate
the desired configuration in [`.github/goblin-ruleset.json`](../.github/goblin-ruleset.json)
after the replacement workflow is merged.

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
