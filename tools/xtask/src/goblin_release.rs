//! Release commands and compatibility with historical Goblin records.
use crate::azure;
#[cfg(test)]
use crate::dependencies;
use crate::release;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Subcommand;
use clap::ValueEnum;
use goblinctl::environment;
use goblinctl::files;
use goblinctl::install;
use serde::Deserialize;
use serde::Serialize;
use serde_json::json;
use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, ValueEnum)]
#[serde(rename_all = "lowercase")]
pub enum Channel {
    Preview,
    Stable,
}

#[derive(Subcommand)]
pub enum Task {
    /// Prepare a coordinated candidate from a checked release-branch commit.
    Prepare(crate::preparation::Options),
    /// Record successful deployment tests and seal the candidate for publication.
    Seal {
        #[arg(long, default_value = ".artifacts/goblin")]
        directory: PathBuf,
        /// Preparation attempt retained when failed verification jobs are rerun.
        #[arg(long)]
        expected_attempt: String,
    },
    /// Check a candidate against its frozen source and reproduce its Azure assets.
    CheckCandidate {
        #[arg(long)]
        directory: PathBuf,
        #[arg(long)]
        source_root: PathBuf,
    },
    /// Authenticate published installer assets, including historical publications.
    AuthenticateInstaller {
        #[arg(long)]
        directory: PathBuf,
    },
    /// Verify all prepared bytes and evidence before publishing or serving them.
    Verify {
        #[arg(long)]
        directory: PathBuf,
        #[arg(long)]
        expected_source: Option<String>,
        #[arg(long)]
        expected_run: Option<String>,
        #[arg(long)]
        expected_attempt: Option<String>,
    },
    /// Download, authenticate and update the installer dependency and lock together.
    PinInstaller {
        #[arg(long)]
        version: String,
    },
    /// Build and package the native installer from a clean checkout.
    BuildInstaller {
        #[arg(long, default_value = ".artifacts/goblinctl")]
        output: PathBuf,
    },
    /// Check source contracts and compiler coverage without requiring publication.
    CheckInstaller {
        #[arg(
            long,
            default_value = "target/x86_64-unknown-linux-musl/release/goblinctl.d"
        )]
        depfile: PathBuf,
    },
    /// Verify the selected published installer and its actual install-request validator.
    VerifyInstaller {
        #[arg(
            long,
            default_value = "target/x86_64-unknown-linux-musl/release/goblinctl.d"
        )]
        depfile: PathBuf,
        #[arg(long, default_value = ".artifacts/goblinctl-check")]
        artifacts: PathBuf,
    },
}

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct Version {
    pub(crate) base: [u64; 3],
    pub(crate) preview: Option<u64>,
}
impl Version {
    pub(crate) fn parse(text: &str) -> Result<Self> {
        let (base, preview) = match text.split_once("-preview.") {
            Some((base, number)) => (base, Some(number)),
            None => (text, None),
        };
        release::validate_version(base)?;
        let numbers = base
            .split('.')
            .map(str::parse)
            .collect::<Result<Vec<u64>, _>>()?;
        let preview = preview
            .map(|number| {
                ensure!(
                    !number.is_empty()
                        && number.bytes().all(|b| b.is_ascii_digit())
                        && !number.starts_with('0'),
                    "Preview numbers start at 1"
                );
                Ok(number.parse::<u64>()?)
            })
            .transpose()?;
        Ok(Self {
            base: [numbers[0], numbers[1], numbers[2]],
            preview,
        })
    }
    pub(crate) fn text(&self) -> String {
        let [major, minor, patch] = self.base;
        let base = format!("{major}.{minor}.{patch}");
        match self.preview {
            Some(number) => format!("{base}-preview.{number}"),
            None => base,
        }
    }
}

pub(crate) fn next_version(tags: &[String], channel: Channel) -> Result<String> {
    let versions = tags
        .iter()
        .filter_map(|tag| tag.strip_prefix("goblin-v"))
        .map(Version::parse)
        .collect::<Result<Vec<_>>>()?;
    let mut base = versions.iter().map(|v| v.base).max().unwrap_or([0, 1, 0]);
    if versions
        .iter()
        .any(|v| v.base == base && v.preview.is_none())
    {
        base[2] = base[2].checked_add(1).context("Version overflow")?;
    }
    let preview = match channel {
        Channel::Stable => None,
        Channel::Preview => Some(
            versions
                .iter()
                .filter(|v| v.base == base)
                .filter_map(|v| v.preview)
                .max()
                .unwrap_or(0)
                .checked_add(1)
                .context("Preview overflow")?,
        ),
    };
    Ok(Version { base, preview }.text())
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Record {
    pub schema_version: u32,
    pub version: String,
    pub channel: Channel,
    pub source_revision: String,
    pub installer: release::Release,
    pub run_id: String,
    pub run_attempt: String,
    pub assets: BTreeMap<String, String>,
    pub deployment_checks: Check,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Check {
    Pending,
    Passed,
    Failed,
}

fn read<T: serde::de::DeserializeOwned>(path: &Path) -> Result<T> {
    Ok(serde_json::from_slice(
        &fs::read(path).with_context(|| path.display().to_string())?,
    )?)
}
#[cfg(test)]
fn write(path: &Path, value: &impl Serialize) -> Result<()> {
    files::write_json(path, &serde_json::to_value(value)?, 0o644)
}
pub(crate) fn output(name: &str, value: &str) -> Result<()> {
    ensure!(!value.contains(['\n', '\r']), "Invalid Actions output");
    if let Ok(path) = std::env::var(environment::GITHUB_OUTPUT) {
        writeln!(
            fs::OpenOptions::new().append(true).open(path)?,
            "{name}={value}"
        )?;
    }
    Ok(())
}
pub(crate) fn summary(message: &str) -> Result<()> {
    println!("{message}");
    if let Ok(path) = std::env::var(environment::GITHUB_STEP_SUMMARY) {
        writeln!(fs::OpenOptions::new().append(true).open(path)?, "{message}")?;
    }
    Ok(())
}
fn clean_source() -> Result<String> {
    ensure!(
        files::output(Command::new("git").args(["status", "--porcelain"]))?
            .trim()
            .is_empty(),
        "Commit release source before preparing or packaging"
    );
    let source = files::output(Command::new("git").args(["rev-parse", "HEAD"]))?
        .trim()
        .to_owned();
    files::run(Command::new("git").args([
        "merge-base",
        "--is-ancestor",
        &source,
        "origin/master",
    ]))?;
    Ok(source)
}
pub fn execute(root: &Path, task: Task) -> Result<()> {
    match task {
        Task::Prepare(options) => crate::preparation::execute(root, options),
        Task::CheckCandidate {
            directory,
            source_root,
        } => crate::candidate::check_source(&directory, &source_root),
        Task::AuthenticateInstaller { directory } => {
            release::verify_artifacts(release::GITHUB_REPOSITORY, &directory).map(|_| ())
        }
        Task::Seal {
            directory,
            expected_attempt,
        } => {
            let workflow = files::output(
                Command::new("git")
                    .current_dir(root)
                    .args(["rev-parse", "HEAD"]),
            )?;
            let record = crate::candidate::seal(
                &directory,
                &std::env::var(environment::GITHUB_RUN_ID)?,
                &expected_attempt,
                workflow.trim(),
            )?;
            summary(&format!(
                "```text\nGoblin {}\nInstaller: goblinctl {}\nDeployment checks: passed\nAzure installation: manual\nReady to publish\n```\n\nUse **Review deployments** to approve **Publish Goblin and goblinctl**. Approval publishes this exact pair. Source: `{}`.",
                record.version, record.installer.version, record.source_revision
            ))
        }
        Task::Verify {
            directory,
            expected_source,
            expected_run,
            expected_attempt,
        } => {
            let record = verify(&directory)?;
            for (expected, actual) in [
                (expected_source.as_deref(), record.source_revision.as_str()),
                (expected_run.as_deref(), record.run_id.as_str()),
                (expected_attempt.as_deref(), record.run_attempt.as_str()),
            ] {
                if let Some(expected) = expected {
                    ensure!(
                        expected == actual,
                        "Candidate does not belong to the selected source/run/attempt"
                    );
                }
            }
            Ok(())
        }
        Task::PinInstaller { version } => release::pin(root, release::GITHUB_REPOSITORY, &version),
        Task::BuildInstaller { output } => {
            clean_source()?;
            let target = tempfile::tempdir()?;
            files::run(
                Command::new("bash")
                    .arg("scripts/build-goblinctl-release.sh")
                    .arg(target.path()),
            )?;
            let binary = target
                .path()
                .join(release::TARGET)
                .join("release/goblinctl");
            release::candidate(root, &binary.with_extension("d"))?;
            crate::package(&binary, release::TARGET, &output)
        }
        Task::CheckInstaller { depfile } => release::candidate(root, &depfile),
        Task::VerifyInstaller { depfile, artifacts } => {
            fs::create_dir_all(&artifacts)?;
            release::check(
                root,
                release::GITHUB_REPOSITORY,
                &depfile,
                &artifacts,
                &artifacts.join("check.json"),
            )
        }
    }
}

pub(crate) fn validate_assets(directory: &Path, record: &Record) -> Result<()> {
    ensure!(
        record.schema_version == 1,
        "Unsupported Goblin release schema"
    );
    let version = Version::parse(&record.version)?;
    ensure!(
        version.preview.is_some() == (record.channel == Channel::Preview),
        "Release channel mismatch"
    );
    ensure!(
        record.source_revision.len() == 40
            && record
                .source_revision
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
        "Invalid Goblin source"
    );
    for id in [&record.run_id, &record.run_attempt] {
        ensure!(
            !id.is_empty() && id.bytes().all(|b| b.is_ascii_digit()),
            "Invalid workflow identity"
        );
    }
    release::validate(&record.installer)?;
    for name in record.assets.keys() {
        ensure!(
            azure::ASSETS.contains(&name.as_str()),
            "Unexpected release asset"
        );
    }
    for name in azure::ASSETS {
        ensure!(
            record.assets.get(name) == Some(&install::checksum(&directory.join(name))?),
            "Release asset changed: {name}"
        );
    }
    for name in &azure::ASSETS[..2] {
        let template = files::json(&directory.join(name))?;
        ensure!(
            template["parameters"]["goblinSourceRef"]["defaultValue"] == record.source_revision
                && template["parameters"]["goblinSourceRef"]["allowedValues"]
                    == json!([record.source_revision])
                && template["metadata"]["goblin"]
                    == json!({"version":record.version,"sourceRevision":record.source_revision,"installerVersion":record.installer.version,"installerSha256":record.installer.sha256}),
            "Template does not identify the release"
        );
    }
    let ui = files::json(&directory.join(azure::ASSETS[2]))?;
    let field = ui["parameters"]["basics"]
        .as_array()
        .context("Missing Azure form")?
        .iter()
        .find(|item| item["name"] == "goblinSourceRef")
        .context("Missing Goblin version")?;
    ensure!(
        field["defaultValue"] == record.version
            && field["constraints"]["allowedValues"]
                == json!([{"label":record.version,"value":record.source_revision}]),
        "Azure form selects another release"
    );
    Ok(())
}
#[cfg(test)]
fn seal(directory: &Path, run_id: &str, run_attempt: &str) -> Result<Record> {
    let mut record: Record = read(&directory.join("release.json"))?;
    validate_assets(directory, &record)?;
    ensure!(
        run_id == record.run_id && run_attempt == record.run_attempt,
        "Seal must run in the preparation attempt"
    );
    record.deployment_checks = Check::Passed;
    write(&directory.join("release.json"), &record)?;
    let mut checksums = record.assets;
    checksums.insert(
        "release.json".into(),
        install::checksum(&directory.join("release.json"))?,
    );
    fs::write(
        directory.join("SHA256SUMS"),
        checksums
            .iter()
            .map(|(name, hash)| format!("{hash}  {name}\n"))
            .collect::<String>(),
    )?;
    verify(directory)
}
pub fn verify(directory: &Path) -> Result<Record> {
    if crate::candidate::coordinated(directory)? {
        return crate::candidate::verify(directory);
    }
    let record: Record = read(&directory.join("release.json"))?;
    validate_assets(directory, &record)?;
    ensure!(
        record.deployment_checks == Check::Passed,
        "Candidate has not passed all required checks"
    );
    let mut checksums = record.assets.clone();
    checksums.insert(
        "release.json".into(),
        install::checksum(&directory.join("release.json"))?,
    );
    ensure!(
        fs::read_to_string(directory.join("SHA256SUMS"))?
            == checksums
                .iter()
                .map(|(name, hash)| format!("{hash}  {name}\n"))
                .collect::<String>(),
        "Checksum manifest changed"
    );
    Ok(record)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn independent_preview_and_stable_versions() {
        let mut tags = vec!["goblinctl-v99.4.3".into()];
        assert_eq!(
            next_version(&tags, Channel::Preview).unwrap(),
            "0.1.0-preview.1"
        );
        tags.push("goblin-v0.1.0-preview.9".into());
        assert_eq!(
            next_version(&tags, Channel::Preview).unwrap(),
            "0.1.0-preview.10"
        );
        assert_eq!(next_version(&tags, Channel::Stable).unwrap(), "0.1.0");
        tags.push("goblin-v0.1.0".into());
        assert_eq!(
            next_version(&tags, Channel::Preview).unwrap(),
            "0.1.1-preview.1"
        );
        tags.push("goblin-v0.2.0-preview.1".into());
        assert_eq!(next_version(&tags, Channel::Stable).unwrap(), "0.2.0");
    }
    #[test]
    fn rejects_ambiguous_or_unsafe_versions() {
        for text in [
            "1.0.0-preview.0",
            "1.0.0-preview.01",
            "1.0.0-preview.1/../../",
            "1.0.0-rc.1",
            "01.0.0",
            "1.0.0+build",
            "999999999999999999999999999999.0.0",
        ] {
            assert!(Version::parse(text).is_err(), "{text}");
        }
    }
    fn record_fixture() -> (tempfile::TempDir, Record) {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .parent()
            .unwrap();
        let directory = tempfile::tempdir().unwrap();
        let mut record = Record {
            schema_version: 1,
            version: "0.1.0-preview.2".into(),
            channel: Channel::Preview,
            source_revision: "a".repeat(40),
            installer: dependencies::installer(root).unwrap(),
            run_id: "123".into(),
            run_attempt: "1".into(),
            assets: BTreeMap::new(),
            deployment_checks: Check::Passed,
        };
        for name in &azure::ASSETS[..2] {
            write(&directory.path().join(name), &json!({
                "parameters": { "goblinSourceRef": { "defaultValue":record.source_revision, "allowedValues":[record.source_revision] } },
                "metadata": { "goblin": {"version":record.version,"sourceRevision":record.source_revision,"installerVersion":record.installer.version,"installerSha256":record.installer.sha256} }
            })).unwrap();
        }
        write(&directory.path().join(azure::ASSETS[2]), &json!({"parameters":{"basics":[{
            "name":"goblinSourceRef","defaultValue":record.version,"constraints":{"allowedValues":[{"label":record.version,"value":record.source_revision}]}
        }]}})).unwrap();
        for name in azure::ASSETS {
            record.assets.insert(
                name.into(),
                install::checksum(&directory.path().join(name)).unwrap(),
            );
        }
        save_fixture(directory.path(), &record);
        (directory, record)
    }
    fn save_fixture(directory: &Path, record: &Record) {
        write(&directory.join("release.json"), record).unwrap();
        let mut sums = record.assets.clone();
        sums.insert(
            "release.json".into(),
            install::checksum(&directory.join("release.json")).unwrap(),
        );
        fs::write(
            directory.join("SHA256SUMS"),
            sums.iter()
                .map(|(name, hash)| format!("{hash}  {name}\n"))
                .collect::<String>(),
        )
        .unwrap();
    }
    #[test]
    fn publication_requires_unchanged_assets_and_passed_deployment_checks() {
        let (directory, mut record) = record_fixture();
        verify(directory.path()).unwrap();
        for check in [Check::Pending, Check::Failed] {
            record.deployment_checks = check;
            save_fixture(directory.path(), &record);
            assert!(verify(directory.path()).is_err());
        }
        record.deployment_checks = Check::Passed;
        save_fixture(directory.path(), &record);
        verify(directory.path()).unwrap();
        fs::write(
            directory.path().join("azuredeploy.json"),
            "tampered template",
        )
        .unwrap();
        assert!(verify(directory.path()).is_err());
    }
    #[test]
    fn seal_requires_the_preparation_attempt_without_azure_evidence() {
        let (directory, mut record) = record_fixture();
        record.deployment_checks = Check::Pending;
        save_fixture(directory.path(), &record);
        assert!(seal(directory.path(), "124", "1").is_err());
        assert!(seal(directory.path(), "123", "2").is_err());
        assert!(verify(directory.path()).is_err());
        seal(directory.path(), "123", "1").unwrap();
        let verified = verify(directory.path()).unwrap();
        assert_eq!(verified.deployment_checks, Check::Passed);
        assert_eq!(verified.assets.len(), azure::ASSETS.len());
    }
    #[test]
    fn publication_rejects_missing_or_unexpected_assets_and_changed_checksums() {
        let (directory, mut record) = record_fixture();
        let hash = record.assets.remove("azuredeploy.json").unwrap();
        save_fixture(directory.path(), &record);
        assert!(verify(directory.path()).is_err());
        record.assets.insert("azuredeploy.json".into(), hash);
        record
            .assets
            .insert("unexpected.json".into(), "a".repeat(64));
        save_fixture(directory.path(), &record);
        assert!(verify(directory.path()).is_err());
        record.assets.remove("unexpected.json");
        save_fixture(directory.path(), &record);
        verify(directory.path()).unwrap();
        fs::write(directory.path().join("SHA256SUMS"), "changed").unwrap();
        assert!(verify(directory.path()).is_err());
    }
    #[test]
    fn even_rehashed_assets_must_select_the_recorded_source() {
        let (directory, mut record) = record_fixture();
        let path = directory.path().join("createUiDefinition.json");
        let mut ui = files::json(&path).unwrap();
        ui["parameters"]["basics"][0]["constraints"]["allowedValues"][0]["value"] =
            json!("b".repeat(40));
        write(&path, &ui).unwrap();
        record.assets.insert(
            "createUiDefinition.json".into(),
            install::checksum(&path).unwrap(),
        );
        save_fixture(directory.path(), &record);
        assert!(verify(directory.path()).is_err());
    }
}
