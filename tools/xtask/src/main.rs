use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Parser;
use clap::Subcommand;
use flate2::Compression;
use flate2::GzBuilder;
use goblinctl::assets;
use goblinctl::credentials;
use goblinctl::environment;
use goblinctl::files;
use goblinctl::install;
use sha2::Digest;
use sha2::Sha256;
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

mod azure;
mod dependencies;
mod goblin_release;
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
        target == "x86_64-unknown-linux-musl",
        "Only the tested x86-64 Linux target is released"
    );
    let version = files::output(Command::new(binary).arg("--version"))?;
    ensure!(
        version.trim() == concat!("goblinctl ", env!("CARGO_PKG_VERSION")),
        "Binary version does not match workspace version"
    );
    fs::create_dir_all(output)?;
    let bytes = fs::read(binary)?;
    verify_static_linux(&bytes)?;
    let name = format!("goblinctl-{target}.tar.gz");
    let gzip = GzBuilder::new()
        .mtime(0)
        .write(Vec::new(), Compression::best());
    let mut archive = tar::Builder::new(gzip);
    for (name, contents) in
        std::iter::once(("goblinctl", bytes.as_slice())).chain(assets::LICENSING.iter().copied())
    {
        let mut header = tar::Header::new_gnu();
        header.set_mode(if name == "goblinctl" { 0o755 } else { 0o644 });
        header.set_uid(0);
        header.set_gid(0);
        header.set_mtime(0);
        header.set_size(contents.len() as u64);
        header.set_cksum();
        archive.append_data(&mut header, name, contents)?;
    }
    let archive = archive.into_inner()?.finish()?;
    let checksum = format!("{:x}", Sha256::digest(&archive));
    fs::write(output.join(&name), archive)?;
    fs::write(output.join("SHA256SUMS"), format!("{checksum}  {name}\n"))?;
    let revision = files::output(Command::new("git").args(["rev-parse", "HEAD"]))?;
    let dirty = !files::output(Command::new("git").args(["status", "--porcelain"]))?
        .trim()
        .is_empty();
    files::write_json(
        &output.join("release.json"),
        &serde_json::to_value(release::Release {
            schema_version: 1,
            version: env!("CARGO_PKG_VERSION").into(),
            target: target.into(),
            sha256: checksum.clone(),
            source_revision: revision.trim().into(),
            source_dirty: dirty,
            cargo_lock_sha256: install::checksum(Path::new("Cargo.lock"))?,
            installer: release::inputs(Path::new("."))?,
        })?,
        0o644,
    )?;
    println!("{}: sha256 {checksum}", output.join(name).display());
    Ok(())
}

fn verify_static_linux(bytes: &[u8]) -> Result<()> {
    ensure!(
        bytes.len() >= 64 && &bytes[..6] == b"\x7fELF\x02\x01" && bytes[18..20] == [62, 0],
        "Release binary must be an x86-64 Linux ELF executable"
    );
    let offset = u64::from_le_bytes(bytes[32..40].try_into()?) as usize;
    let size = u16::from_le_bytes(bytes[54..56].try_into()?) as usize;
    let count = u16::from_le_bytes(bytes[56..58].try_into()?) as usize;
    ensure!(size >= 4 && count > 0, "Missing ELF program headers");
    for index in 0..count {
        let start = offset
            .checked_add(index.checked_mul(size).context("Invalid ELF headers")?)
            .context("Invalid ELF headers")?;
        let header = bytes
            .get(start..start.checked_add(4).context("Invalid ELF header")?)
            .context("Invalid ELF header")?;
        ensure!(
            u32::from_le_bytes(header.try_into()?) != 3,
            "Release binary requires a dynamic loader; build the musl target"
        );
    }
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
