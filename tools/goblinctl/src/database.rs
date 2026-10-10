use crate::assets;
use crate::environment;
use crate::files;
use anyhow::Result;
use anyhow::ensure;
use rand::RngCore;
use serde_json::Value;
use serde_json::json;
use std::process::Command;

pub fn admin_secret() -> Result<Value> {
    let mut bytes = [0; 32];
    rand::rngs::OsRng.try_fill_bytes(&mut bytes)?;
    let password: String = bytes.iter().map(|b| format!("{b:02x}")).collect();
    Ok(
        json!({"apiVersion":"v1","kind":"Secret","type":"Opaque","metadata":{"name":"goblin-postgres-admin","namespace":"goblin"},"stringData":{"password":password}}),
    )
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

/// Provision the selected Kubernetes cluster without exporting client credentials.
pub fn setup() -> Result<()> {
    let temp = tempfile::tempdir()?;
    assets::unpack(temp.path(), assets::DATABASE)?;
    let mut cmd = Command::new("bash");
    cmd.arg(temp.path().join("deploy/postgres/setup.sh"))
        .env(environment::GOBLINCTL, std::env::current_exe()?);
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
