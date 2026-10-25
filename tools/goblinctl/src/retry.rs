//! Privileged retry requests from the public setup page. Systemd hands each request
//! to a root process on a local socket; the unprivileged setup server only relays it.
use crate::credentials;
use crate::files;
use crate::setup;
use anyhow::Context;
use anyhow::Result;
use serde::Deserialize;
use serde::Serialize;
use serde_json::json;
use std::io::BufRead;
use std::io::Read;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use std::process::Stdio;
use std::time::Duration;
use std::time::SystemTime;
use std::time::UNIX_EPOCH;

pub const SOCKET: &str = "/run/goblin-setup-control.sock";
pub const REQUEST_LIMIT: u64 = 2048;
const VERIFIER: &str = "/var/lib/goblin/install/private/owner-password";
const THROTTLE: &str = "/var/lib/goblin/install/private/retry-throttle.json";
const UNTHROTTLED_FAILURES: u64 = 5;
const MAX_LOCKOUT_SECONDS: u64 = 900;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum RetryResult {
    Started,
    Unauthorized,
    Throttled,
    NotFailed,
    Running,
    Invalid,
    Unavailable,
}
impl RetryResult {
    pub const fn http_status(self) -> u16 {
        match self {
            Self::Started => 202,
            Self::Unauthorized => 401,
            Self::Throttled => 429,
            Self::NotFailed | Self::Running => 409,
            Self::Invalid => 400,
            Self::Unavailable => 503,
        }
    }
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RetryRequest {
    pub password: String,
}

pub struct Control {
    pub state: PathBuf,
    pub verifier: PathBuf,
    pub throttle: PathBuf,
}
impl Default for Control {
    fn default() -> Self {
        Self {
            state: PathBuf::from(setup::STATE),
            verifier: PathBuf::from(VERIFIER),
            throttle: PathBuf::from(THROTTLE),
        }
    }
}
impl Control {
    /// Answers one request from standard input with one JSON line on standard output.
    pub fn serve(&self) -> Result<()> {
        let mut line = Vec::new();
        std::io::stdin()
            .lock()
            .take(REQUEST_LIMIT)
            .read_until(b'\n', &mut line)?;
        let result = match serde_json::from_slice::<RetryRequest>(&line) {
            Ok(request) => self.retry(&request.password).unwrap_or_else(|error| {
                eprintln!("Setup page retry is unavailable: {error:#}");
                RetryResult::Unavailable
            }),
            Err(_) => RetryResult::Invalid,
        };
        println!("{}", json!({ "result": result }));
        Ok(())
    }

    pub fn retry(&self, password: &str) -> Result<RetryResult> {
        let state = files::json(&self.state).context("Installation status is unavailable")?;
        if state["status"] != "failed" {
            return Ok(RetryResult::NotFailed);
        }
        let now = SystemTime::now().duration_since(UNIX_EPOCH)?.as_secs();
        let mut throttle = self.throttle()?;
        if throttle.locked_until > now {
            return Ok(RetryResult::Throttled);
        }
        let verifier = credentials::read(&self.verifier)?;
        if !credentials::verify(&verifier, password) {
            throttle.failures += 1;
            if throttle.failures >= UNTHROTTLED_FAILURES {
                let doublings = (throttle.failures - UNTHROTTLED_FAILURES).min(4);
                throttle.locked_until = now + (60 << doublings).min(MAX_LOCKOUT_SECONDS);
            }
            files::write_json(
                &self.throttle,
                &json!({"failures": throttle.failures, "lockedUntil": throttle.locked_until}),
                0o600,
            )?;
            eprintln!("Rejected a setup page retry with an incorrect password.");
            // Rejections take the same minimum time, whatever caused them.
            std::thread::sleep(Duration::from_secs(1));
            return Ok(RetryResult::Unauthorized);
        }
        files::remove_file(&self.throttle)?;
        if systemctl(&["is-active", "--quiet", "goblin-installer.service"])? {
            return Ok(RetryResult::Running);
        }
        ensure_started()?;
        eprintln!("Started an installation retry from the setup page.");
        Ok(RetryResult::Started)
    }

    fn throttle(&self) -> Result<Throttle> {
        if !self.throttle.exists() {
            return Ok(Throttle::default());
        }
        let value = files::json(&self.throttle)?;
        Ok(Throttle {
            failures: value["failures"].as_u64().unwrap_or(0),
            locked_until: value["lockedUntil"].as_u64().unwrap_or(0),
        })
    }
}

#[derive(Default)]
struct Throttle {
    failures: u64,
    locked_until: u64,
}

fn systemctl(args: &[&str]) -> Result<bool> {
    Ok(Command::new("systemctl")
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .status()?
        .success())
}
fn ensure_started() -> Result<()> {
    anyhow::ensure!(
        systemctl(&["enable", "--now", "goblin-installer.service"])?,
        "Could not start goblin-installer.service"
    );
    Ok(())
}

/// Relays one retry request from the unprivileged setup server to the root helper.
pub async fn relay(socket: &Path, password: &str) -> Result<RetryResult> {
    use tokio::io::AsyncReadExt;
    use tokio::io::AsyncWriteExt;
    let mut stream = tokio::net::UnixStream::connect(socket).await?;
    let mut request = serde_json::to_vec(&json!({ "password": password }))?;
    request.push(b'\n');
    stream.write_all(&request).await?;
    stream.shutdown().await?;
    let mut response = Vec::new();
    stream.take(256).read_to_end(&mut response).await?;
    let response: serde_json::Value = serde_json::from_slice(&response)?;
    Ok(serde_json::from_value(response["result"].clone())?)
}
