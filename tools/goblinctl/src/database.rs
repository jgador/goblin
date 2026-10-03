use crate::assets;
use crate::environment;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use base64::Engine;
use base64::engine::general_purpose::STANDARD;
use rand::RngCore;
use serde_json::Value;
use serde_json::json;
use std::path::Path;
use std::process::Command;

pub fn connection(role: &str, directory: &Path, host: &str, port: u16) -> String {
    let quote = |name: &str| {
        format!(
            "\"{}\"",
            directory
                .join(name)
                .display()
                .to_string()
                .replace('"', "\"\"")
        )
    };
    format!(
        "Host={host};Port={port};Database=goblin;Username=goblin_{role};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;Root Certificate={};SSL Certificate={};SSL Key={}",
        quote("ca.crt"),
        quote("tls.crt"),
        quote("tls.key")
    )
}
pub fn admin_secret() -> Result<Value> {
    let mut bytes = [0; 32];
    rand::rngs::OsRng.try_fill_bytes(&mut bytes)?;
    let password: String = bytes.iter().map(|b| format!("{b:02x}")).collect();
    Ok(
        json!({"apiVersion":"v1","kind":"Secret","type":"Opaque","metadata":{"name":"goblin-postgres-admin","namespace":"goblin"},"stringData":{"password":password}}),
    )
}
pub fn export(config_root: &Path, secrets: &Value, port: u16) -> Result<()> {
    ensure!(port > 0, "Invalid database port");
    let items = secrets["items"]
        .as_array()
        .context("Missing client Secrets")?;
    let mut outputs = Vec::new();
    for role in ["app", "admin"] {
        let name = format!("goblin-postgres-{role}-tls");
        let secret = items
            .iter()
            .find(|s| s["metadata"]["name"] == name)
            .context("Missing client Secret")?;
        for key in ["ca.crt", "tls.crt", "tls.key"] {
            let data = STANDARD
                .decode(
                    secret["data"][key]
                        .as_str()
                        .context("Incomplete client Secret")?,
                )
                .context("Invalid client Secret encoding")?;
            ensure!(
                data.iter().any(|b| !b.is_ascii_whitespace()),
                "The client certificate Secret is incomplete"
            );
            outputs.push((
                config_root.join(".goblin-postgres").join(role).join(key),
                data,
            ));
        }
    }
    let web = json!({"Goblin":connection("app", Path::new("/etc/goblin-postgres"), "goblin-postgres", 5432)});
    let tooling = json!({"Goblin":connection("app", &config_root.join(".goblin-postgres/app"), "localhost", port),
        "GoblinAdmin":connection("admin", &config_root.join(".goblin-postgres/admin"), "localhost", port)});
    for (relative, values) in [
        ("backend/src/Goblin.Web/appsettings.json", web),
        ("backend/tools/Goblin.Database/appsettings.json", tooling),
    ] {
        let path = config_root.join(relative);
        let mut settings = if path.exists() {
            files::json(&path)?
        } else {
            json!({})
        };
        if settings.get("ConnectionStrings").is_none() {
            settings["ConnectionStrings"] = json!({});
        }
        settings["ConnectionStrings"]
            .as_object_mut()
            .context("Invalid connection settings")?
            .extend(
                values
                    .as_object()
                    .context("Invalid generated connection settings")?
                    .clone(),
            );
        outputs.push((
            path,
            format!("{}\n", serde_json::to_string_pretty(&settings)?).into_bytes(),
        ));
    }
    // Validate the complete document and all paths before modifying credentials/settings.
    for (path, _) in &outputs {
        files::reject_symlinks(path)?;
    }
    for name in [
        ".goblin-postgres",
        ".goblin-postgres/app",
        ".goblin-postgres/admin",
    ] {
        let path = config_root.join(name);
        files::directory(&path, 0o700)?;
        files::set_owner(&path, files::sudo_owner())?;
    }
    for (path, bytes) in outputs {
        files::atomic_write(&path, &bytes, 0o600, true)?;
    }
    println!(
        "Exported client certificates and updated password-free web/tooling appsettings.json files."
    );
    Ok(())
}
fn replace_named(items: &mut Value, replacement: Value) -> Result<()> {
    if items.is_null() {
        *items = json!([]);
    }
    let items = items.as_array_mut().context("Expected named list")?;
    if let Some(existing) = items.iter_mut().find(|i| i["name"] == replacement["name"]) {
        *existing = replacement;
    } else {
        items.push(replacement);
    }
    Ok(())
}
pub fn patch(sandbox: &Value, connection: &str) -> Result<Option<Value>> {
    let original = sandbox
        .pointer("/spec/podTemplate/spec")
        .context("Missing Sandbox pod specification")?;
    let mut spec = original.clone();
    replace_named(
        &mut spec["volumes"],
        json!({"name":"postgres-client","secret":{"secretName":"goblin-postgres-app-tls","defaultMode":288}}),
    )?;
    let container = spec["containers"]
        .as_array_mut()
        .context("Missing Sandbox containers")?
        .iter_mut()
        .find(|c| c["name"] == "auth")
        .context("Missing auth container")?;
    replace_named(
        &mut container["volumeMounts"],
        json!({"name":"postgres-client","mountPath":"/etc/goblin-postgres","readOnly":true}),
    )?;
    replace_named(
        &mut container["env"],
        json!({"name":environment::CONNECTIONSTRINGS_GOBLIN,"value":connection}),
    )?;
    Ok(if &spec == original {
        None
    } else {
        Some(json!({"spec":{"podTemplate":{"spec":spec}}}))
    })
}
pub fn migration_job(image: &str) -> Value {
    json!({"apiVersion":"batch/v1","kind":"Job","metadata":{"name":"goblin-schema","namespace":"goblin","labels":{"app.kubernetes.io/managed-by":"goblinctl"}},"spec":{"backoffLimit":0,"template":{"metadata":{"labels":{"goblin-database-access":"true"}},"spec":{"automountServiceAccountToken":false,"restartPolicy":"Never","securityContext":{"runAsNonRoot":true,"runAsUser":1000,"runAsGroup":1000,"fsGroup":1000},"containers":[{"name":"migrate","image":image,"imagePullPolicy":"IfNotPresent","command":["dotnet","/tools/database/Goblin.Database.dll","apply","/migrations"],"env":[{"name":environment::CONNECTIONSTRINGS_GOBLINADMIN,"value":"Host=goblin-postgres;Database=goblin;Username=goblin_admin;SSL Mode=VerifyFull;Root Certificate=/etc/postgres/ca.crt;SSL Certificate=/etc/postgres/tls.crt;SSL Key=/etc/postgres/tls.key;GSS Encryption Mode=Disable"}],"securityContext":{"allowPrivilegeEscalation":false,"readOnlyRootFilesystem":true,"capabilities":{"drop":["ALL"]}},"volumeMounts":[{"name":"admin","mountPath":"/etc/postgres","readOnly":true},{"name":"tmp","mountPath":"/tmp"}]}],"volumes":[{"name":"admin","secret":{"secretName":"goblin-postgres-admin-tls","defaultMode":288}},{"name":"tmp","emptyDir":{}}]}}}})
}

/// Reconcile the owned migration job after interruption before another can run.
pub fn migration_state(job: &Value, image: &str) -> Result<&'static str> {
    ensure!(
        job["metadata"]["name"] == "goblin-schema"
            && job["metadata"]["labels"]["app.kubernetes.io/managed-by"] == "goblinctl"
            && job["spec"]["template"]["spec"]["containers"][0]["image"] == image,
        "Existing migration job needs administrator inspection; ownership or image differs"
    );
    if let Some(conditions) = job["status"]["conditions"].as_array() {
        for condition in conditions {
            if condition["status"] == "True" {
                match condition["type"].as_str() {
                    Some("Complete") => return Ok("complete"),
                    Some("Failed") => return Ok("failed"),
                    _ => (),
                }
            }
        }
    }
    Ok("active")
}

/// Runtime scripts and manifests are embedded. Configuration exports stay at the requested destination.
pub fn setup(config_root: &Path, port: Option<u16>) -> Result<()> {
    let temp = tempfile::tempdir()?;
    assets::unpack(temp.path(), assets::DATABASE)?;
    let mut cmd = Command::new("bash");
    cmd.arg(temp.path().join("deploy/postgres/setup.sh"))
        .env(environment::GOBLINCTL, std::env::current_exe()?)
        .env(environment::GOBLIN_CONFIG_ROOT, config_root);
    if let Some(port) = port {
        cmd.args(["--port", &port.to_string()]);
    }
    files::run(&mut cmd)
}
pub fn migrate(image: &str) -> Result<()> {
    let temp = tempfile::tempdir()?;
    assets::unpack(temp.path(), assets::DATABASE)?;
    files::run(
        Command::new("bash")
            .arg(temp.path().join("deploy/postgres/migrate.sh"))
            .arg(image)
            .env(environment::GOBLINCTL, std::env::current_exe()?),
    )
}
