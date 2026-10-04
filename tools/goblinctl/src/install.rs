use crate::assets;
use crate::environment;
use crate::files;

/// The caller stops the old worker/UI and holds installer.lock through activation.
pub fn activate_tooling(system_root: &Path, binary: &Path) -> Result<()> {
    let root = |path: &str| system_root.join(path.trim_start_matches('/'));
    let digest = checksum(binary)?;
    let release = root(&format!("opt/goblin/releases/{}-{digest}", crate::VERSION));
    for directory in [
        root("opt/goblin"),
        root("opt/goblin/releases"),
        release.clone(),
        root("opt/goblin/bin"),
        root("opt/goblin/setup"),
        root("opt/goblin/share"),
        root("opt/goblin/share/licenses"),
        root("opt/goblin/share/licenses/goblinctl"),
        root("usr/local/bin"),
    ] {
        files::directory(&directory, 0o755)?;
    }
    let installed = release.join("goblinctl");
    if installed.exists() {
        ensure!(
            checksum(&installed)? == digest,
            "Existing native release is corrupt"
        );
    } else {
        files::atomic_write(&installed, &fs::read(binary)?, 0o755, false)?;
    }
    let next = root("opt/goblin/bin/goblinctl.next");
    files::remove_file(&next)?;
    std::os::unix::fs::symlink(&installed, &next)?;
    fs::rename(&next, root("opt/goblin/bin/goblinctl"))?;
    let command = root("usr/local/bin/goblinctl");
    files::remove_file(&command)?;
    std::os::unix::fs::symlink(root("opt/goblin/bin/goblinctl"), &command)?;
    assets::unpack(&root("opt/goblin/setup"), assets::SETUP)?;
    assets::unpack(
        &root("opt/goblin/share/licenses/goblinctl"),
        assets::LICENSING,
    )?;
    for name in ["goblin-setup.service", "goblin-installer.service"] {
        files::atomic_write(
            &root("etc/systemd/system").join(name),
            &fs::read(root("opt/goblin/setup").join(name))?,
            0o644,
            false,
        )?;
    }
    // Only obsolete executables are removed; retained source, state and credentials stay.
    files::remove_file(&root("opt/goblin/setup/goblin-setup.pyz"))?;
    Ok(())
}
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use serde_json::Value;
use sha2::Digest;
use sha2::Sha256;
use std::fs;
use std::path::Path;
use std::process::Command;

pub fn render_bootstrap(
    hostname: &str,
    source_ref: &str,
    version: &str,
    checksum: &str,
) -> Result<String> {
    let mut script = assets::BOOTSTRAP.to_owned();
    for (key, value) in [
        ("__GOBLIN_HOSTNAME_BASE64__", STANDARD.encode(hostname)),
        ("__GOBLIN_SOURCE_REF_BASE64__", STANDARD.encode(source_ref)),
        ("__GOBLIN_PASSWORD_BASE64__", String::new()),
        ("__GOBLINCTL_VERSION__", version.to_owned()),
        ("__GOBLINCTL_SHA256__", checksum.to_owned()),
    ] {
        ensure!(
            script.matches(key).count() == 1,
            "Unexpected bootstrap placeholder: {key}"
        );
        script = script.replace(key, &value);
    }
    Ok(script)
}
pub fn bootstrap(hostname: &str, source_ref: &str) -> Result<()> {
    files::require_root()?;
    let binary = std::env::current_exe()?;
    let temporary_credentials = tempfile::tempdir()?;
    let password_path = match std::env::var_os(environment::GOBLIN_PASSWORD_HASH_FILE) {
        Some(p) => {
            let p = std::path::PathBuf::from(p);
            crate::credentials::read(&p)?;
            p
        }
        None => {
            crate::credentials::ensure(&temporary_credentials.path().join("owner-password"), false)?
        }
    };
    let script = render_bootstrap(hostname, source_ref, crate::VERSION, "local")?;
    files::input(
        Command::new("bash")
            .env(environment::GOBLIN_PASSWORD_HASH_FILE, password_path)
            .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
            .env(environment::GOBLINCTL_LOCAL_BINARY, &binary)
            .env(environment::GOBLINCTL_LOCAL_SHA256, checksum(&binary)?),
        script.as_bytes(),
    )
}
pub fn checksum(path: &Path) -> Result<String> {
    Ok(format!("{:x}", Sha256::digest(fs::read(path)?)))
}
pub fn origin_authority(origin: &str, hostname: &str) -> Result<String> {
    let invalid = "Invalid public origin. Use the configured HTTP hostname and an optional port.";
    let authority = origin.strip_prefix("http://").context(invalid)?;
    let suffix = authority.strip_prefix(hostname).context(invalid)?;
    if !suffix.is_empty() {
        let port_text = suffix.strip_prefix(':').context(invalid)?;
        let port: u16 = port_text.parse().context(invalid)?;
        ensure!(port > 0 && port.to_string() == port_text, invalid);
    }
    ensure!(
        !hostname.is_empty()
            && hostname
                .bytes()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'.' || b == b'-'),
        invalid
    );
    Ok(authority.to_owned())
}
pub fn node_address(nodes: &Value) -> Result<String> {
    for item in nodes
        .pointer("/items/0/status/addresses")
        .and_then(Value::as_array)
        .context("Missing Kubernetes node addresses")?
    {
        if item["type"] == "InternalIP"
            && let Some(address) = item["address"].as_str()
            && let Ok(address) = address.parse::<std::net::Ipv4Addr>()
        {
            return Ok(address.to_string());
        }
    }
    anyhow::bail!("The Kubernetes node has no IPv4 InternalIP for the Azure public route.")
}
pub fn docker_config(path: &Path) -> Result<()> {
    let mut config = if path.exists() {
        files::json(path)?
    } else {
        serde_json::json!({})
    };
    ensure!(config.is_object(), "Invalid Docker configuration");
    config["ip-forward-no-drop"] = serde_json::json!(true);
    files::write_json(path, &config, 0o644)
}
pub fn render_overlay(path: &Path, hostname: &str, image: &str, origin: &str) -> Result<()> {
    origin_authority(origin, hostname)?;
    for entry in fs::read_dir(path)? {
        let path = entry?.path();
        if path.extension().is_some_and(|s| s == "yaml") {
            let text = fs::read_to_string(&path)?
                .replace("__GOBLIN_PUBLIC_HOSTNAME__", hostname)
                .replace("__GOBLIN_IMAGE__", image)
                .replace(&format!("http://{hostname}"), origin);
            files::atomic_write(&path, text.as_bytes(), 0o640, false)?;
        }
    }
    Ok(())
}
