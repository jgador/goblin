use crate::environment;
use anyhow::Context;
use anyhow::Result;
use anyhow::bail;
use anyhow::ensure;
use std::fs;
use std::io::Write;
use std::os::unix::fs::MetadataExt;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;
use std::process::Command;
use std::process::Stdio;

pub fn reject_symlinks(path: &Path) -> Result<()> {
    for ancestor in path.ancestors() {
        if let Ok(metadata) = fs::symlink_metadata(ancestor) {
            ensure!(
                !metadata.file_type().is_symlink(),
                "Private storage must not use symbolic links"
            );
        }
    }
    Ok(())
}

pub fn sudo_owner() -> Option<(u32, u32)> {
    if unsafe { libc::geteuid() } != 0 {
        return None;
    }
    Some((
        std::env::var(environment::SUDO_UID).ok()?.parse().ok()?,
        std::env::var(environment::SUDO_GID).ok()?.parse().ok()?,
    ))
}

pub fn set_owner(path: &Path, owner: Option<(u32, u32)>) -> Result<()> {
    if let Some((uid, gid)) = owner {
        std::os::unix::fs::chown(path, Some(uid), Some(gid))?;
    }
    Ok(())
}

pub fn directory(path: &Path, mode: u32) -> Result<()> {
    reject_symlinks(path)?;
    fs::create_dir_all(path)?;
    fs::set_permissions(path, fs::Permissions::from_mode(mode))?;
    Ok(())
}

/// Persist complete files before exposing them to readers; also persist the rename.
pub fn atomic_write(path: &Path, content: &[u8], mode: u32, developer_owned: bool) -> Result<()> {
    reject_symlinks(path)?;
    let parent = path.parent().context("File must have a parent directory")?;
    fs::create_dir_all(parent)?;
    let mut temporary = tempfile::NamedTempFile::new_in(parent)?;
    temporary
        .as_file()
        .set_permissions(fs::Permissions::from_mode(mode))?;
    let owner = if developer_owned { sudo_owner() } else { None }.or_else(|| {
        if unsafe { libc::geteuid() } == 0 {
            fs::metadata(path).ok().map(|m| (m.uid(), m.gid()))
        } else {
            None
        }
    });
    set_owner(temporary.path(), owner)?;
    temporary.write_all(content)?;
    temporary.as_file().sync_all()?;
    temporary
        .persist(path)
        .context("Cannot replace file atomically")?;
    fs::File::open(parent)?.sync_all()?;
    Ok(())
}

pub fn json(path: &Path) -> Result<serde_json::Value> {
    serde_json::from_slice(&fs::read(path)?).context("Invalid JSON document")
}
pub fn write_json(path: &Path, value: &serde_json::Value, mode: u32) -> Result<()> {
    atomic_write(
        path,
        format!("{}\n", serde_json::to_string_pretty(value)?).as_bytes(),
        mode,
        false,
    )
}
pub fn run(command: &mut Command) -> Result<()> {
    command.env_remove(environment::GOBLIN_LOCAL_PASSWORD);
    let result = command.status().context("Cannot start required program")?;
    ensure!(
        result.success(),
        "Command failed; inspect the local installation logs"
    );
    Ok(())
}
pub fn output(command: &mut Command) -> Result<String> {
    command.env_remove(environment::GOBLIN_LOCAL_PASSWORD);
    let result = command.output().context("Cannot start required program")?;
    ensure!(
        result.status.success(),
        "Command failed; inspect the local installation logs"
    );
    String::from_utf8(result.stdout).context("Command returned invalid text")
}
pub fn input(command: &mut Command, bytes: &[u8]) -> Result<()> {
    command.env_remove(environment::GOBLIN_LOCAL_PASSWORD);
    let mut child = command.stdin(Stdio::piped()).spawn()?;
    let written = child
        .stdin
        .take()
        .context("Missing subprocess input")?
        .write_all(bytes);
    let status = child.wait()?;
    written?;
    ensure!(
        status.success(),
        "Command failed; inspect the local installation logs"
    );
    Ok(())
}
pub fn remove_file(path: &Path) -> Result<()> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(e.into()),
    }
}
pub fn executable(name: &str) -> bool {
    std::env::var_os(environment::PATH).is_some_and(|p| {
        std::env::split_paths(&p).any(|d| {
            fs::metadata(d.join(name))
                .is_ok_and(|m| m.is_file() && m.permissions().mode() & 0o111 != 0)
        })
    })
}
pub fn require_root() -> Result<()> {
    if unsafe { libc::geteuid() } != 0 {
        bail!("This operation requires root. Run goblinctl through sudo.");
    }
    Ok(())
}
