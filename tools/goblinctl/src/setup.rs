//! Read-only public setup UI. Privileged state writers are separate processes.
use crate::assets;
use crate::contract_values::SetupAction;
use crate::contract_values::SetupPhase;
use crate::contract_values::SetupStatus;
use crate::contract_values::SetupStep;
use crate::contract_values::SetupStepStatus;
use crate::files;
use anyhow::Context;
use anyhow::Result;
use anyhow::ensure;
use fs2::FileExt;
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
use std::fs::OpenOptions;
use std::os::unix::fs::OpenOptionsExt;
use std::path::Path;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;
use tokio::net::TcpListener;
use tokio::net::UnixListener;
use tokio::sync::Semaphore;

pub const STATE: &str = "/var/lib/goblin/install/status.json";
const STEPS: &[SetupStep] = &[
    SetupStep::Prepare,
    SetupStep::K3s,
    SetupStep::CertManager,
    SetupStep::Sandbox,
    SetupStep::Image,
    SetupStep::Prefetch,
    SetupStep::Database,
    SetupStep::Import,
    SetupStep::Migrate,
    SetupStep::Deploy,
    SetupStep::Verify,
    SetupStep::Activate,
];

/// Callers hold installer.lock for the attempt. Each update also takes a short
/// file lock so parallel workers cannot overwrite each other's progress.
pub fn update(path: &Path, action: SetupAction, value: &str) -> Result<()> {
    update_scoped(path, action, value, None)
}

pub fn update_scoped(
    path: &Path,
    action: SetupAction,
    value: &str,
    scope: Option<SetupStep>,
) -> Result<()> {
    if matches!(action, SetupAction::Start | SetupAction::FailStep)
        || action == SetupAction::Complete && !value.is_empty()
    {
        let _: SetupStep = value.parse()?;
    }
    files::reject_symlinks(path)?;
    if action == SetupAction::Init {
        fs::create_dir_all(path.parent().context("Missing state directory")?)?;
    }
    let lock_path = path.with_extension("lock");
    files::reject_symlinks(&lock_path)?;
    let lock = OpenOptions::new()
        .create(true)
        .truncate(false)
        .write(true)
        .mode(0o600)
        .open(lock_path)?;
    lock.lock_exclusive()?;
    let timestamp = chrono::Utc::now().to_rfc3339();
    let mut state = if action == SetupAction::Init {
        json!({"version":1,"status":SetupStatus::Waiting,"phase":SetupPhase::Installing,"startedAt":timestamp,"updatedAt":timestamp,
            "attempt":0,"currentStep":null,"message":"Waiting for installation to start.",
            "logGeneration":timestamp,"revision":0,"logs":[],
            "steps":STEPS.iter().map(|id|json!({"id":id,"label":id.label(),"status":SetupStepStatus::Waiting,"attempt":0})).collect::<Vec<_>>()})
    } else {
        files::json(path)?
    };
    ensure!(
        state["version"] == 1,
        "Unsupported installation state version"
    );
    let steps = state["steps"].as_array().context("Invalid steps")?;
    ensure!(
        steps.len() == STEPS.len()
            && steps
                .iter()
                .zip(STEPS)
                .all(|(step, id)| step["id"] == id.as_str()),
        "Unsupported installation steps; recreate the development installation"
    );
    state["logGeneration"]
        .as_str()
        .context("Invalid log generation")?;
    let revision = state["revision"].as_u64().context("Invalid revision")? + 1;
    ensure!(state["logs"].is_array(), "Invalid logs");
    let mut event_step = scope.map_or("", SetupStep::as_str).to_owned();
    let mut event = String::new();
    match action {
        SetupAction::Init => (),
        SetupAction::Begin => {
            state["status"] = json!(SetupStatus::Running);
            state["phase"] = json!(SetupPhase::Installing);
            state["currentStep"] = Value::Null;
            state["attempt"] = json!(state["attempt"].as_u64().context("Invalid attempt")? + 1);
            state["message"] = json!("Checking installation progress.");
            event = format!(
                "Installation attempt {} started. Checking retained resources.",
                state["attempt"]
            );
            state
                .as_object_mut()
                .context("Invalid state")?
                .remove("finishedAt");
            state
                .as_object_mut()
                .context("Invalid state")?
                .remove("failedStep");
            for step in state["steps"].as_array_mut().context("Invalid steps")? {
                step["status"] = json!(SetupStepStatus::Waiting);
                let object = step.as_object_mut().context("Invalid step")?;
                object.remove("finishedAt");
                object.remove("error");
                object.remove("startedAt");
                object.remove("detail");
            }
        }
        SetupAction::Start => {
            let step = step(&mut state, value.parse()?)?;
            step["status"] = json!(SetupStepStatus::Running);
            step["startedAt"] = json!(timestamp);
            step["attempt"] = json!(step["attempt"].as_u64().context("Invalid step attempt")? + 1);
            let label = step["label"].clone();
            event = format!("{} started.", label.as_str().context("Invalid step label")?);
            event_step = value.to_owned();
            state["currentStep"] = json!(value);
            state["message"] = label;
        }
        SetupAction::Detail => {
            ensure!(
                value.len() <= 512 && !value.chars().any(char::is_control),
                "Invalid public progress message"
            );
            if let Some(scope) = scope {
                step(&mut state, scope)?["detail"] = json!(value);
            }
            state["message"] = json!(value);
            event = value.to_owned();
        }
        SetupAction::PublicUrl => state["publicUrl"] = json!(value),
        SetupAction::Complete => {
            let id = if value.is_empty() {
                state["currentStep"]
                    .as_str()
                    .context("No current step")?
                    .to_owned()
            } else {
                value.to_owned()
            };
            let step = step(&mut state, id.parse()?)?;
            step["status"] = json!(SetupStepStatus::Complete);
            step["finishedAt"] = json!(timestamp);
            event = format!(
                "{} completed.",
                step["label"].as_str().context("Invalid step label")?
            );
            event_step = id;
        }
        SetupAction::FailStep => {
            let step = step(&mut state, value.parse()?)?;
            step["status"] = json!(SetupStepStatus::Failed);
            step["finishedAt"] = json!(timestamp);
            event = format!(
                "{} failed. Review the private installation log for diagnostics.",
                step["label"].as_str().context("Invalid step label")?
            );
            step["error"] = json!(event);
            event_step = value.to_owned();
            if state["failedStep"].is_null() {
                state["failedStep"] = json!(value);
            }
            state["currentStep"] = state["failedStep"].clone();
        }
        SetupAction::Handoff => {
            state["phase"] = json!(SetupPhase::Activating);
            state["message"] = json!("Goblin is ready. Connecting to your workspace…");
            event = "Opening Goblin. A brief connection gap is expected.".into();
        }
        SetupAction::Failed => {
            state["status"] = json!(SetupStatus::Failed);
            if !state["failedStep"].is_null() {
                state["currentStep"] = state["failedStep"].clone();
            }
            state["message"] =
                json!("Installation stopped. An administrator can retry from the VM.");
            for step in state["steps"].as_array_mut().context("Invalid steps")? {
                if step["status"] == SetupStepStatus::Running.as_str() {
                    step["status"] = json!(SetupStepStatus::Failed);
                    step["finishedAt"] = json!(timestamp);
                    step["error"] =
                        json!("Interrupted. Retained resources will be checked on retry.");
                }
            }
            event = "Installation stopped. Progress and existing data are retained for an administrator to retry.".into();
        }
        SetupAction::Ready => {
            state["status"] = json!(SetupStatus::Ready);
            state["phase"] = json!(SetupPhase::Complete);
            state["message"] = json!("Goblin is ready.");
            state["finishedAt"] = json!(timestamp);
            event = "Goblin is ready.".into();
        }
    }
    state["updatedAt"] = json!(timestamp);
    state["revision"] = json!(revision);
    if !event.is_empty() {
        let entry = json!({"id":revision,"timestamp":timestamp,"attempt":state["attempt"],"step":event_step,"message":event});
        let logs = state["logs"].as_array_mut().context("Invalid logs")?;
        logs.push(entry);
        if logs.len() > 300 {
            logs.drain(..logs.len() - 300);
        }
    }
    files::write_json(path, &state, 0o644)
}
fn step(state: &mut Value, id: SetupStep) -> Result<&mut Value> {
    state["steps"]
        .as_array_mut()
        .context("Invalid steps")?
        .iter_mut()
        .find(|s| s["id"] == id.as_str())
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
    } else if path == "/setup/events" {
        let cursor = request
            .headers()
            .get("last-event-id")
            .and_then(|v| v.to_str().ok())
            .unwrap_or("");
        match event_batch(&state, cursor).await {
            Ok(body) => (200, "text/event-stream", body.into_bytes()),
            Err(_) => (503, "text/plain", b"Progress unavailable".to_vec()),
        }
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

// Finite SSE batches keep the existing connection/time/memory limits. EventSource
// reconnects with Last-Event-ID; a refreshed page receives the retained log tail.
// Only structured public state is read. The private command log is never served.
async fn event_batch(path: &Path, cursor: &str) -> Result<String> {
    for _ in 0..20 {
        let state = files::json(path)?;
        let id = format!(
            "{}:{}",
            state["logGeneration"]
                .as_str()
                .context("Invalid log generation")?,
            state["revision"].as_u64().context("Invalid revision")?
        );
        if cursor != id {
            return Ok(format!(
                "retry: 1000\nid: {id}\nevent: progress\ndata: {}\n\n",
                serde_json::to_string(&state)?
            ));
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
    }
    Ok("retry: 1000\n: keepalive\n\n".into())
}
