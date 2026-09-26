use crate::files;
use crate::local;
use crate::setup;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use serde_json::Value;
use serde_json::json;
use std::fs;
use std::path::Path;
use std::process::Command;
use std::process::Stdio;

pub fn status(as_json: bool, doctor: bool) -> Result<()> {
    let state = Path::new(setup::STATE);
    let progress = if state.exists() {
        Some(files::json(state)?)
    } else {
        None
    };
    let mut checks = vec![
        json!({"name":"installation","ok":progress.is_some()}),
        json!({"name":"systemd","ok":fs::read_to_string("/proc/1/comm").is_ok_and(|s|s.trim()=="systemd")}),
        json!({"name":"k3s","ok":local::active("k3s.service")}),
    ];
    if files::executable("k3s") {
        for (name, args) in [
            (
                "kubernetes",
                vec!["get", "--raw=/readyz", "--request-timeout=5s"],
            ),
            (
                "agent-sandbox",
                vec![
                    "wait",
                    "--for=condition=Established",
                    "crd/sandboxes.agents.x-k8s.io",
                    "--timeout=5s",
                ],
            ),
            (
                "postgresql",
                vec![
                    "rollout",
                    "status",
                    "statefulset/goblin-postgres",
                    "-n",
                    "goblin",
                    "--timeout=5s",
                ],
            ),
        ] {
            let ok = Command::new("k3s")
                .arg("kubectl")
                .args(args)
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .env_remove("GOBLIN_LOCAL_PASSWORD")
                .status()
                .is_ok_and(|s| s.success());
            checks.push(json!({"name":name,"ok":ok}));
        }
    }
    let origin = fs::read_to_string("/var/lib/goblin/public-url").unwrap_or_default();
    if origin.starts_with("http://") || origin.starts_with("https://") {
        let output = Command::new("curl")
            .args([
                "--fail",
                "--silent",
                "--noproxy",
                "*",
                "--connect-timeout",
                "3",
                "--max-time",
                "5",
                &format!("{}/readyz", origin.trim()),
            ])
            .env_remove("GOBLIN_LOCAL_PASSWORD")
            .output();
        let ok = output.is_ok_and(|o| {
            o.status.success()
                && serde_json::from_slice::<Value>(&o.stdout).is_ok_and(|v| v["ready"] == true)
        });
        checks.push(json!({"name":"application","ok":ok}));
    } else {
        checks.push(json!({"name":"application","ok":false}));
    }
    if doctor {
        for deployment in [
            "cert-manager",
            "cert-manager-cainjector",
            "cert-manager-webhook",
        ] {
            let ok = Command::new("k3s")
                .args([
                    "kubectl",
                    "rollout",
                    "status",
                    &format!("deployment/{deployment}"),
                    "-n",
                    "cert-manager",
                    "--timeout=5s",
                ])
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .env_remove("GOBLIN_LOCAL_PASSWORD")
                .status()
                .is_ok_and(|s| s.success());
            checks.push(json!({"name":deployment,"ok":ok}));
        }
        let private = fs::metadata("/var/lib/goblin/install/private");
        use std::os::unix::fs::MetadataExt;
        checks.push(json!({"name":"private-configuration","ok":private.is_ok_and(|m| m.uid()==0 && m.mode() & 0o077 == 0)}));
    }
    let healthy = checks.iter().all(|c| c["ok"] == true);
    let result = json!({"version":1,"healthy":healthy,"installation":progress,"checks":checks});
    if as_json {
        println!("{}", serde_json::to_string_pretty(&result)?);
    } else {
        println!(
            "Goblin: {}",
            if healthy {
                "healthy"
            } else {
                "needs attention"
            }
        );
        for check in checks {
            println!(
                "  {}: {}",
                check["name"]
                    .as_str()
                    .context("Missing health check name")?,
                if check["ok"] == true {
                    "ok"
                } else {
                    "unavailable"
                }
            );
        }
    }
    ensure!(healthy, "One or more health checks failed");
    Ok(())
}
pub fn install_status(as_json: bool) -> Result<()> {
    let state = files::json(Path::new(setup::STATE))
        .context("Bootstrap is incomplete; inspect goblinctl logs")?;
    if as_json {
        println!("{}", serde_json::to_string_pretty(&state)?);
    } else {
        println!(
            "{} (attempt {})",
            state["status"].as_str().unwrap_or("unknown"),
            state["attempt"]
        );
        println!("{}", state["message"].as_str().unwrap_or(""));
        for step in state["steps"]
            .as_array()
            .context("Invalid installation steps")?
        {
            println!(
                "  {:8} {}",
                step["status"].as_str().unwrap_or("unknown"),
                step["label"].as_str().unwrap_or("")
            );
        }
    }
    Ok(())
}
pub fn retry() -> Result<()> {
    files::require_root()?;
    let state = files::json(Path::new(setup::STATE)).context("Bootstrap is incomplete")?;
    ensure!(
        state["status"] != "ready",
        "Installation is already complete"
    );
    files::run(Command::new("systemctl").args([
        "enable",
        "--now",
        "goblin-setup.service",
        "goblin-installer.service",
    ]))?;
    println!("Installer retry started; retained source and password are reused.");
    Ok(())
}
pub fn logs(follow: bool) -> Result<()> {
    let paths: Vec<_> = [
        "/var/log/goblin-bootstrap.log",
        "/var/log/goblin-installer.log",
    ]
    .into_iter()
    .filter(|p| Path::new(p).exists())
    .collect();
    if paths.is_empty() {
        println!("No installation log exists yet.");
        return Ok(());
    }
    let mut cmd = Command::new("tail");
    if follow {
        cmd.arg("-F");
    } else {
        cmd.args(["-n", "100"]);
    }
    files::run(cmd.args(paths))
}

pub fn set_installed_password(replace: bool) -> Result<()> {
    use crate::credentials;
    use fs2::FileExt;
    files::require_root()?;
    let lock = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open("/var/lib/goblin/install/installer.lock")?;
    lock.try_lock_exclusive()
        .context("Installation is running; wait before changing its password")?;
    let path = Path::new("/var/lib/goblin/install/private/owner-password");
    if !replace {
        credentials::read(path)?;
        println!("Owner password is configured. Use --replace to change it.");
        return Ok(());
    }
    ensure!(
        files::json(Path::new(setup::STATE))?["status"] == "ready",
        "Finish installation before changing its password"
    );
    let temporary = tempfile::tempdir_in(path.parent().context("Missing credential directory")?)?;
    let new_path = credentials::ensure(&temporary.path().join("owner-password"), false)?;
    let verifier = credentials::read(&new_path)?;
    let secret = files::output(
        Command::new("k3s")
            .args([
                "kubectl",
                "create",
                "secret",
                "generic",
                "goblin-owner-password",
                "-n",
                "goblin",
            ])
            .arg(format!("--from-file=owner-password={}", new_path.display()))
            .args(["--dry-run=client", "-o", "json"]),
    )?;
    files::input(
        Command::new("k3s").args([
            "kubectl",
            "apply",
            "--server-side",
            "--field-manager=goblin-bootstrap",
            "-f",
            "-",
        ]),
        secret.as_bytes(),
    )?;
    // Once Kubernetes has accepted the verifier, persist it for future repairs.
    files::atomic_write(path, verifier.as_bytes(), 0o600, false)?;
    files::run(Command::new("k3s").args([
        "kubectl",
        "delete",
        "pod",
        "-n",
        "goblin",
        "-l",
        "app=goblin-auth",
        "--ignore-not-found=true",
        "--wait=true",
    ]))?;
    println!(
        "Owner password replaced. Application sessions are invalidated when the replacement pod starts."
    );
    Ok(())
}
