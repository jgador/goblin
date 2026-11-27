//! Inspect and, on explicit request, release a conflicting local TCP listener.
use super::PortConflictPolicy;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::bail;
use anyhow::ensure;
use std::fs;
use std::io;
use std::net::IpAddr;
use std::net::Ipv4Addr;
use std::net::Ipv6Addr;
use std::net::SocketAddr;
use std::net::TcpListener;
use std::os::fd::AsRawFd;
use std::os::fd::FromRawFd;
use std::os::fd::OwnedFd;
use std::os::unix::fs::MetadataExt;
use std::process::Command;
use std::thread;
use std::time::Duration;
use std::time::Instant;

fn busy(port: u16) -> Result<Vec<SocketAddr>> {
    let mut blocked = Vec::new();
    for ip in [
        IpAddr::V4(Ipv4Addr::LOCALHOST),
        IpAddr::V6(Ipv6Addr::LOCALHOST),
    ] {
        let address = SocketAddr::new(ip, port);
        match TcpListener::bind(address) {
            Ok(_) => (),
            Err(error) if error.kind() == io::ErrorKind::AddrInUse => blocked.push(address),
            Err(error) => return Err(error).context(format!("Cannot bind WSL address {address}")),
        }
    }
    Ok(blocked)
}

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
struct Owner {
    pid: i32,
    fd: u32,
}
impl Owner {
    fn name(self) -> String {
        fs::read_link(format!("/proc/{}/exe", self.pid))
            .ok()
            .and_then(|path| {
                path.file_name()
                    .map(|name| name.to_string_lossy().into_owned())
            })
            .unwrap_or_else(|| {
                fs::read_to_string(format!("/proc/{}/comm", self.pid)).unwrap_or_default()
            })
            .chars()
            .filter(|character| !character.is_control())
            .collect()
    }
}

#[derive(PartialEq, Eq)]
struct Listener<'a> {
    endpoint: &'a str,
    inode: u64,
    v6_only: bool,
    owners: Vec<Owner>,
}
impl Listener<'_> {
    fn blocks(&self, address: SocketAddr) -> bool {
        let Some((host, _)) = self.endpoint.rsplit_once(':') else {
            return false;
        };
        let host = host.trim_matches(['[', ']']);
        if host == "*" {
            return address.is_ipv6() || !self.v6_only;
        }
        match (address.ip(), host.parse::<IpAddr>()) {
            (IpAddr::V4(target), Ok(IpAddr::V4(actual))) => {
                actual.is_unspecified() || actual == target
            }
            (IpAddr::V6(target), Ok(IpAddr::V6(actual))) => {
                actual.is_unspecified() || actual == target
            }
            (IpAddr::V4(target), Ok(IpAddr::V6(actual))) => {
                !self.v6_only
                    && (actual.is_unspecified() || actual.to_ipv4_mapped() == Some(target))
            }
            _ => false,
        }
    }

    fn description(&self) -> String {
        let owners: Vec<_> = self
            .owners
            .iter()
            .map(|owner| {
                let name = owner.name();
                if name.is_empty() {
                    format!("PID {}", owner.pid)
                } else {
                    format!("{name} (PID {})", owner.pid)
                }
            })
            .collect();
        if owners.is_empty() {
            self.endpoint.to_owned()
        } else {
            format!("{} by {}", self.endpoint, owners.join(", "))
        }
    }
}

fn inspect(port: u16) -> Result<String> {
    files::output(Command::new("ss").args(["-Hlntep", &format!("sport = :{port}")]))
        .context("Cannot inspect local listeners with ss")
}

fn listeners(output: &str) -> impl Iterator<Item = Listener<'_>> {
    output.lines().filter_map(|line| {
        let endpoint = line.split_whitespace().nth(3)?;
        let (processes, details) = line.rsplit_once(" ino:")?;
        let inode = details.split_whitespace().next()?.parse().ok()?;
        let mut owners: Vec<_> = processes
            .split(",pid=")
            .skip(1)
            .filter_map(|entry| {
                let (pid, fd) = entry.split_once(",fd=")?;
                let pid = pid.parse().ok()?;
                let fd = fd.split(')').next()?.parse().ok()?;
                let target = fs::read_link(format!("/proc/{pid}/fd/{fd}")).ok()?;
                (target.as_os_str() == format!("socket:[{inode}]").as_str())
                    .then_some(Owner { pid, fd })
            })
            .collect();
        owners.sort_unstable();
        owners.dedup();
        Some(Listener {
            endpoint,
            inode,
            v6_only: details.split_whitespace().any(|part| part == "v6only:1"),
            owners,
        })
    })
}

fn systemctl(user: bool) -> Command {
    let mut command = Command::new("systemctl");
    if user {
        command.arg("--user");
    }
    command
}

enum Stop {
    Unit {
        user: bool,
        name: String,
        activates: Vec<String>,
    },
    Process {
        owner: Owner,
        handle: OwnedFd,
    },
}
impl Stop {
    fn unit_for_socket(listener: &Listener<'_>, manager: Owner) -> Result<Self> {
        let user = manager.pid != 1;
        if user {
            let uid = fs::metadata(format!("/proc/{}", manager.pid))?.uid();
            ensure!(
                uid == unsafe { libc::geteuid() },
                "The socket belongs to another systemd user session"
            );
        }
        let output =
            files::output(systemctl(user).args(["list-sockets", "--no-legend", "--no-pager"]))?;
        let endpoint = listener.endpoint.replace("*:", "[::]:");
        for line in output.lines() {
            let mut columns = line.split_whitespace();
            if columns.next() == Some(&endpoint)
                && let Some(socket) = columns.next().filter(|unit| unit.ends_with(".socket"))
            {
                let activates = columns
                    .filter(|unit| unit.ends_with(".service"))
                    .map(str::to_owned)
                    .collect();
                return Ok(Self::Unit {
                    user,
                    name: socket.to_owned(),
                    activates,
                });
            }
        }
        bail!(
            "Cannot identify the systemd socket unit listening on {}",
            listener.endpoint
        );
    }

    fn identify(listener: &Listener<'_>) -> Result<Self> {
        if let Some(manager) = listener
            .owners
            .iter()
            .find(|owner| owner.name() == "systemd")
        {
            return Self::unit_for_socket(listener, *manager);
        }
        let [owner] = listener.owners.as_slice() else {
            bail!(
                "Cannot verify a single owner for the listener on {}",
                listener.endpoint
            );
        };
        let pid = owner.pid;
        ensure!(
            pid > 1 && !is_ancestor(pid),
            "The listener belongs to Goblin's own session"
        );
        // A pidfd keeps the signal tied to this process if the PID is recycled.
        let handle = unsafe { libc::syscall(libc::SYS_pidfd_open, pid, 0) } as i32;
        if handle < 0 {
            return Err(io::Error::last_os_error())
                .context(format!("Cannot open a process handle for PID {pid}"));
        }
        let handle = unsafe { OwnedFd::from_raw_fd(handle) };
        for user in [true, false] {
            let unit = files::output(systemctl(user).args(["whoami", &pid.to_string()]))
                .unwrap_or_default();
            let unit = unit.trim();
            if unit.ends_with(".service") && !unit.starts_with("user@") {
                let current = files::output(
                    systemctl(user).args(["whoami", &std::process::id().to_string()]),
                )
                .unwrap_or_default();
                ensure!(
                    current.trim() != unit,
                    "The listener belongs to Goblin's own service"
                );
                return Ok(Self::Unit {
                    user,
                    name: unit.to_owned(),
                    activates: Vec::new(),
                });
            }
        }
        Ok(Self::Process {
            owner: *owner,
            handle,
        })
    }

    fn run(self, port: u16) -> Result<()> {
        match self {
            Self::Unit {
                user,
                name,
                activates,
            } => {
                eprintln!("Stopping {name} to take over WSL port {port}.");
                files::run(systemctl(user).args(["stop", &name]))
                    .with_context(|| format!("Cannot stop {name}"))?;
                for service in activates {
                    let state = files::output(systemctl(user).args([
                        "show",
                        "--property=LoadState",
                        "--value",
                        &service,
                    ]))?;
                    if state.trim() == "loaded" {
                        eprintln!("Stopping {service}, activated by {name}.");
                        files::run(systemctl(user).args(["stop", &service]))
                            .with_context(|| format!("Cannot stop {service}"))?;
                    }
                }
                Ok(())
            }
            Self::Process { owner, handle } => {
                let pid = owner.pid;
                let name = owner.name();
                eprintln!("Stopping {name} (PID {pid}) to take over WSL port {port}.");
                let status = unsafe {
                    libc::syscall(
                        libc::SYS_pidfd_send_signal,
                        handle.as_raw_fd(),
                        libc::SIGTERM,
                        std::ptr::null::<libc::siginfo_t>(),
                        0,
                    )
                };
                if status < 0 {
                    return Err(io::Error::last_os_error())
                        .with_context(|| format!("Cannot stop PID {pid}"));
                }
                Ok(())
            }
        }
    }
}

fn is_ancestor(pid: i32) -> bool {
    let mut current = std::process::id() as i32;
    while current > 1 {
        if current == pid {
            return true;
        }
        let status = fs::read_to_string(format!("/proc/{current}/status")).unwrap_or_default();
        current = status
            .lines()
            .find_map(|line| line.strip_prefix("PPid:"))
            .and_then(|parent| parent.trim().parse().ok())
            .unwrap_or(0);
    }
    false
}

fn take_over(port: u16, blocked: &[SocketAddr], conflicts: Vec<Listener<'_>>) -> Result<()> {
    ensure!(
        blocked
            .iter()
            .all(|address| conflicts.iter().any(|listener| listener.blocks(*address))),
        "Cannot identify every listener occupying the requested port"
    );
    let stops: Vec<_> = conflicts
        .iter()
        .map(Stop::identify)
        .collect::<Result<_>>()?;
    for (listener, stop) in conflicts.into_iter().zip(stops) {
        // Recheck socket identity after opening the handle or resolving the unit.
        if listeners(&inspect(port)?).any(|current| current == listener) {
            stop.run(port)?;
        }
    }
    let deadline = Instant::now() + Duration::from_secs(2);
    while !busy(port)?.is_empty() {
        ensure!(
            Instant::now() < deadline,
            "The port is still in use after stopping the listener"
        );
        thread::sleep(Duration::from_millis(50));
    }
    Ok(())
}

pub(super) fn prepare(port: u16, policy: PortConflictPolicy, retry: &str) -> Result<()> {
    let blocked = busy(port)?;
    if blocked.is_empty() {
        return Ok(());
    }
    let inspection = inspect(port);
    let conflicts: Vec<_> = listeners(inspection.as_deref().unwrap_or_default())
        .filter(|listener| blocked.iter().any(|address| listener.blocks(*address)))
        .collect();
    match policy {
        PortConflictPolicy::Report => {
            let mut details: Vec<_> = conflicts.iter().map(Listener::description).collect();
            details.extend(
                blocked
                    .iter()
                    .filter(|address| !conflicts.iter().any(|listener| listener.blocks(**address)))
                    .map(SocketAddr::to_string),
            );
            bail!(
                "WSL port {port} is already in use on {}.\nTo stop the listener and take over the port, rerun\n\n  {retry} --port-forward {port} --force",
                details.join("; ")
            );
        }
        PortConflictPolicy::TakeOver => {
            if let Err(error) = inspection.as_ref() {
                bail!("Cannot take over WSL port {port}: {error:#}");
            }
            if let Err(error) = take_over(port, &blocked, conflicts) {
                bail!("Cannot take over WSL port {port}: {error:#}");
            }
            Ok(())
        }
    }
}
