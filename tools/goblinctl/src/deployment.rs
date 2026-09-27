//! Code-defined installation input, shared by validation and deployment preparation.
use crate::files;
use crate::install;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use schemars::JsonSchema;
use serde::Deserialize;
use serde::Serialize;
use std::collections::BTreeSet;
use std::fs;
use std::path::Component;
use std::path::Path;

#[derive(Debug, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct InstallRequest {
    pub format: Format,
    pub resources: Vec<Resource>,
}

#[derive(Debug, Serialize, Deserialize, JsonSchema)]
pub enum Format {
    #[serde(rename = "goblin.install.v1")]
    V1,
}

#[derive(Debug, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct Resource {
    pub path: String,
    pub sha256: String,
}

impl InstallRequest {
    pub fn read(path: &Path) -> Result<Self> {
        serde_json::from_slice(&fs::read(path)?)
            .context("This goblinctl cannot read the installation request")
    }

    /// Validate every resource before any deployment directory is changed.
    pub fn validate(&self, source: &Path) -> Result<()> {
        match self.format {
            Format::V1 => {
                let mut seen = BTreeSet::new();
                for resource in &self.resources {
                    ensure!(
                        seen.insert(&resource.path),
                        "Duplicate installation resource: {}",
                        resource.path
                    );
                    let path = Path::new(&resource.path);
                    ensure!(
                        path.components()
                            .all(|part| matches!(part, Component::Normal(_)))
                            && (path.starts_with("deploy/auth")
                                || path.starts_with("deploy/azure/app")),
                        "Resource is outside application deployment: {}",
                        resource.path
                    );
                    ensure!(
                        path.extension().is_some_and(|v| v == "yaml" || v == "conf"),
                        "Unsupported installation resource: {}",
                        resource.path
                    );
                    let mut file = source.to_path_buf();
                    for component in path.components() {
                        file.push(component);
                        ensure!(
                            !fs::symlink_metadata(&file)?.file_type().is_symlink(),
                            "Installation resources cannot use symlinks"
                        );
                    }
                    ensure!(
                        file.is_file() && install::checksum(&file)? == resource.sha256,
                        "Installation resource is missing or changed: {}",
                        resource.path
                    );
                }
                for required in [
                    "deploy/auth/kustomization.yaml",
                    "deploy/auth/sandbox.yaml",
                    "deploy/azure/app/kustomization.yaml",
                ] {
                    ensure!(
                        seen.contains(&required.to_owned()),
                        "Missing required installation resource: {required}"
                    );
                }
            }
        }
        Ok(())
    }

    pub fn prepare(&self, source: &Path, destination: &Path) -> Result<()> {
        self.validate(source)?;
        for resource in &self.resources {
            let relative = Path::new(&resource.path).strip_prefix("deploy")?;
            let target = destination.join(relative);
            // An existing deployment cannot redirect writes outside its directory.
            let mut parent = destination.to_path_buf();
            files::directory(&parent, 0o750)?;
            for part in relative
                .parent()
                .context("Resource has no parent")?
                .components()
            {
                parent.push(part);
                if parent.exists() {
                    ensure!(
                        !fs::symlink_metadata(&parent)?.file_type().is_symlink(),
                        "Deployment directories cannot use symlinks"
                    );
                }
                files::directory(&parent, 0o750)?;
            }
            files::atomic_write(
                &target,
                &fs::read(source.join(&resource.path))?,
                0o640,
                false,
            )?;
        }
        Ok(())
    }
}

pub fn metadata() -> serde_json::Value {
    serde_json::json!({
        "version": env!("CARGO_PKG_VERSION"),
        "installRequest": schemars::schema_for!(InstallRequest)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_operations_and_fields_are_rejected_by_the_installer_parser() {
        for value in [
            serde_json::json!({"format": "goblin.install.v2", "resources": []}),
            serde_json::json!({"format": "goblin.install.v1", "resources": [], "newOption": true}),
            serde_json::json!({"format": "goblin.install.v1", "resources": [{"path": "a", "sha256": "b", "execute": true}]}),
        ] {
            assert!(serde_json::from_value::<InstallRequest>(value).is_err());
        }
    }

    #[test]
    fn traversal_and_resource_changes_fail_before_copying_any_files() {
        let source = tempfile::tempdir().unwrap();
        let destination = tempfile::tempdir().unwrap();
        let resource = Resource {
            path: "deploy/auth/../../outside.yaml".into(),
            sha256: "0".repeat(64),
        };
        let request = InstallRequest {
            format: Format::V1,
            resources: vec![resource],
        };
        assert!(request.prepare(source.path(), destination.path()).is_err());
        assert_eq!(fs::read_dir(destination.path()).unwrap().count(), 0);
        fs::create_dir_all(source.path().join("deploy/auth")).unwrap();
        fs::write(source.path().join("deploy/auth/test.yaml"), "changed").unwrap();
        let request = InstallRequest {
            format: Format::V1,
            resources: vec![Resource {
                path: "deploy/auth/test.yaml".into(),
                sha256: "0".repeat(64),
            }],
        };
        assert!(request.prepare(source.path(), destination.path()).is_err());
        assert_eq!(fs::read_dir(destination.path()).unwrap().count(), 0);
    }
}
