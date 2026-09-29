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
    ensure!(files::executable("bicep"), "Install the pinned Bicep CLI");
    ensure!(
        source.len() == 40 && source.bytes().all(|b| b.is_ascii_hexdigit()),
        "Expected a full source SHA"
    );
    let installer = crate::dependencies::installer(root)?;
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
