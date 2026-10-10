//! Forwarders are owned by a durable record independent of successful TLS setup.
use super::Profile;
use super::Source;
use super::validate_port;
use super::validate_source;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use serde::Deserialize;
use serde::Serialize;
use sha2::Digest;
use sha2::Sha256;
use std::net::TcpListener;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use std::process::Stdio;

#[derive(Clone, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
struct Forward {
    source: Source,
    port: u16,
}
fn record(directory: &Path) -> PathBuf {
    directory.join("forward.json")
}
fn read(directory: &Path) -> Result<Option<Forward>> {
    let path = record(directory);
    files::reject_symlinks(&path)?;
    if !path.exists() {
        return Ok(None);
    }
    let value: Forward = serde_json::from_value(files::json(&path)?)?;
    validate_source(&value.source)?;
    validate_port(Some(value.port))?;
    Ok(Some(value))
}
fn save(directory: &Path, forward: &Forward) -> Result<()> {
    files::write_json(&record(directory), &serde_json::to_value(forward)?, 0o600)
}
fn unit(directory: &Path) -> String {
    format!(
        "goblin-db-{:x}",
        Sha256::digest(directory.as_os_str().as_encoded_bytes())
    )
}
fn control(directory: &Path) -> Result<PathBuf> {
    let root = directory
        .parent()
        .context("Missing profile root")?
        .join(".ssh");
    files::directory(&root, 0o700)?;
    let path = root.join(&unit(directory)[..26]);
    files::reject_symlinks(&path)?;
    ensure!(
        path.as_os_str().as_encoded_bytes().len() < 100,
        "The profile directory is too long for an SSH control socket; use a shorter XDG_CONFIG_HOME"
    );
    Ok(path)
}
fn ssh_check(directory: &Path, destination: &str) -> Result<bool> {
    Ok(Command::new("ssh")
        .arg("-S")
        .arg(control(directory)?)
        .args(["-o", "BatchMode=yes", "-O", "check", "--", destination])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()?
        .success())
}
fn loaded(name: &str) -> Result<bool> {
    let result = Command::new("systemctl")
        .args(["--user", "show", "--property=LoadState", "--value", name])
        .output()?;
    let state = String::from_utf8(result.stdout)?;
    if state.trim() == "not-found" {
        return Ok(false);
    }
    ensure!(
        result.status.success() && state.trim() == "loaded",
        "Cannot inspect the WSL forwarding unit; check your systemd user session"
    );
    Ok(true)
}
fn local_units(directory: &Path, expected: Option<&Forward>) -> Result<Vec<String>> {
    let mut units = Vec::new();
    for kind in ["socket", "service"] {
        let name = format!("{}.{kind}", unit(directory));
        if loaded(&name)? {
            let expected = expected.context("A forwarding unit exists without this profile's ownership record; refusing to change it")?;
            let property = if kind == "socket" {
                "Listen"
            } else {
                "ExecStart"
            };
            let value = files::output(Command::new("systemctl").args([
                "--user",
                "show",
                &format!("--property={property}"),
                "--value",
                &name,
            ]))?;
            if kind == "socket" {
                let addresses = value.replace("(Stream)", "");
                let addresses: Vec<_> = addresses.split_whitespace().collect();
                ensure!(
                    addresses.len() == 2
                        && addresses.contains(&format!("127.0.0.1:{}", expected.port).as_str())
                        && addresses.contains(&format!("[::1]:{}", expected.port).as_str()),
                    "The profile's forwarding socket has unexpected listeners; refusing to change it"
                );
            } else {
                ensure!(
                    value
                        .contains("argv[]=/usr/lib/systemd/systemd-socket-proxyd 127.0.0.1:5432 ;"),
                    "The profile's forwarding service has an unexpected destination; refusing to change it"
                );
            }
            units.push(name);
        }
    }
    Ok(units)
}
fn running(directory: &Path, forward: &Forward) -> Result<bool> {
    match &forward.source {
        Source::Local => {
            local_units(directory, Some(forward))?;
            Ok(Command::new("systemctl")
                .args([
                    "--user",
                    "is-active",
                    "--quiet",
                    &format!("{}.socket", unit(directory)),
                ])
                .status()?
                .success())
        }
        Source::Ssh { destination } => ssh_check(directory, destination),
    }
}
fn stop(directory: &Path, forward: &Forward) -> Result<()> {
    match &forward.source {
        Source::Local => {
            for name in local_units(directory, Some(forward))? {
                files::run(Command::new("systemctl").args(["--user", "stop", &name]))?;
            }
        }
        Source::Ssh { destination } => {
            if ssh_check(directory, destination)? {
                files::run(
                    Command::new("ssh")
                        .arg("-S")
                        .arg(control(directory)?)
                        .args(["-o", "BatchMode=yes", "-O", "exit", "--", destination]),
                )?;
            }
            files::remove_file(&control(directory)?)?;
        }
    }
    Ok(())
}
fn check_port(port: u16) -> Result<()> {
    for address in ["127.0.0.1", "::1"] {
        TcpListener::bind((address, port)).with_context(|| format!("WSL forwarding port {port} is occupied. Choose an explicit --forward-port; it will be saved in this profile"))?;
    }
    Ok(())
}
fn start(directory: &Path, forward: &Forward) -> Result<()> {
    match &forward.source {
        Source::Local => files::run(Command::new("systemd-run").args([
            "--user",
            "--quiet",
            "--collect",
            &format!("--unit={}", unit(directory)),
            &format!("--socket-property=ListenStream=127.0.0.1:{}", forward.port),
            &format!("--socket-property=ListenStream=[::1]:{}", forward.port),
            "--socket-property=BindIPv6Only=ipv6-only",
            "--property=NoNewPrivileges=yes",
            "/usr/lib/systemd/systemd-socket-proxyd",
            "127.0.0.1:5432",
        ]))
        .context("Cannot start the WSL listener; check your systemd user session"),
        Source::Ssh { destination } => {
            files::remove_file(&control(directory)?)?;
            files::run(
                Command::new("ssh")
                    .args(["-M", "-S"])
                    .arg(control(directory)?)
                    .args([
                        "-fNT",
                        "-o",
                        "BatchMode=yes",
                        "-o",
                        "ConnectTimeout=10",
                        "-o",
                        "ExitOnForwardFailure=yes",
                        "-o",
                        "ControlPersist=no",
                        "-o",
                        "ServerAliveInterval=30",
                        "-o",
                        "ServerAliveCountMax=3",
                        "-L",
                        &format!("127.0.0.1:{}:127.0.0.1:5432", forward.port),
                        "-L",
                        &format!("[::1]:{}:127.0.0.1:5432", forward.port),
                        "--",
                        destination,
                    ]),
            )
        }
    }
}
/// An unsuccessful TLS check restores the prior forwarder. A crashed process leaves
/// forward.json, so disconnect and the next connect can reconcile its owned listener.
pub struct Connection {
    directory: PathBuf,
    change: Option<(Forward, Option<Forward>, bool)>,
}
impl Connection {
    pub fn commit(mut self) {
        self.change = None;
    }
}
impl Drop for Connection {
    fn drop(&mut self) {
        if let Some((current, previous, was_running)) = self.change.take() {
            let restore = || -> Result<()> {
                stop(&self.directory, &current)?;
                if let Some(previous) = &previous {
                    save(&self.directory, previous)?;
                    if was_running {
                        start(&self.directory, previous)?;
                    }
                } else {
                    files::remove_file(&record(&self.directory))?;
                }
                Ok(())
            };
            if restore().is_err() {
                eprintln!(
                    "Could not restore the previous forwarding state; run goblinctl db disconnect for this profile, then reconnect"
                );
            }
        }
    }
}
pub fn connect(directory: &Path, profile: &Profile) -> Result<Connection> {
    let Some(port) = profile.forward_port else {
        return Ok(Connection {
            directory: directory.into(),
            change: None,
        });
    };
    let current = Forward {
        source: profile.source.clone(),
        port,
    };
    let previous = read(directory)?;
    let was_running = previous
        .as_ref()
        .map(|p| running(directory, p))
        .transpose()?
        .unwrap_or(false);
    if previous.as_ref() == Some(&current) && was_running {
        return Ok(Connection {
            directory: directory.into(),
            change: None,
        });
    }
    if previous.is_none() {
        match &current.source {
            Source::Local => {
                local_units(directory, None)?;
            }
            Source::Ssh { destination } => ensure!(
                !ssh_check(directory, destination)?,
                "An SSH master exists without this profile's ownership record; refusing to reuse it"
            ),
        }
    }
    check_port(port)?;
    // Write the new ownership record before starting anything. Preserve the old
    // working listener until all read-only preflight checks have passed.
    if let Some(previous) = &previous {
        stop(directory, previous)?;
    }
    let guard = Connection {
        directory: directory.into(),
        change: Some((current.clone(), previous, was_running)),
    };
    save(directory, &current)?;
    start(directory, &current)?;
    Ok(guard)
}
pub fn disconnect(directory: &Path) -> Result<()> {
    if let Some(forward) = read(directory)? {
        stop(directory, &forward)?;
    }
    println!("Profile forwarding stopped; connection settings and certificates retained.");
    Ok(())
}
