//! Application image and explicit runtime settings for the owned WSL cluster.
use crate::environment;
use crate::files;
use crate::k3s::BrowserRoute;
use crate::k3s::K3s;
use crate::k3s_runtime::Overrides;
use anyhow::Context;
use anyhow::Result;
use anyhow::bail;
use anyhow::ensure;
use flate2::read::GzDecoder;
use fs2::FileExt;
use serde_json::Value;
use serde_json::json;
use sha2::Digest;
use sha2::Sha256;
use std::collections::BTreeMap;
use std::fs;
use std::io::Read;
use std::net::Ipv4Addr;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;
use std::process::Stdio;
use std::time::Duration;
use std::time::Instant;

const CONTRACT_ROOTS: &[&str] = &[
    "backend/database/migrations",
    "deploy/platform",
    "deploy/azure/platform",
    "deploy/postgres",
];
const BUILD_TAG: &str = "localhost/goblin-platform:development";

pub fn deploy(k3s: &K3s) -> Result<()> {
    let overrides = Overrides::from_environment()?;
    files::reject_symlinks(&k3s.path("var/lib/goblin/local-test/config.json"))?;
    let config = k3s.owned_config()?;
    ensure!(
        k3s.status()?.is_some_and(|s| s["status"] == "ready"),
        "Finish installation with make k3s-start or k3s-retry before deploying."
    );
    ensure!(
        !crate::k3s::active("goblin-installer.service"),
        "The installer is running; wait for it to finish."
    );
    let lock_path = k3s.path("var/lib/goblin/install/installer.lock");
    files::reject_symlinks(&lock_path)?;
    let lock = fs::OpenOptions::new()
        .create(true)
        .append(true)
        .mode(0o600)
        .open(lock_path)?;
    lock.try_lock_exclusive()
        .context("Another installer operation is running")?;
    compatible_source(k3s)?;

    let artifacts = k3s.git_repository.join(".artifacts/k3s-deploy");
    files::directory(&artifacts, 0o700)?;
    let directory = tempfile::Builder::new()
        .prefix("run-")
        .tempdir_in(&artifacts)?
        .keep();
    files::directory(&directory.join("docker"), 0o700)?;
    files::atomic_write(
        &directory.join("docker/config.json"),
        br#"{"auths":{"https://index.docker.io/v1/":{}},"cliPluginsExtraDirs":["/usr/libexec/docker/cli-plugins","/usr/lib/docker/cli-plugins"]}"#,
        0o600,
        false,
    )?;
    let mut runner = ProcessRunner {
        k3s,
        directory: directory.clone(),
        sequence: 0,
        local_cluster_verified: false,
    };
    println!("Deployment diagnostics: {}", directory.display());
    let started = Instant::now();
    let result = update(
        &mut runner,
        &k3s.git_repository,
        &directory,
        &config,
        &overrides,
    );
    if result.is_err() && runner.local_cluster_verified {
        // Only private log files receive application diagnostics, never CLI output.
        let _ = runner.command(
            Tool::Kube,
            &[
                "logs",
                "-n",
                "goblin",
                "-l",
                "app=platform",
                "-c",
                "platform",
                "--tail=100",
            ],
            None,
            Output::Log,
        );
    }
    let report = match &result {
        Ok(receipt) => receipt.clone(),
        Err(error) => json!({"status":"failed","reason":error.to_string()}),
    };
    files::write_json(&directory.join("result.json"), &report, 0o600)?;
    println!(
        "Deployment elapsed: {:.1}s",
        started.elapsed().as_secs_f64()
    );
    let receipt = result.map_err(|error| {
        let message = format!(
            "Deployment failed: {error}; inspect {}",
            directory.display()
        );
        error.context(message)
    })?;
    files::write_json(
        &k3s.git_repository.join(".goblin-local/deployment.json"),
        &receipt,
        0o600,
    )?;
    println!(
        "Goblin ready: {}\nImage: {}\nPod: {}\nVerify the changed behavior in this instance.",
        receipt["publicOrigin"]
            .as_str()
            .context("Missing public origin receipt")?,
        receipt["image"].as_str().context("Missing image receipt")?,
        receipt["pod"].as_str().context("Missing pod receipt")?,
    );
    Ok(())
}

fn compatible_source(k3s: &K3s) -> Result<()> {
    let archive = k3s.path("var/lib/goblin/local-test/source.tar.gz");
    files::reject_symlinks(&archive)?;
    let mut installed = BTreeMap::new();
    for entry in tar::Archive::new(GzDecoder::new(fs::File::open(archive)?)).entries()? {
        let mut entry = entry?;
        let path = entry.path()?.into_owned();
        let Some(relative) = path.to_str().and_then(|p| p.strip_prefix("goblin/")) else {
            continue;
        };
        if contract_path(relative) && entry.header().entry_type().is_file() {
            let name = relative.to_owned();
            let mut bytes = Vec::new();
            entry.read_to_end(&mut bytes)?;
            installed.insert(name, format!("{:x}", Sha256::digest(bytes)));
        }
    }
    ensure!(
        !installed.is_empty(),
        "The original installation source snapshot is incomplete."
    );
    let mut current = BTreeMap::new();
    for root in CONTRACT_ROOTS {
        collect_contract(
            &k3s.git_repository,
            &k3s.git_repository.join(root),
            &mut current,
        )?;
    }
    ensure!(
        installed == current,
        "Schema or installation manifests differ from the installed baseline. Image deployment cannot apply these changes; request a deliberate make k3s-reset and reinstall."
    );
    Ok(())
}

fn contract_path(path: &str) -> bool {
    CONTRACT_ROOTS.iter().any(|root| {
        path.strip_prefix(root)
            .is_some_and(|rest| rest.starts_with('/'))
    })
}

fn collect_contract(root: &Path, path: &Path, result: &mut BTreeMap<String, String>) -> Result<()> {
    files::reject_symlinks(path)?;
    if !path.exists() {
        return Ok(());
    }
    if path.is_dir() {
        for entry in fs::read_dir(path)? {
            collect_contract(root, &entry?.path(), result)?;
        }
    } else {
        result.insert(
            path.strip_prefix(root)?
                .to_str()
                .context("Non-UTF-8 source path")?
                .to_owned(),
            crate::install::checksum(path)?,
        );
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Tool {
    Docker,
    Kube,
    Cri,
    Content,
    Curl,
}
#[derive(Clone, Copy)]
enum Output {
    Capture,
    Log,
}
trait Runner {
    fn command(
        &mut self,
        tool: Tool,
        args: &[&str],
        input: Option<&[u8]>,
        output: Output,
    ) -> Result<String>;
    fn import(&mut self, image: &str) -> Result<()>;
    fn pause(&mut self);
    fn configure_browser(&mut self, route: BrowserRoute) -> Result<()>;
    fn confirmed_cluster(&mut self) {}
}
struct ProcessRunner<'a> {
    k3s: &'a K3s,
    directory: PathBuf,
    sequence: usize,
    local_cluster_verified: bool,
}
impl ProcessRunner<'_> {
    fn process(&self, tool: Tool) -> Command {
        let mut command = Command::new(match tool {
            Tool::Docker => "docker",
            Tool::Kube | Tool::Cri | Tool::Content => "/usr/local/bin/k3s",
            Tool::Curl => "curl",
        });
        command.env_clear().env(
            environment::PATH,
            "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        );
        match tool {
            Tool::Docker => {
                command
                    .arg("--config")
                    .arg(self.directory.join("docker"))
                    .args(["--host", "unix:///var/run/docker.sock"]);
            }
            Tool::Kube => {
                command.args([
                    "kubectl",
                    "--kubeconfig=/etc/rancher/k3s/k3s.yaml",
                    "--request-timeout=15s",
                ]);
                command
                    .arg("--cache-dir")
                    .arg(self.directory.join("kubectl-cache"));
            }
            Tool::Cri => {
                command.args([
                    "crictl",
                    "--runtime-endpoint=unix:///run/k3s/containerd/containerd.sock",
                    "--timeout=10s",
                ]);
            }
            Tool::Content => {
                command.args(["ctr", "--namespace", "k8s.io", "content"]);
            }
            Tool::Curl => {}
        }
        command
    }
    fn log(&mut self, label: &str) -> Result<fs::File> {
        self.sequence += 1;
        Ok(fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .mode(0o600)
            .open(
                self.directory
                    .join(format!("step-{:03}-{label}.log", self.sequence)),
            )?)
    }
}
impl Runner for ProcessRunner<'_> {
    fn configure_browser(&mut self, route: BrowserRoute) -> Result<()> {
        self.k3s.set_browser_route(route)
    }
    fn confirmed_cluster(&mut self) {
        self.local_cluster_verified = true;
    }
    fn command(
        &mut self,
        tool: Tool,
        args: &[&str],
        input: Option<&[u8]>,
        output: Output,
    ) -> Result<String> {
        let mut command = self.process(tool);
        command.args(args);
        let label = format!("{tool:?}-{}", args.first().copied().unwrap_or("command"));
        let log = self.log(&label)?;
        command.stderr(log.try_clone()?);
        if let Some(input) = input {
            command.stdout(log);
            files::input(&mut command, input).with_context(|| format!("{label} failed"))?;
            return Ok(String::new());
        }
        match output {
            Output::Capture => {
                let result = command.output()?;
                ensure!(result.status.success(), "{label} failed");
                String::from_utf8(result.stdout).context("Command returned non-UTF-8 text")
            }
            Output::Log => {
                command.stdout(log);
                ensure!(command.status()?.success(), "{label} failed");
                Ok(String::new())
            }
        }
    }
    fn import(&mut self, image: &str) -> Result<()> {
        let log = self.log("image-import")?;
        let mut producer = self
            .process(Tool::Docker)
            .args(["save", image])
            .stdout(Stdio::piped())
            .stderr(log.try_clone()?)
            .spawn()?;
        let stream = producer.stdout.take().context("Missing image stream")?;
        let consumer = Command::new("/usr/local/bin/k3s")
            .env_clear()
            .env(
                environment::PATH,
                "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            )
            .args(["ctr", "--namespace", "k8s.io", "images", "import", "-"])
            .stdin(Stdio::from(stream))
            .stdout(log.try_clone()?)
            .stderr(log)
            .spawn();
        let mut consumer = match consumer {
            Ok(child) => child,
            Err(error) => {
                let _ = producer.kill();
                let _ = producer.wait();
                return Err(error.into());
            }
        };
        let imported = consumer.wait()?;
        let saved = producer.wait()?;
        ensure!(imported.success() && saved.success(), "Image import failed");
        Ok(())
    }
    fn pause(&mut self) {
        std::thread::sleep(Duration::from_secs(1));
    }
}

fn json_command(runner: &mut impl Runner, tool: Tool, args: &[&str]) -> Result<Value> {
    serde_json::from_str(&runner.command(tool, args, None, Output::Capture)?)
        .context("Command returned invalid JSON")
}
fn text(value: &Value, key: &str) -> Result<String> {
    Ok(value[key]
        .as_str()
        .context("Required deployment metadata is missing")?
        .to_owned())
}

fn normalized_environment(environment: &Value) -> Value {
    let mut normalized = environment.clone();
    if let Some(entries) = normalized.as_array_mut() {
        for entry in entries {
            if let Some(reference) = entry
                .get_mut("valueFrom")
                .and_then(|source| source.get_mut("fieldRef"))
                .and_then(Value::as_object_mut)
                && reference
                    .get("apiVersion")
                    .is_none_or(|version| version == "")
            {
                // Pod admission defaults ObjectFieldSelector.apiVersion to v1.
                // The Sandbox template retains the author's omitted value.
                reference.insert("apiVersion".to_owned(), json!("v1"));
            }
        }
    }
    normalized
}
fn platform_index(sandbox: &Value) -> Result<usize> {
    sandbox["spec"]["podTemplate"]["spec"]["containers"]
        .as_array()
        .context("Missing platform containers")?
        .iter()
        .position(|c| c["name"] == "platform")
        .context("Missing platform container")
}
fn platform_pods(runner: &mut impl Runner) -> Result<Value> {
    json_command(
        runner,
        Tool::Kube,
        &[
            "get",
            "pods",
            "-n",
            "goblin",
            "-l",
            "app=platform",
            "-o",
            "json",
        ],
    )
}
fn owned_pod<'a>(pods: &'a Value, sandbox_uid: &str) -> Result<Option<&'a Value>> {
    let items = pods["items"].as_array().context("Missing pod inventory")?;
    ensure!(
        items.len() <= 1,
        "Multiple platform pods exist; reconcile them before deploying."
    );
    if let Some(pod) = items.first() {
        ensure!(
            pod["metadata"]["ownerReferences"]
                .as_array()
                .is_some_and(|owners| owners.iter().any(|o| o["kind"] == "Sandbox"
                    && o["uid"] == sandbox_uid
                    && o["controller"] == true)),
            "The platform pod is not owned by the selected Sandbox"
        );
        return Ok(Some(pod));
    }
    Ok(None)
}
fn ready(pod: &Value, image: &str) -> bool {
    pod["metadata"]["deletionTimestamp"].is_null()
        && pod["spec"]["containers"].as_array().is_some_and(|cs| {
            cs.iter()
                .any(|c| c["name"] == "platform" && c["image"] == image)
        })
        && pod["status"]["containerStatuses"]
            .as_array()
            .is_some_and(|cs| {
                cs.iter()
                    .any(|c| c["name"] == "platform" && c["ready"] == true)
            })
}
fn image_id(runner: &mut impl Runner, image: &str) -> Result<String> {
    text(
        &json_command(runner, Tool::Cri, &["inspecti", image])?["status"],
        "id",
    )
}

fn configuration_id(runner: &mut impl Runner, manifest_digest: &str) -> Result<String> {
    let manifest = runner.command(
        Tool::Content,
        &["get", manifest_digest],
        None,
        Output::Capture,
    )?;
    ensure!(
        format!("sha256:{:x}", Sha256::digest(manifest.as_bytes())) == manifest_digest,
        "Imported manifest differs from the built platform manifest"
    );
    let manifest: Value = serde_json::from_str(&manifest).context("Invalid image manifest")?;
    text(&manifest["config"], "digest")
}
fn verify_pod_image(runner: &mut impl Runner, pod: &Value, expected: &str) -> Result<()> {
    let status = pod["status"]["containerStatuses"]
        .as_array()
        .context("Missing container statuses")?
        .iter()
        .find(|c| c["name"] == "platform")
        .context("Missing platform status")?;
    let running = text(status, "imageID")?;
    let running = running
        .strip_prefix("docker-pullable://")
        .or_else(|| running.strip_prefix("containerd://"))
        .unwrap_or(&running);
    ensure!(
        image_id(runner, running)? == expected,
        "The running pod image differs from the built image"
    );
    Ok(())
}
fn update(
    runner: &mut impl Runner,
    repository: &Path,
    directory: &Path,
    config: &Value,
    overrides: &Overrides,
) -> Result<Value> {
    let server = runner.command(
        Tool::Kube,
        &[
            "config",
            "view",
            "--minify",
            "-o",
            "jsonpath={.clusters[0].cluster.server}",
        ],
        None,
        Output::Capture,
    )?;
    ensure!(
        server.trim() == "https://127.0.0.1:6443" || server.trim() == "https://localhost:6443",
        "The selected kubeconfig does not point to the local k3s API"
    );
    let nodes = json_command(runner, Tool::Kube, &["get", "nodes", "-o", "json"])?;
    ensure!(
        nodes["items"]
            .as_array()
            .is_some_and(|nodes| nodes.len() == 1),
        "Image import requires the owned single-node cluster"
    );
    let sandbox = json_command(
        runner,
        Tool::Kube,
        &["get", "sandbox", "platform", "-n", "goblin", "-o", "json"],
    )?;
    let uid = text(&sandbox["metadata"], "uid")?;
    let index = platform_index(&sandbox)?;
    let container = &sandbox["spec"]["podTemplate"]["spec"]["containers"][index];
    let previous_image = text(container, "image")?;
    let settings = overrides.resolve(container, &crate::k3s::origin(config)?)?;
    let route = if settings.origin.starts_with("https://") {
        let service = json_command(
            runner,
            Tool::Kube,
            &["get", "service", "platform", "-n", "goblin", "-o", "json"],
        )?;
        BrowserRoute::Platform(platform_service(&service)?)
    } else {
        BrowserRoute::Ingress
    };
    ensure!(
        previous_image.starts_with("localhost/goblin-platform:"),
        "The platform uses an unexpected image repository"
    );
    ensure!(
        sandbox["spec"]["podTemplate"]["spec"]["volumes"]
            .as_array()
            .is_some_and(|vs| vs.iter().any(|v| v["name"] == "data"
                && v["persistentVolumeClaim"]["claimName"] == "platform-data")),
        "The platform uses unexpected persistent storage"
    );
    let pvc = json_command(
        runner,
        Tool::Kube,
        &["get", "pvc", "platform-data", "-n", "goblin", "-o", "json"],
    )?;
    ensure!(
        pvc["status"]["phase"] == "Bound",
        "The platform PVC is not bound"
    );
    let pods = platform_pods(runner)?;
    let old_pod = owned_pod(&pods, &uid)?;
    let old_uid = old_pod.map(|p| text(&p["metadata"], "uid")).transpose()?;
    runner.confirmed_cluster();
    let iid = directory.join("image-id");
    println!("Building Goblin with the retained Docker cache…");
    let started = Instant::now();
    runner.command(
        Tool::Docker,
        &[
            "build",
            "--load",
            "--network=host",
            "--platform",
            "linux/amd64",
            "--progress=plain",
            "--iidfile",
            iid.to_str().context("Invalid artifact path")?,
            "--tag",
            BUILD_TAG,
            repository.to_str().context("Invalid checkout path")?,
        ],
        None,
        Output::Log,
    )?;
    println!("Build elapsed: {:.1}s", started.elapsed().as_secs_f64());
    let built_reference = fs::read_to_string(iid)?.trim().to_owned();
    // Platform manifests stay stable when OCI index provenance timestamps vary.
    // CRI identifies their linked image configuration rather than the OCI index.
    let inspected = json_command(
        runner,
        Tool::Docker,
        &[
            "image",
            "inspect",
            "--platform",
            "linux/amd64",
            &built_reference,
        ],
    )?;
    let descriptor = &inspected[0]["Descriptor"];
    ensure!(
        matches!(
            descriptor["mediaType"].as_str(),
            Some(
                "application/vnd.oci.image.manifest.v1+json"
                    | "application/vnd.docker.distribution.manifest.v2+json"
            )
        ),
        "Docker did not return a platform-specific image manifest"
    );
    let manifest_digest = text(descriptor, "digest")?;
    let digest = manifest_digest
        .strip_prefix("sha256:")
        .context("Docker returned an invalid image ID")?;
    ensure!(
        digest.len() == 64 && digest.bytes().all(|b| b.is_ascii_hexdigit()),
        "Docker returned an invalid image digest"
    );
    let image = format!("localhost/goblin-platform:dev-{digest}");
    runner.command(
        Tool::Docker,
        &["tag", &built_reference, &image],
        None,
        Output::Log,
    )?;
    let env = settings
        .environment
        .as_array()
        .context("Missing execution image configuration")?;
    let execution_index = env
        .iter()
        .position(|e| e["name"] == environment::GOBLIN_EXECUTION_IMAGE)
        .context("Missing execution image configuration")?;
    let mut desired_env = settings.environment.clone();
    desired_env[execution_index]["value"] = json!(image);
    let unchanged = previous_image == image
        && env[execution_index]["value"] == image
        && container["env"] == desired_env
        && container["imagePullPolicy"] == "Never";
    if !unchanged {
        println!("Updating the application and replacing the platform pod…");
        let started = Instant::now();
        if previous_image != image {
            runner.import(&image)?;
        }
        let built_id = configuration_id(runner, &manifest_digest)?;
        ensure!(
            image_id(runner, &image)? == built_id,
            "Imported image differs from the Docker build"
        );
        let base = format!("/spec/podTemplate/spec/containers/{index}");
        let patch = json!([
            {"op":"test","path":"/metadata/uid","value":uid},
            {"op":"test","path":"/metadata/resourceVersion","value":sandbox["metadata"]["resourceVersion"]},
            {"op":"test","path":format!("{base}/image"),"value":previous_image},
            {"op":"replace","path":format!("{base}/image"),"value":image},
            {"op":"replace","path":format!("{base}/env"),"value":desired_env},
            {"op":"replace","path":format!("{base}/imagePullPolicy"),"value":"Never"}
        ]);
        runner.command(
            Tool::Kube,
            &[
                "patch",
                "sandbox",
                "platform",
                "-n",
                "goblin",
                "--type=json",
                "--patch-file=/dev/stdin",
            ],
            Some(serde_json::to_string(&patch)?.as_bytes()),
            Output::Log,
        )?;
        if let Some(pod) = old_pod {
            let name = text(&pod["metadata"], "name")?;
            runner.command(
                Tool::Kube,
                &[
                    "delete",
                    "pod",
                    &name,
                    "-n",
                    "goblin",
                    "--wait=true",
                    "--timeout=60s",
                ],
                None,
                Output::Log,
            )?;
        }
        println!(
            "Update/restart elapsed: {:.1}s",
            started.elapsed().as_secs_f64()
        );
    } else {
        println!("The image and application settings are unchanged; import and restart skipped.");
    }
    let started = Instant::now();
    let pod = loop {
        let pods = platform_pods(runner)?;
        if let Some(pod) = owned_pod(&pods, &uid)? {
            let replaced = unchanged
                || old_uid
                    .as_ref()
                    .is_none_or(|old| pod["metadata"]["uid"] != *old);
            if replaced && ready(pod, &image) {
                break pod.clone();
            }
            if pod["status"]["containerStatuses"]
                .as_array()
                .is_some_and(|cs| {
                    cs.iter().any(|c| {
                        ["CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull"]
                            .iter()
                            .any(|reason| c["state"]["waiting"]["reason"] == *reason)
                    })
                })
            {
                bail!(
                    "The new platform container failed to start; inspect the deployment diagnostics"
                );
            }
        }
        ensure!(
            started.elapsed() < Duration::from_secs(180),
            "The expected platform image did not become ready within 180 seconds"
        );
        runner.pause();
    };
    let built_id = configuration_id(runner, &manifest_digest)?;
    verify_pod_image(runner, &pod, &built_id)?;
    let running_index = pod["spec"]["containers"]
        .as_array()
        .and_then(|cs| cs.iter().position(|c| c["name"] == "platform"))
        .context("Missing platform container")?;
    ensure!(
        normalized_environment(&pod["spec"]["containers"][running_index]["env"])
            == normalized_environment(&desired_env),
        "The running platform settings differ from the requested settings"
    );
    runner.configure_browser(route)?;
    let endpoint = format!("{}/readyz", crate::k3s::origin(config)?);
    let health = json_command(
        runner,
        Tool::Curl,
        &[
            "--fail",
            "--silent",
            "--show-error",
            "--noproxy",
            "*",
            "--connect-timeout",
            "2",
            "--max-time",
            "5",
            &endpoint,
        ],
    )?;
    ensure!(health["ready"] == true, "The browser endpoint is not ready");
    if settings.chatgpt {
        let authority = settings
            .origin
            .strip_prefix("https://")
            .context("Invalid HTTPS origin")?;
        let discovery = json_command(
            runner,
            Tool::Curl,
            &[
                "--fail",
                "--silent",
                "--show-error",
                "--noproxy",
                "*",
                "--connect-timeout",
                "2",
                "--max-time",
                "5",
                "--header",
                &format!("Host: {authority}"),
                "--header",
                "X-Forwarded-Proto: https",
                &format!(
                    "{}/.well-known/oauth-authorization-server",
                    crate::k3s::origin(config)?
                ),
            ],
        )?;
        ensure!(
            discovery["issuer"] == format!("{}/", settings.origin),
            "The local endpoint did not return the configured OAuth issuer"
        );
    }
    println!("Readiness elapsed: {:.1}s", started.elapsed().as_secs_f64());
    Ok(
        json!({"status":"ready","image":image,"imageDigest":manifest_digest,"imageId":built_id,"pod":pod["metadata"]["name"],"podUid":pod["metadata"]["uid"],"publicOrigin":settings.origin,"chatgptEnabled":settings.chatgpt,"restarted":!unchanged,"verifiedAtUtc":chrono::Utc::now().to_rfc3339()}),
    )
}

fn platform_service(service: &Value) -> Result<Ipv4Addr> {
    ensure!(
        service["metadata"]["name"] == "platform"
            && service["metadata"]["namespace"] == "goblin"
            && service["spec"]["type"] == "ClusterIP"
            && service["spec"]["selector"] == json!({"app":"platform"})
            && service["spec"]["ports"].as_array().is_some_and(|ps| ps
                .iter()
                .any(|p| p["port"] == 8787 && p["targetPort"] == "http")),
        "The platform Service has unexpected routing"
    );
    let address: Ipv4Addr = text(&service["spec"], "clusterIP")?
        .parse()
        .context("Invalid platform Service address")?;
    ensure!(
        address.is_private(),
        "The platform Service must have a private IPv4 address"
    );
    Ok(address)
}

#[cfg(test)]
mod tests {
    use super::*;

    const ID: &str = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const IMAGE: &str = "localhost/goblin-platform:dev-1a825edfbd412eb7a57c8b94819097c83f9e9eb1164cf5127e34f1809e80a132";
    const MANIFEST: &str = r#"{"config":{"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"#;
    #[derive(Clone, Copy, PartialEq)]
    enum Failure {
        None,
        Build,
        Import,
        Patch,
        Health,
        Image,
    }
    struct Fake {
        sandbox: Value,
        pod: Value,
        calls: Vec<(Tool, Vec<String>)>,
        imports: usize,
        failure: Failure,
        server: String,
        old_polls: usize,
        deleted: bool,
        routes: Vec<BrowserRoute>,
        service: Value,
    }
    impl Fake {
        fn new(image: &str) -> Self {
            Self {
                sandbox: json!({"metadata":{"uid":"sandbox-id","resourceVersion":"10"},"spec":{"podTemplate":{"spec":{
                    "containers":[{"name":"platform","image":image,"imagePullPolicy":"Never","env":[{"name":environment::GOBLIN_EXECUTION_IMAGE,"value":image},{"name":environment::GOBLIN_PUBLIC_ORIGIN,"value":"http://localhost:8788"},{"name":environment::GOBLIN_CHATGPT_ENABLED,"value":"false"}]}],
                    "volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"platform-data"}}]
                }}}}),
                pod: json!({"metadata":{"name":"platform","uid":"old-pod","ownerReferences":[{"kind":"Sandbox","uid":"sandbox-id","controller":true}]},"spec":{"containers":[{"name":"platform","image":image,"env":[{"name":environment::GOBLIN_EXECUTION_IMAGE,"value":image},{"name":environment::GOBLIN_PUBLIC_ORIGIN,"value":"http://localhost:8788"},{"name":environment::GOBLIN_CHATGPT_ENABLED,"value":"false"}]}]},"status":{"containerStatuses":[{"name":"platform","ready":true,"imageID":ID}]}}),
                calls: Vec::new(),
                imports: 0,
                failure: Failure::None,
                server: "https://127.0.0.1:6443".to_owned(),
                old_polls: 0,
                deleted: false,
                routes: Vec::new(),
                service: json!({"metadata":{"name":"platform","namespace":"goblin"},"spec":{"type":"ClusterIP","clusterIP":"10.43.0.20","selector":{"app":"platform"},"ports":[{"port":8787,"targetPort":"http"}]}}),
            }
        }
        fn called(&self, tool: Tool, verb: &str) -> bool {
            self.calls
                .iter()
                .any(|(t, a)| *t == tool && a.first().is_some_and(|a| a == verb))
        }
    }
    impl Runner for Fake {
        fn command(
            &mut self,
            tool: Tool,
            args: &[&str],
            input: Option<&[u8]>,
            _output: Output,
        ) -> Result<String> {
            self.calls
                .push((tool, args.iter().map(|a| (*a).to_owned()).collect()));
            match tool {
                Tool::Docker if args[0]=="build" => {
                    ensure!(self.failure!=Failure::Build,"fixture build failed");
                    let index=args.iter().position(|a|*a=="--iidfile").unwrap();
                    fs::write(args[index+1],"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")?;
                    Ok(String::new())
                }
                Tool::Docker if args[0] == "image" => Ok(json!([{"Descriptor":{"mediaType":"application/vnd.oci.image.manifest.v1+json","digest":"sha256:1a825edfbd412eb7a57c8b94819097c83f9e9eb1164cf5127e34f1809e80a132"}}]).to_string()),
                Tool::Docker => Ok(String::new()),
                Tool::Content => Ok(MANIFEST.to_owned()),
                Tool::Cri => Ok(json!({"status":{"id":if self.failure==Failure::Image {"sha256:unexpected"} else {ID}}}).to_string()),
                Tool::Curl => {
                    ensure!(self.old_polls==0,"health checked before replacement");
                    if args.last().unwrap().contains("/.well-known/") {
                        let env=self.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"].as_array().unwrap();
                        let origin=env.iter().find(|e|e["name"]==environment::GOBLIN_PUBLIC_ORIGIN).unwrap()["value"].as_str().unwrap();
                        return Ok(json!({"issuer":format!("{origin}/")}).to_string());
                    }
                    Ok(json!({"ready":self.failure!=Failure::Health}).to_string())
                }
                Tool::Kube => match args[..] {
                    ["config",..] => Ok(self.server.clone()),
                    ["get","nodes",..] => Ok(json!({"items":[{}]}).to_string()),
                    ["get","sandbox",..] => Ok(self.sandbox.to_string()),
                    ["get","service",..] => Ok(self.service.to_string()),
                    ["get","pvc",..] => Ok(json!({"status":{"phase":"Bound"}}).to_string()),
                    ["get","pods",..] => {
                        let mut pod=self.pod.clone();
                        if self.deleted {
                            if self.old_polls>0 {self.old_polls-=1;} else {pod["metadata"]["uid"]=json!("new-pod");}
                            pod["spec"]["containers"][0]["image"]=self.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["image"].clone();
                            pod["spec"]["containers"][0]["env"]=self.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"].clone();
                        }
                        Ok(json!({"items":[pod]}).to_string())
                    }
                    ["patch",..] => {
                        ensure!(self.failure!=Failure::Patch,"fixture concurrent update");
                        let patch:Value=serde_json::from_slice(input.unwrap())?;
                        assert_eq!(patch[0],json!({"op":"test","path":"/metadata/uid","value":"sandbox-id"}));
                        assert_eq!(patch[1],json!({"op":"test","path":"/metadata/resourceVersion","value":"10"}));
                        self.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["image"]=patch[3]["value"].clone();
                        self.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"]=patch[4]["value"].clone();
                        Ok(String::new())
                    }
                    ["delete",..] => {self.deleted=true;Ok(String::new())}
                    _ => panic!("unexpected fixture command: {args:?}"),
                }
            }
        }
        fn import(&mut self, _: &str) -> Result<()> {
            self.imports += 1;
            ensure!(self.failure != Failure::Import, "fixture import failed");
            Ok(())
        }
        fn pause(&mut self) {}
        fn configure_browser(&mut self, route: BrowserRoute) -> Result<()> {
            self.routes.push(route);
            Ok(())
        }
    }
    fn fixture() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../.artifacts/k3s-deploy-tests");
        fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }
    fn run(fake: &mut Fake, root: &Path) -> Result<Value> {
        update(
            fake,
            root,
            root,
            &json!({"http_port":8788}),
            &Overrides::default(),
        )
    }
    #[test]
    fn pod_environment_defaults_are_accepted_without_ignoring_real_configuration_drift() {
        let root = fixture();
        let mut fake = Fake::new(IMAGE);
        fake.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"]
            .as_array_mut()
            .unwrap()
            .push(
                json!({"name":"NODE_NAME","valueFrom":{"fieldRef":{"fieldPath":"spec.nodeName"}}}),
            );
        fake.pod["spec"]["containers"][0]["env"] =
            fake.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"].clone();
        fake.pod["spec"]["containers"][0]["env"][3]["valueFrom"]["fieldRef"]["apiVersion"] =
            json!("v1");
        assert_eq!(run(&mut fake, root.path()).unwrap()["restarted"], false);
        assert!(!fake.called(Tool::Kube, "patch"));
        assert!(!fake.called(Tool::Kube, "delete"));
        for (pointer, value) in [
            (
                "/spec/containers/0/env/3/valueFrom/fieldRef/fieldPath",
                json!("metadata.name"),
            ),
            (
                "/spec/containers/0/env/3/valueFrom/fieldRef/apiVersion",
                json!("v2"),
            ),
            ("/spec/containers/0/env/2/value", json!("true")),
        ] {
            let mut stale = Fake::new(IMAGE);
            stale.sandbox = fake.sandbox.clone();
            stale.pod = fake.pod.clone();
            *stale.pod.pointer_mut(pointer).unwrap() = value;
            assert!(
                run(&mut stale, root.path())
                    .unwrap_err()
                    .to_string()
                    .contains("running platform settings")
            );
            assert!(stale.routes.is_empty());
        }
    }
    fn public_overrides() -> Overrides {
        Overrides::parse(|name| {
            Ok(Some(
                if name == environment::GOBLIN_PUBLIC_ORIGIN {
                    "https://app.example.com"
                } else {
                    "true"
                }
                .to_owned(),
            ))
        })
        .unwrap()
    }
    #[test]
    fn explicit_runtime_settings_restart_the_current_image_and_unset_retains_them() {
        let root = fixture();
        let mut fake = Fake::new(IMAGE);
        let receipt = update(
            &mut fake,
            root.path(),
            root.path(),
            &json!({"http_port":8788}),
            &public_overrides(),
        )
        .unwrap();
        assert_eq!(receipt["publicOrigin"], "https://app.example.com");
        assert_eq!(receipt["chatgptEnabled"], true);
        assert_eq!(receipt["restarted"], true);
        assert_eq!(fake.imports, 0);
        assert_eq!(
            fake.routes,
            [BrowserRoute::Platform("10.43.0.20".parse().unwrap())]
        );
        let discovery = fake
            .calls
            .iter()
            .find(|(tool, args)| {
                *tool == Tool::Curl && args.last().unwrap().contains("/.well-known/")
            })
            .unwrap();
        assert!(discovery.1.iter().any(|a| a == "Host: app.example.com"));
        assert!(discovery.1.iter().any(|a| a == "X-Forwarded-Proto: https"));
        fake.pod["spec"]["containers"][0] =
            fake.sandbox["spec"]["podTemplate"]["spec"]["containers"][0].clone();
        fake.deleted = false;
        fake.calls.clear();
        let receipt = run(&mut fake, root.path()).unwrap();
        assert_eq!(receipt["restarted"], false);
        assert_eq!(receipt["chatgptEnabled"], true);
        assert!(!fake.called(Tool::Kube, "patch"));
        assert!(!fake.called(Tool::Kube, "delete"));
        let reset = Overrides::parse(|name| {
            Ok(Some(
                if name == environment::GOBLIN_PUBLIC_ORIGIN {
                    "http://localhost:8788"
                } else {
                    "false"
                }
                .to_owned(),
            ))
        })
        .unwrap();
        update(
            &mut fake,
            root.path(),
            root.path(),
            &json!({"http_port":8788}),
            &reset,
        )
        .unwrap();
        assert_eq!(fake.routes.last(), Some(&BrowserRoute::Ingress));
    }
    #[test]
    fn invalid_settings_and_foreign_service_fail_before_build_or_mutation() {
        let root = fixture();
        let mut fake = Fake::new(IMAGE);
        let invalid = Overrides::parse(|name| {
            Ok((name == environment::GOBLIN_CHATGPT_ENABLED).then(|| "true".to_owned()))
        })
        .unwrap();
        assert!(
            update(
                &mut fake,
                root.path(),
                root.path(),
                &json!({"http_port":8788}),
                &invalid
            )
            .is_err()
        );
        assert!(!fake.called(Tool::Docker, "build"));
        fake.service["spec"]["selector"] = json!({"app":"other"});
        assert!(
            update(
                &mut fake,
                root.path(),
                root.path(),
                &json!({"http_port":8788}),
                &public_overrides()
            )
            .is_err()
        );
        assert!(!fake.called(Tool::Kube, "patch"));
        assert!(fake.routes.is_empty());
    }
    #[test]
    fn deployment_imports_and_updates_new_workers_before_replacing_only_the_platform_pod() {
        let root = fixture();
        let mut fake = Fake::new("localhost/goblin-platform:previous");
        fake.old_polls = 2;
        let receipt = run(&mut fake, root.path()).unwrap();
        assert_eq!(receipt["image"], IMAGE);
        assert_eq!(receipt["podUid"], "new-pod");
        assert_eq!(fake.imports, 1);
        assert_eq!(
            fake.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"][0]["value"],
            IMAGE
        );
        let deletion = fake
            .calls
            .iter()
            .find(|(_, args)| args[0] == "delete")
            .unwrap();
        assert_eq!(deletion.1[1..4], ["pod", "platform", "-n"]);
        assert!(!fake.called(Tool::Kube, "apply"));
    }
    #[test]
    fn unchanged_image_checks_actual_running_content_without_import_or_restart() {
        let root = fixture();
        let mut fake = Fake::new(IMAGE);
        let receipt = run(&mut fake, root.path()).unwrap();
        assert_eq!(receipt["restarted"], false);
        assert_eq!(receipt["podUid"], "old-pod");
        assert_eq!(fake.imports, 0);
        assert!(!fake.called(Tool::Kube, "patch"));
        assert!(!fake.called(Tool::Kube, "delete"));
        assert!(fake.called(Tool::Cri, "inspecti"));
    }
    #[test]
    fn worker_image_drift_requires_an_update_even_when_platform_image_is_current() {
        let root = fixture();
        let mut fake = Fake::new(IMAGE);
        fake.sandbox["spec"]["podTemplate"]["spec"]["containers"][0]["env"][0]["value"] =
            json!("localhost/goblin-platform:old-worker");
        assert_eq!(run(&mut fake, root.path()).unwrap()["restarted"], true);
        assert_eq!(fake.imports, 0);
    }
    #[test]
    fn failed_build_or_import_never_changes_the_running_platform() {
        for failure in [Failure::Build, Failure::Import, Failure::Image] {
            let root = fixture();
            let mut fake = Fake::new("localhost/goblin-platform:previous");
            fake.failure = failure;
            assert!(run(&mut fake, root.path()).is_err());
            assert!(!fake.called(Tool::Kube, "patch"));
            assert!(!fake.called(Tool::Kube, "delete"));
        }
    }
    #[test]
    fn concurrent_sandbox_change_is_not_overwritten_and_does_not_delete_a_pod() {
        let root = fixture();
        let mut fake = Fake::new("localhost/goblin-platform:previous");
        fake.failure = Failure::Patch;
        assert!(run(&mut fake, root.path()).is_err());
        assert!(!fake.called(Tool::Kube, "delete"));
    }
    #[test]
    fn remote_api_and_foreign_pod_are_rejected_before_building() {
        let root = fixture();
        let mut remote = Fake::new(IMAGE);
        remote.server = "https://cluster.example.com:6443".to_owned();
        assert!(run(&mut remote, root.path()).is_err());
        assert!(!remote.called(Tool::Docker, "build"));
        let mut foreign = Fake::new(IMAGE);
        foreign.pod["metadata"]["ownerReferences"][0]["uid"] = json!("other-sandbox");
        assert!(run(&mut foreign, root.path()).is_err());
        assert!(!foreign.called(Tool::Docker, "build"));
    }
    #[test]
    fn healthy_image_needs_a_healthy_browser_endpoint() {
        let root = fixture();
        let mut fake = Fake::new(IMAGE);
        fake.failure = Failure::Health;
        assert!(run(&mut fake, root.path()).is_err());
    }
    #[test]
    fn schema_changes_are_rejected_without_running_any_cluster_commands() {
        let root = fixture();
        let repository = root.path().join("repo");
        let system = root.path().join("system");
        let file = repository.join("backend/database/migrations/0001_initial.sql");
        fs::create_dir_all(file.parent().unwrap()).unwrap();
        fs::write(&file, "initial baseline").unwrap();
        let archive = system.join("var/lib/goblin/local-test/source.tar.gz");
        fs::create_dir_all(archive.parent().unwrap()).unwrap();
        let encoder = flate2::write::GzEncoder::new(
            fs::File::create(&archive).unwrap(),
            flate2::Compression::default(),
        );
        let mut tar = tar::Builder::new(encoder);
        tar.append_path_with_name(&file, "goblin/backend/database/migrations/0001_initial.sql")
            .unwrap();
        tar.into_inner().unwrap().finish().unwrap();
        let k3s = K3s {
            git_repository: repository,
            system,
        };
        compatible_source(&k3s).unwrap();
        fs::write(&file, "changed baseline").unwrap();
        assert!(
            compatible_source(&k3s)
                .unwrap_err()
                .to_string()
                .contains("Schema or installation")
        );
    }
}
