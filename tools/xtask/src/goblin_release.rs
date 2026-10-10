//! Coordinated release commands.
use crate::release;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Subcommand;
use clap::ValueEnum;
use goblinctl::environment;
use goblinctl::files;
use serde::Deserialize;
use serde::Serialize;
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
    /// Authenticate published installer assets.
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

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Check {
    Pending,
    Passed,
    Failed,
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
                record.version, record.installer.version, record.source.revision
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
                (expected_source.as_deref(), record.source.revision.as_str()),
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

pub(crate) use crate::candidate::verify;

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
}
