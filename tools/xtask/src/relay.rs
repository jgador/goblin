//! Developer-only HTTPS relay. Nothing in this module ships in goblinctl.
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use clap::Subcommand;
use goblinctl::files;
use serde::Deserialize;
use serde::Serialize;
use serde_json::Value;
use serde_json::json;
use sha2::Digest;
use sha2::Sha256;
use std::fs;
use std::io::Read;
use std::path::Path;
use std::path::PathBuf;
use std::process::Command;

const NAME: &str = "goblin-dev-relay";
const OWNER: &str = "goblin.dev/relay-owner";
const PROXY_IMAGE: &str = "nginx:1.30.5-alpine";

#[derive(Clone, Copy, PartialEq, Eq)]
enum Endpoint {
    Local,
    Public,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Upstream {
    Development,
    Azure,
}

#[derive(Subcommand)]
pub enum Task {
    /// Route the dedicated test VM to this local build; retain the tunnel in the background.
    Start {
        /// Azure HTTPS origin; also configure this origin on the local Goblin host.
        #[arg(long)]
        origin: String,
        /// Explicit SSH username and hostname, e.g. goblinadmin@test.example.com.
        #[arg(long)]
        ssh: String,
        /// Saved VM private key. Only its path is passed to OpenSSH.
        #[arg(long)]
        identity: PathBuf,
        /// Direct Goblin HTTP port, or a port-forward to service/platform, bypassing local ingress.
        #[arg(long, default_value_t = 8787, value_parser = clap::value_parser!(u16).range(1024..))]
        local_port: u16,
    },
    /// Inspect the selected upstream and tunnel without changing routing.
    Status,
    /// Explicitly restore Azure's Goblin upstream, remove owned relay resources, and stop SSH.
    Stop,
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Session {
    origin: String,
    ssh: String,
    identity: PathBuf,
    owner: String,
    local_port: u16,
}

struct Relay {
    directory: PathBuf,
    session_name: String,
    session: Session,
}

pub fn execute(root: &Path, session_name: &str, task: Task) -> Result<()> {
    ensure!(
        !session_name.is_empty()
            && session_name.len() <= 32
            && session_name
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"_-".contains(&b)),
        "Invalid relay session name"
    );
    let directory = root.join(".artifacts").join(session_name);
    files::directory(&directory, 0o700)?;
    ensure!(
        directory.join("ssh.sock").as_os_str().len() < 100,
        "OpenSSH control socket path is too long; use a shorter checkout path"
    );
    match task {
        Task::Start {
            origin,
            ssh,
            identity,
            local_port,
        } => {
            ensure!(
                !directory.join("session.json").exists(),
                "A relay session already exists. Inspect relay status or run relay stop first."
            );
            validate_target(&origin, &ssh)?;
            let identity = identity
                .canonicalize()
                .context("The saved SSH private key does not exist")?;
            ensure!(identity.is_file(), "The SSH identity must be a file");
            let mut random = [0_u8; 16];
            fs::File::open("/dev/urandom")?.read_exact(&mut random)?;
            let owner = random.iter().map(|b| format!("{b:02x}")).collect();
            let relay = Relay {
                directory,
                session_name: session_name.to_owned(),
                session: Session {
                    origin,
                    ssh,
                    identity,
                    owner,
                    local_port,
                },
            };
            relay.start().with_context(|| {
                if relay.directory.join("session.json").exists() {
                    format!("Relay setup did not complete. Retained session state allows cleanup with {}", relay.stop_command())
                } else {
                    "Relay setup did not complete; no tunnel or route was created.".to_owned()
                }
            })
        }
        Task::Status | Task::Stop => {
            let session: Session =
                serde_json::from_value(files::json(&directory.join("session.json"))?)
                    .context("No readable relay session; start a relay first")?;
            validate_target(&session.origin, &session.ssh)?;
            ensure!(
                session.owner.len() == 32 && session.owner.bytes().all(|b| b.is_ascii_hexdigit()),
                "Invalid relay owner"
            );
            let relay = Relay {
                directory,
                session_name: session_name.to_owned(),
                session,
            };
            match task {
                Task::Status => relay.status(),
                Task::Stop => relay.stop(),
                Task::Start { .. } => unreachable!(),
            }
        }
    }
}

fn validate_target(origin: &str, ssh: &str) -> Result<()> {
    let hostname = origin
        .strip_prefix("https://")
        .context("The relay needs an HTTPS origin without a path or port")?;
    ensure!(
        hostname.contains('.')
            && hostname.split('.').all(|label| !label.is_empty()
                && !label.starts_with('-')
                && !label.ends_with('-')
                && label
                    .bytes()
                    .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-')),
        "Invalid HTTPS relay hostname"
    );
    let (user, host) = ssh
        .split_once('@')
        .context("Use an explicit SSH username@hostname")?;
    ensure!(
        !user.is_empty()
            && !user.starts_with('-')
            && user
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
            && host == hostname,
        "SSH must target the hostname in the HTTPS origin"
    );
    Ok(())
}

fn quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

impl Relay {
    fn stop_command(&self) -> String {
        format!("cargo xtask relay --session {} stop", self.session_name)
    }

    fn ssh(&self) -> Command {
        let mut command = Command::new("ssh");
        command
            .args(["-F", "/dev/null", "-T", "-S"])
            .arg(self.directory.join("ssh.sock"))
            .arg("-i")
            .arg(&self.session.identity)
            .args([
                "-o",
                "IdentitiesOnly=yes",
                "-o",
                "GlobalKnownHostsFile=/dev/null",
                "-o",
            ])
            .arg(format!(
                "UserKnownHostsFile={}",
                self.directory.join("known_hosts").display()
            ))
            .args([
                "-o",
                "StrictHostKeyChecking=ask",
                "-o",
                "ConnectTimeout=10",
                "-o",
                "ServerAliveInterval=15",
                "-o",
                "ServerAliveCountMax=3",
                "-o",
                "StreamLocalBindMask=0177",
                "-o",
                "StreamLocalBindUnlink=yes",
                "-o",
                "ExitOnForwardFailure=yes",
            ]);
        command
    }

    fn remote(&self, script: &str) -> Result<String> {
        files::output(
            self.ssh()
                .args(["-o", "BatchMode=yes", &self.session.ssh, script]),
        )
    }

    fn ingress(&self) -> Result<Value> {
        serde_json::from_str(
            &self.remote("sudo -n k3s kubectl get ingress goblin -n goblin -o json")?,
        )
        .context("Cannot inspect the Goblin ingress")
    }

    fn socket_directory(&self) -> String {
        format!("/var/lib/goblin/dev-relay/{}", self.session.owner)
    }

    fn keys(&self, endpoint: Endpoint) -> Result<String> {
        let mut command = Command::new("curl");
        command.args([
            "--fail",
            "--silent",
            "--show-error",
            "--noproxy",
            "*",
            "--connect-timeout",
            "5",
            "--max-time",
            "15",
        ]);
        let url = if endpoint == Endpoint::Local {
            command
                .arg("--header")
                .arg(format!(
                    "Host: {}",
                    self.session.origin.trim_start_matches("https://")
                ))
                .args(["--header", "X-Forwarded-Proto: https"]);
            format!("http://127.0.0.1:{}/oauth/keys", self.session.local_port)
        } else {
            format!("{}/oauth/keys", self.session.origin)
        };
        command.arg(url);
        let keys: Value = serde_json::from_str(&files::output(&mut command)?)?;
        ensure!(
            keys["keys"].as_array().is_some_and(|keys| !keys.is_empty()),
            "Goblin did not return OAuth signing keys. Enable ChatGPT on the local HTTPS origin."
        );
        Ok(format!("{:x}", Sha256::digest(serde_json::to_vec(&keys)?)))
    }

    fn start(&self) -> Result<()> {
        let local_keys = self
            .keys(Endpoint::Local)
            .context("The local Goblin build is not ready for this HTTPS origin")?;
        files::write_json(
            &self.directory.join("session.json"),
            &serde_json::to_value(&self.session)?,
            0o600,
        )?;
        println!(
            "Connecting to the dedicated HTTPS test VM. Verify the SSH host fingerprint if prompted."
        );
        files::run(self.ssh().args(["-M", "-f", "-N", &self.session.ssh]))?;
        let installed_origin = self.remote("sudo -n cat /var/lib/goblin/public-url")?;
        ensure!(
            installed_origin.trim() == self.session.origin,
            "The selected VM uses a different Goblin origin. Run relay stop to close this session."
        );
        let ingress = self.ingress()?;
        let (index, service) = upstream(&ingress)?;
        ensure!(
            service == "platform"
                && ingress
                    .pointer("/metadata/annotations/goblin.dev~1relay-owner")
                    .is_none(),
            "The VM already has another upstream or relay owner; it cannot be replaced"
        );
        ensure!(
            ingress["spec"]["ingressClassName"] == "nginx"
                && ingress["spec"]["rules"][0]["host"]
                    == self.session.origin.trim_start_matches("https://")
                && ingress["spec"]["tls"]
                    .as_array()
                    .is_some_and(|tls| !tls.is_empty()),
            "The VM needs a completed NGINX HTTPS installation"
        );
        self.remote("sudo -n k3s kubectl wait --for=condition=Ready certificate/goblin-public-tls -n goblin --timeout=10s")?;
        self.remote("test -z \"$(sudo -n k3s kubectl get deployment/goblin-dev-relay service/goblin-dev-relay configmap/goblin-dev-relay -n goblin --ignore-not-found -o name)\"")?;
        let uid: u32 = self.remote("id -u")?.trim().parse()?;
        let gid: u32 = self.remote("id -g")?.trim().parse()?;
        ensure!(uid != 0, "Use the VM's non-root SSH administrator");
        let socket_directory = self.socket_directory();
        self.remote(&format!(
            "sudo -n install -d -m 0700 -o {uid} -g {gid} {}",
            quote(&socket_directory)
        ))?;
        let forwarding = format!(
            "{socket_directory}/upstream.sock:127.0.0.1:{}",
            self.session.local_port
        );
        files::run(
            self.ssh()
                .args(["-O", "forward", "-R", &forwarding, &self.session.ssh]),
        )?;
        let resources = manifests(&self.session.owner, &socket_directory, uid, gid);
        files::atomic_write(
            &self.directory.join("resources.json"),
            &serde_json::to_vec_pretty(&resources)?,
            0o600,
            false,
        )?;
        self.remote(&format!("set -eu\nprintf '%s' {} | sudo -n k3s kubectl apply -f -\nsudo -n k3s kubectl rollout status deployment/{NAME} -n goblin --timeout=120s", quote(&resources.to_string())))?;
        // Reload resourceVersion after rollout. The test operation rejects a
        // concurrent ingress edit, including cert-manager challenge updates.
        let ingress = self.ingress()?;
        let (current_index, service) = upstream(&ingress)?;
        ensure!(
            index == current_index && service == "platform",
            "Ingress routing changed during relay preparation"
        );
        self.patch(&route_patch(
            &ingress,
            &self.session.owner,
            Upstream::Development,
        )?)?;
        ensure!(
            self.keys(Endpoint::Public)? == local_keys,
            "Public HTTPS is not serving this local Goblin identity. Inspect relay status; relay stop restores Azure routing."
        );
        println!(
            "Development relay active: {}\nOpen {}/settings?section=chatgpt to test the local build.\nRun {} to restore Azure's Goblin instance.",
            self.session.origin,
            self.session.origin,
            self.stop_command()
        );
        Ok(())
    }

    fn patch(&self, patch: &Value) -> Result<()> {
        self.remote(&format!(
            "sudo -n k3s kubectl patch ingress goblin -n goblin --type=json -p {}",
            quote(&patch.to_string())
        ))?;
        Ok(())
    }

    fn status(&self) -> Result<()> {
        let ingress = self.ingress()?;
        let (_, service) = upstream(&ingress)?;
        println!(
            "HTTPS origin: {}\nSelected upstream: {service}",
            self.session.origin
        );
        let connected = self
            .ssh()
            .args(["-O", "check", &self.session.ssh])
            .status()?
            .success();
        println!(
            "SSH tunnel: {}",
            if connected {
                "connected"
            } else {
                "unavailable"
            }
        );
        ensure!(
            service == NAME
                && ingress
                    .pointer("/metadata/annotations/goblin.dev~1relay-owner")
                    .and_then(Value::as_str)
                    == Some(&self.session.owner)
                && connected
                && self.keys(Endpoint::Public)? == self.keys(Endpoint::Local)?,
            "Development relay is not healthy. Routing remains explicit; use relay stop to restore Azure's Goblin instance."
        );
        Ok(())
    }

    fn stop(&self) -> Result<()> {
        let ingress = self.ingress()?;
        let (_, service) = upstream(&ingress)?;
        let patch = if service == NAME {
            Some(route_patch(&ingress, &self.session.owner, Upstream::Azure)?)
        } else {
            ensure!(
                service == "platform"
                    && ingress
                        .pointer("/metadata/annotations/goblin.dev~1relay-owner")
                        .is_none(),
                "The ingress belongs to another route; nothing was removed"
            );
            None
        };
        // Check all resources before restoring routing or deleting any of them.
        // An existing resource with a missing owner is also somebody else's.
        self.remote(&format!("set -eu\nfor resource in deployment/{NAME} service/{NAME} configmap/{NAME}; do\n  exists=$(sudo -n k3s kubectl get \"$resource\" -n goblin --ignore-not-found -o name)\n  if [ -n \"$exists\" ]; then\n    owner=$(sudo -n k3s kubectl get \"$resource\" -n goblin -o 'jsonpath={{.metadata.annotations.goblin\\.dev/relay-owner}}')\n    test \"$owner\" = {}\n  fi\ndone", quote(&self.session.owner)))?;
        if let Some(patch) = patch {
            self.patch(&patch)?;
        }
        // Every deletion checks the session's owner annotation first. A lost
        // local session cannot silently delete somebody else's relay resources.
        self.remote(&format!("set -eu\nfor resource in deployment/{NAME} service/{NAME} configmap/{NAME}; do\n  exists=$(sudo -n k3s kubectl get \"$resource\" -n goblin --ignore-not-found -o name)\n  if [ -n \"$exists\" ]; then\n    owner=$(sudo -n k3s kubectl get \"$resource\" -n goblin -o 'jsonpath={{.metadata.annotations.goblin\\.dev/relay-owner}}')\n    test \"$owner\" = {}\n    sudo -n k3s kubectl delete \"$resource\" -n goblin\n  fi\ndone\nsudo -n rm -rf {}", quote(&self.session.owner), quote(&self.socket_directory())))?;
        let _ = self.ssh().args(["-O", "exit", &self.session.ssh]).status();
        files::remove_file(&self.directory.join("ssh.sock"))?;
        files::remove_file(&self.directory.join("session.json"))?;
        files::remove_file(&self.directory.join("resources.json"))?;
        println!("Azure routing restored: {}", self.session.origin);
        Ok(())
    }
}

fn upstream(ingress: &Value) -> Result<(usize, &str)> {
    let rules = ingress["spec"]["rules"]
        .as_array()
        .context("Missing ingress rules")?;
    ensure!(
        rules.len() == 1,
        "The relay requires the installation's single-host ingress"
    );
    let paths = rules[0]["http"]["paths"]
        .as_array()
        .context("Missing ingress paths")?;
    let matches: Vec<_> = paths
        .iter()
        .enumerate()
        .filter(|(_, path)| path["path"] == "/")
        .collect();
    ensure!(
        matches.len() == 1,
        "The ingress must have one default Goblin path"
    );
    let (index, path) = matches[0];
    Ok((
        index,
        path["backend"]["service"]["name"]
            .as_str()
            .context("Missing upstream service")?,
    ))
}

fn route_patch(ingress: &Value, owner: &str, target: Upstream) -> Result<Value> {
    let (index, service) = upstream(ingress)?;
    let annotations = ingress["metadata"]["annotations"]
        .as_object()
        .context("Missing ingress annotations")?;
    if target == Upstream::Development {
        ensure!(
            service == "platform" && !annotations.contains_key(OWNER),
            "Another route already owns the ingress"
        );
    } else {
        ensure!(
            service == NAME && annotations.get(OWNER).and_then(Value::as_str) == Some(owner),
            "The relay owner does not match; routing was preserved"
        );
    }
    let mut patch = vec![
        json!({"op":"test","path":"/metadata/resourceVersion","value":ingress["metadata"]["resourceVersion"].as_str().context("Missing ingress version")?}),
        json!({"op":"replace","path":format!("/spec/rules/0/http/paths/{index}/backend/service"),"value":{"name":if target == Upstream::Development { NAME } else { "platform" },"port":{"number":8787}}}),
    ];
    patch.push(if target == Upstream::Development {
        json!({"op":"add","path":"/metadata/annotations/goblin.dev~1relay-owner","value":owner})
    } else {
        json!({"op":"remove","path":"/metadata/annotations/goblin.dev~1relay-owner"})
    });
    // F5 validates WebSocket services against the current Ingress backends.
    // Keep the annotation and route in the same resourceVersion-guarded patch.
    patch.push(json!({"op":"add","path":"/metadata/annotations/nginx.org~1websocket-services","value":if target == Upstream::Development { NAME } else { "platform" }}));
    Ok(json!(patch))
}

fn manifests(owner: &str, socket_directory: &str, uid: u32, gid: u32) -> Value {
    let metadata =
        json!({"name":NAME,"namespace":"goblin","labels":{"app":NAME},"annotations":{OWNER:owner}});
    let config = "pid /tmp/nginx.pid;\nerror_log /dev/stderr warn;\nevents {}\nhttp {\n  access_log off;\n  map $http_upgrade $connection_upgrade {\n    default upgrade;\n    '' '';\n  }\n  client_body_temp_path /tmp/client;\n  proxy_temp_path /tmp/proxy;\n  fastcgi_temp_path /tmp/fastcgi;\n  uwsgi_temp_path /tmp/uwsgi;\n  scgi_temp_path /tmp/scgi;\n  server {\n    listen 8080;\n    client_max_body_size 64m;\n    location / {\n      proxy_pass http://unix:/relay/upstream.sock:;\n      proxy_http_version 1.1;\n      proxy_set_header Host $http_host;\n      proxy_set_header X-Forwarded-Proto https;\n      proxy_set_header X-Forwarded-Host $http_host;\n      proxy_set_header Authorization $http_authorization;\n      proxy_set_header Upgrade $http_upgrade;\n      proxy_set_header Connection $connection_upgrade;\n      proxy_buffering off;\n      proxy_request_buffering off;\n      proxy_read_timeout 3600s;\n      proxy_send_timeout 3600s;\n    }\n  }\n}\n";
    json!({"apiVersion":"v1","kind":"List","items":[
        {"apiVersion":"v1","kind":"ConfigMap","metadata":metadata,"data":{"nginx.conf":config}},
        {"apiVersion":"v1","kind":"Service","metadata":metadata,"spec":{"selector":{"app":NAME},"ports":[{"name":"http","port":8787,"targetPort":8080}]}},
        {"apiVersion":"apps/v1","kind":"Deployment","metadata":metadata,"spec":{
            "replicas":1,"strategy":{"type":"Recreate"},"selector":{"matchLabels":{"app":NAME}},
            "template":{"metadata":{"labels":{"app":NAME}},"spec":{
                "automountServiceAccountToken":false,
                "securityContext":{"runAsNonRoot":true,"runAsUser":uid,"runAsGroup":gid,"seccompProfile":{"type":"RuntimeDefault"}},
                "containers":[{"name":"proxy","image":PROXY_IMAGE,"command":["nginx","-g","daemon off;"],
                    "ports":[{"name":"http","containerPort":8080}],
                    "securityContext":{"allowPrivilegeEscalation":false,"readOnlyRootFilesystem":true,"capabilities":{"drop":["ALL"]}},
                    "resources":{"requests":{"cpu":"10m","memory":"16Mi"},"limits":{"cpu":"250m","memory":"64Mi"}},
                    "readinessProbe":{"tcpSocket":{"port":"http"},"periodSeconds":5},
                    "volumeMounts":[{"name":"socket","mountPath":"/relay","readOnly":true},{"name":"config","mountPath":"/etc/nginx/nginx.conf","subPath":"nginx.conf","readOnly":true},{"name":"temporary","mountPath":"/tmp"}]}],
                "volumes":[{"name":"socket","hostPath":{"path":socket_directory,"type":"Directory"}},{"name":"config","configMap":{"name":NAME}},{"name":"temporary","emptyDir":{"sizeLimit":"64Mi"}}]
            }}
        }}
    ]})
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relay_targets_reject_shell_options_and_different_origins() {
        validate_target("https://test.example.com", "goblinadmin@test.example.com").unwrap();
        for (origin, ssh) in [
            ("http://test.example.com", "goblinadmin@test.example.com"),
            ("https://test.example.com/", "goblinadmin@test.example.com"),
            (
                "https://test.example.com:443",
                "goblinadmin@test.example.com",
            ),
            (
                "https://test.example.com",
                "-oProxyCommand@test.example.com",
            ),
            ("https://test.example.com", "goblinadmin@other.example.com"),
            (
                "https://test.example.com;touch-x",
                "goblinadmin@test.example.com;touch-x",
            ),
        ] {
            assert!(validate_target(origin, ssh).is_err(), "{origin} {ssh}");
        }
        assert_eq!(quote("a'b\n$(x)"), "'a'\\''b\n$(x)'");
    }

    fn ingress(service: &str, owner: Option<&str>) -> Value {
        let mut annotations =
            json!({"nginx.org/ssl-redirect":"true","nginx.org/websocket-services":service});
        if let Some(owner) = owner {
            annotations[OWNER] = json!(owner);
        }
        json!({"metadata":{"resourceVersion":"42","annotations":annotations},"spec":{"rules":[{"http":{"paths":[
            {"path":"/.well-known/acme-challenge/token","backend":{"service":{"name":"cm-acme-http-solver","port":{"number":8089}}}},
            {"path":"/","backend":{"service":{"name":service,"port":{"number":8787}}}}
        ]}}]}})
    }

    #[test]
    fn routing_preserves_acme_paths_and_rejects_other_owners() {
        let patch =
            route_patch(&ingress("platform", None), "owned", Upstream::Development).unwrap();
        assert_eq!(
            patch[0],
            json!({"op":"test","path":"/metadata/resourceVersion","value":"42"})
        );
        assert_eq!(
            patch[1]["path"],
            "/spec/rules/0/http/paths/1/backend/service"
        );
        assert_eq!(patch[1]["value"]["name"], NAME);
        assert_eq!(
            patch[3],
            json!({"op":"add","path":"/metadata/annotations/nginx.org~1websocket-services","value":NAME})
        );
        assert!(route_patch(&ingress(NAME, Some("another")), "owned", Upstream::Azure).is_err());
        assert!(route_patch(&ingress("other", None), "owned", Upstream::Development).is_err());
        let restore = route_patch(&ingress(NAME, Some("owned")), "owned", Upstream::Azure).unwrap();
        assert_eq!(restore[1]["value"]["name"], "platform");
        assert_eq!(restore[2]["op"], "remove");
        assert_eq!(
            restore[3],
            json!({"op":"add","path":"/metadata/annotations/nginx.org~1websocket-services","value":"platform"})
        );
    }
}
