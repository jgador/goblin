//! Native installer identity, packaging inputs, and published artifact verification.
use anyhow::Context;
use anyhow::Result;
use anyhow::bail;
use anyhow::ensure;
use goblinctl::files;
use goblinctl::install;
use serde::Deserialize;
use serde::Serialize;
use sha2::Digest;
use sha2::Sha256;
use std::collections::BTreeMap;
use std::collections::BTreeSet;
use std::fs;
use std::path::Component;
use std::path::Path;
use std::process::Command;

// goblinctl ships from Goblin's own repository, not a configurable dependency source.
pub const GITHUB_REPOSITORY: &str = "jgador/goblin";
pub const TARGET: &str = "x86_64-unknown-linux-musl";
pub const ARCHIVE: &str = "goblinctl-x86_64-unknown-linux-musl.tar.gz";
const INPUTS: &str = "tools/goblinctl/release-inputs.json";

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct InputPolicy {
    schema_version: u32,
    description: String,
    files: BTreeSet<String>,
    directories: BTreeSet<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Capabilities {
    schema_version: u32,
    description: String,
    capabilities: BTreeSet<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Inputs {
    schema_version: u32,
    pub sha256: String,
    pub files: BTreeMap<String, String>,
    pub capabilities: BTreeSet<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Release {
    pub schema_version: u32,
    pub version: String,
    pub target: String,
    pub sha256: String,
    pub source_revision: String,
    pub source_dirty: bool,
    pub cargo_lock_sha256: String,
    pub installer: Inputs,
}

#[derive(Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum Outcome {
    Ready,
    ReleaseRequired,
    PinRequired,
    VerificationFailed,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    schema_version: u32,
    pub outcome: Outcome,
    pub message: String,
    pub changed_inputs: Vec<String>,
}

fn json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<T> {
    serde_json::from_slice(&fs::read(path).with_context(|| path.display().to_string())?)
        .with_context(|| format!("Invalid {}", path.display()))
}

fn safe_relative(path: &str) -> Result<()> {
    ensure!(
        !path.is_empty()
            && Path::new(path)
                .components()
                .all(|part| matches!(part, Component::Normal(_))),
        "Release input must be a relative path without traversal: {path}"
    );
    Ok(())
}

pub fn capabilities(path: &Path) -> Result<BTreeSet<String>> {
    let value: Capabilities = json(path)?;
    ensure!(value.schema_version == 1, "Unsupported capability schema");
    ensure!(
        !value.description.is_empty(),
        "Describe the capability contract"
    );
    ensure!(
        value.capabilities.iter().all(|name| !name.is_empty()
            && name
                .bytes()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'.' || b == b'-')),
        "Invalid capability name"
    );
    Ok(value.capabilities)
}

fn digest(inputs: &BTreeMap<String, String>) -> Result<String> {
    // JSON escaping and sorted keys make file names, boundaries, and ordering unambiguous.
    Ok(format!(
        "{:x}",
        Sha256::digest(serde_json::to_vec(&(1, inputs))?)
    ))
}

pub fn inputs(root: &Path) -> Result<Inputs> {
    let policy: InputPolicy = json(&root.join(INPUTS))?;
    ensure!(
        policy.schema_version == 1,
        "Unsupported release input schema"
    );
    ensure!(
        !policy.description.is_empty(),
        "Describe the release boundary"
    );
    // These inputs may not be removed by accidentally narrowing the allowlist.
    for required in [
        "LICENSE",
        "NOTICE",
        "THIRD_PARTY_NOTICES.md",
        "Cargo.toml",
        "Cargo.lock",
        "rust-toolchain.toml",
        INPUTS,
        "tools/goblinctl/Cargo.toml",
        "tools/goblinctl/capabilities.json",
        "scripts/build-goblinctl-release.sh",
    ] {
        ensure!(
            policy.files.contains(required),
            "Missing mandatory release input: {required}"
        );
    }
    for required in ["tools/goblinctl/src", ".cargo"] {
        ensure!(
            policy.directories.contains(required),
            "Missing mandatory input directory: {required}"
        );
    }
    let mut paths = policy.files.clone();
    for path in policy.files.iter().chain(&policy.directories) {
        safe_relative(path)?;
    }
    for directory in &policy.directories {
        collect(root, &root.join(directory), &mut paths)?;
    }
    // Build scripts and additional crate roots cannot silently bypass the policy.
    for name in ["build.rs", "src"] {
        let path = root.join("tools/goblinctl").join(name);
        if path.is_file() {
            paths.insert(format!("tools/goblinctl/{name}"));
        }
    }
    let mut values = BTreeMap::new();
    for path in paths {
        let full = root.join(&path);
        ensure!(
            !fs::symlink_metadata(&full)?.file_type().is_symlink(),
            "Symlink release input: {path}"
        );
        values.insert(path, install::checksum(&full)?);
    }
    Ok(Inputs {
        schema_version: 1,
        sha256: digest(&values)?,
        files: values,
        capabilities: capabilities(&root.join("tools/goblinctl/capabilities.json"))?,
    })
}

fn collect(root: &Path, directory: &Path, paths: &mut BTreeSet<String>) -> Result<()> {
    ensure!(
        !fs::symlink_metadata(directory)?.file_type().is_symlink(),
        "Symlink input directory"
    );
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        ensure!(!entry.file_type()?.is_symlink(), "Symlink release input");
        if entry.file_type()?.is_dir() {
            collect(root, &entry.path(), paths)?;
        } else {
            paths.insert(
                entry
                    .path()
                    .strip_prefix(root)?
                    .to_string_lossy()
                    .replace('\\', "/"),
            );
        }
    }
    Ok(())
}

/// Check the compiler's actual local dependencies, including include_bytes!/include_str!.
/// Only simple Unix make depfiles are accepted; unusual escaping fails closed.
pub fn coverage(root: &Path, depfile: &Path, snapshot: &Inputs) -> Result<()> {
    let text = fs::read_to_string(depfile)?;
    let line = text.lines().next().context("Empty Rust dependency file")?;
    let (_, dependencies) = line
        .split_once(": ")
        .context("Invalid Rust dependency file")?;
    ensure!(
        !dependencies.contains('\\'),
        "Escaped dependency paths are unsupported"
    );
    let root = root.canonicalize()?;
    ensure!(!dependencies.trim().is_empty(), "No compiler dependencies");
    for dependency in dependencies.split_whitespace() {
        let path = root.join(dependency).canonicalize()?;
        let relative = path
            .strip_prefix(&root)
            .context("Compiler input outside repository")?;
        let name = relative.to_str().context("Non-UTF8 compiler input")?;
        ensure!(
            snapshot.files.contains_key(name),
            "Compiler input is absent from {INPUTS}: {name}"
        );
    }
    Ok(())
}

pub fn validate_version(version: &str) -> Result<()> {
    let parts: Vec<_> = version.split('.').collect();
    ensure!(
        parts.len() == 3
            && parts.iter().all(|p| !p.is_empty()
                && p.bytes().all(|b| b.is_ascii_digit())
                && (p.len() == 1 || !p.starts_with('0'))),
        "Expected MAJOR.MINOR.PATCH release version"
    );
    Ok(())
}

fn hex(value: &str, size: usize) -> bool {
    value.len() == size
        && value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

pub fn validate(release: &Release) -> Result<()> {
    validate_version(&release.version)?;
    ensure!(
        release.schema_version == 1 && release.installer.schema_version == 1,
        "Unsupported release schema"
    );
    ensure!(release.target == TARGET, "Unsupported release target");
    ensure!(
        !release.source_dirty,
        "Dirty-source packages cannot be published or pinned"
    );
    ensure!(
        hex(&release.sha256, 64)
            && hex(&release.cargo_lock_sha256, 64)
            && hex(&release.source_revision, 40),
        "Invalid release checksums or source revision"
    );
    ensure!(
        release.installer.files.values().all(|value| hex(value, 64)),
        "Invalid input checksum"
    );
    ensure!(
        release.installer.sha256 == digest(&release.installer.files)?,
        "Invalid installer fingerprint"
    );
    ensure!(
        release.installer.files.get("Cargo.lock") == Some(&release.cargo_lock_sha256),
        "Lockfile digest mismatch"
    );
    Ok(())
}

pub fn compare(current: &Inputs, published: &Inputs, required: &BTreeSet<String>) -> Report {
    let paths: BTreeSet<_> = current.files.keys().chain(published.files.keys()).collect();
    let changed_inputs = paths
        .into_iter()
        .filter(|path| current.files.get(*path) != published.files.get(*path))
        .cloned()
        .collect();
    if current != published || !required.is_subset(&published.capabilities) {
        Report { schema_version: 1, outcome: Outcome::ReleaseRequired,
            message: "Goblin needs a new goblinctl release: installer inputs or required capabilities differ from the pinned release.".into(), changed_inputs }
    } else {
        Report {
            schema_version: 1,
            outcome: Outcome::Ready,
            message: "Published goblinctl matches this Goblin source tree.".into(),
            changed_inputs,
        }
    }
}

pub fn download(github_repository: &str, version: &str, destination: &Path) -> Result<()> {
    download_files(
        github_repository,
        version,
        destination,
        &[ARCHIVE, "release.json", "SHA256SUMS"],
    )
}

pub fn download_files(
    github_repository: &str,
    version: &str,
    destination: &Path,
    names: &[&str],
) -> Result<()> {
    validate_version(version)?;
    ensure!(
        github_repository.split('/').count() == 2
            && github_repository
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"/._-".contains(&b)),
        "Invalid GitHub repository"
    );
    let mut command = Command::new("gh");
    command
        .args([
            "release",
            "download",
            &format!("goblinctl-v{version}"),
            "--repo",
            github_repository,
            "--clobber",
            "--dir",
        ])
        .arg(destination);
    for name in names {
        command.args(["--pattern", name]);
    }
    files::run(&mut command)
}

pub fn verify_manifest(github_repository: &str, directory: &Path) -> Result<Release> {
    let release: Release = json(&directory.join("release.json"))?;
    validate(&release)?;
    authenticate(github_repository, &directory.join("release.json"))?;
    Ok(release)
}

pub fn verify_archive(directory: &Path) -> Result<Release> {
    let release: Release = json(&directory.join("release.json"))?;
    validate(&release)?;
    ensure!(
        install::checksum(&directory.join(ARCHIVE))? == release.sha256,
        "Published archive checksum mismatch"
    );
    ensure!(
        fs::read_to_string(directory.join("SHA256SUMS"))?
            == format!("{}  {ARCHIVE}\n", release.sha256),
        "SHA256SUMS mismatch"
    );
    Ok(release)
}

fn authenticate(github_repository: &str, artifact: &Path) -> Result<()> {
    files::run(
        Command::new("gh")
            .args(["attestation", "verify"])
            .arg(artifact)
            .args([
                "--repo",
                github_repository,
                "--cert-identity-regex",
                &format!("^https://github\\.com/{}/\\.github/workflows/(goblinctl-release|goblin-release)\\.yml@refs/heads/master$", regex::escape(github_repository)),
                "--deny-self-hosted-runners",
            ]),
    )
}

pub fn verify_artifacts(github_repository: &str, directory: &Path) -> Result<Release> {
    let release = verify_archive(directory)?;
    // Authenticate the dependency claims as well as the binary bytes.
    authenticate(github_repository, &directory.join("release.json"))?;
    authenticate(github_repository, &directory.join(ARCHIVE))?;
    Ok(release)
}

pub fn check(
    root: &Path,
    github_repository: &str,
    depfile: &Path,
    directory: &Path,
    report_path: &Path,
) -> Result<()> {
    let result = check_inner(root, github_repository, depfile, directory);
    let report = match result {
        Ok(report) => report,
        Err(error) => Report {
            schema_version: 1,
            outcome: Outcome::VerificationFailed,
            message: format!("Installer release verification failed: {error:#}"),
            changed_inputs: vec![],
        },
    };
    files::write_json(report_path, &serde_json::to_value(&report)?, 0o644)?;
    println!("{}", report.message);
    for path in &report.changed_inputs {
        println!("  {path}");
    }
    ensure!(
        report.outcome == Outcome::Ready,
        "Published installer verification: {:?}",
        report.outcome
    );
    Ok(())
}

fn check_inner(
    root: &Path,
    github_repository: &str,
    depfile: &Path,
    directory: &Path,
) -> Result<Report> {
    crate::dependencies::check(root)?;
    verify_github_repository(github_repository)?;
    let snapshot = inputs(root)?;
    coverage(root, depfile, &snapshot)?;
    let required = capabilities(&root.join("deploy/goblinctl-requirements.json"))?;
    let pin = crate::dependencies::installer(root)?;
    let version = pin.version.as_str();
    download(github_repository, version, directory)?;
    let raw = files::json(&directory.join("release.json"))?;
    if raw.get("schemaVersion").is_none() {
        return Ok(Report { schema_version: 1, outcome: Outcome::ReleaseRequired,
            message: "The pinned legacy release has no authenticated installer fingerprint. Publish a release with dependency metadata.".into(),
            changed_inputs: snapshot.files.keys().cloned().collect() });
    }
    let published = verify_artifacts(github_repository, directory)?;
    ensure!(published.version == version, "Release tag/version mismatch");
    let mut report = compare(&snapshot, &published.installer, &required);
    if report.outcome == Outcome::Ready && pin != published {
        report.outcome = Outcome::PinRequired;
        report.message = "The release is compatible, but the checked-in pin differs from its published manifest. Refresh the dependency lock.".into();
    }
    if report.outcome == Outcome::Ready {
        let executable = tempfile::tempdir()?;
        files::run(
            Command::new("tar")
                .arg("-xzf")
                .arg(directory.join(ARCHIVE))
                .arg("-C")
                .arg(executable.path()),
        )?;
        files::run(
            Command::new(executable.path().join("goblinctl"))
                .arg("validate-install")
                .arg("--request")
                .arg(root.join("deploy/install-request.json"))
                .arg("--source")
                .arg(root),
        )?;
    }
    Ok(report)
}

pub fn pin(root: &Path, github_repository: &str, version: &str) -> Result<()> {
    verify_github_repository(github_repository)?;
    let directory = tempfile::tempdir()?;
    download(github_repository, version, directory.path())?;
    let release = verify_artifacts(github_repository, directory.path())?;
    ensure!(release.version == version, "Release tag/version mismatch");
    let report = compare(
        &inputs(root)?,
        &release.installer,
        &capabilities(&root.join("deploy/goblinctl-requirements.json"))?,
    );
    ensure!(report.outcome == Outcome::Ready, "{}", report.message);
    crate::dependencies::pin_release(root, &release)
}

fn verify_github_repository(github_repository: &str) -> Result<()> {
    ensure!(
        github_repository == GITHUB_REPOSITORY,
        "goblinctl releases must come from Goblin's repository: {GITHUB_REPOSITORY}"
    );
    Ok(())
}

pub fn candidate(root: &Path, depfile: &Path) -> Result<()> {
    crate::dependencies::check(root)?;
    goblinctl::deployment::InstallRequest::read(&root.join("deploy/install-request.json"))?
        .validate(root)?;
    let snapshot = inputs(root)?;
    coverage(root, depfile, &snapshot)?;
    let required = capabilities(&root.join("deploy/goblinctl-requirements.json"))?;
    if !required.is_subset(&snapshot.capabilities) {
        bail!(
            "Candidate installer lacks capabilities required by Goblin: {:?}",
            required
                .difference(&snapshot.capabilities)
                .collect::<Vec<_>>()
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn snapshot() -> Inputs {
        let files = BTreeMap::from([
            ("Cargo.lock".into(), "a".repeat(64)),
            ("deploy/azure/install-app.sh".into(), "b".repeat(64)),
        ]);
        Inputs {
            schema_version: 1,
            sha256: digest(&files).unwrap(),
            files,
            capabilities: BTreeSet::from(["install.victorialogs.v1".into()]),
        }
    }

    #[test]
    fn installer_changes_require_release_and_report_additions_deletions() {
        let previous = snapshot();
        let mut current = previous.clone();
        current.files.remove("Cargo.lock");
        current.files.insert("new-asset".into(), "c".repeat(64));
        let report = compare(&current, &previous, &BTreeSet::new());
        assert_eq!(report.outcome, Outcome::ReleaseRequired);
        assert_eq!(report.changed_inputs, ["Cargo.lock", "new-asset"]);
    }

    #[test]
    fn unchanged_installer_is_reusable_but_missing_capability_blocks() {
        let current = snapshot();
        assert_eq!(
            compare(&current, &current, &current.capabilities).outcome,
            Outcome::Ready
        );
        assert_eq!(
            compare(
                &current,
                &current,
                &BTreeSet::from(["install.new.v1".into()])
            )
            .outcome,
            Outcome::ReleaseRequired
        );
    }

    #[test]
    fn dirty_or_forged_metadata_is_rejected() {
        let mut value = Release {
            schema_version: 1,
            version: "0.1.1".into(),
            target: TARGET.into(),
            sha256: "c".repeat(64),
            source_revision: "d".repeat(40),
            source_dirty: false,
            cargo_lock_sha256: "a".repeat(64),
            installer: snapshot(),
        };
        validate(&value).unwrap();
        value.source_dirty = true;
        assert!(validate(&value).is_err());
        value.source_dirty = false;
        value
            .installer
            .files
            .insert("hidden".into(), "e".repeat(64));
        assert!(validate(&value).is_err());
    }

    #[test]
    fn compiler_detects_an_embedded_input_outside_allowlist() {
        let directory = tempfile::tempdir().unwrap();
        let asset = directory.path().join("new-asset.txt");
        fs::write(&asset, "embedded").unwrap();
        let depfile = directory.path().join("goblinctl.d");
        fs::write(&depfile, format!("target: {}\n", asset.display())).unwrap();
        assert!(coverage(directory.path(), &depfile, &snapshot()).is_err());
        let mut current = snapshot();
        current.files.insert("new-asset.txt".into(), "e".repeat(64));
        coverage(directory.path(), &depfile, &current).unwrap();
    }

    #[test]
    fn reject_unsafe_versions_and_paths() {
        for value in ["../tag", "01.2.3", "1.2", "1.2.3;echo", "1.2.3-rc1"] {
            assert!(validate_version(value).is_err());
        }
        for value in ["../asset", "/asset", ""] {
            assert!(safe_relative(value).is_err());
        }
    }

    #[test]
    fn fingerprint_tracks_shipping_changes_but_not_pin_or_arm_updates() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .parent()
            .unwrap();
        let original = inputs(root).unwrap();
        let directory = tempfile::tempdir().unwrap();
        for path in original.files.keys() {
            let destination = directory.path().join(path);
            fs::create_dir_all(destination.parent().unwrap()).unwrap();
            fs::copy(root.join(path), destination).unwrap();
        }
        assert_eq!(inputs(directory.path()).unwrap(), original);
        for path in ["LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"] {
            let destination = directory.path().join(path);
            fs::write(&destination, "updated licensing").unwrap();
            assert_ne!(inputs(directory.path()).unwrap().sha256, original.sha256);
            fs::copy(root.join(path), destination).unwrap();
        }
        for path in [
            "README.md",
            "dependencies.lock.json",
            "deploy/azure/azuredeploy.json",
        ] {
            fs::write(directory.path().join(path), "updated output").unwrap();
        }
        assert_eq!(inputs(directory.path()).unwrap(), original);
        fs::write(
            directory.path().join("deploy/azure/install-app.sh"),
            "new installer behavior",
        )
        .unwrap();
        assert_ne!(inputs(directory.path()).unwrap().sha256, original.sha256);
        fs::write(
            directory.path().join("tools/goblinctl/src/new.rs"),
            "// new module",
        )
        .unwrap();
        assert!(
            inputs(directory.path())
                .unwrap()
                .files
                .contains_key("tools/goblinctl/src/new.rs")
        );
    }
}
