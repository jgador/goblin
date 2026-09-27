# Installer release dependencies

The required `goblinctl-release-ready` check prevents Goblin from merging with an
unpublished or incompatible installer. Ordinary GitHub Actions and Rust `xtask`
commands make the decision. An LLM, release label, or bot comment cannot override it.

## Authoritative files

| File | Responsibility |
| --- | --- |
| `dependencies.toml` / `dependencies.lock.json` | Published goblinctl selection and direct Docker images; generated artifact hashes. |
| `tools/goblinctl/release-inputs.json` | Shipping source and build inputs. Includes external embedded assets, Cargo manifests/lockfile, toolchain, Cargo configuration, and the native build recipe. |
| `tools/goblinctl/capabilities.json` | Installer behavior provided by this source. |
| `deploy/goblinctl-requirements.json` | Installer behavior this Goblin source requires. |
| `deploy/goblinctl-release.json` | Exact published installer selection and authenticated dependency metadata. |
| `.github/goblinctl-ruleset.json` | Required GitHub Actions check and PR-only policy for `master`. |

The fingerprint is SHA-256 over a schema-versioned, sorted JSON map of relative
paths and content hashes. Source additions/deletions are significant. The compiler's
native `.d` file must contain no repository dependency outside that inventory;
this catches newly embedded assets even when the inventory was not updated.
Symlinks and unsupported dependency-path escaping fail verification.

Pin files, generated ARM output, application-only files, and tests/docs outside
shipping directories are excluded. Updating the pin does not require another
installer release. An unrelated change inside a shipping directory can trigger a
conservative release; the checker does not attempt to infer semantic compatibility.

When Goblin starts depending on new installer behavior, add a named requirement,
implement it in goblinctl, and add a deployment contract test. The initial
`install.victorialogs.v1` capability requires installation and readiness handling
for VictoriaLogs and Fluent Bit, exercised by `tests/deployment/azure.test.ts`.
File diffs cannot discover arbitrary undeclared semantic requirements. Keep these
contracts reviewed alongside application and installer changes.

## Checks and companion PRs

`goblinctl-dependency.yml` runs on PRs, pushes to `master`, merge groups, and explicit
dispatch. It checks the proposed merge commit, not only the PR's changed paths.
Its status always requires an already-published installer, including on companion
branches. Candidate source validation must never produce a green release-readiness
status that could later be reused on `master`.

The checker downloads the pin's GitHub release and verifies the archive checksum,
`SHA256SUMS`, clean release metadata, matching input fingerprint and capabilities,
and GitHub attestations for **both** the archive and `release.json`. Provenance must
come from this repository's `goblinctl-release.yml` on GitHub-hosted runners.
The signed manifest identifies the reviewed source commit; the workflow identity
can be the orchestration commit rather than the candidate commit.

CI also checks generated ARM drift and runs the deployment/local-login contracts
against the downloaded native executable and the proposed Goblin source. Existing
database opt-in tests remain opt-in; passing this check is not proof of a real Azure
installation or an authenticated production database connection.

The JSON report distinguishes `ready`, `release-required`, `pin-required`, and
`verification-failed`. Missing network access, bad provenance, and corrupt artifacts
fail closed. A legacy release without dependency metadata requires a new release.

`goblinctl-automation.yml` consumes completed checks on a separate runner, executing
only default-branch orchestration. It updates one PR comment and creates a companion
branch `automation/goblinctl/pr-N` targeting the feature branch. Direct-push failures
produce a repair PR targeting `master`. Fork PRs receive instructions for a maintainer
to prepare a repository branch; fork code never receives publication credentials.

The version proposal considers published and open companion versions. Patch is a
proposal for review, not automatic SemVer classification. PR creation is serialized,
and publication is separately serialized. Existing companions are preserved: merge
new parent changes into them and resolve conflicts rather than overwriting reviewed
work. If installer inputs change after publication, choose another version and
update the candidate marker in the PR body along with Cargo and its lockfile.

Bot writes use `GITHUB_TOKEN`. Because these writes do not normally trigger CI,
the automation explicitly dispatches `goblinctl-dependency.yml`. A trusted follow-up
sets the check status on the evaluated PR head only if its head and synthetic merge
SHA still match. Stale results cannot unlock a newer candidate. The required check
remains failed even if notification or companion creation fails.

## Publication before merging

The companion starts with the version change and a machine-readable candidate
marker in its body. Review the parent PR's installer changes as well as this bump.
Dispatch from Actions or with:

```bash
gh workflow run goblinctl-release.yml --ref master -f pr=NUMBER -f sha=EXACT_HEAD_SHA
```

The selected SHA must belong to an open repository companion PR containing the
latest parent changes. A maintainer initiates publication; the environment approval
is the explicit approval of that candidate. The workflow performs source, browser,
fresh native build, compiler-coverage, and packaged-binary tests before the approval
checkpoint. It rechecks the SHA after approval and never builds in a job with release
write credentials. The publishing job creates the immutable tag and release.

`goblinctl-pin.yml` then verifies the published artifacts and regenerates the pin
and ARM templates in a read-only verification job. A separate writer commits only
the release pin, deployment dependency catalog and lock, and both ARM templates,
refusing to update a branch which moved while verification
ran. Checks run again. Review and merge the companion into the feature branch;
the original Goblin PR must then pass its own merge-result check.

For a pin failure after successful publication, inspect the failure and dispatch
`goblinctl-pin.yml` with the companion PR number and published version. It validates
the current inputs again. Do not republish or replace an existing release. If tag
creation succeeded but release creation failed, inspect the existing tag/artifacts
before choosing recovery; automatic publication deliberately refuses an existing tag.

## Repository setup and bootstrap

Using an authenticated administrator account, first inspect and prepare the policy:

```bash
node scripts/configure-release-policy.mts show
node scripts/configure-release-policy.mts prepare
```

Preparation configures the current administrator as the `goblinctl-release`
environment reviewer and enables GitHub Actions to create PRs. Workflow permissions
still default to read-only. The GitHub setting also permits bot approvals, but none
of these workflows approves a PR. Self-approval of the environment is allowed so
a single-maintainer repository has a usable explicit publication checkpoint.

The initial local `0.1.0` pin is intentionally rejected. Before activating the branch
rule, prepare a companion release PR from the initial feature branch, bump the Cargo
version/lockfile, and include its candidate marker as described above. Until the new
automation reaches `master`, this one companion must be created by a maintainer.
The existing `goblinctl-release.yml` filename is already registered on the default
branch: dispatch it with `--ref FEATURE_BRANCH` for this initial reviewed workflow.
After publication, review its generated pin. Explicit dispatch of newly added workflow
files may remain unavailable until those files reach the default branch; a maintainer
push or merge into the feature branch triggers normal PR validation during bootstrap.

Merge the initial automation and verified baseline, wait for `master`'s required check
to pass, then activate:

```bash
node scripts/configure-release-policy.mts activate
```

Activation refuses a legacy/dirty pin or a master commit without a successful GitHub
Actions `goblinctl-release-ready` check. It creates/updates only the named ruleset,
preserving unrelated rules. The rule requires PRs, an up-to-date branch, resolved
review threads, and the named check from GitHub Actions (integration ID 15368), with
no bypass actors. Zero required PR approvals accommodates the current single
maintainer; release publication still requires the environment approval. Increase
PR review requirements when independent reviewers are available.

Rulesets are repository settings; committing the JSON does not activate them.
A workflow triggered by a direct push can detect a violation only after it lands.
The active ruleset prevents ordinary direct pushes; any exceptional administrator
policy change still requires treating a failed push check as non-deployable.

## Deployment integration

`goblin-deployment-check.yml` is a reusable verification prerequisite and can also
be dispatched with a full Goblin commit SHA. It independently verifies the installer
and exact application/installer pair and emits `deployment-pair.json`. Future Goblin
release/deployment jobs must depend on this workflow's successful result and deploy
that recorded SHA, passing it as Azure's `goblinSourceRef`.

There is currently no automated Azure deployment job to attach to. This workflow
does not intercept manual portal deployments or arbitrary release creation. Azure
templates still expose their existing source-ref parameter; use the verified SHA
instead of its floating `master` default. Source pinning in a future deployment job
must not be replaced by resolving the branch again after verification.

Local checks:

```bash
bash scripts/build-goblinctl-release.sh target
cargo xtask release-candidate
cargo xtask release-check --repo jgador/goblin
npm run test:release
just test -p xtask
```

`release-candidate` verifies input coverage and implemented capabilities without
claiming publication. `release-check` writes `.artifacts/goblinctl-check.json` even
when release verification fails, so CI can explain the dependency and keep merging
blocked. `cargo xtask azure --check` remains a local generation/drift check; it does
not independently establish publication.
