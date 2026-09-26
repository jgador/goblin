//! Read-only public setup UI. Privileged state writers are separate processes.
use crate::assets;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::bail;
use anyhow::ensure;
use http_body_util::Full;
use hyper::Method;
use hyper::Request;
use hyper::Response;
use hyper::body::Bytes;
use hyper::body::Incoming;
use hyper::service::service_fn;
use hyper_util::rt::TokioIo;
use hyper_util::rt::TokioTimer;
use serde_json::Value;
use serde_json::json;
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::net::TcpListener;
use tokio::net::UnixListener;
use tokio::sync::Semaphore;

pub const STATE: &str = "/var/lib/goblin/install/status.json";
const STEPS: &[(&str, &str)] = &[
    ("prepare", "Prepare installation"),
    ("k3s", "Install Kubernetes"),
    ("cert-manager", "Install certificate manager"),
    ("sandbox", "Install Agent Sandbox"),
    ("image", "Build Goblin"),
    ("deploy", "Deploy Goblin"),
    ("verify", "Check application readiness"),
    ("activate", "Open Goblin"),
];

/// Callers hold installer.lock for the entire attempt, including initialization.
pub fn update(path: &Path, action: &str, value: &str) -> Result<()> {
    let timestamp = chrono::Utc::now().to_rfc3339();
    let mut state = if action == "init" {
        json!({"version":1,"status":"waiting","phase":"installing","startedAt":timestamp,"updatedAt":timestamp,
            "attempt":0,"currentStep":null,"message":"Waiting for installation to start.",
            "steps":STEPS.iter().map(|(id,label)|json!({"id":id,"label":label,"status":"waiting","attempt":0})).collect::<Vec<_>>()})
    } else {
        files::json(path)?
    };
    ensure!(
        state["version"] == 1,
        "Unsupported installation state version"
    );
    match action {
        "init" => (),
        "begin" => {
            state["status"] = json!("running");
            state["phase"] = json!("installing");
            state["currentStep"] = Value::Null;
            state["attempt"] = json!(state["attempt"].as_u64().context("Invalid attempt")? + 1);
            state["message"] = json!("Checking installation progress.");
            for step in state["steps"].as_array_mut().context("Invalid steps")? {
                step["status"] = json!("waiting");
                let object = step.as_object_mut().context("Invalid step")?;
                object.remove("finishedAt");
                object.remove("error");
            }
        }
        "start" => {
            let step = step(&mut state, value)?;
            step["status"] = json!("running");
            step["startedAt"] = json!(timestamp);
            step["attempt"] = json!(step["attempt"].as_u64().context("Invalid step attempt")? + 1);
            let label = step["label"].clone();
            state["currentStep"] = json!(value);
            state["message"] = label;
        }
        "detail" => state["message"] = json!(value),
        "public-url" => state["publicUrl"] = json!(value),
        "complete" => {
            let id = state["currentStep"]
                .as_str()
                .context("No current step")?
                .to_owned();
            let step = step(&mut state, &id)?;
            step["status"] = json!("complete");
            step["finishedAt"] = json!(timestamp);
        }
        "handoff" => {
            state["phase"] = json!("activating");
            state["message"] = json!("Goblin is ready. Connecting to your workspace…");
        }
        "failed" => {
            state["status"] = json!("failed");
            state["message"] =
                json!("Installation stopped. An administrator can retry from the VM.");
            if let Some(id) = state["currentStep"].as_str().map(str::to_owned) {
                let step = step(&mut state, &id)?;
                step["status"] = json!("failed");
                step["finishedAt"] = json!(timestamp);
                step["error"] =
                    json!("This step did not finish. Review the installation log on the VM.");
            }
        }
        "ready" => {
            state["status"] = json!("ready");
            state["phase"] = json!("complete");
            state["message"] = json!("Goblin is ready.");
            state["finishedAt"] = json!(timestamp);
        }
        _ => bail!("Unknown state transition"),
    }
    state["updatedAt"] = json!(timestamp);
    files::write_json(path, &state, 0o644)
}
fn step<'a>(state: &'a mut Value, id: &str) -> Result<&'a mut Value> {
    state["steps"]
        .as_array_mut()
        .context("Invalid steps")?
        .iter_mut()
        .find(|s| s["id"] == id)
        .context("Unknown installation step")
}

pub fn serve(state: &Path, host: &str, port: u16, health_socket: Option<&Path>) -> Result<()> {
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()?
        .block_on(async {
            // Local health is published only after the public listener has bound.
            let listener = TcpListener::bind((host, port))
                .await
                .context("Cannot bind setup listener")?;
            let local = if let Some(path) = health_socket {
                files::remove_file(path)?;
                Some(UnixListener::bind(path)?)
            } else {
                None
            };
            println!(
                "Goblin setup listening at http://{host}:{}",
                listener.local_addr()?.port()
            );
            let capacity = Arc::new(Semaphore::new(32));
            loop {
                tokio::select! {
                    connection = listener.accept() => {
                        let (stream, _) = connection?;
                        connection_task(stream, state.to_owned(), capacity.clone());
                    },
                    connection = async {
                        if let Some(local) = &local { local.accept().await }
                        else { std::future::pending().await }
                    } => {
                        let (stream, _) = connection?;
                        connection_task(stream, state.to_owned(), capacity.clone());
                    }
                }
            }
        })
}
fn connection_task<T>(stream: T, state: PathBuf, capacity: Arc<Semaphore>)
where
    T: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin + Send + 'static,
{
    let Ok(permit) = capacity.try_acquire_owned() else {
        return;
    };
    tokio::spawn(async move {
        let _permit = permit;
        let service = service_fn(move |request| respond(request, state.clone()));
        let mut builder = hyper::server::conn::http1::Builder::new();
        builder
            .keep_alive(false)
            .max_buf_size(16 * 1024)
            .timer(TokioTimer::new())
            .header_read_timeout(Duration::from_secs(10));
        // Bound both slow headers and blocked responses; request bodies are never consumed.
        let _ = tokio::time::timeout(
            Duration::from_secs(10),
            builder.serve_connection(TokioIo::new(stream), service),
        )
        .await;
    });
}
async fn respond(
    request: Request<Incoming>,
    state: PathBuf,
) -> std::result::Result<Response<Full<Bytes>>, hyper::http::Error> {
    let path = request.uri().path();
    let (status, kind, body) = if request.method() != Method::GET {
        (405, "text/plain", b"Method not allowed".to_vec())
    } else if path == "/setup/healthz" {
        (200, "application/json", b"{\"setup\":true}".to_vec())
    } else if path == "/setup/status" {
        match fs::read(&state) {
            Ok(body) => (200, "application/json", body),
            Err(_) => (503, "text/plain", b"Status unavailable".to_vec()),
        }
    } else if let Some((kind, body)) = assets::web(path) {
        (200, kind, body.to_vec())
    } else {
        (404, "text/plain", b"Not found".to_vec())
    };
    // Never log public paths, headers, or arbitrary request input.
    Response::builder().status(status)
        .header("Content-Type", kind).header("Cache-Control", "no-store")
        .header("X-Content-Type-Options", "nosniff").header("Referrer-Policy", "no-referrer")
        .header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
        .body(Full::new(Bytes::from(body)))
}
