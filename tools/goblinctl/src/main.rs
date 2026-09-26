use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Args;
use clap::Parser;
use clap::Subcommand;
use goblinctl::assets;
use goblinctl::credentials;
use goblinctl::database;
use goblinctl::files;
use goblinctl::install;
use goblinctl::local::Local;
use goblinctl::local::{self};
use goblinctl::operations;
use goblinctl::setup;
use serde_json::Value;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

#[derive(Parser)]
#[command(
    version,
    about = "Install and administer Goblin",
    long_about = "Install and administer Goblin. The web UI controls Work; this CLI manages the installation and its infrastructure."
)]
struct Cli {
    /// Source checkout or destination for local credentials and database settings.
    #[arg(long, global = true)]
    repo: Option<PathBuf>,
    #[command(subcommand)]
    command: Commands,
}
#[derive(Subcommand)]
enum Commands {
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
    #[command(alias = "destroy")]
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
    Database {
        #[arg(long, value_parser = clap::value_parser!(u16).range(1024..))]
        port: Option<u16>,
    },
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
    Setup {
        #[arg(long, value_parser = clap::value_parser!(u16).range(1..))]
        port: Option<u16>,
    },
    Migrate {
        image: String,
    },
    Credentials {
        #[arg(long, default_value_t = 5432, value_parser = clap::value_parser!(u16).range(1..))]
        port: u16,
    },
    Forward {
        #[arg(long, value_parser = clap::value_parser!(u16).range(1024..))]
        port: Option<u16>,
    },
}
#[derive(Subcommand)]
enum InternalCommand {
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
        action: String,
        #[arg(default_value = "")]
        value: String,
        #[arg(long, default_value = setup::STATE)]
        path: PathBuf,
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
    DbExport {
        #[arg(long, default_value_t = 5432, value_parser = clap::value_parser!(u16).range(1..))]
        port: u16,
    },
    DbPatch,
    MigrationJob {
        image: String,
    },
    LocalPort {
        #[arg(long, default_value = "/var/lib/goblin/local-test/config.json")]
        owner: PathBuf,
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
    let repo = match cli.repo {
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
        Commands::Install(args) => match args.command {
            Some(InstallCommand::Status { json }) => operations::install_status(json),
            Some(InstallCommand::Retry) => {
                if Path::new("/var/lib/goblin/local-test/config.json").exists() {
                    local_action(&repo, LocalCommand::Retry)
                } else {
                    operations::retry()
                }
            }
            None => {
                if let Some(hostname) = args.hostname {
                    install::bootstrap(&hostname, &args.source_ref)
                } else {
                    local_action(
                        &repo,
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
        Commands::Local { command } => local_action(&repo, command),
        Commands::Password {
            command: PasswordCommand::Set { replace, path },
        } => {
            if path.is_none() && repo == Path::new("/var/lib/goblin/config") {
                return operations::set_installed_password(replace);
            }
            let path = path.unwrap_or_else(|| repo.join(".goblin-secrets/owner-password"));
            credentials::ensure(&path, replace)?;
            println!(
                "Goblin password verifier: {}\nUse the password chosen during setup to open Goblin.",
                path.display()
            );
            Ok(())
        }
        Commands::Db { command } => match command {
            DbCommand::Setup { port } => database::setup(&repo, port),
            DbCommand::Migrate { image } => database::migrate(&image),
            DbCommand::Credentials { port } => {
                let mut cmd = if files::executable("kubectl") {
                    Command::new("kubectl")
                } else {
                    let mut c = Command::new("k3s");
                    c.arg("kubectl");
                    c
                };
                let text = files::output(cmd.args([
                    "get",
                    "secret",
                    "goblin-postgres-app-tls",
                    "goblin-postgres-admin-tls",
                    "-n",
                    "goblin",
                    "-o",
                    "json",
                ]))?;
                database::export(&repo, &serde_json::from_str(&text)?, port)
            }
            DbCommand::Forward { port } => local_action(&repo, LocalCommand::Database { port }),
        },
        Commands::Internal { command } => match command {
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
            } => setup::update(&path, &action, &value),
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
            InternalCommand::DbExport { port } => database::export(&repo, &stdin_json()?, port),
            InternalCommand::DbPatch => {
                let settings = files::json(&repo.join("backend/src/Goblin.Web/appsettings.json"))?;
                if let Some(patch) = database::patch(
                    &stdin_json()?,
                    settings["ConnectionStrings"]["Goblin"]
                        .as_str()
                        .context("Missing database connection")?,
                )? {
                    print_json(&patch)?;
                }
                Ok(())
            }
            InternalCommand::MigrationJob { image } => print_json(&database::migration_job(&image)),
            InternalCommand::LocalPort { owner } => {
                let config = files::json(&owner)?;
                if config["mode"] == "direct" && config["repo"].as_str() == repo.to_str() {
                    println!("{}", config["postgres_port"].as_u64().unwrap_or(55432));
                }
                Ok(())
            }
            InternalCommand::Forward => local::forward(),
            InternalCommand::Snapshot { destination } => {
                local::snapshot_source(&repo, &destination)
            }
            InternalCommand::Preflight { port } => print_json(&Local::new(&repo)?.preflight(port)?),
        },
    }
}
fn local_action(repo: &Path, command: LocalCommand) -> Result<()> {
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
                        repo.as_os_str().to_owned(),
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
    let local = Local::new(repo)?;
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
            LocalCommand::Database { port } => {
                local.configure_database(&mut local.owned_config()?, port)
            }
            _ => unreachable!(),
        }),
    }
}
