//! The coordinated candidate is immutable except for sealing successful verification.
use crate::azure;
use crate::goblin_release::Channel;
use crate::goblin_release::Check;
use crate::goblin_release::Version;
use crate::release;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use goblinctl::files;
use goblinctl::install;
use serde::Deserialize;
use serde::Serialize;
use serde_json::json;
use std::collections::BTreeMap;
use std::fs;
use std::path::Path;
use std::process::Command;

#[cfg(test)]
mod tests;

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Source {
    pub branch: String,
    pub revision: String,
    pub branch_tip: String,
    pub check_run_id: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub(crate) enum InstallerOrigin {
    Published,
    Built,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Candidate {
    pub schema_version: u32,
    pub version: String,
    pub channel: Channel,
    pub source: Source,
    pub workflow_revision: String,
    pub installer: release::Release,
    pub installer_origin: InstallerOrigin,
    pub changed_installer_inputs: Vec<String>,
    pub run_id: String,
    pub run_attempt: String,
    pub assets: BTreeMap<String, String>,
    pub deployment_checks: Check,
}

fn checksums(directory: &Path, candidate: &Candidate) -> Result<String> {
    let mut sums = candidate.assets.clone();
    sums.insert(
        "release.json".into(),
        install::checksum(&directory.join("release.json"))?,
    );
    Ok(sums
        .iter()
        .map(|(name, hash)| format!("{hash}  {name}\n"))
        .collect())
}

pub(crate) fn validate(directory: &Path) -> Result<Candidate> {
    for name in ["release.json", "SHA256SUMS"] {
        ensure!(
            fs::symlink_metadata(directory.join(name))?
                .file_type()
                .is_file(),
            "Candidate metadata is not a regular file"
        );
    }
    ensure!(
        fs::symlink_metadata(directory.join("installer"))?
            .file_type()
            .is_dir(),
        "Installer directory must not be a symlink"
    );
    let candidate: Candidate =
        serde_json::from_value(files::json(&directory.join("release.json"))?)?;
    ensure!(
        candidate.schema_version == 2,
        "Expected coordinated candidate schema 2"
    );
    let version = Version::parse(&candidate.version)?;
    ensure!(
        version.preview.is_some() == (candidate.channel == Channel::Preview),
        "Release channel mismatch"
    );
    for id in [&candidate.run_id, &candidate.run_attempt] {
        ensure!(
            !id.is_empty() && id.bytes().all(|b| b.is_ascii_digit()),
            "Invalid workflow identity"
        );
    }
    let line = crate::preparation::line(&candidate.source.branch)?;
    ensure!(
        Version::parse(&candidate.version)?.base[..2] == line,
        "Candidate version belongs to another release line"
    );
    for revision in [
        &candidate.source.revision,
        &candidate.source.branch_tip,
        &candidate.workflow_revision,
    ] {
        ensure!(
            revision.len() == 40
                && revision
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)),
            "Invalid candidate source/tooling revision"
        );
    }
    ensure!(
        candidate.source.check_run_id > 0,
        "Missing source check identity"
    );
    let mut names: Vec<String> = azure::ASSETS
        .iter()
        .map(|name| (*name).to_owned())
        .collect();
    names.extend(
        [release::ARCHIVE, "release.json", "SHA256SUMS"].map(|name| format!("installer/{name}")),
    );
    ensure!(
        candidate.assets.len() == names.len()
            && names.iter().all(|name| candidate.assets.contains_key(name)),
        "Unexpected or missing candidate asset"
    );
    for name in names {
        let path = directory.join(&name);
        ensure!(
            fs::symlink_metadata(&path)?.file_type().is_file(),
            "Candidate asset is not a regular file: {name}"
        );
        ensure!(
            candidate.assets[&name] == install::checksum(&path)?,
            "Candidate asset changed: {name}"
        );
    }
    for name in &azure::ASSETS[..2] {
        let template = files::json(&directory.join(name))?;
        ensure!(
            template["parameters"]["goblinSourceRef"]["defaultValue"] == candidate.source.revision
                && template["parameters"]["goblinSourceRef"]["allowedValues"]
                    == json!([candidate.source.revision])
                && template["metadata"]["goblin"]
                    == json!({"version":candidate.version,"sourceRevision":candidate.source.revision,"installerVersion":candidate.installer.version,"installerSha256":candidate.installer.sha256}),
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
        field["defaultValue"] == candidate.version
            && field["constraints"]["allowedValues"]
                == json!([{"label":candidate.version,"value":candidate.source.revision}]),
        "Azure form selects another release"
    );
    ensure!(
        release::verify_archive(&directory.join("installer"))? == candidate.installer,
        "Candidate selects a different installer manifest"
    );
    match candidate.installer_origin {
        InstallerOrigin::Built => ensure!(
            candidate.installer.source_revision == candidate.source.revision,
            "New installer was built from another source"
        ),
        InstallerOrigin::Published => {}
    }
    ensure!(
        fs::read_to_string(directory.join("SHA256SUMS"))? == checksums(directory, &candidate)?,
        "Candidate checksum manifest changed"
    );
    Ok(candidate)
}

/// Execute only in the read-only verification job, before granting signing rights.
pub(crate) fn check_source(directory: &Path, source: &Path) -> Result<()> {
    let candidate = validate(directory)?;
    let revision = files::output(
        Command::new("git")
            .current_dir(source)
            .args(["rev-parse", "HEAD"]),
    )?;
    ensure!(
        revision.trim() == candidate.source.revision,
        "Verification checkout is not the frozen source"
    );
    ensure!(
        files::output(
            Command::new("git")
                .current_dir(source)
                .args(["status", "--porcelain"])
        )?
        .trim()
        .is_empty(),
        "Verification source must be clean"
    );
    let snapshot = release::inputs(source)?;
    let required = release::capabilities(&source.join("deploy/goblinctl-requirements.json"))?;
    ensure!(
        release::compare(&snapshot, &candidate.installer.installer, &required).outcome
            == release::Outcome::Ready,
        "Installer does not match the frozen branch's shipping inputs and capabilities"
    );
    crate::preparation::validate_executable(
        source,
        &directory.join("installer"),
        &candidate.installer.version,
    )?;
    let scratch = source.join(".artifacts/candidate-verification");
    fs::create_dir_all(&scratch)?;
    let rendered = tempfile::tempdir_in(scratch)?;
    azure::generate_for_installer(
        source,
        rendered.path(),
        &candidate.version,
        &candidate.source.revision,
        &candidate.installer,
    )?;
    for name in azure::ASSETS {
        ensure!(
            install::checksum(&rendered.path().join(name))? == candidate.assets[name],
            "Prepared Azure asset differs from frozen source: {name}"
        );
    }
    Ok(())
}

pub(crate) fn seal(
    directory: &Path,
    run: &str,
    attempt: &str,
    workflow: &str,
) -> Result<Candidate> {
    let mut candidate = validate(directory)?;
    ensure!(
        candidate.run_id == run
            && candidate.run_attempt == attempt
            && candidate.workflow_revision == workflow,
        "Seal does not belong to the prepared run, attempt, and tooling revision"
    );
    ensure!(
        candidate.deployment_checks == Check::Pending,
        "Only an unsealed candidate can be sealed"
    );
    candidate.deployment_checks = Check::Passed;
    files::write_json(
        &directory.join("release.json"),
        &serde_json::to_value(&candidate)?,
        0o644,
    )?;
    fs::write(
        directory.join("SHA256SUMS"),
        checksums(directory, &candidate)?,
    )?;
    verify(directory)
}

pub(crate) fn verify(directory: &Path) -> Result<Candidate> {
    let candidate = validate(directory)?;
    ensure!(
        candidate.deployment_checks == Check::Passed,
        "Candidate has not passed all required checks"
    );
    Ok(candidate)
}
