//! User-owned PostgreSQL clients, independent of the checkout and installer state.
use crate::database_host;
use crate::environment;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use clap::ValueEnum;
use fs2::FileExt;
use serde::Deserialize;
use serde::Serialize;
use serde_json::Value;
use sha2::Digest;
use sha2::Sha256;
use std::ffi::CString;
use std::fs;
use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

mod access;
mod windows;

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq, ValueEnum)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    App,
    Admin,
}
impl Role {
    pub fn name(self) -> &'static str {
        match self {
            Self::App => "app",
            Self::Admin => "admin",
        }
    }
}
#[derive(Clone, Copy, Debug, ValueEnum)]
pub enum Access {
    App,
    Admin,
    Both,
}

#[derive(Clone, Copy, Debug, ValueEnum)]
pub enum Usage {
    Development,
    Tests,
}
impl Access {
    fn roles(self) -> &'static [Role] {
        match self {
            Self::App => &[Role::App],
            Self::Admin => &[Role::Admin],
            Self::Both => &[Role::App, Role::Admin],
        }
    }
}
impl From<Role> for Access {
    fn from(role: Role) -> Self {
        match role {
            Role::App => Self::App,
            Role::Admin => Self::Admin,
        }
    }
}
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "kebab-case", deny_unknown_fields)]
pub enum Source {
    Local,
    Ssh { destination: String },
}
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct Identity {
    cluster: String,
    volume: String,
    ca_sha256: String,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Profile {
    version: u32,
    source: Source,
    identity: Identity,
    server_port: u16,
    forward_port: Option<u16>,
    windows_exports: Vec<Role>,
}
impl Profile {
    pub fn port(&self) -> u16 {
        match self.source {
            Source::Local => 5432,
            Source::Ssh { .. } => self.forward_port.unwrap_or(55433),
        }
    }
}

pub struct Store {
    root: PathBuf,
}
pub struct Client {
    pub profile: Profile,
    directory: PathBuf,
}
impl Client {
    pub fn connection(&self, role: Role) -> String {
        let quote =
            |path: PathBuf| format!("\"{}\"", path.display().to_string().replace('"', "\"\""));
        let name = role.name();
        format!(
            "Host=localhost;Port={};Database=goblin;Username=goblin_{name};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Root Certificate={};SSL Certificate={};SSL Key={}",
            self.profile.port(),
            quote(self.directory.join("ca.crt")),
            quote(self.directory.join(name).join("tls.crt")),
            quote(self.directory.join(name).join("tls.key"))
        )
    }
    pub fn configure(&self, command: &mut Command, access: Access) {
        command
            .env_remove(environment::CONNECTIONSTRINGS_GOBLIN)
            .env_remove(environment::CONNECTIONSTRINGS_GOBLINADMIN)
            .env_remove(environment::GOBLIN_TEST_POSTGRES_APP)
            .env_remove(environment::GOBLIN_TEST_POSTGRES_ADMIN);
        for role in access.roles() {
            command.env(
                match role {
                    Role::App => environment::CONNECTIONSTRINGS_GOBLIN,
                    Role::Admin => environment::CONNECTIONSTRINGS_GOBLINADMIN,
                },
                self.connection(*role),
            );
        }
    }
    pub fn configure_tests(&self, command: &mut Command, access: Access) {
        self.configure(command, access);
        for role in access.roles() {
            command.env(
                match role {
                    Role::App => environment::GOBLIN_TEST_POSTGRES_APP,
                    Role::Admin => environment::GOBLIN_TEST_POSTGRES_ADMIN,
                },
                self.connection(*role),
            );
        }
    }
    fn verify(&self, role: Role, port: u16) -> Result<()> {
        let quote = |value: &str| format!("'{}'", value.replace('\\', "\\\\").replace('\'', "\\'"));
        let parameter =
            |name: &str, path: PathBuf| format!("{name}={}", quote(&path.to_string_lossy()));
        let name = role.name();
        let connection = format!(
            "host=localhost hostaddr=127.0.0.1 port={} dbname=goblin user=goblin_{name} sslmode=verify-full gssencmode=disable connect_timeout=10 passfile=/dev/null {} {} {}",
            port,
            parameter("sslrootcert", self.directory.join("ca.crt")),
            parameter("sslcert", self.directory.join(name).join("tls.crt")),
            parameter("sslkey", self.directory.join(name).join("tls.key"))
        );
        let value = files::output(Command::new("psql")
            .env_remove(environment::PGSERVICE).env_remove(environment::PGSERVICEFILE)
            .env_remove(environment::PGOPTIONS).env_remove(environment::PGPASSWORD)
            .args(["-X", "-w", "-A", "-t", "--dbname", &connection, "--command", "SELECT current_database() || '|' || current_user || '|' || ssl::text FROM pg_stat_ssl WHERE pid = pg_backend_pid()"])).context("PostgreSQL TLS login failed; check the host service, forwarding listener and client certificates")?;
        ensure!(
            value.trim() == format!("goblin|goblin_{name}|true"),
            "PostgreSQL returned an unexpected database, role or TLS state"
        );
        Ok(())
    }
}
impl Store {
    pub fn user() -> Result<Self> {
        ensure!(
            files::sudo_owner().is_none(),
            "Run client profile commands as your development user, without sudo"
        );
        let base = match std::env::var_os(environment::XDG_CONFIG_HOME) {
            Some(base) if !base.is_empty() => PathBuf::from(base),
            _ => PathBuf::from(
                std::env::var_os(environment::HOME).context("Cannot locate your home directory")?,
            )
            .join(".config"),
        };
        ensure!(
            base.is_absolute(),
            "The user configuration directory must be absolute"
        );
        Ok(Self::at(base.join("goblin/postgres")))
    }
    pub fn at(root: PathBuf) -> Self {
        Self { root }
    }
    fn directory(&self, name: &str) -> Result<PathBuf> {
        ensure!(
            !name.is_empty()
                && name.len() <= 48
                && name.as_bytes()[0].is_ascii_alphanumeric()
                && name
                    .bytes()
                    .all(|c| c.is_ascii_alphanumeric() || b"-_.".contains(&c)),
            "Profile names must start with a letter or digit and contain only letters, digits, dots, underscores and hyphens (up to 48 characters)"
        );
        let path = self.root.join(name);
        files::reject_symlinks(&path)?;
        Ok(path)
    }
    fn lock(&self, directory: &Path) -> Result<fs::File> {
        files::directory(&self.root, 0o700)?;
        files::directory(directory, 0o700)?;
        let path = directory.join(".lock");
        files::reject_symlinks(&path)?;
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .mode(0o600)
            .open(path)?;
        file.lock_exclusive()?;
        for entry in fs::read_dir(directory)? {
            let entry = entry?;
            if entry.file_name().to_string_lossy().starts_with(".refresh-")
                && entry.file_type()?.is_dir()
            {
                files::reject_symlinks(&entry.path())?;
                fs::remove_dir_all(entry.path())?;
            }
        }
        Ok(file)
    }
    fn read(&self, directory: &Path) -> Result<Profile> {
        files::reject_symlinks(&directory.join("profile.json"))?;
        let profile: Profile =
            serde_json::from_value(files::json(&directory.join("profile.json"))?)?;
        ensure!(
            profile.version == 1 && profile.server_port == 5432,
            "Unsupported PostgreSQL profile; the server port must be 5432"
        );
        validate_source(&profile.source)?;
        validate_port(profile.forward_port)?;
        Ok(profile)
    }
    /// Refresh credentials only after the saved installation identity matches.
    pub fn connect(
        &self,
        name: &str,
        source: Option<Source>,
        port: Option<u16>,
        access: Access,
    ) -> Result<Client> {
        let directory = self.directory(name)?;
        let _lock = self.lock(&directory)?;
        let previous = if directory.join("profile.json").exists() {
            Some(self.read(&directory)?)
        } else {
            None
        };
        let source = source
            .or_else(|| previous.as_ref().map(|p| p.source.clone()))
            .context("Create the profile with --local or --ssh USER@HOST")?;
        validate_source(&source)?;
        if let Some(previous) = &previous {
            ensure!(
                previous.source == source,
                "This profile belongs to another source; use a new profile name"
            );
        }
        let (identity, ca) = identity(&source)?;
        if let Some(previous) = &previous {
            ensure!(
                previous.identity == identity,
                "The profile's cluster, database volume or CA changed. Restore the original installation or create a new profile after reviewing the new identity; credentials were not replaced"
            );
        }
        let forward_port = port
            .or_else(|| previous.as_ref().and_then(|p| p.forward_port))
            .or(match source {
                Source::Local => None,
                Source::Ssh { .. } => Some(55433),
            });
        validate_port(forward_port)?;
        let profile = Profile {
            version: 1,
            source,
            identity,
            server_port: 5432,
            forward_port,
            windows_exports: previous
                .as_ref()
                .map(|p| p.windows_exports.clone())
                .unwrap_or_default(),
        };
        let stage = tempfile::Builder::new()
            .prefix(".refresh-")
            .tempdir_in(&directory)?;
        files::directory(stage.path(), 0o700)?;
        files::atomic_write(&stage.path().join("ca.crt"), &ca, 0o600, false)?;
        for role in access.roles() {
            fetch_certificate(&profile.source, *role, &ca, stage.path())?;
        }
        let forwarding = access::connect(&directory, &profile)?;
        let staged = Client {
            profile: profile.clone(),
            directory: stage.path().to_owned(),
        };
        for role in access.roles() {
            staged.verify(*role, profile.port())?;
            if matches!(profile.source, Source::Local)
                && let Some(port) = profile.forward_port
            {
                staged.verify(*role, port)?;
            }
        }
        // Validate every destination before publishing any of the complete, verified files.
        for relative in
            std::iter::once("ca.crt".to_owned()).chain(access.roles().iter().flat_map(|r| {
                [
                    format!("{}/tls.crt", r.name()),
                    format!("{}/tls.key", r.name()),
                ]
            }))
        {
            files::reject_symlinks(&directory.join(relative))?;
        }
        files::atomic_write(&directory.join("ca.crt"), &ca, 0o600, false)?;
        for role in access.roles() {
            publish_role(
                &stage.path().join(role.name()),
                &directory.join(role.name()),
            )?;
        }
        files::write_json(
            &directory.join("profile.json"),
            &serde_json::to_value(&profile)?,
            0o600,
        )?;
        forwarding.commit();
        let client = Client { profile, directory };
        if access
            .roles()
            .iter()
            .any(|role| client.profile.windows_exports.contains(role))
        {
            println!(
                "Windows exports may need refreshing: goblinctl db export {name} --client windows --role <app|admin>"
            );
        }
        println!(
            "Profile {name}: localhost:{}, database goblin; TLS verified",
            client.profile.port()
        );
        for role in access.roles() {
            let expiry = files::output(
                Command::new("openssl")
                    .args(["x509", "-noout", "-enddate", "-in"])
                    .arg(client.directory.join(role.name()).join("tls.crt")),
            )?;
            println!(
                "  goblin_{}: {}\n  {}",
                role.name(),
                expiry.trim(),
                client.connection(*role)
            );
        }
        Ok(client)
    }
    pub fn export_windows(&self, name: &str, role: Role) -> Result<()> {
        let client = self.connect(name, None, None, role.into())?;
        let _lock = self.lock(&client.directory)?;
        let mut profile = self.read(&client.directory)?;
        // Re-read the endpoint under the lock: a concurrent connect may have changed the port.
        let client = Client {
            profile: profile.clone(),
            directory: client.directory,
        };
        windows::export(name, &client, role)?;
        if !profile.windows_exports.contains(&role) {
            profile.windows_exports.push(role);
        }
        files::write_json(
            &client.directory.join("profile.json"),
            &serde_json::to_value(profile)?,
            0o600,
        )
    }
    pub fn disconnect(&self, name: &str) -> Result<()> {
        let directory = self.directory(name)?;
        let _lock = self.lock(&directory)?;
        access::disconnect(&directory)
    }
}

/// Linux rename exchange publishes a matching certificate/key pair in one operation.
/// After interruption either complete generation is visible; the temporary directory
/// contains the old generation and is disposable. The profile's CA never changes in place.
fn publish_role(staged: &Path, target: &Path) -> Result<()> {
    files::reject_symlinks(staged)?;
    files::reject_symlinks(target)?;
    if target.exists() {
        ensure!(target.is_dir(), "Invalid client certificate directory");
        let from = CString::new(staged.as_os_str().as_encoded_bytes())?;
        let to = CString::new(target.as_os_str().as_encoded_bytes())?;
        let status = unsafe {
            libc::renameat2(
                libc::AT_FDCWD,
                from.as_ptr(),
                libc::AT_FDCWD,
                to.as_ptr(),
                libc::RENAME_EXCHANGE,
            )
        };
        ensure!(
            status == 0,
            "Cannot atomically replace the certificate/key directory: {}",
            std::io::Error::last_os_error()
        );
    } else {
        fs::rename(staged, target)?;
    }
    for path in [staged.parent(), target.parent()].into_iter().flatten() {
        fs::File::open(path)?.sync_all()?;
    }
    Ok(())
}
fn validate_port(port: Option<u16>) -> Result<()> {
    ensure!(
        port.is_none_or(|p| p >= 1024 && p != 5432),
        "Choose a forwarding port from 1024 to 65535 other than the host's PostgreSQL port 5432"
    );
    Ok(())
}
fn validate_source(source: &Source) -> Result<()> {
    if let Source::Ssh { destination } = source {
        ensure!(
            !destination.is_empty()
                && !destination.starts_with('-')
                && destination
                    .bytes()
                    .all(|c| c.is_ascii_alphanumeric() || b"@._-:[]".contains(&c)),
            "Use an SSH host alias or USER@HOST, with SSH options in ~/.ssh/config"
        );
    }
    Ok(())
}
fn query(source: &Source, args: &[&str]) -> Result<String> {
    let mut command = match source {
        Source::Local if unsafe { libc::geteuid() } == 0 => database_host::kubectl(),
        Source::Local => {
            let mut c = Command::new("sudo");
            c.args([
                "-n",
                "k3s",
                "kubectl",
                "--kubeconfig",
                database_host::KUBECONFIG,
            ]);
            c
        }
        Source::Ssh { destination } => {
            let quote = |value: &str| format!("'{}'", value.replace('\'', "'\\''"));
            let remote = [
                "sudo",
                "-n",
                "k3s",
                "kubectl",
                "--kubeconfig",
                database_host::KUBECONFIG,
            ]
            .into_iter()
            .chain(args.iter().copied())
            .chain(["--request-timeout=15s"])
            .map(quote)
            .collect::<Vec<_>>()
            .join(" ");
            return files::output(Command::new("ssh").args([
                "-o",
                "BatchMode=yes",
                "-o",
                "ConnectTimeout=10",
                "--",
                destination,
                &remote,
            ]))
            .context("Cannot read the selected Azure cluster over SSH");
        }
    };
    files::output(command.args(args).arg("--request-timeout=15s"))
        .context("Cannot read the local k3s cluster; run sudo -v as your development user and check the installation")
}
fn identity(source: &Source) -> Result<(Identity, Vec<u8>)> {
    let cluster = query(
        source,
        &[
            "get",
            "namespace",
            "kube-system",
            "-o",
            "jsonpath={.metadata.uid}",
        ],
    )?;
    let volume = query(
        source,
        &[
            "get",
            "pvc",
            "goblin-postgres-data",
            "-n",
            "goblin",
            "-o",
            "jsonpath={.metadata.uid}",
        ],
    )?;
    let ca = STANDARD
        .decode(
            query(
                source,
                &[
                    "get",
                    "secret",
                    "goblin-postgres-ca",
                    "-n",
                    "goblin",
                    "-o",
                    "jsonpath={.data.tls\\.crt}",
                ],
            )?
            .trim(),
        )
        .context("Invalid database CA")?;
    ensure!(
        !cluster.trim().is_empty() && !volume.trim().is_empty() && !ca.is_empty(),
        "The cluster identity, PostgreSQL volume or CA is missing"
    );
    Ok((
        Identity {
            cluster: cluster.trim().into(),
            volume: volume.trim().into(),
            ca_sha256: format!("{:x}", Sha256::digest(&ca)),
        },
        ca,
    ))
}
fn fetch_certificate(source: &Source, role: Role, ca: &[u8], stage: &Path) -> Result<()> {
    let secret: Value = serde_json::from_str(&query(
        source,
        &[
            "get",
            "secret",
            &format!("goblin-postgres-{}-tls", role.name()),
            "-n",
            "goblin",
            "-o",
            "json",
        ],
    )?)?;
    let decode = |key: &str| -> Result<Vec<u8>> {
        let bytes = STANDARD.decode(
            secret["data"][key]
                .as_str()
                .context("Incomplete client Secret")?,
        )?;
        ensure!(!bytes.is_empty(), "Incomplete client Secret");
        Ok(bytes)
    };
    ensure!(
        decode("ca.crt")? == ca,
        "Client certificate belongs to a different CA"
    );
    let directory = stage.join(role.name());
    files::directory(&directory, 0o700)?;
    for file in ["tls.crt", "tls.key"] {
        files::atomic_write(&directory.join(file), &decode(file)?, 0o600, false)?;
    }
    files::output(
        Command::new("openssl")
            .args(["verify", "-purpose", "sslclient", "-CAfile"])
            .arg(stage.join("ca.crt"))
            .arg(directory.join("tls.crt")),
    )
    .context("The client certificate is expired or untrusted")?;
    let cert = files::output(
        Command::new("openssl")
            .args(["x509", "-pubkey", "-noout", "-in"])
            .arg(directory.join("tls.crt")),
    )?;
    let key = files::output(
        Command::new("openssl")
            .args(["pkey", "-pubout", "-in"])
            .arg(directory.join("tls.key")),
    )?;
    ensure!(
        cert.trim() == key.trim(),
        "Client certificate and private key do not match"
    );
    Ok(())
}
