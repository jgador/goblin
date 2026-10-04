//! Generate release assets without changing the source tree.
use anyhow::Result;
use anyhow::ensure;
use goblinctl::files;
use serde_json::json;
use std::fs;
use std::path::Path;
use std::process::Command;

pub const ASSETS: [&str; 3] = [
    "azuredeploy.json",
    "azuredeploy.portal.json",
    "createUiDefinition.json",
];

pub fn generate(root: &Path, output: &Path, version: &str, source: &str) -> Result<()> {
    let installer = crate::dependencies::installer(root)?;
    render(root, output, version, source, &installer)
}

/// Bind both bootstrap bytes and visible metadata to this candidate's installer.
/// Bicep still reads its usual lock path, but only in an isolated rendering tree.
pub fn generate_for_installer(
    root: &Path,
    output: &Path,
    version: &str,
    source: &str,
    installer: &crate::release::Release,
) -> Result<()> {
    crate::release::validate(installer)?;
    let scratch = root.join(".artifacts/release-assets");
    fs::create_dir_all(&scratch)?;
    let staging = tempfile::tempdir_in(scratch)?;
    copy_tree(
        &root.join("deploy/azure"),
        &staging.path().join("deploy/azure"),
    )?;
    let mut lock = files::json(&root.join("dependencies.lock.json"))?;
    lock["goblinctl"]["release"] = serde_json::to_value(installer)?;
    files::write_json(&staging.path().join("dependencies.lock.json"), &lock, 0o644)?;
    render(staging.path(), output, version, source, installer)
}

fn copy_tree(source: &Path, destination: &Path) -> Result<()> {
    fs::create_dir_all(destination)?;
    for entry in fs::read_dir(source)? {
        let entry = entry?;
        let kind = entry.file_type()?;
        ensure!(!kind.is_symlink(), "Symlink in Azure rendering inputs");
        let target = destination.join(entry.file_name());
        if kind.is_dir() {
            copy_tree(&entry.path(), &target)?;
        } else {
            fs::copy(entry.path(), target)?;
        }
    }
    Ok(())
}

fn render(
    root: &Path,
    output: &Path,
    version: &str,
    source: &str,
    installer: &crate::release::Release,
) -> Result<()> {
    ensure!(files::executable("bicep"), "Install the pinned Bicep CLI");
    ensure!(
        source.len() == 40 && source.bytes().all(|b| b.is_ascii_hexdigit()),
        "Expected a full source SHA"
    );
    fs::create_dir_all(output)?;
    for (input, name) in [("main.bicep", ASSETS[0]), ("portal.bicep", ASSETS[1])] {
        let path = output.join(name);
        files::run(
            Command::new("bicep")
                .arg("build")
                .arg(root.join("deploy/azure").join(input))
                .arg("--outfile")
                .arg(&path),
        )?;
        let mut template = files::json(&path)?;
        template["parameters"]["goblinSourceRef"]["defaultValue"] = json!(source);
        template["parameters"]["goblinSourceRef"]["allowedValues"] = json!([source]);
        template["metadata"]["goblin"] = json!({
            "version": version, "sourceRevision": source,
            "installerVersion": installer.version, "installerSha256": installer.sha256
        });
        files::write_json(&path, &template, 0o644)?;
    }
    let ui = fs::read_to_string(root.join("deploy/azure/createUiDefinition.json"))?
        .replace("__GOBLIN_VERSION__", version)
        .replace("__GOBLIN_SOURCE_SHA__", source);
    let ui: serde_json::Value = serde_json::from_str(&ui)?;
    files::write_json(&output.join(ASSETS[2]), &ui, 0o644)
}
