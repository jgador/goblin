"""Small, standard-library-only status server and installer state utility."""

import argparse
import json
import os
import tempfile
import threading
import zipfile
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from socketserver import ThreadingUnixStreamServer

STEPS = [
    ("prepare", "Prepare installation"),
    ("k3s", "Install Kubernetes"),
    ("cert-manager", "Install certificate manager"),
    ("sandbox", "Install Agent Sandbox"),
    ("image", "Build Goblin"),
    ("deploy", "Deploy Goblin"),
    ("verify", "Check application readiness"),
    ("activate", "Open Goblin"),
]


def now():
    return datetime.now(timezone.utc).isoformat()


def atomic_write(path, content, mode=0o644):
    path = Path(path)
    fd, temporary = tempfile.mkstemp(prefix=".status-", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        Path(temporary).unlink(missing_ok=True)


def update_state(path, action, value):
    # Writers hold the installer's flock for the entire attempt (including init).
    timestamp = now()
    if action == "init":
        state = {
            "version": 1,
            "status": "waiting",
            "phase": "installing",
            "startedAt": timestamp,
            "updatedAt": timestamp,
            "attempt": 0,
            "currentStep": None,
            "message": "Waiting for installation to start.",
            "steps": [
                {"id": key, "label": label, "status": "waiting", "attempt": 0}
                for key, label in STEPS
            ],
        }
    else:
        state = json.loads(Path(path).read_text())
        if action == "begin":
            state.update(
                status="running",
                phase="installing",
                currentStep=None,
                attempt=state["attempt"] + 1,
                message="Checking installation progress.",
            )
            # A completed marker is never sufficient evidence of readiness after a reboot.
            for step in state["steps"]:
                step.update(status="waiting")
                step.pop("finishedAt", None)
                step.pop("error", None)
        elif action == "start":
            step = next(item for item in state["steps"] if item["id"] == value)
            step.update(
                status="running", startedAt=timestamp, attempt=step["attempt"] + 1
            )
            state.update(currentStep=value, message=step["label"])
        elif action == "detail":
            state["message"] = value
        elif action == "public-url":
            # The worker supplies its validated origin, never a request Host or query.
            state["publicUrl"] = value
        elif action == "complete":
            step = next(
                item for item in state["steps"] if item["id"] == state["currentStep"]
            )
            step.update(status="complete", finishedAt=timestamp)
        elif action == "handoff":
            state.update(
                phase="activating",
                message="Goblin is ready. Connecting to your workspace…",
            )
        elif action == "failed":
            state.update(
                status="failed",
                message="Installation stopped. An administrator can retry from the VM.",
            )
            if state["currentStep"]:
                step = next(
                    item
                    for item in state["steps"]
                    if item["id"] == state["currentStep"]
                )
                step.update(
                    status="failed",
                    finishedAt=timestamp,
                    error="This step did not finish. Review the installation log on the VM.",
                )
        elif action == "ready":
            state.update(
                status="ready",
                phase="complete",
                message="Goblin is ready.",
                finishedAt=timestamp,
            )
        else:
            raise ValueError("Unknown state transition")
    state["updatedAt"] = timestamp
    atomic_write(path, json.dumps(state, indent=2) + "\n")


def serve(state_path, host, port, health_socket=None):
    with zipfile.ZipFile(Path(__file__).parent) as bundle:
        assets = {
            "/": ("text/html; charset=utf-8", bundle.read("index.html")),
            "/setup/app.js": ("text/javascript; charset=utf-8", bundle.read("app.js")),
            "/setup/styles.css": ("text/css; charset=utf-8", bundle.read("styles.css")),
            "/setup/icon.svg": ("image/svg+xml", bundle.read("icon.svg")),
        }

    class Handler(BaseHTTPRequestHandler):
        server_version = "GoblinSetup"
        sys_version = ""

        def log_message(self, *_):
            pass  # Do not log URLs, headers, or arbitrary public request input.

        def handle(self):
            self.connection.settimeout(10)
            super().handle()

        def do_GET(self):
            path = self.path.split("?", 1)[0]
            if path == "/setup/healthz":
                content_type, body = "application/json", b'{"setup":true}'
            elif path == "/setup/status":
                try:
                    body = Path(state_path).read_bytes()
                except OSError:
                    self.send_error(503)
                    return
                content_type = "application/json"
            elif path in assets:
                content_type, body = assets[path]
            else:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header(
                "Content-Security-Policy",
                "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            )
            self.end_headers()
            self.wfile.write(body)

    class Server(ThreadingHTTPServer):
        # systemd's TasksMax also bounds request threads; pending connections time out.
        request_queue_size = 32
        daemon_threads = True

    # A local socket lets provisioning check this process even on an existing
    # VM where ServiceLB still directs port 80 to the previous application.
    local = None
    try:
        with Server((host, port), Handler) as server:
            # Publish local health only after the public listener has bound.
            if health_socket:
                Path(health_socket).unlink(missing_ok=True)
                local = ThreadingUnixStreamServer(health_socket, Handler)
                local.daemon_threads = True
                threading.Thread(target=local.serve_forever, daemon=True).start()
            print(
                f"Goblin setup listening at http://{host}:{server.server_port}",
                flush=True,
            )
            server.serve_forever()
    finally:
        if local:
            local.shutdown()
            local.server_close()


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)
    server = commands.add_parser("serve")
    server.add_argument("--state", default="/var/lib/goblin/install/status.json")
    server.add_argument("--host", default="0.0.0.0")
    server.add_argument("--port", type=int, default=80)
    server.add_argument("--health-socket")
    state = commands.add_parser("state")
    state.add_argument("action")
    state.add_argument("value", nargs="?", default="")
    state.add_argument("--path", default="/var/lib/goblin/install/status.json")
    unpack = commands.add_parser("unpack")
    unpack.add_argument("destination")
    args = parser.parse_args()
    if args.command == "serve":
        serve(args.state, args.host, args.port, args.health_socket)
    elif args.command == "state":
        update_state(args.path, args.action, args.value)
    elif args.command == "unpack":
        with zipfile.ZipFile(Path(__file__).parent) as bundle:
            for name in (
                "installer.sh",
                "sandbox-kustomization.yaml",
                "install-app.sh",
                "goblin-setup.service",
                "goblin-installer.service",
            ):
                target = Path(args.destination) / name
                target.write_bytes(bundle.read(name))
                target.chmod(0o755 if name.endswith(".sh") else 0o644)


if __name__ == "__main__":
    main()
