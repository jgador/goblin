use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Parser;
use clap::Subcommand;
use goblinctl::credentials;
use goblinctl::environment;
use goblinctl::files;
use goblinctl::postgres::Access;
use goblinctl::postgres::Store;
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

mod azure;
mod candidate;
mod dependencies;
mod goblin_release;
mod package;
mod preparation;
mod release;

#[derive(Parser)]
struct Cli {
    #[command(subcommand)]
    command: Task,
}
#[derive(Subcommand)]
enum Task {
    /// Resolve Docker images and verify Goblin's installer dependency.
    Dependencies {
        #[command(subcommand)]
        command: dependencies::Task,
    },
    /// Launch the development app after preparing credentials.
    Dev {
        #[arg(long, default_value = "wsl")]
        profile: String,
        #[arg(long)]
        without_database: bool,
    },
    /// Package an already-built native binary deterministically.
    Package {
        #[arg(long)]
        binary: PathBuf,
        #[arg(long, default_value = "x86_64-unknown-linux-musl")]
        target: String,
        #[arg(long, default_value = ".artifacts/goblinctl")]
        output: PathBuf,
    },
    /// Prepare, verify and publish immutable Goblin and installer releases.
    Release {
        #[command(subcommand)]
        command: goblin_release::Task,
    },
    /// Generate Azure templates and a version-specific portal form.
    Azure {
        #[arg(long, default_value = ".artifacts/azure")]
        output: PathBuf,
    },
    /// Normalize EF's generated C# line endings and UTF-8 BOM.
    NormalizeEf,
    /// Run opt-in database tests against the selected development installation.
    TestPostgres {
        #[arg(long, default_value = "wsl")]
        profile: String,
    },
    /// Apply local SQL migrations through a selected profile.
    DatabaseMigrate {
        #[arg(long, default_value = "wsl")]
        profile: String,
    },
    /// Regenerate EF mappings through a selected profile.
    DatabaseScaffold {
        #[arg(long, default_value = "wsl")]
        profile: String,
    },
}
fn main() {
    if let Err(e) = execute() {
        eprintln!("xtask: {e:#}");
        std::process::exit(1);
    }
}
fn execute() -> Result<()> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .context("Missing tools directory")?
        .parent()
        .context("Missing repository directory")?;
    std::env::set_current_dir(root)?;
    match Cli::parse().command {
        Task::Dependencies { command } => dependencies::execute(root, command),
        Task::Dev {
            profile,
            without_database,
        } => {
            let client = if without_database
                || std::env::var(environment::GOBLIN_WORK_ENABLED)
                    .is_ok_and(|v| v.eq_ignore_ascii_case("false"))
            {
                None
            } else {
                Some(Store::user()?.connect(&profile, None, None, Access::App)?)
            };
            credentials::dev(root, client.as_ref())
        }
        Task::Package {
            binary,
            target,
            output,
        } => package(&binary, &target, &output),
        Task::Release { command } => goblin_release::execute(root, command),
        Task::Azure { output } => {
            let source = files::output(Command::new("git").args(["rev-parse", "HEAD"]))?;
            azure::generate(root, &output, "0.0.0-preview.1", source.trim())
        }
        Task::NormalizeEf => normalize(Path::new("backend/src/Goblin.Persistence/Generated")),
        Task::TestPostgres { profile } => {
            let client = Store::user()?.connect(&profile, None, None, Access::Both)?;
            let mut command = Command::new("dotnet");
            command.args(["test", "backend/Goblin.slnx"]);
            client.configure_tests(&mut command, Access::Both);
            files::run(&mut command)
        }
        Task::DatabaseMigrate { profile } => {
            let client = Store::user()?.connect(&profile, None, None, Access::Admin)?;
            let mut command = Command::new("dotnet");
            command.args([
                "run",
                "--project",
                "backend/tools/Goblin.Database",
                "--",
                "apply",
                "backend/database/migrations",
            ]);
            client.configure(&mut command, Access::Admin);
            files::run(&mut command)
        }
        Task::DatabaseScaffold { profile } => {
            let client = Store::user()?.connect(&profile, None, None, Access::App)?;
            let mut command = Command::new("bash");
            command
                .arg("backend/scripts/scaffold-database.sh")
                .arg("--configured");
            client.configure(&mut command, Access::App);
            files::run(&mut command)
        }
    }
}
fn package(binary: &Path, target: &str, output: &Path) -> Result<()> {
    ensure!(
        target == release::TARGET,
        "Only the tested x86-64 Linux target is released"
    );
    let revision = files::output(Command::new("git").args(["rev-parse", "HEAD"]))?;
    let dirty = !files::output(Command::new("git").args(["status", "--porcelain"]))?
        .trim()
        .is_empty();
    package::create(
        Path::new("."),
        binary,
        output,
        &package::Identity {
            version: env!("CARGO_PKG_VERSION"),
            revision: revision.trim(),
            dirty,
        },
    )?;
    Ok(())
}

fn normalize(path: &Path) -> Result<()> {
    for entry in fs::read_dir(path)? {
        let path = entry?.path();
        if path.is_dir() {
            normalize(&path)?;
        } else if path.extension().is_some_and(|s| s == "cs") {
            let text = fs::read_to_string(&path)?;
            fs::write(
                &path,
                text.trim_start_matches('\u{feff}')
                    .replace("\r\n", "\n")
                    .replace('\r', "\n"),
            )?;
        }
    }
    Ok(())
}
