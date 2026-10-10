use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Args;
use clap::Parser;
use clap::Subcommand;
use goblinctl::assets;
use goblinctl::credentials;
use goblinctl::database;
use goblinctl::database_host;
use goblinctl::deployment;
use goblinctl::files;
use goblinctl::install;
use goblinctl::local::Local;
use goblinctl::local::{self};
use goblinctl::operations;
use goblinctl::postgres::Access;
use goblinctl::postgres::Role;
use goblinctl::postgres::Source;
use goblinctl::postgres::Store;
use goblinctl::postgres::Usage;
use goblinctl::progress;
use goblinctl::setup;
use serde_json::Value;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

#[derive(Parser)]
#[command(
    version = goblinctl::VERSION,
    about = "Install and administer Goblin",
    long_about = "Install and administer Goblin. The web UI controls Work; this CLI manages the installation and its infrastructure."
)]
struct Cli {
    /// Source checkout for local installation and owner credentials.
    #[arg(long = "repo", global = true)]
    config_root: Option<PathBuf>,
    #[command(subcommand)]
    command: Commands,
}
#[derive(Subcommand)]
enum Commands {
    /// Report the installer version and code-generated installation schema.
    Metadata {
        #[arg(long)]
        json: bool,
    },
    /// Check installation inputs without changing the host.
    ValidateInstall {
        #[arg(long)]
        request: PathBuf,
        #[arg(long, default_value = ".")]
        source: PathBuf,
    },
    /// Install on this Ubuntu host, or inspect/retry an installation.
    Install(InstallArgs),
    /// Inspect live services and application readiness (exit 1 when unhealthy).
    Status {
        #[arg(long)]
        json: bool,
    },
    /// Run read-only infrastructure and configuration checks.
    Doctor {
        #[arg(long)]
        json: bool,
    },
    /// Read private installation logs; requires access to the host logs.
    Logs {
        #[arg(long)]
        follow: bool,
    },
    /// Manage the dedicated local Ubuntu/WSL cluster.
    Local {
        #[command(subcommand)]
        command: LocalCommand,
    },
    /// Prepare or replace owner credentials.
    Password {
        #[command(subcommand)]
        command: PasswordCommand,
    },
    /// Provision PostgreSQL, export credentials, or apply SQL migrations.
    Db {
        #[command(subcommand)]
        command: DbCommand,
    },
    #[command(hide = true)]
    Internal {
        #[command(subcommand)]
        command: InternalCommand,
    },
}
#[derive(Args)]
struct InstallArgs {
    /// Public DNS hostname. Omit for a dedicated local installation.
    #[arg(long)]
    hostname: Option<String>,
    #[arg(long, default_value = "master")]
    source_ref: String,
    #[arg(long, value_parser = clap::value_parser!(u16).range(1024..))]
    http_port: Option<u16>,
    #[command(subcommand)]
    command: Option<InstallCommand>,
}
#[derive(Subcommand)]
enum InstallCommand {
    Status {
        #[arg(long)]
        json: bool,
    },
    Retry,
}
#[derive(Subcommand)]
enum LocalCommand {
    Start {
        #[arg(long, value_parser = clap::value_parser!(u16).range(1024..))]
        http_port: Option<u16>,
    },
    Stop,
    Reset {
        #[arg(long, required = true)]
        yes: bool,
    },
    Status {
        #[arg(long)]
        json: bool,
    },
    Retry,
    Logs {
        #[arg(long)]
        follow: bool,
    },
    Password,
    Database,
}
#[derive(Subcommand)]
enum PasswordCommand {
    Set {
        #[arg(long)]
        replace: bool,
        #[arg(long)]
        path: Option<PathBuf>,
    },
}
#[derive(Subcommand)]
enum DbCommand {
    /// Provision PostgreSQL in the selected Kubernetes cluster (no client exports).
    Setup,
    /// Configure this installation's loopback endpoint on port 5432 (requires root).
    Host {
        #[arg(long, hide = true, default_value = "/")]
        system_root: PathBuf,
    },
    /// Run the deployment migration Job in the selected cluster.
    Migrate { image: String },
    /// Refresh certificates, establish access and verify a profile's TLS login.
    Connect {
        profile: String,
        #[arg(long, conflicts_with = "ssh")]
        local: bool,
        #[arg(long)]
        ssh: Option<String>,
        #[arg(long, value_parser = clap::value_parser!(u16).range(1024..))]
        forward_port: Option<u16>,
        #[arg(long, value_enum, default_value = "app")]
        role: Access,
    },
    /// Refresh an existing profile without changing its identity or forwarding port.
    Refresh {
        profile: String,
        #[arg(long, value_enum, default_value = "app")]
        role: Access,
    },
    /// Export certificates and Windows connection settings through WSL interop.
    Export {
        profile: String,
        #[arg(long, value_parser = ["windows"], required = true)]
        client: String,
        #[arg(long, value_enum, default_value = "app")]
        role: Role,
    },
    /// Stop this profile's WSL listener or SSH tunnel; retain its settings.
    Disconnect { profile: String },
    /// Run a development command with the selected profile's connections.
    Run {
        profile: String,
        #[arg(long, value_enum, default_value = "app")]
        role: Access,
        /// Explicitly expose opt-in PostgreSQL test connections to the child.
        #[arg(long, value_enum, default_value = "development")]
        usage: Usage,
        #[arg(last = true, required = true)]
        command: Vec<std::ffi::OsString>,
    },
}
#[derive(Subcommand)]
enum InternalCommand {
    PrepareInstall {
        #[arg(long)]
        request: PathBuf,
        #[arg(long)]
        source: PathBuf,
        #[arg(long)]
        destination: PathBuf,
    },
    Activate {
        #[arg(long)]
        binary: PathBuf,
        #[arg(long, default_value = "/")]
        system_root: PathBuf,
    },
    HashPassword,
    ValidatePassword {
        path: PathBuf,
    },
    Serve {
        #[arg(long, default_value = setup::STATE)]
        state: PathBuf,
        #[arg(long, default_value = "0.0.0.0")]
        host: String,
        #[arg(long, default_value_t = 80)]
        port: u16,
        #[arg(long)]
        health_socket: Option<PathBuf>,
    },
    State {
        action: goblinctl::contract_values::SetupAction,
        #[arg(default_value = "")]
        value: String,
        #[arg(long, default_value = setup::STATE)]
        path: PathBuf,
        #[arg(long)]
        step: Option<goblinctl::contract_values::SetupStep>,
    },
    BuildProgress {
        #[arg(long, default_value = setup::STATE)]
        path: PathBuf,
    },
    PrefetchImages {
        #[arg(long)]
        source: PathBuf,
    },
    Unpack {
        destination: PathBuf,
    },
    JsonTest {
        path: PathBuf,
        key: String,
        value: String,
    },
    Origin {
        origin: String,
        hostname: String,
    },
    NodeAddress,
    DockerConfig {
        #[arg(default_value = "/etc/docker/daemon.json")]
        path: PathBuf,
    },
    RenderOverlay {
        path: PathBuf,
        hostname: String,
        image: String,
        origin: String,
    },
    AdminSecret,
    MigrationJob {
        image: String,
    },
    MigrationState {
        image: String,
    },
    Forward,
    Snapshot {
        destination: PathBuf,
    },
    Preflight {
        #[arg(long)]
        port: Option<u16>,
    },
}
fn stdin_json() -> Result<Value> {
    serde_json::from_reader(std::io::stdin()).context("Invalid JSON input")
}
fn print_json(value: &Value) -> Result<()> {
    println!("{}", serde_json::to_string(value)?);
    Ok(())
}

fn main() {
    if let Err(error) = execute(Cli::parse()) {
        eprintln!("goblinctl: {error}");
        std::process::exit(1);
    }
}
fn execute(cli: Cli) -> Result<()> {
    // Client access must work outside a checkout and without reading root-owned installer state.
    if let Commands::Db { command } = cli.command {
        return database_action(command);
    }
    let config_root = match cli.config_root {
        Some(path) => {
            if path.exists() {
                path.canonicalize()?
            } else {
                std::path::absolute(path)?
            }
        }
        None => {
            let cwd = std::env::current_dir()?;
            if cwd.join("Cargo.toml").exists() && cwd.join("deploy").exists() {
                cwd
            } else if Path::new("/var/lib/goblin/local-test/config.json").exists() {
                PathBuf::from(
                    files::json(Path::new("/var/lib/goblin/local-test/config.json"))?["repo"]
                        .as_str()
                        .context("Invalid local owner")?,
                )
            } else if Path::new("/var/lib/goblin/install").exists() {
                PathBuf::from("/var/lib/goblin/config")
            } else {
                cwd
            }
        }
    };
    match cli.command {
        Commands::Metadata { json: _ } => print_json(&deployment::metadata()),
        Commands::ValidateInstall { request, source } => {
            deployment::InstallRequest::read(&request)?.validate(&source)
        }
        Commands::Install(args) => match args.command {
            Some(InstallCommand::Status { json }) => operations::install_status(json),
            Some(InstallCommand::Retry) => {
                if Path::new("/var/lib/goblin/local-test/config.json").exists() {
                    local_action(&config_root, LocalCommand::Retry)
                } else {
                    operations::retry()
                }
            }
            None => {
                if let Some(hostname) = args.hostname {
                    install::bootstrap(&hostname, &args.source_ref)
                } else {
                    local_action(
                        &config_root,
                        LocalCommand::Start {
                            http_port: args.http_port,
                        },
                    )
                }
            }
        },
        Commands::Status { json } => operations::status(json, false),
        Commands::Doctor { json } => operations::status(json, true),
        Commands::Logs { follow } => operations::logs(follow),
        Commands::Local { command } => local_action(&config_root, command),
        Commands::Password {
            command: PasswordCommand::Set { replace, path },
        } => {
            if path.is_none() && config_root == Path::new("/var/lib/goblin/config") {
                return operations::set_installed_password(replace);
            }
            let path = path.unwrap_or_else(|| config_root.join(".goblin-secrets/owner-password"));
            credentials::ensure(&path, replace)?;
            println!(
                "Goblin password verifier: {}\nUse the password chosen during setup to open Goblin.",
                path.display()
            );
            Ok(())
        }
        Commands::Db { .. } => unreachable!(),
        Commands::Internal { command } => match command {
            InternalCommand::PrepareInstall {
                request,
                source,
                destination,
            } => deployment::InstallRequest::read(&request)?.prepare(&source, &destination),
            InternalCommand::Activate {
                binary,
                system_root,
            } => install::activate_tooling(&system_root, &binary),
            InternalCommand::HashPassword => {
                println!("{}", credentials::hash_stdin()?);
                Ok(())
            }
            InternalCommand::ValidatePassword { path } => {
                credentials::read(&path)?;
                Ok(())
            }
            InternalCommand::Serve {
                state,
                host,
                port,
                health_socket,
            } => setup::serve(&state, &host, port, health_socket.as_deref()),
            InternalCommand::State {
                action,
                value,
                path,
                step,
            } => setup::update_scoped(&path, action, &value, step),
            InternalCommand::BuildProgress { path } => progress::build_output(&path),
            InternalCommand::PrefetchImages { source } => {
                for image in deployment::prefetch_images(&source)? {
                    println!("{image}");
                }
                Ok(())
            }
            InternalCommand::Unpack { destination } => assets::unpack(&destination, assets::SETUP),
            InternalCommand::JsonTest { path, key, value } => {
                let expected: Value = serde_json::from_str(&value).unwrap_or(Value::String(value));
                ensure!(files::json(&path)?[key] == expected, "JSON check failed");
                Ok(())
            }
            InternalCommand::Origin { origin, hostname } => {
                println!("{}", install::origin_authority(&origin, &hostname)?);
                Ok(())
            }
            InternalCommand::NodeAddress => {
                println!("{}", install::node_address(&stdin_json()?)?);
                Ok(())
            }
            InternalCommand::DockerConfig { path } => install::docker_config(&path),
            InternalCommand::RenderOverlay {
                path,
                hostname,
                image,
                origin,
            } => install::render_overlay(&path, &hostname, &image, &origin),
            InternalCommand::AdminSecret => print_json(&database::admin_secret()?),
            InternalCommand::MigrationJob { image } => print_json(&database::migration_job(&image)),
            InternalCommand::MigrationState { image } => {
                println!("{}", database::migration_state(&stdin_json()?, &image)?);
                Ok(())
            }
            InternalCommand::Forward => local::forward(),
            InternalCommand::Snapshot { destination } => {
                local::snapshot_source(&config_root, &destination)
            }
            InternalCommand::Preflight { port } => {
                print_json(&Local::new(&config_root)?.preflight(port)?)
            }
        },
    }
}
fn local_action(config_root: &Path, command: LocalCommand) -> Result<()> {
    if unsafe { libc::geteuid() } != 0 {
        use std::os::unix::process::CommandExt;
        return Err(Command::new("sudo")
            .arg("--preserve-env=GOBLIN_LOCAL_PASSWORD")
            .arg(std::env::current_exe()?)
            .args(
                if std::env::args_os()
                    .any(|a| a == "--repo" || a.to_string_lossy().starts_with("--repo="))
                {
                    vec![]
                } else {
                    vec![
                        std::ffi::OsString::from("--repo"),
                        config_root.as_os_str().to_owned(),
                    ]
                },
            )
            .args(std::env::args_os().skip(1))
            .exec()
            .into());
    }
    unsafe {
        libc::umask(0o077);
    }
    let local = Local::new(config_root)?;
    match command {
        LocalCommand::Password => {
            println!(
                "Local password verifier: {}\nThe original password is not stored and cannot be displayed.",
                local.password().display()
            );
            Ok(())
        }
        LocalCommand::Status { json } => {
            local.owned_config()?;
            operations::install_status(json)
        }
        LocalCommand::Logs { follow } => {
            local.owned_config()?;
            operations::logs(follow)
        }
        command => local.locked(|local| match command {
            LocalCommand::Start { http_port } => local.start(http_port),
            LocalCommand::Stop => local.stop(),
            LocalCommand::Reset { yes } => {
                ensure!(yes, "Reset requires --yes");
                local.reset()
            }
            LocalCommand::Retry => local.retry(),
            LocalCommand::Database => local.configure_database(&local.owned_config()?),
            _ => unreachable!(),
        }),
    }
}

fn database_action(command: DbCommand) -> Result<()> {
    match command {
        DbCommand::Setup => database::setup(),
        DbCommand::Host { system_root } => {
            files::require_root()?;
            if system_root == Path::new("/") {
                database_host::configure(&system_root)
            } else {
                database_host::configure_with_probe(&system_root, || Ok(()))
            }
        }
        DbCommand::Migrate { image } => database::migrate(&image),
        DbCommand::Connect {
            profile,
            local,
            ssh,
            forward_port,
            role,
        } => {
            let source = if local {
                Some(Source::Local)
            } else {
                ssh.map(|destination| Source::Ssh { destination })
            };
            Store::user()?.connect(&profile, source, forward_port, role)?;
            Ok(())
        }
        DbCommand::Refresh { profile, role } => {
            Store::user()?.connect(&profile, None, None, role)?;
            Ok(())
        }
        DbCommand::Export {
            profile,
            client: _,
            role,
        } => Store::user()?.export_windows(&profile, role),
        DbCommand::Disconnect { profile } => Store::user()?.disconnect(&profile),
        DbCommand::Run {
            profile,
            role,
            usage,
            command,
        } => {
            let client = Store::user()?.connect(&profile, None, None, role)?;
            let mut child = Command::new(&command[0]);
            child.args(&command[1..]);
            match usage {
                Usage::Development => client.configure(&mut child, role),
                Usage::Tests => client.configure_tests(&mut child, role),
            }
            files::run(&mut child)
        }
    }
}
