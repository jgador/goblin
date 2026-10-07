//! A read-only GitHub operation producing one unpublished Goblin/goblinctl candidate.
use crate::azure;
use crate::candidate::Candidate;
use crate::candidate::InstallerOrigin;
use crate::candidate::Source;
use crate::dependencies;
use crate::goblin_release::Channel;
use crate::goblin_release::Check;
use crate::goblin_release::Version;
use crate::goblin_release::next_version;
use crate::goblin_release::output;
use crate::goblin_release::summary;
use crate::package;
use crate::release;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Args;
use goblinctl::environment;
use goblinctl::files;
use goblinctl::install;
use serde_json::Value;
use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

#[cfg(test)]
mod tests;

#[derive(Args)]
pub struct Options {
    /// Stabilization line, for example release/0.1.
    #[arg(long)]
    source_branch: String,
    /// Optional full SHA already merged into the selected line.
    #[arg(long)]
    source_sha: Option<String>,
    #[arg(long, value_enum, default_value = "preview")]
    channel: Channel,
    #[arg(long)]
    version: Option<String>,
    /// Reuse this matching published installer or build this unused version.
    #[arg(long)]
    installer_version: Option<String>,
    #[arg(long, default_value = ".artifacts/goblin")]
    output: PathBuf,
}

#[derive(Default)]
struct Inventory {
    reserved: BTreeSet<String>,
    published_installers: BTreeSet<String>,
}

#[derive(Debug)]
enum Selection {
    Reuse(release::Release),
    Build(String),
}

pub(crate) fn line(branch: &str) -> Result<[u64; 2]> {
    let text = branch
        .strip_prefix("release/")
        .context("Select release/<major>.<minor>, for example release/0.1")?;
    let version = Version::parse(&format!("{text}.0"))?;
    ensure!(
        version.preview.is_none(),
        "Select a release/<major>.<minor> branch"
    );
    Ok([version.base[0], version.base[1]])
}

fn version_for_line(
    tags: &BTreeSet<String>,
    branch: &str,
    channel: Channel,
    requested: Option<&str>,
) -> Result<String> {
    let [major, minor] = line(branch)?;
    let mut relevant = Vec::new();
    for tag in tags {
        if let Some(value) = tag.strip_prefix("goblin-v") {
            let version = Version::parse(value)?;
            if version.base[..2] == [major, minor] {
                relevant.push(tag.clone());
            }
        }
    }
    let version = match requested {
        Some(version) => version.to_owned(),
        None if relevant.is_empty() => Version {
            base: [major, minor, 0],
            preview: match channel {
                Channel::Preview => Some(1),
                Channel::Stable => None,
            },
        }
        .text(),
        None => next_version(&relevant, channel)?,
    };
    let parsed = Version::parse(&version)?;
    ensure!(
        parsed.base[..2] == [major, minor],
        "Goblin version must belong to {branch}; choose a version in this line"
    );
    ensure!(
        parsed.preview.is_some() == (channel == Channel::Preview),
        "Version must match the selected release channel"
    );
    ensure!(
        !tags.contains(&format!("goblin-v{version}")),
        "Goblin version is reserved by a release or tag; choose another version"
    );
    Ok(version)
}

fn installer_version(inventory: &Inventory, workspace: &str) -> Result<String> {
    release::validate_version(workspace)?;
    if !inventory
        .reserved
        .contains(&format!("goblinctl-v{workspace}"))
    {
        return Ok(workspace.to_owned());
    }
    let mut base = Version::parse(workspace)?.base;
    for tag in &inventory.reserved {
        if let Some(version) = tag.strip_prefix("goblinctl-v") {
            release::validate_version(version)?;
            base = base.max(Version::parse(version)?.base);
        }
    }
    base[2] = base[2]
        .checked_add(1)
        .context("Installer version overflow")?;
    Ok(Version {
        base,
        preview: None,
    }
    .text())
}

fn select_installer(
    snapshot: &release::Inputs,
    required: &BTreeSet<String>,
    pin: &release::Release,
    workspace: &str,
    inventory: &Inventory,
    requested: Option<&str>,
    mut inspect: impl FnMut(&str) -> Result<Option<release::Release>>,
) -> Result<Selection> {
    ensure!(
        required.is_subset(&snapshot.capabilities),
        "Release source lacks required goblinctl capabilities: {:?}. Backport the implementation and capability declaration, then prepare again",
        required
            .difference(&snapshot.capabilities)
            .collect::<Vec<_>>()
    );
    if let Some(version) = requested {
        release::validate_version(version)?;
        if inventory.published_installers.contains(version) {
            let published = inspect(version)?.context("Requested installer has no supported dependency manifest; choose an unused version")?;
            ensure!(
                release::compare(snapshot, &published.installer, required).outcome
                    == release::Outcome::Ready,
                "Requested published goblinctl {version} does not match this source; omit the override or choose an unused version"
            );
            return Ok(Selection::Reuse(published));
        }
        ensure!(
            !inventory
                .reserved
                .contains(&format!("goblinctl-v{version}")),
            "goblinctl {version} is reserved by a tag or incomplete publication; recover that publication or choose another version"
        );
        return Ok(Selection::Build(version.to_owned()));
    }
    // Prefer the development selection, then the source's version. Later coordinated
    // releases can also be reused without ever updating the checked-in pin.
    let mut candidates = inventory
        .published_installers
        .iter()
        .map(|v| {
            release::validate_version(v)?;
            Ok((Version::parse(v)?, v.clone()))
        })
        .collect::<Result<Vec<_>>>()?;
    candidates.sort_by(|a, b| b.0.cmp(&a.0));
    let mut seen = BTreeSet::new();
    for version in std::iter::once(pin.version.as_str())
        .chain(std::iter::once(workspace))
        .chain(candidates.iter().map(|(_, v)| v.as_str()))
    {
        if inventory.published_installers.contains(version)
            && seen.insert(version)
            && let Some(published) = inspect(version)?
            && release::compare(snapshot, &published.installer, required).outcome
                == release::Outcome::Ready
        {
            return Ok(Selection::Reuse(published));
        }
    }
    Ok(Selection::Build(installer_version(inventory, workspace)?))
}

fn git(root: &Path, args: &[&str]) -> Result<String> {
    Ok(
        files::output(Command::new("git").current_dir(root).args(args))
            .with_context(|| format!("Git {} failed; check the source checkout and origin access before preparing again", args.first().copied().unwrap_or("command")))?
            .trim()
            .to_owned(),
    )
}

fn api(route: &str) -> Result<Value> {
    Ok(serde_json::from_str(&files::output(
        Command::new("gh").args([
            "api",
            &format!("repos/{}/{route}", release::GITHUB_REPOSITORY),
        ]),
    ).with_context(|| format!("GitHub lookup failed for {route}; check repository read access and GitHub availability, then prepare again"))?)?)
}

fn pages(route: &str) -> Result<Vec<Value>> {
    let text = files::output(Command::new("gh").args([
        "api",
        "--paginate",
        "--slurp",
        &format!("repos/{}/{route}", release::GITHUB_REPOSITORY),
    ])).with_context(|| format!("GitHub lookup failed for {route}; check repository read access and GitHub availability, then prepare again"))?;
    Ok(serde_json::from_str(&text)?)
}

fn successful_check(pages: &[Value], source: &str) -> Result<u64> {
    let latest = pages.iter().filter_map(|page| page["check_runs"].as_array()).flatten()
        .filter(|check| check["name"] == "goblin-checks" && check["app"]["id"] == 15368 && check["head_sha"] == source)
        .max_by_key(|check| check["id"].as_u64().unwrap_or(0))
        .context("No goblin-checks run for this exact source; select a checked commit or run Goblin checks on the release branch and prepare its checked tip")?;
    ensure!(
        latest["status"] == "completed" && latest["conclusion"] == "success",
        "The latest goblin-checks run for this source has not passed; fix or finish those checks before preparing again"
    );
    latest["id"]
        .as_u64()
        .context("Source check has no identity")
}

fn freeze(root: &Path, options: &Options) -> Result<Source> {
    line(&options.source_branch)?;
    let reference = format!("refs/heads/{}", options.source_branch);
    git(root, &["fetch", "--no-tags", "origin", &reference]).context(
        "Cannot fetch the release branch; cut the stabilization line first, then prepare again",
    )?;
    let branch_tip = git(root, &["rev-parse", "FETCH_HEAD^{commit}"])?;
    let revision = options.source_sha.as_ref().unwrap_or(&branch_tip).clone();
    ensure!(
        revision.len() == 40
            && revision
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        "Source override must be a full lowercase commit SHA"
    );
    git(root, &["merge-base", "--is-ancestor", &revision, &branch_tip])
        .context("Source is not merged into the selected release branch; merge the backport PR or select a commit from that line")?;
    let check_run_id = successful_check(
        &pages(&format!(
            "commits/{revision}/check-runs?check_name=goblin-checks&filter=latest&per_page=100"
        ))?,
        &revision,
    )?;
    Ok(Source {
        branch: options.source_branch.clone(),
        revision,
        branch_tip,
        check_run_id,
    })
}

fn inventory(root: &Path) -> Result<Inventory> {
    let mut inventory = Inventory::default();
    for page in pages("releases?per_page=100")? {
        for release in page.as_array().context("Invalid GitHub release page")? {
            let tag = release["tag_name"].as_str().context("Release has no tag")?;
            inventory.reserved.insert(tag.to_owned());
            if release["draft"] == false
                && release["prerelease"] == false
                && let Some(version) = tag.strip_prefix("goblinctl-v")
            {
                inventory.published_installers.insert(version.to_owned());
            }
        }
    }
    for text in git(root, &["ls-remote", "--tags", "--refs", "origin"])?.lines() {
        let (_, tag) = text.split_once('\t').context("Invalid remote tag")?;
        inventory.reserved.insert(
            tag.strip_prefix("refs/tags/")
                .context("Invalid tag ref")?
                .to_owned(),
        );
    }
    Ok(inventory)
}

fn export_source(root: &Path, revision: &str, destination: &Path) -> Result<()> {
    fs::create_dir_all(destination)?;
    let archive = destination.with_extension("tar");
    files::run(
        Command::new("git")
            .current_dir(root)
            .args(["archive", "--format=tar", "--output"])
            .arg(&archive)
            .arg(revision),
    )?;
    tar::Archive::new(fs::File::open(archive)?).unpack(destination)?;
    Ok(())
}

fn published_manifest(version: &str, directory: &Path) -> Result<Option<release::Release>> {
    fs::create_dir_all(directory)?;
    release::download_files(
        release::GITHUB_REPOSITORY,
        version,
        directory,
        &["release.json"],
    )?;
    // Metadata can rule out incompatible releases without trusting their claims.
    // Only the selected installer is authenticated, before any executable is used.
    let metadata = files::json(&directory.join("release.json"))?;
    if metadata.get("schemaVersion").is_none() {
        return Ok(None);
    }
    let record: release::Release = serde_json::from_value(metadata)
        .with_context(|| format!("Invalid goblinctl {version} dependency manifest"))?;
    release::validate(&record)?;
    ensure!(record.version == version, "Installer tag/version mismatch");
    Ok(Some(record))
}

fn validate_installer_tag(record: &release::Release, tag: &Value) -> Result<()> {
    let kind = tag["object"]["type"].as_str().unwrap_or("missing");
    let revision = tag["object"]["sha"].as_str().unwrap_or("missing");
    ensure!(
        kind == "commit" && revision == record.source_revision,
        "Published goblinctl {} cannot be reused: its tag points to {kind} {revision}, but its authenticated manifest records commit {}. Start a new preparation with an unused --installer-version (Optional goblinctl version in Actions); keep existing tags and assets unchanged",
        record.version,
        record.source_revision
    );
    Ok(())
}

fn verify_reused_installer(
    selected: &release::Release,
    authenticate: impl FnOnce() -> Result<release::Release>,
    tag: impl FnOnce() -> Result<Value>,
) -> Result<release::Release> {
    let verified = authenticate()
        .with_context(|| format!("Cannot verify selected goblinctl {}; investigate its provenance or start a new preparation with an unused --installer-version (Optional goblinctl version in Actions)", selected.version))?;
    ensure!(
        &verified == selected,
        "Published installer changed during preparation; investigate before preparing again"
    );
    validate_installer_tag(&verified, &tag()?)?;
    Ok(verified)
}

pub(crate) fn validate_executable(root: &Path, directory: &Path, version: &str) -> Result<()> {
    let executable = tempfile::tempdir_in(directory)?;
    let archive = flate2::read::GzDecoder::new(fs::File::open(directory.join(release::ARCHIVE))?);
    tar::Archive::new(archive).unpack(executable.path())?;
    let binary = executable.path().join("goblinctl");
    ensure!(
        files::output(Command::new(&binary).arg("--version"))?.trim()
            == format!("goblinctl {version}"),
        "Packaged executable version mismatch"
    );
    let metadata: Value = serde_json::from_str(&files::output(
        Command::new(&binary).args(["metadata", "--json"]),
    )?)?;
    ensure!(
        metadata["version"] == version,
        "Packaged installer metadata version mismatch"
    );
    files::run(Command::new(binary).arg("validate-install").arg("--request").arg(root.join("deploy/install-request.json")).arg("--source").arg(root))
        .context("Packaged goblinctl rejected this source's installation request; fix or backport the installer contract before preparing again")
}

pub fn execute(root: &Path, options: Options) -> Result<()> {
    let result = prepare(root, &options);
    if let Err(error) = &result {
        summary(&format!(
            "## Preparation failed\n\n{error:#}\n\nNo candidate is ready. Resolve the reported prerequisite and start preparation again."
        ))?;
    }
    result
}

fn prepare(root: &Path, options: &Options) -> Result<()> {
    ensure!(
        git(root, &["status", "--porcelain"])?.is_empty(),
        "Preparation tooling must be committed; use a clean workflow checkout"
    );
    let directory = root.join(&options.output);
    ensure!(
        !directory.exists(),
        "Candidate output already exists; choose a fresh output directory"
    );
    let run_id = std::env::var(environment::GITHUB_RUN_ID)
        .context("Preparation requires a GitHub Actions run identity")?;
    let run_attempt = std::env::var(environment::GITHUB_RUN_ATTEMPT)?;
    for id in [&run_id, &run_attempt] {
        ensure!(
            !id.is_empty() && id.bytes().all(|b| b.is_ascii_digit()),
            "Invalid preparation run identity"
        );
    }
    let workflow_revision = git(root, &["rev-parse", "HEAD"])?;
    let source = freeze(root, options)?;
    let inventory = inventory(root)?;
    let version = version_for_line(
        &inventory.reserved,
        &source.branch,
        options.channel,
        options.version.as_deref(),
    )?;
    let scratch = root.join(".artifacts/release-preparation");
    fs::create_dir_all(&scratch)?;
    let staging = tempfile::tempdir_in(&scratch)?;
    let source_root = staging.path().join("source");
    export_source(root, &source.revision, &source_root)?;
    dependencies::check(&source_root).context(
        "Release-branch dependency files are inconsistent; fix them through the branch's PR flow",
    )?;
    let snapshot = release::inputs(&source_root)?;
    let required = release::capabilities(&source_root.join("deploy/goblinctl-requirements.json"))?;
    let pin = dependencies::installer(&source_root)?;
    let workspace: toml_edit::DocumentMut =
        fs::read_to_string(source_root.join("Cargo.toml"))?.parse()?;
    let workspace = workspace["workspace"]["package"]["version"]
        .as_str()
        .context("Missing workspace version")?;
    let selected = select_installer(
        &snapshot,
        &required,
        &pin,
        workspace,
        &inventory,
        options.installer_version.as_deref(),
        |version| published_manifest(version, &staging.path().join("published").join(version)),
    )?;
    let installer_directory = staging.path().join("installer");
    fs::create_dir_all(&installer_directory)?;
    let (installer, installer_origin) = match selected {
        Selection::Reuse(published) => {
            release::download(
                release::GITHUB_REPOSITORY,
                &published.version,
                &installer_directory,
            )?;
            let verified = verify_reused_installer(
                &published,
                || release::verify_artifacts(release::GITHUB_REPOSITORY, &installer_directory),
                || api(&format!("git/ref/tags/goblinctl-v{}", published.version)),
            )?;
            (verified, InstallerOrigin::Published)
        }
        Selection::Build(version) => {
            let target = staging.path().join("target");
            files::run(
                Command::new("bash")
                    .current_dir(&source_root)
                    .arg("scripts/build-goblinctl-release.sh")
                    .arg(&target)
                    .env(environment::GOBLINCTL_BUILD_VERSION, &version),
            )
            .context(
                "goblinctl build failed; fix the selected release branch before preparing again",
            )?;
            let binary = target.join(release::TARGET).join("release/goblinctl");
            release::coverage(&source_root, &binary.with_extension("d"), &snapshot)?;
            let packaged = package::create(
                &source_root,
                &binary,
                &installer_directory,
                &package::Identity {
                    version: &version,
                    revision: &source.revision,
                    dirty: false,
                },
            )?;
            ensure!(
                packaged.installer == snapshot,
                "Installer inputs changed while building"
            );
            release::verify_archive(&installer_directory)?;
            (packaged, InstallerOrigin::Built)
        }
    };
    validate_executable(&source_root, &installer_directory, &installer.version)?;
    let prepared = staging.path().join("candidate");
    azure::generate_for_installer(
        &source_root,
        &prepared,
        &version,
        &source.revision,
        &installer,
    )?;
    fs::create_dir_all(prepared.join("installer"))?;
    let mut assets = BTreeMap::new();
    for name in [release::ARCHIVE, "release.json", "SHA256SUMS"] {
        let relative = format!("installer/{name}");
        fs::copy(installer_directory.join(name), prepared.join(&relative))?;
        assets.insert(
            relative.clone(),
            install::checksum(&prepared.join(relative))?,
        );
    }
    for name in azure::ASSETS {
        assets.insert(name.to_owned(), install::checksum(&prepared.join(name))?);
    }
    let candidate = Candidate {
        schema_version: 2,
        version,
        channel: options.channel,
        source,
        workflow_revision,
        installer,
        installer_origin,
        changed_installer_inputs: release::compare(&snapshot, &pin.installer, &required)
            .changed_inputs,
        run_id,
        run_attempt,
        assets,
        deployment_checks: Check::Pending,
    };
    files::write_json(
        &prepared.join("release.json"),
        &serde_json::to_value(&candidate)?,
        0o644,
    )?;
    let mut sums = candidate.assets.clone();
    sums.insert(
        "release.json".into(),
        install::checksum(&prepared.join("release.json"))?,
    );
    fs::write(
        prepared.join("SHA256SUMS"),
        sums.iter()
            .map(|(name, hash)| format!("{hash}  {name}\n"))
            .collect::<String>(),
    )?;
    fs::create_dir_all(
        directory
            .parent()
            .context("Candidate output has no parent")?,
    )?;
    fs::rename(prepared, &directory)?;
    output("outcome", "prepared")?;
    output("version", &candidate.version)?;
    output("installer", &candidate.installer.version)?;
    output(
        "installer-origin",
        match candidate.installer_origin {
            InstallerOrigin::Published => "published",
            InstallerOrigin::Built => "built",
        },
    )?;
    output("source", &candidate.source.revision)?;
    let disposition = match candidate.installer_origin {
        InstallerOrigin::Published => "reused",
        InstallerOrigin::Built => "built, unpublished",
    };
    summary(&format!(
        "## Candidate prepared\n\nGoblin {}\n\ngoblinctl {} — {disposition}\n\nSource: `{}` at `{}`\n\nThe candidate contains the installer archive, its manifest, generated Azure assets, and their checksums. The workflow must finish candidate verification before requesting publication approval. Azure installation is manual.",
        candidate.version,
        candidate.installer.version,
        candidate.source.branch,
        candidate.source.revision
    ))
}
