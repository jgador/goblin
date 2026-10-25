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
    for name in [
        "goblin-setup.service",
        "goblin-setup-control.socket",
        "goblin-setup-control@.service",
        "goblin-installer.service",
    ] {
        files::atomic_write(
            &root("etc/systemd/system").join(name),
            &fs::read(root("opt/goblin/setup").join(name))?,
            0o644,
            false,
        )?;
    }
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
    certificate_email: &str,
    version: &str,
    checksum: &str,
) -> Result<String> {
    let mut script = assets::BOOTSTRAP.to_owned();
    for (key, value) in [
        ("__GOBLIN_HOSTNAME_BASE64__", STANDARD.encode(hostname)),
        ("__GOBLIN_SOURCE_REF_BASE64__", STANDARD.encode(source_ref)),
        (
            "__GOBLIN_CERTIFICATE_EMAIL_BASE64__",
            STANDARD.encode(certificate_email),
        ),
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
pub fn bootstrap(hostname: &str, source_ref: &str, certificate_email: &str) -> Result<()> {
    files::require_root()?;
    validate_certificate_email(certificate_email)?;
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
    let script = render_bootstrap(
        hostname,
        source_ref,
        certificate_email,
        crate::VERSION,
        "local",
    )?;
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
    let invalid = "Invalid public origin. Use HTTPS with the configured hostname, or HTTP with localhost, and an optional port.";
    let authority = origin
        .strip_prefix("https://")
        .or_else(|| {
            (hostname == "localhost")
                .then(|| origin.strip_prefix("http://"))
                .flatten()
        })
        .context(invalid)?;
    let suffix = authority.strip_prefix(hostname).context(invalid)?;
    ensure!(
        hostname == "localhost" || suffix.is_empty(),
        "Public installation HTTPS uses the assigned hostname on port 443"
    );
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

pub fn validate_certificate_email(email: &str) -> Result<()> {
    let invalid = "Supply a certificate contact email for the public HTTPS installation.";
    let (local, domain) = email.split_once('@').context(invalid)?;
    ensure!(
        email.len() <= 254
            && !local.is_empty()
            && domain.contains('.')
            && local
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"._%+-".contains(&b))
            && domain
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b".-".contains(&b))
            && domain
                .split('.')
                .all(|label| !label.is_empty() && !label.starts_with('-') && !label.ends_with('-')),
        invalid
    );
    Ok(())
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
pub fn ingress_backend_patch(ingress: &Value, service: &str, port: u16) -> Result<Value> {
    ensure!(
        matches!(service, "platform" | "goblin-installation") && port == 8787,
        "Invalid installation upstream"
    );
    let rules = ingress["spec"]["rules"]
        .as_array()
        .context("Missing ingress rules")?;
    ensure!(
        rules.len() == 1,
        "Expected the installation's single-host ingress"
    );
    let paths = rules[0]["http"]["paths"]
        .as_array()
        .context("Missing ingress paths")?;
    let indexes: Vec<_> = paths
        .iter()
        .enumerate()
        .filter(|(_, path)| path["path"] == "/")
        .map(|(index, _)| index)
        .collect();
    ensure!(indexes.len() == 1, "Expected one default Goblin path");
    Ok(serde_json::json!([
        {"op":"test","path":"/metadata/resourceVersion","value":ingress["metadata"]["resourceVersion"].as_str().context("Missing ingress version")?},
        {"op":"replace","path":format!("/spec/rules/0/http/paths/{}/backend/service", indexes[0]),"value":{"name":service,"port":{"number":port}}}
    ]))
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
            let mut text = fs::read_to_string(&path)?
                .replace("__GOBLIN_PUBLIC_HOSTNAME__", hostname)
                .replace("__GOBLIN_IMAGE__", image)
                .replace("__GOBLIN_PUBLIC_ORIGIN__", origin);
            if origin.starts_with("http://") {
                text = text.replace(
                    "name: GOBLIN_CHATGPT_ENABLED\n          value: \"true\"",
                    "name: GOBLIN_CHATGPT_ENABLED\n          value: \"false\"",
                );
                if path.file_name().is_some_and(|name| name == "ingress.yaml") {
                    text = text
                        .lines()
                        .take_while(|line| *line != "  tls:")
                        .filter(|line| {
                            !line.contains("cert-manager.io/")
                                && !line.contains("acme.cert-manager.io/")
                        })
                        .map(|line| {
                            line.replace(
                                "nginx.org/ssl-redirect: \"true\"",
                                "nginx.org/ssl-redirect: \"false\"",
                            )
                        })
                        .collect::<Vec<_>>()
                        .join("\n")
                        + "\n";
                }
            }
            files::atomic_write(&path, text.as_bytes(), 0o640, false)?;
        }
    }
    Ok(())
}
