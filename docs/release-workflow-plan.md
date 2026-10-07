# Goblin release workflow plan

Steps 1–6 are implemented. The release guide in [releases.md](releases.md) is the
operating procedure. Local validation covers installer selection, candidate
integrity, coordinated publication and interruption recovery; exercising the new
workflow in GitHub Actions remains a rollout check. Azure installation is manual.

## Release operation

1. **Stabilization branches and checks.** Develop on master and cut a checked
   release/<major>.<minor> branch when a line is ready. Both branch families require
   PRs and passing goblin-checks. Branch creation requires no installer publication
   or dependency-pin update.
2. **Targeted backports.** Fix master first, then cherry-pick with provenance into
   each affected release line through its own PR. Include prerequisites deliberately.
   Apply this process to application code, goblinctl and bundled assets alike.
3. **Coordinated preparation.** Run tooling from master against a frozen, checked
   release-branch commit. Select independent Goblin and goblinctl versions. Reuse an
   authenticated installer with matching shipping inputs and capabilities, or build
   a new version from that source. The candidate owns the exact pair; Cargo versions
   and development pins need no update PR.
4. **Verify, approve and publish.** Check candidate hashes, its installer manifest,
   source inputs and executable, and reproduce the Azure assets from the frozen
   source. Run formatting, lints, the complete source suite with the candidate binary,
   and the setup/installation browser journeys. A separate job seals and attests the
   original candidate only after verification succeeds. One protected publication
   job obtains approval for the pair, revalidates its identity, source and existing
   versions, then publishes new goblinctl first and Goblin second. Installation-site
   delivery follows; recommendation changes only when requested.
5. **Recover partial publication.** Use GitHub's explicit **Re-run failed jobs**
   action on the original run. Successful preparation and sealing jobs retain their
   artifact IDs, manifest digest and original preparation attempt. Publication checks
   existing tags and bytes, adds only missing draft assets and never overwrites a
   published version. GitHub may require environment approval again for a retried
   publication job. Missing artifacts or changed source/bytes require fresh
   preparation and approval. Site delivery is reported separately and can be repaired
   without publishing again.
6. **Service release lines and consolidate workflows.** The same operation releases
   backports while master advances. Application-only changes can reuse the installer;
   changed bundles require a matching published installer or a new build. The
   standalone Publish goblinctl workflow and automatic pin-PR handoff are removed.
   Source checks, installation delivery and recommendation/recovery remain.

## Artifact and approval boundaries

The read-only preparation job uploads goblin-candidate-<attempt>. Verification
downloads that exact artifact ID and runs release-source code with read permissions.
Signing and publication run trusted tooling from the workflow's frozen master
revision in separate jobs; neither executes the release-source test suite.

The sealing job downloads the original candidate again, records passed deployment
checks and attests its manifest plus any newly built installer archive and manifest.
Reused installers retain their original publication attestations. The
goblin-ready-<attempt> artifact contains the sealed pair. The publication job binds
approval to its artifact ID and manifest SHA-256 and requires the original run,
preparation attempt, source revision and workflow revision. Reruns never select the
newest artifact by name or rebuild approved bytes.

The approval environment is goblin-release, limited to workflow branch master.
The selected product source is validated separately against its release branch.
The existing master environment policy therefore also supports backport releases.
One approval covers both releases. Retrying a failed job uses GitHub's normal
environment review behavior, not a separate installer approval flow.

Publication is serialized across release lines. Both release slots are checked
before any writes. Tags must point directly to the recorded source; draft identity
markers and existing assets must match the retained candidate. Every upload is
downloaded and checked before changing the draft to a published release. Completed
components are verified and skipped during recovery. Authentication or mismatched
published assets stop the operation for maintainer attention.

Installer assets remain on the goblinctl release. Goblin's release record includes
their hashes and exact installer manifest. Installation-site verification downloads
and authenticates that installer before accepting the Goblin release; the site
itself carries only Goblin's templates, UI, record and checksums.

## Compatibility and rollout checks

Goblin version suggestions stay within the selected major/minor line. A new line
starts at patch zero; preview numbers increase until a stable version is selected.
Once that stable version is reserved, the next suggestion advances the patch.
Installer versions are independent: reuse a matching publication, otherwise use
the source's Cargo version if unused, or increment the patch of the highest reserved
goblinctl version (considering the source's Cargo version too). Maintainer overrides
must satisfy the line/channel and availability rules. Commits do not infer semantic
version significance. Releases, drafts and standalone tags reserve versions;
candidate artifacts alone do not. Publication rechecks both version slots.

Historical schema-1 Goblin records remain readable. New publication requires a
sealed schema-2 candidate. Installer authentication accepts only the historical
goblinctl-release.yml or coordinated goblin-release.yml signing identity on master.
Deleting the standalone workflow does not remove trust in its historical releases.

Verify these scenarios before the first production publication:

- A real GitHub run prepares and seals a candidate from a protected release branch.
- An application-only backport reuses matching goblinctl from an older source.
- Added, removed or changed installer bundles cause a new build from the release
  branch, even when master has moved ahead.
- Version conflicts and incorrect signatures stop before publication writes.
- Publication interrupted after a tag, draft, asset or component completes resumes
  only the unfinished writes, using the same candidate.
- Missing/expired artifacts and conflicting published bytes fail explicitly.
- Site failure leaves release publication successful; delivery and recommendation
  can be repaired independently.

No automated check authenticates to Azure or provisions cloud resources. Offline
deployment checks, real PostgreSQL tests and manual Azure installation are distinct
evidence. Record database test skips and the first real deployment separately.
