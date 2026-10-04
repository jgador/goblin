//! Deterministic native archives built from the selected source and explicit version.
use crate::release;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use flate2::Compression;
use flate2::GzBuilder;
use goblinctl::files;
use goblinctl::install;
use sha2::Digest;
use sha2::Sha256;
use std::fs;
use std::path::Path;
use std::process::Command;

pub struct Identity<'a> {
    pub version: &'a str,
    pub revision: &'a str,
    pub dirty: bool,
}

pub fn create(
    root: &Path,
    binary: &Path,
    output: &Path,
    identity: &Identity<'_>,
) -> Result<release::Release> {
    release::validate_version(identity.version)?;
    let target = release::TARGET;
    let version = files::output(Command::new(binary).arg("--version"))?;
    ensure!(
        version.trim() == format!("goblinctl {}", identity.version),
        "Binary does not report the selected goblinctl version; backport coordinated build support to the release branch"
    );
    fs::create_dir_all(output)?;
    let bytes = fs::read(binary)?;
    verify_static_linux(&bytes)?;
    let name = format!("goblinctl-{target}.tar.gz");
    let gzip = GzBuilder::new()
        .mtime(0)
        .write(Vec::new(), Compression::best());
    let mut archive = tar::Builder::new(gzip);
    let mut contents = vec![("goblinctl", bytes)];
    for name in ["LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"] {
        contents.push((name, fs::read(root.join(name))?));
    }
    for (name, contents) in contents {
        let mut header = tar::Header::new_gnu();
        header.set_mode(if name == "goblinctl" { 0o755 } else { 0o644 });
        header.set_uid(0);
        header.set_gid(0);
        header.set_mtime(0);
        header.set_size(contents.len() as u64);
        header.set_cksum();
        archive.append_data(&mut header, name, contents.as_slice())?;
    }
    let archive = archive.into_inner()?.finish()?;
    let checksum = format!("{:x}", Sha256::digest(&archive));
    fs::write(output.join(&name), archive)?;
    fs::write(output.join("SHA256SUMS"), format!("{checksum}  {name}\n"))?;
    let record = release::Release {
        schema_version: 1,
        version: identity.version.into(),
        target: target.into(),
        sha256: checksum.clone(),
        source_revision: identity.revision.into(),
        source_dirty: identity.dirty,
        cargo_lock_sha256: install::checksum(&root.join("Cargo.lock"))?,
        installer: release::inputs(root)?,
    };
    files::write_json(
        &output.join("release.json"),
        &serde_json::to_value(&record)?,
        0o644,
    )?;
    println!("{}: sha256 {checksum}", output.join(name).display());
    Ok(record)
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
