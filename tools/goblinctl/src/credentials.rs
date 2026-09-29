use crate::environment;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use rand::RngCore;
use sha2::Sha256;
use std::fs::File;
use std::io::IsTerminal;
use std::io::Read;
use std::path::Path;
use std::path::PathBuf;

const INVALID: &str =
    "Enter a non-blank Goblin password of up to 128 characters without control characters.";

pub fn hash(password: &str) -> Result<String> {
    ensure!(
        password.encode_utf16().count() <= 128
            && !password.trim().is_empty()
            && !password.chars().any(|c| c < ' ' || c == '\u{7f}'),
        INVALID
    );
    let mut salt = [0; 16];
    rand::rngs::OsRng
        .try_fill_bytes(&mut salt)
        .context("Cannot obtain secure randomness")?;
    let mut digest = [0; 32];
    pbkdf2::pbkdf2_hmac::<Sha256>(password.as_bytes(), &salt, 600_000, &mut digest);
    Ok(format!(
        "pbkdf2-sha256$600000${}${}",
        STANDARD.encode(salt),
        STANDARD.encode(digest)
    ))
}
pub fn hash_stdin() -> Result<String> {
    let mut bytes = Vec::new();
    std::io::stdin().take(513).read_to_end(&mut bytes)?;
    hash(std::str::from_utf8(&bytes).context(INVALID)?)
}
pub fn read(path: &Path) -> Result<String> {
    files::reject_symlinks(path)?;
    let mut bytes = Vec::new();
    File::open(path)?.take(257).read_to_end(&mut bytes)?;
    ensure!(
        bytes.is_ascii(),
        "The saved Goblin password verifier is invalid; restore it before starting."
    );
    let value = std::str::from_utf8(&bytes)
        .context("The saved Goblin password verifier is invalid; restore it before starting.")?;
    let fields: Vec<_> = value.trim().split('$').collect();
    ensure!(
        bytes.len() <= 256
            && fields.len() == 4
            && fields[0] == "pbkdf2-sha256"
            && fields[1] == "600000"
            && STANDARD.decode(fields[2]).is_ok_and(|b| b.len() == 16)
            && STANDARD.decode(fields[3]).is_ok_and(|b| b.len() == 32),
        "The saved Goblin password verifier is invalid; restore it before starting."
    );
    Ok(value.to_owned())
}
pub fn save(path: &Path, verifier: &str) -> Result<()> {
    files::reject_symlinks(path)?;
    let parent = path.parent().context("Missing password directory")?;
    files::directory(parent, 0o700)?;
    files::set_owner(parent, files::sudo_owner())?;
    files::atomic_write(
        path,
        format!("{}\n", verifier.trim_end_matches('\n')).as_bytes(),
        0o600,
        true,
    )
}
pub fn ensure(path: &Path, replace: bool) -> Result<PathBuf> {
    if !replace && (path.exists() || path.is_symlink()) {
        read(path)?;
        return Ok(path.to_owned());
    }
    let password = match std::env::var(environment::GOBLIN_LOCAL_PASSWORD) {
        Ok(password) => password,
        Err(_) => {
            ensure!(
                std::io::stdin().is_terminal(),
                "No local password is configured. Run npm run setup:password in a terminal, or supply GOBLIN_LOCAL_PASSWORD for unattended setup."
            );
            let password = rpassword::prompt_password("Goblin password: ")?;
            let confirmation = rpassword::prompt_password("Confirm Goblin password: ")?;
            ensure!(
                password == confirmation,
                "The Goblin passwords do not match. No password was saved."
            );
            password
        }
    };
    save(path, &hash(&password)?)?;
    Ok(path.to_owned())
}

pub fn dev(repo: &Path) -> Result<()> {
    use std::os::unix::process::CommandExt;
    let path = match std::env::var_os(environment::GOBLIN_PASSWORD_HASH_FILE) {
        Some(p) => {
            let p = PathBuf::from(p);
            read(&p)?;
            p.canonicalize()?
        }
        None => ensure(&repo.join(".goblin-secrets/owner-password"), false)?,
    };
    Err(std::process::Command::new("dotnet")
        .arg(repo.join("backend/src/Goblin.Web/bin/Debug/net10.0/Goblin.Web.dll"))
        .env(environment::GOBLIN_PASSWORD_HASH_FILE, path)
        .env_remove(environment::GOBLIN_LOCAL_PASSWORD)
        .exec()
        .into())
}
