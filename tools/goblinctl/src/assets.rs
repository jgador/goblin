//! Explicit release allowlist: no settings, checkout credentials, or generated stores.
use crate::files;
use anyhow::Result;
use std::path::Path;

pub fn web(path: &str) -> Option<(&'static str, &'static [u8])> {
    Some(match path {
        "/" => (
            "text/html; charset=utf-8",
            include_bytes!("../../../deploy/azure/setup/index.html"),
        ),
        "/setup/app.js" => (
            "text/javascript; charset=utf-8",
            include_bytes!("../../../deploy/azure/setup/app.js"),
        ),
        "/setup/styles.css" => (
            "text/css; charset=utf-8",
            include_bytes!("../../../deploy/azure/setup/styles.css"),
        ),
        "/setup/icon.svg" => (
            "image/svg+xml",
            include_bytes!("../../../assets/branding/svg/icon-light.svg"),
        ),
        _ => return None,
    })
}
pub const BOOTSTRAP: &str = include_str!("../../../deploy/azure/bootstrap.sh");
pub const SETUP: &[(&str, &[u8])] = &[
    (
        "installer.sh",
        include_bytes!("../../../deploy/azure/setup/installer.sh"),
    ),
    (
        "install-app.sh",
        include_bytes!("../../../deploy/azure/install-app.sh"),
    ),
    (
        "sandbox-kustomization.yaml",
        include_bytes!("../../../deploy/azure/setup/sandbox-kustomization.yaml"),
    ),
    (
        "goblin-setup.service",
        include_bytes!("../../../deploy/azure/setup/goblin-setup.service"),
    ),
    (
        "goblin-installer.service",
        include_bytes!("../../../deploy/azure/setup/goblin-installer.service"),
    ),
];
pub const DATABASE: &[(&str, &[u8])] = &[
    (
        "deploy/postgres/setup.sh",
        include_bytes!("../../../deploy/postgres/setup.sh"),
    ),
    (
        "deploy/postgres/migrate.sh",
        include_bytes!("../../../deploy/postgres/migrate.sh"),
    ),
    (
        "deploy/postgres/ca.yaml",
        include_bytes!("../../../deploy/postgres/ca.yaml"),
    ),
    (
        "deploy/postgres/certificates.yaml",
        include_bytes!("../../../deploy/postgres/certificates.yaml"),
    ),
    (
        "deploy/postgres/init-database.sh",
        include_bytes!("../../../deploy/postgres/init-database.sh"),
    ),
    (
        "deploy/postgres/kustomization.yaml",
        include_bytes!("../../../deploy/postgres/kustomization.yaml"),
    ),
    (
        "deploy/postgres/pg_hba.conf",
        include_bytes!("../../../deploy/postgres/pg_hba.conf"),
    ),
    (
        "deploy/postgres/pg_ident.conf",
        include_bytes!("../../../deploy/postgres/pg_ident.conf"),
    ),
    (
        "deploy/postgres/postgres.yaml",
        include_bytes!("../../../deploy/postgres/postgres.yaml"),
    ),
    (
        "deploy/postgres/start-postgres.sh",
        include_bytes!("../../../deploy/postgres/start-postgres.sh"),
    ),
    (
        "deploy/postgres/verify.yaml",
        include_bytes!("../../../deploy/postgres/verify.yaml"),
    ),
];
pub const POSTGRES_SERVICE: &[u8] = include_bytes!("../../../deploy/local/postgres-service.yaml");
pub fn unpack(destination: &Path, assets: &[(&str, &[u8])]) -> Result<()> {
    for (name, bytes) in assets {
        files::atomic_write(
            &destination.join(name),
            bytes,
            if name.ends_with(".sh") { 0o755 } else { 0o644 },
            false,
        )?;
    }
    Ok(())
}
