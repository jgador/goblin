//! Ownership-scoped administration of the dedicated Ubuntu/WSL installation.
use crate::assets;
use crate::credentials;
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
const UNITS: &[&str] = &[
    "goblin-local.socket",
    "goblin-local.service",
    "goblin-setup.service",
    "goblin-installer.service",
    DATABASE_SOCKET,
    DATABASE_SERVICE,
];

pub struct Local {
    pub git_repository: PathBuf,
    pub system: PathBuf,
}
impl Local {
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
            "No direct local installation exists. Run goblinctl local start."
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
        let opt = self.path("opt/goblin");
        if opt.exists() {
            for entry in fs::read_dir(&opt)? {
                let name = entry?.file_name();
                ensure!(
                    name == "bin" || name == "releases",
                    "/opt/goblin contains files outside this runner"
                );
            }
        }
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
        let legacy = self.state().join("login-password");
        if !path.exists() && !path.is_symlink() {
            let retained = self.path("var/lib/goblin/install/private/owner-password");
            if retained.exists() {
                credentials::save(&path, &credentials::read(&retained)?)?;
            } else if legacy.exists() {
                files::reject_symlinks(&legacy)?;
                let text = fs::read_to_string(&legacy)?;
                credentials::save(
                    &path,
                    &credentials::hash(text.strip_suffix('\n').unwrap_or(&text))?,
                )?;
            }
        }
        credentials::ensure(&path, false)?;
        files::remove_file(&legacy)?;
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
        files::atomic_write(&self.unit("goblin-local.service"), b"[Unit]\nDescription=Forward the local browser to Goblin setup or Kubernetes\nRequires=goblin-local.socket\nAfter=network.target\n[Service]\nExecStart=/opt/goblin/bin/goblinctl internal forward\nDynamicUser=yes\nNoNewPrivileges=yes\nProtectSystem=strict\nProtectHome=yes\nPrivateTmp=yes\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX\n", 0o644, false)?;
        systemctl(&["daemon-reload"])?;
        systemctl(&["enable", "--now", "goblin-local.socket"])
    }
    pub fn configure_database(
        &self,
        config: &mut Value,
        requested_port: Option<u16>,
    ) -> Result<()> {
        let port =
            requested_port.unwrap_or(config["postgres_port"].as_u64().unwrap_or(55432) as u16);
        ensure!(
            port >= 1024 && config["http_port"] != port,
            "Choose a database port between 1024 and 65535, different from the browser port."
        );
        ensure!(
            active("k3s.service"),
            "Start the local cluster first: goblinctl local start."
        );
        ensure!(
            config.get("postgres_port").is_some()
                || ![DATABASE_SOCKET, DATABASE_SERVICE]
                    .iter()
                    .any(|u| self.unit(u).exists()),
            "Database forwarding units already exist outside this runner; refusing to replace them."
        );
        if config["postgres_port"] != port || !active(DATABASE_SOCKET) {
            for address in ["127.0.0.1", "::1"] {
                TcpListener::bind((address, port)).with_context(|| format!("Local port {port} is occupied. Stop its existing port-forward or choose another port."))?;
            }
        }
        files::run(
            kubectl()
                .args(["get", "statefulset", "goblin-postgres", "-n", "goblin"])
                .stdout(Stdio::null()),
        )?;
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
        .context("The local PostgreSQL service has no usable IPv4 cluster address.")?;
        let units = [
            (
                DATABASE_SOCKET,
                socket_unit("Goblin local PostgreSQL port", port),
            ),
            (
                DATABASE_SERVICE,
                format!(
                    "[Unit]\nDescription=Forward local PostgreSQL clients to the Kubernetes service\nRequires={DATABASE_SOCKET} k3s.service\nAfter=network.target k3s.service\n[Service]\nExecStart=/usr/lib/systemd/systemd-socket-proxyd {address}:5432\nDynamicUser=yes\nNoNewPrivileges=yes\nProtectSystem=strict\nProtectHome=yes\nPrivateTmp=yes\nRestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX\n"
                ),
            ),
        ];
        if units
            .iter()
            .any(|(name, text)| fs::read_to_string(self.unit(name)).ok().as_ref() != Some(text))
        {
            for (name, _) in &units {
                if self.unit(name).exists() {
                    systemctl(&["stop", name])?;
                }
            }
            for (name, text) in &units {
                files::atomic_write(&self.unit(name), text.as_bytes(), 0o644, false)?;
            }
        }
        config["postgres_port"] = json!(port);
        files::write_json(&self.owner(), config, 0o600)?;
        systemctl(&["daemon-reload"])?;
        systemctl(&["enable", "--now", DATABASE_SOCKET])?;
        println!(
            "PostgreSQL is available at localhost:{port}; local access runs in the background. No kubectl port-forward is needed."
        );
        Ok(())
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
        let script = install::render_bootstrap(
            "localhost",
            source_ref.trim(),
            env!("CARGO_PKG_VERSION"),
            "local",
        )?;
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
            self.migrate_legacy_tooling()?;
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
                    "app=goblin-auth",
                    "--ignore-not-found=true",
                    "--wait=true",
                ]))?;
                files::atomic_write(&retained, verifier.as_bytes(), 0o600, false)?;
            }
            if config.get("postgres_port").is_some() {
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
        for attempt in 0..30 {
            if Command::new("curl")
                .args([
                    "--fail",
                    "--silent",
                    "--noproxy",
                    "*",
                    "--max-time",
                    "2",
                    &format!("{url}/"),
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
        println!(
            "Open in your Windows or Linux browser: {url}\nInstallation continues in WSL/Linux after this command exits.\nProgress: goblinctl install status\nSign in with the Goblin password chosen during setup."
        );
        if self.status()?.is_some_and(|s| s["status"] == "failed") {
            println!(
                "The previous attempt failed. Inspect logs, then run goblinctl install retry."
            );
        }
        Ok(())
    }
    fn migrate_legacy_tooling(&self) -> Result<()> {
        if !self.path("opt/goblin/setup/goblin-setup.pyz").exists() {
            return Ok(());
        }
        // A local installation may predate the native CLI. Preserve its progress,
        // source archive and credentials while replacing only the operational tools.
        if self.unit("goblin-installer.service").exists() {
            systemctl(&["stop", "goblin-installer.service"])?;
        }
        let lock = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.path("var/lib/goblin/install/installer.lock"))?;
        lock.lock_exclusive()?;
        if self.unit("goblin-setup.service").exists() {
            systemctl(&["stop", "goblin-setup.service"])?;
        }
        install::activate_tooling(&self.system, &std::env::current_exe()?)?;
        files::remove_file(&self.path("opt/goblin/local/forward.py"))?;
        systemctl(&["daemon-reload"])
    }
    pub fn retry(&self) -> Result<()> {
        let config = self.owned_config()?;
        let state = self
            .status()?
            .context("Bootstrap is incomplete. Run start again.")?;
        ensure!(
            state["status"] != "ready",
            "Installation is complete. Use local reset --yes, then start for a fresh test."
        );
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
        println!("Installer retry started; retained source and password are reused.");
        Ok(())
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
        self.stop()?;
        let uninstall = self.path("usr/local/bin/k3s-uninstall.sh");
        if uninstall.exists() {
            files::run(
                Command::new(uninstall)
                    .stdout(Stdio::null())
                    .stderr(Stdio::null()),
            )?;
        } else {
            ensure!(
                !self.unit("k3s.service").exists() && !files::executable("k3s"),
                "K3s is installed but its uninstaller is missing; local data was retained."
            );
            for p in ["etc/rancher/k3s", "var/lib/rancher/k3s"] {
                remove_tree(&self.path(p))?;
            }
        }
        let kubelet = self.path("var/lib/kubelet");
        if kubelet.exists() {
            ensure!(
                fs::read_dir(&kubelet)?.next().is_none(),
                "Kubelet data remains after uninstall. Inspect /var/lib/kubelet, then retry reset."
            );
            if Command::new("mountpoint")
                .arg("--quiet")
                .arg(&kubelet)
                .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
                .status()?
                .success()
            {
                files::run(Command::new("umount").arg("--").arg(&kubelet))?;
            }
            fs::remove_dir(kubelet)?;
        }
        for name in UNITS {
            let _ = Command::new("systemctl")
                .args(["reset-failed", name])
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
                .status();
            files::remove_file(&self.unit(name))?;
        }
        let override_dir = self.unit("goblin-installer.service.d");
        files::remove_file(&override_dir.join("local-test.conf"))?;
        if override_dir.exists() && fs::read_dir(&override_dir)?.next().is_none() {
            fs::remove_dir(override_dir)?;
        }
        for p in ["var/lib/goblin", "opt/goblin/setup", "opt/goblin/local"] {
            remove_tree(&self.path(p))?;
        }
        for name in [
            "source.tar.gz",
            "bootstrap.sh",
            "bootstrap.log",
            "login-password",
        ] {
            files::remove_file(&self.state().join(name))?;
        }
        for name in [
            "var/log/goblin-bootstrap.log",
            "var/log/goblin-installer.log",
        ] {
            files::remove_file(&self.path(name))?;
        }
        systemctl(&["daemon-reload"])?;
        println!(
            "Local Goblin cluster and application data removed. The repository password verifier, native CLI, Docker and download caches are retained."
        );
        Ok(())
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
fn remove_tree(path: &Path) -> Result<()> {
    if path.exists() {
        fs::remove_dir_all(path)?;
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
    files::run(Command::new("systemctl").args(args))
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
