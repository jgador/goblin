use goblinctl::contract_values::SetupAction;
use goblinctl::contract_values::SetupStep;
use goblinctl::credentials;
use goblinctl::database;
use goblinctl::environment;
use goblinctl::files;
use goblinctl::install;
use goblinctl::local::Local;
use goblinctl::setup;
use serde_json::json;
use std::fs;
use std::net::TcpListener;
use std::os::unix::fs::PermissionsExt;
use std::path::Path;
use std::process::Command;

#[test]
fn concurrent_progress_preserves_every_update_and_completes_the_named_step() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("status.json");
    setup::update(&path, SetupAction::Init, "").unwrap();
    setup::update(&path, SetupAction::Begin, "").unwrap();
    std::thread::scope(|scope| {
        for id in ["k3s", "image", "prefetch", "sandbox"] {
            let path = &path;
            scope.spawn(move || {
                setup::update(path, SetupAction::Start, id).unwrap();
                for i in 0..10 {
                    setup::update_scoped(
                        path,
                        SetupAction::Detail,
                        &format!("{id} progress {i}"),
                        Some(id.parse().unwrap()),
                    )
                    .unwrap();
                }
                setup::update(path, SetupAction::Complete, id).unwrap();
            });
        }
    });
    let state = files::json(&path).unwrap();
    assert_eq!(state["logs"].as_array().unwrap().len(), 49);
    for id in ["k3s", "image", "prefetch", "sandbox"] {
        assert_eq!(
            state["steps"]
                .as_array()
                .unwrap()
                .iter()
                .find(|s| s["id"] == id)
                .unwrap()["status"],
            "complete"
        );
    }
    setup::update(&path, SetupAction::Start, "database").unwrap();
    setup::update(&path, SetupAction::FailStep, "database").unwrap();
    setup::update(&path, SetupAction::FailStep, "prefetch").unwrap();
    // A worker already being spawned may report its start after the first failure.
    setup::update(&path, SetupAction::Start, "sandbox").unwrap();
    setup::update(&path, SetupAction::Failed, "private failure payload").unwrap();
    let state = files::json(&path).unwrap();
    assert_eq!(state["currentStep"], "database");
    assert!(!state.to_string().contains("private failure payload"));
    setup::update(&path, SetupAction::Begin, "").unwrap();
    let state = files::json(&path).unwrap();
    assert!(state["failedStep"].is_null());
    assert!(
        state["steps"]
            .as_array()
            .unwrap()
            .iter()
            .all(|step| step["startedAt"].is_null() && step["detail"].is_null())
    );
}

#[test]
fn public_log_tail_is_bounded_and_retains_attempt_identity() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("status.json");
    setup::update(&path, SetupAction::Init, "").unwrap();
    setup::update(&path, SetupAction::Begin, "").unwrap();
    for i in 0..320 {
        setup::update_scoped(
            &path,
            SetupAction::Detail,
            &format!("Image progress {i}"),
            Some(SetupStep::Prefetch),
        )
        .unwrap();
    }
    setup::update(&path, SetupAction::Begin, "").unwrap();
    let state = files::json(&path).unwrap();
    let logs = state["logs"].as_array().unwrap();
    assert_eq!(logs.len(), 300);
    assert!(
        logs.windows(2)
            .all(|pair| pair[0]["id"].as_u64() < pair[1]["id"].as_u64())
    );
    assert_eq!(logs[0]["attempt"], 1);
    assert_eq!(logs.last().unwrap()["attempt"], 2);
    assert!(
        setup::update_scoped(
            &path,
            SetupAction::Detail,
            "bad\nmessage",
            Some(SetupStep::Image)
        )
        .is_err()
    );
}

#[test]
fn interrupted_migration_is_reconciled_before_replacement() {
    let mut job = database::migration_job("localhost/goblin-auth:test");
    assert_eq!(
        database::migration_state(&job, "localhost/goblin-auth:test").unwrap(),
        "active"
    );
    job["status"] = json!({"conditions":[{"type":"Failed","status":"False"}]});
    assert_eq!(
        database::migration_state(&job, "localhost/goblin-auth:test").unwrap(),
        "active"
    );
    for (condition, expected) in [("Complete", "complete"), ("Failed", "failed")] {
        job["status"] = json!({"conditions":[{"type":condition,"status":"True"}]});
        assert_eq!(
            database::migration_state(&job, "localhost/goblin-auth:test").unwrap(),
            expected
        );
    }
    assert!(database::migration_state(&job, "localhost/goblin-auth:other").is_err());
    job["metadata"]["labels"] = json!({});
    assert!(database::migration_state(&job, "localhost/goblin-auth:test").is_err());
}

#[test]
fn origin_validation_preserves_the_configured_authority() {
    for origin in ["http://localhost", "http://localhost:8788"] {
        assert_eq!(
            install::origin_authority(origin, "localhost").unwrap(),
            origin.strip_prefix("http://").unwrap()
        );
    }
    for origin in [
        "https://localhost",
        "http://localhost/",
        "http://localhost:0",
        "http://localhost:65536",
        "http://localhost:080",
        "http://localhost:80?x",
        "http://localhost@evil",
        "http://evil",
        "http://localhost\n",
        "http://localhost:",
        "http://localhost:80/path",
    ] {
        assert!(
            install::origin_authority(origin, "localhost").is_err(),
            "{origin:?}"
        );
    }
}

#[test]
fn failed_setup_and_restarts_keep_attempt_history_without_claiming_readiness() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("status.json");
    for (action, value) in [
        (SetupAction::Init, ""),
        (SetupAction::Begin, ""),
        (SetupAction::Start, "k3s"),
        (SetupAction::Complete, ""),
        (SetupAction::Start, "image"),
        (SetupAction::Failed, "private upstream failure"),
    ] {
        setup::update(&path, action, value).unwrap();
    }
    let state = files::json(&path).unwrap();
    assert_eq!(state["status"], "failed");
    assert_eq!(state["attempt"], 1);
    assert!(
        !fs::read_to_string(&path)
            .unwrap()
            .contains("private upstream")
    );
    setup::update(&path, SetupAction::Begin, "").unwrap();
    let state = files::json(&path).unwrap();
    assert_eq!(state["attempt"], 2);
    for step in state["steps"].as_array().unwrap() {
        assert_eq!(step["status"], "waiting");
        assert!(step.get("finishedAt").is_none());
        assert!(step.get("error").is_none());
    }
    setup::update(&path, SetupAction::Start, "image").unwrap();
    let state = files::json(&path).unwrap();
    assert_eq!(state["steps"][4]["attempt"], 2);
    assert_eq!(
        fs::metadata(path).unwrap().permissions().mode() & 0o777,
        0o644
    );
}

#[test]
fn local_ownership_is_required_before_reset() {
    let dir = tempfile::tempdir().unwrap();
    let repo = dir.path().join("repo");
    fs::create_dir(&repo).unwrap();
    let local = Local {
        repo: repo.clone(),
        system: dir.path().join("system"),
    };
    let owner = local.path("var/lib/goblin/local-test/config.json");
    for config in [
        json!({"mode":"direct","repo":"/another-checkout"}),
        json!({"mode":"vm","repo":repo}),
    ] {
        files::write_json(&owner, &config, 0o600).unwrap();
        assert!(
            local
                .owned_config()
                .unwrap_err()
                .to_string()
                .contains("another runner or checkout")
        );
        assert!(
            local
                .reset()
                .unwrap_err()
                .to_string()
                .contains("another runner or checkout")
        );
        assert_eq!(files::json(&owner).unwrap(), config);
    }
    fs::remove_file(owner).unwrap();
    assert!(local.reset().is_err());
}

#[test]
fn retained_and_legacy_credentials_migrate_without_new_passwords() {
    let dir = tempfile::tempdir().unwrap();
    let repo = dir.path().join("repo");
    fs::create_dir(&repo).unwrap();
    let local = Local {
        repo,
        system: dir.path().join("system"),
    };
    let retained = local.path("var/lib/goblin/install/private/owner-password");
    let legacy = local.repo.join(".goblin-local/login-password");
    let verifier = credentials::hash("retained-test").unwrap();
    credentials::save(&retained, &verifier).unwrap();
    files::atomic_write(&legacy, b"retained-test\n", 0o600, false).unwrap();
    let path = local.prepare_password().unwrap();
    assert_eq!(credentials::read(&path).unwrap().trim(), verifier);
    assert!(!legacy.exists());
    fs::remove_file(path).unwrap();
    fs::remove_file(retained).unwrap();
    files::atomic_write(&legacy, b"legacy-test\n", 0o600, false).unwrap();
    assert!(
        credentials::read(&local.prepare_password().unwrap())
            .unwrap()
            .starts_with("pbkdf2-sha256$600000$")
    );
    assert!(!legacy.exists());
}

#[test]
fn rendered_bootstrap_contains_native_pins_and_no_plaintext_password() {
    let script =
        install::render_bootstrap("goblin.example", "test-source", "0.1.0", &"a".repeat(64))
            .unwrap();
    assert!(!script.contains("__GOBLIN"));
    assert!(!script.contains("python3"));
    assert!(script.contains(environment::GOBLIN_PASSWORD_HASH_FILE));
    assert!(script.contains(&"a".repeat(64)));
    let mut child = Command::new("bash")
        .arg("-n")
        .stdin(std::process::Stdio::piped())
        .spawn()
        .unwrap();
    use std::io::Write;
    child
        .stdin
        .take()
        .unwrap()
        .write_all(script.as_bytes())
        .unwrap();
    assert!(child.wait().unwrap().success());
}

// Run the service scenarios in a child with a private PATH. No global test environment
// mutations and no real host administration commands are possible through these mocks.
#[test]
fn local_services_preserve_connections_credentials_and_paused_data() {
    let Ok(root) = std::env::var(environment::GOBLIN_NATIVE_TEST_ROOT) else {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path();
        let bin = root.join("bin");
        fs::create_dir(&bin).unwrap();
        let script = r##"#!/bin/bash
set -eu
name=${0##*/}
printf '%s %s\n' "$name" "$*" >> "$GOBLIN_NATIVE_TEST_ROOT/calls"
case "$name" in
systemctl)
  if [[ "$1" == is-active ]]; then
    [[ "$3" == k3s.service || -f "$GOBLIN_NATIVE_TEST_ROOT/database-active" ]]
  fi
  ;;
k3s)
  case "$*" in
    *'get service'*) printf '10.43.23.45';;
    *'create secret'*) printf '{"kind":"Secret"}';;
    *'apply '*) cat >/dev/null;;
  esac
  ;;
esac
"##;
        for name in ["systemctl", "k3s", "curl", "git", "flock"] {
            let path = bin.join(name);
            fs::write(&path, script).unwrap();
            fs::set_permissions(path, fs::Permissions::from_mode(0o755)).unwrap();
        }
        let output = Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "local_services_preserve_connections_credentials_and_paused_data",
                "--nocapture",
            ])
            .env(environment::GOBLIN_NATIVE_TEST_ROOT, root)
            .env(
                environment::PATH,
                format!("{}:/usr/bin:/bin", bin.display()),
            )
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        return;
    };
    let root = Path::new(&root);
    let repo = root.join("repo");
    fs::create_dir(&repo).unwrap();
    let local = Local {
        repo: repo.clone(),
        system: root.join("system"),
    };
    let owner = local.path("var/lib/goblin/local-test/config.json");
    let mut config = json!({"mode":"direct","repo":repo,"http_port":8788});
    files::write_json(&owner, &config, 0o600).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    assert!(
        local
            .configure_database(&mut config, Some(port))
            .unwrap_err()
            .to_string()
            .contains("occupied")
    );
    assert_eq!(files::json(&owner).unwrap(), config);
    assert!(
        !fs::read_to_string(root.join("calls"))
            .unwrap()
            .contains("k3s kubectl")
    );
    drop(listener);
    local.configure_database(&mut config, Some(port)).unwrap();
    let socket = local.path("etc/systemd/system/goblin-local-postgres.socket");
    let service = local.path("etc/systemd/system/goblin-local-postgres.service");
    let socket_text = fs::read_to_string(&socket).unwrap();
    assert!(
        socket_text.contains(&format!("ListenStream=127.0.0.1:{port}"))
            && socket_text.contains(&format!("ListenStream=[::1]:{port}"))
    );
    assert!(!socket_text.contains("0.0.0.0"));
    assert!(
        fs::read_to_string(&service)
            .unwrap()
            .contains("systemd-socket-proxyd 10.43.23.45:5432")
    );
    assert_eq!(
        fs::metadata(socket).unwrap().permissions().mode() & 0o777,
        0o644
    );
    assert_eq!(files::json(&owner).unwrap()["postgres_port"], port);
    fs::write(root.join("calls"), "").unwrap();
    fs::write(root.join("database-active"), "").unwrap();
    let _live = TcpListener::bind(("127.0.0.1", port)).unwrap();
    local.configure_database(&mut config, None).unwrap();
    assert!(
        !fs::read_to_string(root.join("calls"))
            .unwrap()
            .contains("systemctl stop")
    );
    // Resume a completed installation, synchronize a changed verifier exactly once.
    for (name, bytes) in [
        ("proc/1/comm", b"systemd\n".as_slice()),
        ("etc/os-release", b"ID=ubuntu\n"),
        ("usr/lib/systemd/systemd-socket-proxyd", b""),
        ("var/lib/goblin/bootstrap-status", b"setup-ready\n"),
    ] {
        files::atomic_write(&local.path(name), bytes, 0o644, false).unwrap();
    }
    let state = local.path(setup::STATE);
    setup::update(&state, SetupAction::Init, "").unwrap();
    setup::update(&state, SetupAction::Ready, "").unwrap();
    let retained = local.path("var/lib/goblin/install/private/owner-password");
    credentials::save(&retained, &credentials::hash("previous-test").unwrap()).unwrap();
    credentials::save(
        &local.password(),
        &credentials::hash("replacement-test").unwrap(),
    )
    .unwrap();
    local.start(None).unwrap();
    assert_eq!(
        credentials::read(&retained).unwrap(),
        credentials::read(&local.password()).unwrap()
    );
    let calls = fs::read_to_string(root.join("calls")).unwrap();
    assert!(calls.contains("delete pod"));
    assert!(!calls.contains("replacement-test"));
    fs::write(root.join("calls"), "").unwrap();
    local.start(None).unwrap();
    assert!(
        !fs::read_to_string(root.join("calls"))
            .unwrap()
            .contains("k3s kubectl")
    );
    local.stop().unwrap();
    assert!(owner.with_file_name("paused").exists());
    assert!(retained.exists());
    let calls = fs::read_to_string(root.join("calls")).unwrap();
    assert!(calls.contains("disable --now goblin-local-postgres.socket"));
}

#[test]
fn native_activation_is_repeatable_and_preserves_installation_state() {
    let dir = tempfile::tempdir().unwrap();
    let status = dir.path().join("var/lib/goblin/install/status.json");
    let password = dir
        .path()
        .join("var/lib/goblin/install/private/owner-password");
    let legacy = dir.path().join("opt/goblin/setup/goblin-setup.pyz");
    files::atomic_write(&status, b"retained state", 0o644, false).unwrap();
    files::atomic_write(&password, b"retained verifier", 0o600, false).unwrap();
    files::atomic_write(&legacy, b"obsolete executable", 0o755, false).unwrap();
    for _ in 0..2 {
        install::activate_tooling(dir.path(), Path::new(env!("CARGO_BIN_EXE_goblinctl"))).unwrap();
        let output = Command::new(dir.path().join("usr/local/bin/goblinctl"))
            .arg("--version")
            .env(environment::PATH, "/nonexistent")
            .output()
            .unwrap();
        assert!(output.status.success());
        assert!(String::from_utf8_lossy(&output.stdout).starts_with("goblinctl "));
        assert_eq!(fs::read(&status).unwrap(), b"retained state");
        assert_eq!(fs::read(&password).unwrap(), b"retained verifier");
        assert!(!legacy.exists());
    }
}

#[test]
fn closed_setup_contracts_reject_unknown_cli_values_without_mutating_state() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("status.json");
    setup::update(&path, SetupAction::Init, "").unwrap();
    let before = fs::read(&path).unwrap();
    for value in ["unknown", "Start", "1"] {
        assert!(value.parse::<SetupAction>().is_err());
    }
    for action in [
        SetupAction::Start,
        SetupAction::FailStep,
        SetupAction::Complete,
    ] {
        assert!(setup::update(&path, action, "unknown-step").is_err());
        assert_eq!(fs::read(&path).unwrap(), before);
    }
    let output = Command::new(env!("CARGO_BIN_EXE_goblinctl"))
        .args(["internal", "state", "unknown-action", "--path"])
        .arg(&path)
        .output()
        .unwrap();
    assert!(!output.status.success());
    assert_eq!(fs::read(&path).unwrap(), before);
}
