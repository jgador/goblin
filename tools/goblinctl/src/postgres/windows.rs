use super::Client;
use super::Role;
use super::Source;
use crate::environment;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use serde_json::json;
use std::fs;
use std::io::Write;
use std::process::Command;
use std::process::Stdio;

pub fn export(name: &str, client: &Client, role: Role) -> Result<()> {
    let port = client
        .profile
        .forward_port
        .context("Connect with --forward-port before exporting for Windows")?;
    // PowerShell resolves the actual Windows account and filesystem; no guessed /mnt/c path.
    let payload = json!({
        "profile":name, "role":role.name(), "port":port,
        "ca":fs::read_to_string(client.directory.join("ca.crt"))?,
        "certificate":fs::read_to_string(client.directory.join(role.name()).join("tls.crt"))?,
        "key":fs::read_to_string(client.directory.join(role.name()).join("tls.key"))?,
        "identity":client.profile.identity,
    });
    let mut child = Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", include_str!("windows-export.ps1")])
        .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
        .stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::null())
        .spawn().context("Windows PowerShell is unavailable; run the export inside WSL with Windows interop enabled")?;
    let written = child
        .stdin
        .take()
        .context("Missing Windows export input")?
        .write_all(&serde_json::to_vec(&payload)?);
    let output = child.wait_with_output()?;
    written?;
    ensure!(
        output.status.success(),
        "Windows export failed; check access to your Windows LocalAppData directory and its private file permissions"
    );
    let settings = String::from_utf8(output.stdout)?;
    let settings = settings.trim();
    let directory = settings
        .strip_suffix("connection.json")
        .context("Windows export did not return its connection settings path")?;
    println!("Windows connection settings: {settings}");
    println!("pgAdmin import: {directory}pgadmin.json");
    println!(
        "First use: pgAdmin > Tools > Import/Export Servers > Import; keep existing servers.\nAfter certificate refresh, reconnect the saved server. Import again only for a new profile or role; update its port if you change forwarding."
    );
    println!(
        "Windows host client: localhost:{port}, database goblin, user goblin_{}, sslmode verify-full",
        role.name()
    );
    if matches!(client.profile.source, Source::Local) {
        println!("Forwarding runs in WSL to its PostgreSQL host endpoint localhost:5432.");
    } else {
        println!(
            "Forwarding runs in WSL through SSH to Azure's PostgreSQL host endpoint localhost:5432."
        );
    }
    Ok(())
}
