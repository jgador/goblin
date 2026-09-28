# goblinctl releases

Goblin and goblinctl have independent release schedules. Merge application and
installer changes after the normal source checks pass. Maintainers choose when
to publish an installer and which version to assign. CI does not create branches,
PRs, version bumps, pin commits, comments, or follow-up workflow dispatches.

## See pending installer changes

```bash
cargo xtask release-status
# Or: npm run release:status
```

The same report appears in the **Installer changes pending** job summary on PRs
and pushes to `master`. It compares the current input inventory with the committed
`deploy/goblinctl-release.json` baseline, including additions and deletions. Changes
remain visible across later commits until the release pin is updated. Required
capabilities absent from the pinned installer are listed separately.

Pending changes return success and do not block merging. Missing or corrupt
metadata returns an error: it cannot be reported as up to date. The report is
offline, read-only, and needs no native release build, compiler dependency file,
published-asset download, or GitHub credentials. Cargo may build xtask and fetch its
locked dependencies on first use.

`tools/goblinctl/release-inputs.json` is the existing shipping-input inventory.
It covers Rust source, build inputs, embedded setup UI/scripts and PostgreSQL
assets. Application-only files, the release pin, and generated ARM templates are
outside that boundary. Changes inside a shipping directory can include comments
or tests; the report identifies source differences, not semantic incompatibility.
Review whether Goblin now requires new installer behavior. Record new contracts
in `deploy/goblinctl-requirements.json`, implement the matching capability in
`tools/goblinctl/capabilities.json`, and retain a deployment contract test.

Ship a new binary to distribute installer behavior changes. If Goblin requires a
new installer capability, publish and pin it before deploying that Goblin revision.
Several installer changes can be batched into one release.

## Publish deliberately

1. Choose the version in `[workspace.package]` in `Cargo.toml`. Run
   `cargo check --workspace` to update the workspace package entries in `Cargo.lock`.
   Review and commit both files through a normal PR. Do not change the published
   version in `dependencies.toml` until its artifacts exist.
2. Merge the reviewed source, including the version bump, into `master` and choose
   its full commit SHA. Dispatch **goblinctl native release** from `master`:

   ```bash
   gh workflow run goblinctl-release.yml --ref master -f sha=FULL_COMMIT_SHA
   ```

3. Source, browser, clean native build, compiler input coverage, and packaged-binary
   checks run with read-only repository permissions. The selected commit must be
   in `master`'s history. Approve the existing `goblinctl-release` environment after
   those checks pass. Publication verifies the artifact's source and checksum,
   attests the archive and manifest, and creates `goblinctl-vX.Y.Z` and its release.
   It does not modify a branch. Existing tags are never moved or overwritten.
4. In a checkout whose installer inputs match that release, run:

   ```bash
   cargo xtask pin-release --version X.Y.Z
   cargo xtask azure
   ```

   Review and commit all five generated outputs: `dependencies.toml`,
   `dependencies.lock.json`, `deploy/goblinctl-release.json`, and both Azure ARM
   templates. Use an ordinary PR; no companion PR or bot approval is required.

Pinning still authenticates the archive and manifest's GitHub provenance, verifies
checksums and clean-source metadata, and checks the input fingerprint and required
capabilities. The compiler's dependency file is checked during release building
to catch embedded assets omitted from the inventory.

If publication succeeds but pinning fails, correct the pin operation and rerun it;
do not publish another release just to update the pin. If tag creation succeeds but
release creation fails, inspect the existing tag and the run's tested artifacts
before manually completing the release. Never replace an existing version.

## Verify before deployment

Development `master` may contain unreleased installer changes. A green advisory
report is not proof that published artifacts exist or that a deployment works.
Keep the explicit **Goblin deployment readiness** workflow for the full check:

```bash
gh workflow run goblin-deployment-check.yml --ref master -f source-sha=FULL_COMMIT_SHA
```

It verifies the published binary, provenance, matching installer inputs, generated
templates, and application/installer contract tests, then records the pair in
`deployment-pair.json`. It retains conservative exact-input matching; do not use it
as a development merge requirement. It does not provision Azure or exercise tests
that require an unconfigured live PostgreSQL service.

Deploy the templates from that same verified Goblin commit and supply its SHA as
`goblinSourceRef`. Both Azure entry points require an explicit revision, and the
portal asks for it. There is no floating `master` default. The workflow does not
intercept manual Azure deployments; checking the selected pair remains a release
step. Existing local development installation continues to build from its checkout.

## One-time repository setting change

The existing live ruleset **Goblin installer release dependency** requires
`goblinctl-release-ready`. Before merging this migration, wait for this PR's new
`goblin-checks` check to pass, then edit that ruleset to replace the old required
check with `goblin-checks` from GitHub Actions. Preserve PR requirements, strict
up-to-date checks, resolved review threads, and deletion/force-push protections.
The updated `.github/goblinctl-ruleset.json` documents the desired configuration;
committing it does not change GitHub settings.

The workflow is now named **Goblin checks**, so the old default-branch
`workflow_run` listener will not create a companion PR for this migration while
it is under review. After merge, the listener and pin workflow are removed.
The former bot-PR permission can be disabled if no other workflow needs it.
Retain the publication environment approval and artifact provenance permissions.
