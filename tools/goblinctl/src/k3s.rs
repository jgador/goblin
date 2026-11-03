//! Ownership-scoped administration of the dedicated WSL k3s installation.
use crate::credentials;
use crate::database_host;
use crate::environment;
use crate::files;
use crate::install;
use crate::setup;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use flate2::Compression;
use flate2::GzBuilder;
use fs2::FileExt;
use serde_json::Value;
use serde_json::json;
use std::collections::BTreeSet;
use std::fs;
use std::net::Ipv4Addr;
use std::net::TcpListener;
use std::net::UdpSocket;
use std::os::unix::fs::OpenOptionsExt;
use std::os::unix::fs::PermissionsExt;
use std::os::unix::process::CommandExt;
use std::path::Component;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use std::process::Stdio;
use std::time::Duration;

const DATABASE_SOCKET: &str = "goblin-local-postgres.socket";
const DATABASE_SERVICE: &str = "goblin-local-postgres.service";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BrowserRoute {
    Ingress,
    Platform(Ipv4Addr),
}
const UNITS: &[&str] = &[
    "goblin-local.socket",
    "goblin-local.service",
    "goblin-setup.service",
    "goblin-setup-control.socket",
    "goblin-setup-control@.service",
    "goblin-installer.service",
    DATABASE_SOCKET,
    DATABASE_SERVICE,
];

pub struct K3s {
    pub git_repository: PathBuf,
    pub system: PathBuf,
}
impl K3s {
    pub fn new(git_repository: &Path) -> Result<Self> {
        Ok(Self {
            git_repository: git_repository.canonicalize()?,
            system: PathBuf::from("/"),
        })
    }
    pub fn path(&self, path: &str) -> PathBuf {
        self.system.join(path.trim_start_matches('/'))
    }
    fn state(&self) -> PathBuf {
        self.git_repository.join(".goblin-local")
    }
    fn owner(&self) -> PathBuf {
        self.path("var/lib/goblin/local-test/config.json")
    }
    fn unit(&self, name: &str) -> PathBuf {
        self.path("etc/systemd/system").join(name)
    }
    pub fn password(&self) -> PathBuf {
        self.git_repository.join(".goblin-secrets/owner-password")
    }
    pub fn owned_config(&self) -> Result<Value> {
        ensure!(
            self.owner().exists(),
            "No direct local installation exists. Run goblinctl k3s start."
        );
        let config = files::json(&self.owner())?;
        ensure!(
            config["mode"] == "direct" && config["repo"].as_str() == self.git_repository.to_str(),
            "This installation belongs to another runner or checkout; refusing to change it."
        );
        ensure!(
            config["http_port"]
                .as_u64()
                .is_some_and(|p| (1024..=65535).contains(&p)),
            "Invalid local browser port"
        );
        Ok(config)
    }
    pub fn status(&self) -> Result<Option<Value>> {
        let path = self.path(setup::STATE);
        if path.exists() {
            Ok(Some(files::json(&path)?))
        } else {
            Ok(None)
        }
    }
    pub fn preflight(&self, port: Option<u16>) -> Result<Value> {
        ensure!(
            fs::read_to_string(self.path("proc/1/comm"))?.trim() == "systemd",
            "Enable systemd in WSL (/etc/wsl.conf: [boot] systemd=true), then restart WSL."
        );
        ensure!(
            std::env::consts::ARCH == "x86_64"
                && fs::read_to_string(self.path("etc/os-release"))?
                    .lines()
                    .any(|l| l == "ID=ubuntu"),
            "The local installer requires Ubuntu on x86-64 (Ubuntu 24.04 recommended)."
        );
        for name in ["git", "curl", "systemctl", "flock"] {
            ensure!(
                files::executable(name),
                "Missing {name}. Install it before starting Goblin."
            );
        }
        ensure!(
            self.path("usr/lib/systemd/systemd-socket-proxyd").exists(),
            "Missing systemd-socket-proxyd. Install the Ubuntu systemd package."
        );
        self.check_ownership(port)
    }
    pub fn check_ownership(&self, port: Option<u16>) -> Result<Value> {
        if self.owner().exists() {
            let config = self.owned_config()?;
            ensure!(
                port.is_none_or(|p| config["http_port"] == p),
                "The existing installation uses a different port. Reset before changing it."
            );
            return Ok(config);
        }
        for name in [
            "var/lib/goblin",
            "opt/goblin/setup",
            "etc/rancher/k3s",
            "var/lib/rancher/k3s",
            "etc/kubernetes",
            "var/lib/kubelet",
            "var/lib/cni",
            "etc/systemd/system/k3s.service",
        ] {
            ensure!(
                !self.path(name).exists(),
                "{name} already exists outside this runner. Use a clean WSL distribution for this test."
            );
        }
        check_retained_tooling(&self.path("opt/goblin"))?;
        for name in UNITS {
            ensure!(
                !self.unit(name).exists(),
                "{name} already exists outside this runner"
            );
        }
        ensure!(
            !files::executable("k3s"),
            "K3s is already installed outside this runner. Use a clean WSL distribution for this test."
        );
        let port = port.unwrap_or(8788);
        ensure!(port >= 1024, "Choose a browser port between 1024 and 65535");
        for candidate in [80, 443, port] {
            TcpListener::bind((Ipv4Addr::UNSPECIFIED, candidate)).with_context(|| {
                format!("WSL/Linux port {candidate} is already in use. Stop that listener first.")
            })?;
        }
        Ok(json!({"version":2,"mode":"direct","repo":self.git_repository,"http_port":port}))
    }
    pub fn prepare_password(&self) -> Result<PathBuf> {
        let path = self.password();
        if !path.exists() && !path.is_symlink() {
            let retained = self.path("var/lib/goblin/install/private/owner-password");
            if retained.exists() {
                credentials::save(&path, &credentials::read(&retained)?)?;
            }
        }
        credentials::ensure(&path, false)?;
        Ok(path)
    }
    pub fn configure_forwarder(&self, config: &Value) -> Result<()> {
        files::atomic_write(
            &self.unit("goblin-local.socket"),
            socket_unit(
                "Goblin local browser port",
                config["http_port"].as_u64().context("Invalid port")? as u16,
            )
            .as_bytes(),
            0o644,
            false,
        )?;
        self.write_browser_forwarder(self.browser_route()?)?;
        systemctl(&["daemon-reload"])?;
        systemctl(&["enable", "--now", "goblin-local.socket"])
    }
    fn browser_upstream(&self) -> PathBuf {
        self.owner().with_file_name("browser-upstream")
    }
    fn browser_route(&self) -> Result<BrowserRoute> {
        let path = self.browser_upstream();
        files::reject_symlinks(&path)?;
        if path.exists() {
            let address: Ipv4Addr = fs::read_to_string(path)?.trim().parse()?;
            ensure!(address.is_private(), "Invalid platform Service address");
            Ok(BrowserRoute::Platform(address))
        } else {
            Ok(BrowserRoute::Ingress)
        }
    }
    fn write_browser_forwarder(&self, route: BrowserRoute) -> Result<bool> {
        let executable = match route {
            BrowserRoute::Ingress => "/opt/goblin/bin/goblinctl internal forward".to_owned(),
            BrowserRoute::Platform(address) => {
                ensure!(address.is_private(), "Invalid platform Service address");
                format!("/usr/lib/systemd/systemd-socket-proxyd {address}:8787")
            }
        };
        let content = format!(
            "[Unit]\nDescription=Forward the local browser to Goblin setup or Kubernetes\nRequires=goblin-local.socket\nAfter=network.target\n[Service]\nExecStart={executable}\nDynamicUser=yes\nNoNewPrivileges=yes\nProtectSystem=strict\nProtectHome=yes\nPrivateTmp=yes\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX\n"
        );
        let path = self.unit("goblin-local.service");
        files::reject_symlinks(&path)?;
        if fs::read_to_string(&path).is_ok_and(|current| current == content) {
            return Ok(false);
        }
        files::atomic_write(&path, content.as_bytes(), 0o644, false)?;
        Ok(true)
    }
    pub fn set_browser_route(&self, route: BrowserRoute) -> Result<()> {
        self.owned_config()?;
        let path = self.browser_upstream();
        files::reject_symlinks(&path)?;
        match route {
            BrowserRoute::Ingress => files::remove_file(&path)?,
            BrowserRoute::Platform(address) => {
                ensure!(address.is_private(), "Invalid platform Service address");
                files::atomic_write(&path, address.to_string().as_bytes(), 0o600, false)?;
            }
        }
        let pending = self.owner().with_file_name("browser-forwarder-pending");
        files::reject_symlinks(&pending)?;
        if self.write_browser_forwarder(route)?
            || pending.exists()
            || !active("goblin-local.service")
        {
            files::atomic_write(&pending, b"", 0o600, false)?;
            systemctl(&["daemon-reload"])?;
            systemctl(&["restart", "goblin-local.service"])?;
            files::remove_file(&pending)?;
        }
        Ok(())
    }
    pub fn configure_database(&self, config: &Value) -> Result<()> {
        ensure!(
            config["http_port"] != 5432,
            "The browser occupies PostgreSQL port 5432"
        );
        database_host::configure(&self.system)
    }
    fn prepare(&self, config: &Value) -> Result<String> {
        println!("Preparing the installation page and current source checkout…");
        let source = self.state().join("source.tar.gz");
        snapshot_source(&self.git_repository, &source)?;
        let source_ref = files::output(
            Command::new("git")
                .arg("-C")
                .arg(&self.git_repository)
                .args(["rev-parse", "HEAD"]),
        )?;
        let script =
            install::render_bootstrap("localhost", source_ref.trim(), "", crate::VERSION, "local")?;
        files::directory(&self.path("var/lib/goblin"), 0o751)?;
        files::directory(
            self.owner()
                .parent()
                .context("Missing local owner directory")?,
            0o700,
        )?;
        files::write_json(&self.owner(), config, 0o600)?;
        files::atomic_write(
            &self.owner().with_file_name("source.tar.gz"),
            &fs::read(source)?,
            0o600,
            false,
        )?;
        let dir = self.unit("goblin-installer.service.d");
        files::directory(&dir, 0o755)?;
        files::atomic_write(&dir.join("local-test.conf"), format!("[Service]\nEnvironment={public_origin}={}\nExecStartPre=/usr/bin/install -D -m 0600 /var/lib/goblin/local-test/source.tar.gz /var/lib/goblin/install/private/work/goblin-source.tar.gz\n", origin(config)?, public_origin = environment::GOBLIN_PUBLIC_ORIGIN).as_bytes(), 0o644, false)?;
        Ok(script)
    }
    pub fn start(&self, port: Option<u16>) -> Result<()> {
        let config = self.preflight(port)?;
        let password = self.prepare_password()?;
        let complete = fs::read_to_string(self.path("var/lib/goblin/bootstrap-status"))
            .is_ok_and(|s| s.trim() == "setup-ready");
        let script = if complete {
            None
        } else {
            Some(self.prepare(&config)?)
        };
        if complete {
            self.configure_forwarder(&config)?;
            let state = self.status()?;
            if self.unit("k3s.service").exists() {
                systemctl(&["enable", "--now", "k3s"])?;
            }
            let retained = self.path("var/lib/goblin/install/private/owner-password");
            let verifier = credentials::read(&password)?;
            if credentials::read(&retained)? != verifier {
                ensure!(
                    state.as_ref().is_some_and(|s| s["status"] == "ready"),
                    "Finish or retry the current installation before changing its password verifier."
                );
                let secret = files::output(
                    kubectl()
                        .args([
                            "create",
                            "secret",
                            "generic",
                            "goblin-owner-password",
                            "-n",
                            "goblin",
                        ])
                        .arg(format!("--from-file=owner-password={}", password.display()))
                        .args(["--dry-run=client", "-o", "json"]),
                )?;
                files::input(
                    kubectl().args([
                        "apply",
                        "--server-side",
                        "--field-manager=goblin-bootstrap",
                        "-f",
                        "-",
                    ]),
                    secret.as_bytes(),
                )?;
                files::run(kubectl().args([
                    "delete",
                    "pod",
                    "-n",
                    "goblin",
                    "-l",
                    "app=platform",
                    "--ignore-not-found=true",
                    "--wait=true",
                ]))?;
                files::atomic_write(&retained, verifier.as_bytes(), 0o600, false)?;
            }
            if self.unit(DATABASE_SOCKET).exists() {
                systemctl(&["enable", "--now", DATABASE_SOCKET])?;
            }
            if let Some(state) = state
                && state["status"] != "ready"
            {
                systemctl(&["enable", "--now", "goblin-setup.service"])?;
                if state["status"] != "failed" || self.owner().with_file_name("paused").exists() {
                    systemctl(&["enable", "--now", "goblin-installer.service"])?;
                }
            }
            files::remove_file(&self.owner().with_file_name("paused"))?;
        } else {
            println!("Starting the setup page and background installation…");
            let log = fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(self.state().join("bootstrap.log"))?;
            let binary = std::env::current_exe()?;
            files::input(
                Command::new("bash")
                    .env(environment::GOBLIN_PASSWORD_HASH_FILE, password)
                    .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
                    .env(environment::GOBLINCTL_LOCAL_BINARY, &binary)
                    .env(
                        environment::GOBLINCTL_LOCAL_SHA256,
                        install::checksum(&binary)?,
                    )
                    .stdout(log.try_clone()?)
                    .stderr(log),
                script
                    .as_ref()
                    .context("Missing bootstrap script")?
                    .as_bytes(),
            )?;
            self.configure_forwarder(&config)?;
        }
        let url = origin(&config)?;
        let public_route = matches!(self.browser_route()?, BrowserRoute::Platform(_));
        let probe = if public_route {
            format!("{url}/readyz")
        } else {
            format!("{url}/")
        };
        for attempt in 0..30 {
            if Command::new("curl")
                .args([
                    "--fail",
                    "--silent",
                    "--noproxy",
                    "*",
                    "--max-time",
                    "2",
                    &probe,
                ])
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
                .status()
                .is_ok_and(|s| s.success())
            {
                break;
            }
            ensure!(
                attempt < 29,
                "The browser port is not ready. Inspect goblinctl logs."
            );
            std::thread::sleep(Duration::from_secs(1));
        }
        if public_route {
            println!(
                "WSL forwarding endpoint: {url}\nOpen Goblin at the HTTPS public origin configured by k3s deploy.\nSign in with the Goblin password chosen during setup."
            );
        } else {
            println!(
                "Open in your Windows or Linux browser: {url}\nInstallation continues in WSL/Linux after this command exits.\nProgress: goblinctl install status\nSign in with the Goblin password chosen during setup."
            );
        }
        if self.status()?.is_some_and(|s| s["status"] == "failed") {
            println!(
                "The previous attempt failed. Inspect logs, then run goblinctl install retry."
            );
        }
        Ok(())
    }
    pub fn retry(&self, refresh: bool) -> Result<()> {
        let config = self.owned_config()?;
        let state = self
            .status()?
            .context("Bootstrap is incomplete. Run start again.")?;
        ensure!(
            state["status"] != "ready",
            "Installation is complete. Use k3s reset --yes, then start for a fresh test."
        );
        if refresh {
            ensure!(
                state["status"] == "failed" && !active("goblin-installer.service"),
                "Only a stopped, failed installation can be refreshed. Wait for the current attempt to finish."
            );
            self.refresh()?;
        }
        self.configure_forwarder(&config)?;
        if self.unit("k3s.service").exists() {
            systemctl(&["enable", "--now", "k3s"])?;
        }
        systemctl(&[
            "enable",
            "--now",
            "goblin-setup.service",
            "goblin-installer.service",
        ])?;
        println!(
            "{}",
            if refresh {
                "Installer retry started with the current checkout; the password and data are reused."
            } else {
                "Installer retry started; retained source and password are reused."
            }
        );
        Ok(())
    }
    /// Replaces a failed attempt's source snapshot and installer with the current
    /// checkout's, so a fix can be tested without deleting the cluster or downloads.
    fn refresh(&self) -> Result<()> {
        println!("Refreshing the installer and source snapshot from the current checkout…");
        let source = self.state().join("source.tar.gz");
        snapshot_source(&self.git_repository, &source)?;
        let source_ref = files::output(
            Command::new("git")
                .arg("-C")
                .arg(&self.git_repository)
                .args(["rev-parse", "HEAD"]),
        )?;
        // Bootstrap's rule: stop the UI and hold the installer lock while activating.
        systemctl(&["stop", "goblin-setup.service"])?;
        let lock = fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .write(true)
            .mode(0o600)
            .open(self.path("var/lib/goblin/install/installer.lock"))?;
        lock.try_lock_exclusive()
            .context("Another installer operation is running. Try again when it finishes.")?;
        files::atomic_write(
            &self.owner().with_file_name("source.tar.gz"),
            &fs::read(source)?,
            0o600,
            false,
        )?;
        files::atomic_write(
            &self.path("var/lib/goblin/install/private/source-ref"),
            format!("{}\n", source_ref.trim()).as_bytes(),
            0o600,
            false,
        )?;
        install::activate_tooling(&self.system, &std::env::current_exe()?)?;
        drop(lock);
        systemctl(&["daemon-reload"])
    }
    pub fn stop(&self) -> Result<()> {
        self.owned_config()?;
        // Stop recovery before disabling the setup UI; suspend retains every PVC.
        for name in [
            "goblin-installer.service",
            "goblin-setup.service",
            "goblin-local.socket",
            DATABASE_SOCKET,
        ] {
            if self.unit(name).exists() {
                systemctl(&["disable", "--now", name])?;
            }
        }
        for name in ["goblin-local.service", DATABASE_SERVICE] {
            if self.unit(name).exists() {
                systemctl(&["stop", name])?;
            }
        }
        if self.unit("k3s.service").exists() {
            systemctl(&["disable", "--now", "k3s"])?;
            files::run(
                Command::new(self.path("usr/local/bin/k3s-killall.sh"))
                    .stdout(Stdio::null())
                    .stderr(Stdio::null()),
            )?;
        }
        files::atomic_write(&self.owner().with_file_name("paused"), b"", 0o600, false)?;
        println!("Local Goblin services stopped; installation data retained.");
        Ok(())
    }
    pub fn reset(&self) -> Result<()> {
        files::reject_symlinks(&self.owner())?;
        if self.owner().exists() {
            self.owned_config()?;
        }
        files::input(
            Command::new("bash")
                .env_clear()
                .env(
                    environment::PATH,
                    std::env::var_os(environment::PATH)
                        .context("Missing executable search path")?,
                )
                .args(["-s", "--", "--yes", "--repo"])
                .arg(&self.git_repository)
                .arg(&self.system),
            crate::assets::K3S_RESET,
        )
    }
    pub fn locked<T>(&self, operation: impl FnOnce(&Self) -> Result<T>) -> Result<T> {
        files::require_root()?;
        files::directory(&self.state(), 0o700)?;
        let lock = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.path("run/goblin-local.lock"))?;
        lock.lock_exclusive()?;
        operation(self)
    }
}
fn check_retained_tooling(path: &Path) -> Result<()> {
    if path.exists() {
        for entry in fs::read_dir(path)? {
            let name = entry?.file_name();
            ensure!(
                name == "bin" || name == "releases" || name == "share",
                "/opt/goblin contains files outside this runner"
            );
        }
    }
    Ok(())
}
pub fn origin(config: &Value) -> Result<String> {
    Ok(format!(
        "http://localhost:{}",
        config["http_port"]
            .as_u64()
            .context("Invalid browser port")?
    ))
}
pub fn active(unit: &str) -> bool {
    Command::new("systemctl")
        .args(["is-active", "--quiet", unit])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
        .status()
        .is_ok_and(|s| s.success())
}
fn systemctl(args: &[&str]) -> Result<()> {
    files::run(
        Command::new("systemctl")
            .env_clear()
            .env(
                environment::PATH,
                std::env::var_os(environment::PATH).unwrap_or_else(|| {
                    "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin".into()
                }),
            )
            .args(args),
    )
}
fn kubectl() -> Command {
    let mut c = Command::new("k3s");
    c.args(["kubectl", "--kubeconfig=/etc/rancher/k3s/k3s.yaml"]);
    c
}
fn socket_unit(description: &str, port: u16) -> String {
    format!(
        "[Unit]\nDescription={description}\n[Socket]\nListenStream=127.0.0.1:{port}\nListenStream=[::1]:{port}\nBindIPv6Only=ipv6-only\n[Install]\nWantedBy=sockets.target\n"
    )
}
pub fn forward() -> Result<()> {
    let route = UdpSocket::bind("0.0.0.0:0")?;
    route.connect("192.0.2.1:9")?;
    let address = route.local_addr()?.ip();
    Err(Command::new("/usr/lib/systemd/systemd-socket-proxyd")
        .arg(format!("{address}:80"))
        .exec()
        .into())
}
pub fn snapshot_source(git_repository: &Path, destination: &Path) -> Result<()> {
    let git_repository = git_repository.canonicalize()?;
    let inventory = Command::new("git")
        .arg("-C")
        .arg(&git_repository)
        .args([
            "ls-files",
            "--cached",
            "--others",
            "--exclude-standard",
            "-z",
        ])
        .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
        .output()?;
    ensure!(inventory.status.success(), "Cannot read source inventory");
    use std::os::unix::ffi::OsStrExt;
    let names: BTreeSet<_> = inventory
        .stdout
        .split(|b| *b == 0)
        .filter(|b| !b.is_empty())
        .collect();
    let mut temporary = tempfile::NamedTempFile::new_in(
        destination.parent().context("Missing archive directory")?,
    )?;
    {
        let gzip = GzBuilder::new()
            .mtime(0)
            .write(temporary.as_file_mut(), Compression::best());
        let mut archive = tar::Builder::new(gzip);
        for name in names {
            let relative = Path::new(std::ffi::OsStr::from_bytes(name));
            ensure!(
                !relative.is_absolute()
                    && relative
                        .components()
                        .all(|p| matches!(p, Component::Normal(_))),
                "Unsafe source archive path"
            );
            let source = git_repository.join(relative);
            let parent = source.parent().context("Missing source directory")?;
            let metadata = match fs::symlink_metadata(&source) {
                Ok(m) => m,
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => continue,
                Err(e) => return Err(e.into()),
            };
            ensure!(
                !metadata.file_type().is_symlink() && parent.canonicalize()? == parent,
                "Local source snapshots do not accept symlinks: {}",
                relative.display()
            );
            ensure!(metadata.is_file(), "Unsupported source entry");
            let mut header = tar::Header::new_gnu();
            header.set_size(metadata.len());
            header.set_mode(metadata.permissions().mode() & 0o777);
            header.set_uid(0);
            header.set_gid(0);
            header.set_mtime(0);
            header.set_cksum();
            archive.append_data(
                &mut header,
                Path::new("goblin").join(relative),
                fs::File::open(source)?,
            )?;
        }
        archive.into_inner()?.finish()?;
    }
    temporary
        .as_file()
        .set_permissions(fs::Permissions::from_mode(0o600))?;
    temporary.persist(destination)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::BrowserRoute;
    use super::K3s;
    use super::check_retained_tooling;
    use crate::install;
    use std::fs;
    use std::path::Path;

    #[test]
    fn browser_route_survives_resume_and_can_return_to_loopback_ingress() {
        let artifacts =
            Path::new(env!("CARGO_MANIFEST_DIR")).join("../../.artifacts/k3s-browser-route");
        fs::create_dir_all(&artifacts).unwrap();
        let root = tempfile::tempdir_in(artifacts).unwrap();
        let k3s = K3s {
            git_repository: root.path().to_owned(),
            system: root.path().join("system"),
        };
        assert_eq!(k3s.browser_route().unwrap(), BrowserRoute::Ingress);
        let route = BrowserRoute::Platform("10.43.0.20".parse().unwrap());
        assert!(k3s.write_browser_forwarder(route).unwrap());
        assert!(!k3s.write_browser_forwarder(route).unwrap());
        let unit = fs::read_to_string(k3s.unit("goblin-local.service")).unwrap();
        assert!(
            unit.contains("ExecStart=/usr/lib/systemd/systemd-socket-proxyd 10.43.0.20:8787\n")
        );
        assert!(unit.contains("DynamicUser=yes"));
        fs::create_dir_all(k3s.browser_upstream().parent().unwrap()).unwrap();
        fs::write(k3s.browser_upstream(), "10.43.0.20").unwrap();
        assert_eq!(k3s.browser_route().unwrap(), route);
        fs::write(k3s.browser_upstream(), "203.0.113.20").unwrap();
        assert!(k3s.browser_route().is_err());
        assert!(
            k3s.write_browser_forwarder(BrowserRoute::Platform("203.0.113.20".parse().unwrap()))
                .is_err()
        );
        fs::remove_file(k3s.browser_upstream()).unwrap();
        assert!(
            k3s.write_browser_forwarder(k3s.browser_route().unwrap())
                .unwrap()
        );
        assert!(
            fs::read_to_string(k3s.unit("goblin-local.service"))
                .unwrap()
                .contains("ExecStart=/opt/goblin/bin/goblinctl internal forward\n")
        );
    }

    #[test]
    fn retained_native_tooling_and_licenses_allow_a_fresh_local_installation() {
        let artifacts =
            Path::new(env!("CARGO_MANIFEST_DIR")).join("../../.artifacts/k3s-start-regression");
        fs::create_dir_all(&artifacts).unwrap();
        let fixture = tempfile::tempdir_in(artifacts).unwrap();
        let binary = fixture.path().join("fixture-goblinctl");
        fs::write(&binary, b"inert native tooling fixture").unwrap();
        let system = fixture.path().join("system");
        install::activate_tooling(&system, &binary).unwrap();
        let opt = system.join("opt/goblin");
        assert!(check_retained_tooling(&opt).is_err());

        // Reset removes setup assets while retaining the CLI and its licenses.
        fs::remove_dir_all(opt.join("setup")).unwrap();
        assert!(opt.join("share/licenses/goblinctl/LICENSE").is_file());
        check_retained_tooling(&opt).unwrap();

        fs::create_dir(opt.join("unrelated")).unwrap();
        assert!(
            check_retained_tooling(&opt)
                .unwrap_err()
                .to_string()
                .contains("files outside this runner")
        );
    }
}
