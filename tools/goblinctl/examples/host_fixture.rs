//! Offline installer tests call the library with a private filesystem and mocked commands.
//! The shipping `db host` command retains its root requirement and real port probe.
use anyhow::Context;
use anyhow::Result;
use goblinctl::database_host;
use std::path::PathBuf;

fn main() -> Result<()> {
    let root = PathBuf::from(std::env::args_os().nth(1).context("Missing fixture root")?);
    database_host::configure_with_probe(&root, || Ok(()))
}
