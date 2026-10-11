//! The installation's loopback endpoint is always 5432, on WSL and Azure.
use crate::assets;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use fs2::FileExt;
use serde::Deserialize;
use serde::Serialize;
use serde_json::Value;
use serde_json::json;
use std::fs;
use std::net::Ipv4Addr;
use std::net::TcpListener;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use std::process::Stdio;

// Shared host units are also controlled by local stop/resume.
const SOCKET: &str = "goblin-local-postgres.socket";
const SERVICE: &str = "goblin-local-postgres.service";
pub const KUBECONFIG: &str = "/etc/rancher/k3s/k3s.yaml";

pub fn kubectl() -> Command {
    let mut command = Command::new("k3s");
    command.args(["kubectl", "--kubeconfig", KUBECONFIG]);
    command
}
fn unit(system: &Path, name: &str) -> PathBuf {
    system.join("etc/systemd/system").join(name)
}
fn state(system: &Path) -> PathBuf {
    system.join("var/lib/goblin/postgres")
}
fn active() -> Result<bool> {
    Ok(Command::new("systemctl")
        .args(["is-active", "--quiet", SOCKET])
        .status()?
        .success())
}
#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Previous {
    socket: Option<String>,
    service: Option<String>,
    marker: Option<Value>,
    active: bool,
    enabled: bool,
}
fn read_unit(path: &Path) -> Result<Option<String>> {
    files::reject_symlinks(path)?;
    if path.exists() {
        Ok(Some(fs::read_to_string(path)?))
    } else {
        Ok(None)
    }
}
fn restore(system: &Path, previous: &Previous) -> Result<()> {
    for name in [SOCKET, SERVICE] {
        if unit(system, name).exists() {
            files::run(Command::new("systemctl").args(["stop", name]))?;
        }
    }
    if !previous.enabled && unit(system, SOCKET).exists() {
        files::run(Command::new("systemctl").args(["disable", SOCKET]))?;
    }
    for (name, contents) in [(SOCKET, &previous.socket), (SERVICE, &previous.service)] {
        if let Some(contents) = contents {
            files::atomic_write(&unit(system, name), contents.as_bytes(), 0o644, false)?;
        } else {
            files::remove_file(&unit(system, name))?;
        }
    }
    let marker = state(system).join("host.json");
    if let Some(value) = &previous.marker {
        files::write_json(&marker, value, 0o600)?;
    } else {
        files::remove_file(&marker)?;
    }
    files::run(Command::new("systemctl").arg("daemon-reload"))?;
    if previous.enabled {
        files::run(Command::new("systemctl").args(["enable", SOCKET]))?;
    }
    if previous.active {
        files::run(Command::new("systemctl").args(["start", SOCKET]))?;
    }
    files::remove_file(&state(system).join("pending.json"))
}
pub fn configure(system: &Path) -> Result<()> {
    configure_with_probe(system, || {
        for address in ["127.0.0.1", "::1"] {
            TcpListener::bind((address, 5432)).context("PostgreSQL host port 5432 is occupied; stop that listener before configuring Goblin")?;
        }
        Ok(())
    })
}

/// Isolated filesystem fixtures supply a port probe without binding an installed database.
pub fn configure_with_probe(system: &Path, probe: impl Fn() -> Result<()>) -> Result<()> {
    let directory = state(system);
    files::directory(&directory, 0o700)?;
    let lock_path = directory.join(".lock");
    files::reject_symlinks(&lock_path)?;
    let lock = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .mode(0o600)
        .open(lock_path)?;
    lock.lock_exclusive()?;
    let marker = directory.join("host.json");
    let pending = directory.join("pending.json");
    for path in [&marker, &pending] {
        files::reject_symlinks(path)?;
    }
    if pending.exists() {
        let previous: Previous = serde_json::from_value(files::json(&pending)?)?;
        restore(system, &previous)
            .context("Cannot recover interrupted host forwarding configuration")?;
    }
    let marker_value = if marker.exists() {
        Some(files::json(&marker)?)
    } else {
        None
    };
    if let Some(value) = &marker_value {
        ensure!(
            value == &json!({"port":5432}),
            "Unsupported PostgreSQL host state; recreate the development installation"
        );
    }
    let previous = Previous {
        socket: read_unit(&unit(system, SOCKET))?,
        service: read_unit(&unit(system, SERVICE))?,
        marker: marker_value,
        active: active()?,
        enabled: Command::new("systemctl")
            .args(["is-enabled", "--quiet", SOCKET])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()?
            .success(),
    };
    ensure!(
        previous.marker.is_some() || (previous.socket.is_none() && previous.service.is_none()),
        "PostgreSQL forwarding units exist without an owner; inspect them before configuring host access"
    );
    if previous.marker.is_none() || !previous.active {
        probe()?;
    }
    files::input(
        kubectl().args(["apply", "-f", "-"]),
        assets::POSTGRES_SERVICE,
    )?;
    let address: Ipv4Addr = files::output(kubectl().args([
        "get",
        "service",
        "goblin-postgres-local",
        "-n",
        "goblin",
        "-o",
        "jsonpath={.spec.clusterIP}",
    ]))?
    .trim()
    .parse()
    .context("PostgreSQL has no usable cluster address")?;
    let socket = "[Unit]\nDescription=Goblin PostgreSQL host access\n[Socket]\nListenStream=127.0.0.1:5432\nListenStream=[::1]:5432\nBindIPv6Only=ipv6-only\n[Install]\nWantedBy=sockets.target\n";
    let service = format!(
        "[Unit]\nDescription=Forward host PostgreSQL clients into Kubernetes\nRequires={SOCKET} k3s.service\nAfter=network.target k3s.service\n[Service]\nExecStart=/usr/lib/systemd/systemd-socket-proxyd {address}:5432\nDynamicUser=yes\nNoNewPrivileges=yes\nProtectSystem=strict\nProtectHome=yes\nPrivateTmp=yes\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX\n"
    );
    let changed =
        previous.socket.as_deref() != Some(socket) || previous.service.as_deref() != Some(&service);
    // Keep enough information to restore the prior endpoint after any interruption.
    files::write_json(&pending, &serde_json::to_value(&previous)?, 0o600)?;
    let apply = || -> Result<()> {
        if changed {
            for name in [SOCKET, SERVICE] {
                if unit(system, name).exists() {
                    files::run(Command::new("systemctl").args(["stop", name]))?;
                }
            }
            files::atomic_write(&unit(system, SOCKET), socket.as_bytes(), 0o644, false)?;
            files::atomic_write(&unit(system, SERVICE), service.as_bytes(), 0o644, false)?;
            files::run(Command::new("systemctl").arg("daemon-reload"))?;
        }
        files::run(Command::new("systemctl").args(["enable", "--now", SOCKET]))?;
        files::write_json(&marker, &json!({"port":5432}), 0o600)?;
        files::remove_file(&pending)
    };
    if let Err(error) = apply() {
        restore(system, &previous).context(
            "Host forwarding failed and recovery is incomplete; rerun goblinctl db host",
        )?;
        return Err(error.context("Host forwarding failed; the previous endpoint was restored"));
    }
    println!("PostgreSQL host endpoint: localhost:5432");
    Ok(())
}
