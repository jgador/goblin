use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Parser;
use clap::Subcommand;
use goblinctl::credentials;
use goblinctl::environment;
use goblinctl::files;
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

mod azure;
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
    Dev,
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
    /// Run opt-in persistence tests using the private local tooling settings.
    TestPostgres,
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
        Task::Dev => credentials::dev(root),
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
        Task::TestPostgres => {
            let settings =
                files::json(Path::new("backend/tools/Goblin.Database/appsettings.json"))?;
            files::run(
                Command::new("dotnet")
                    .args(["test", "backend/tests/Goblin.Persistence.Tests"])
                    .env(
                        environment::GOBLIN_TEST_POSTGRES_ADMIN,
                        settings["ConnectionStrings"]["GoblinAdmin"]
                            .as_str()
                            .context("Missing administrator connection")?,
                    )
                    .env(
                        environment::GOBLIN_TEST_POSTGRES_APP,
                        settings["ConnectionStrings"]["Goblin"]
                            .as_str()
                            .context("Missing application connection")?,
                    ),
            )
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
