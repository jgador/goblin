"""Export client certificates privately and write password-free Npgsql settings."""

import argparse
import base64
import json
import os
from pathlib import Path
import secrets
import sys
import tempfile


ROOT = Path(__file__).resolve().parents[2]


def private_write(path, contents):
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(
        dir=path.parent, prefix=".goblin-", suffix=".tmp"
    )
    try:
        with os.fdopen(descriptor, "wb") as output:
            if (
                os.geteuid() == 0
                and "SUDO_UID" in os.environ
                and "SUDO_GID" in os.environ
            ):
                os.fchown(
                    output.fileno(),
                    int(os.environ["SUDO_UID"]),
                    int(os.environ["SUDO_GID"]),
                )
            output.write(contents)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def connection(role, directory, host="localhost", port=5432):
    def quote(value):
        return '"' + str(value).replace('"', '""') + '"'

    return (
        f"Host={host};Port={port};Database=goblin;Username=goblin_{role};SSL Mode=VerifyFull;GSS Encryption Mode=Disable;"
        f"Root Certificate={quote(directory / 'ca.crt')};"
        f"SSL Certificate={quote(directory / 'tls.crt')};SSL Key={quote(directory / 'tls.key')}"
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--create-secret",
        choices=["admin"],
        help="Generate the image's initialization-only password Secret",
    )
    parser.add_argument(
        "--port",
        type=int,
        default=5432,
        help="Local database endpoint port for tooling",
    )
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    if args.create_secret:
        json.dump(
            {
                "apiVersion": "v1",
                "kind": "Secret",
                "type": "Opaque",
                "metadata": {"name": "goblin-postgres-admin", "namespace": "goblin"},
                "stringData": {"password": secrets.token_hex(32)},
            },
            sys.stdout,
        )
        return

    cluster_secrets = {
        item["metadata"]["name"]: item["data"] for item in json.load(sys.stdin)["items"]
    }
    certificates = {}
    # Validate every required value before changing either settings or credentials.
    for role in ("app", "admin"):
        for name in ("ca.crt", "tls.crt", "tls.key"):
            value = base64.b64decode(
                cluster_secrets[f"goblin-postgres-{role}-tls"][name], validate=True
            )
            if not value.strip():
                raise ValueError("The client certificate Secret is incomplete.")
            certificates[ROOT / ".goblin-postgres" / role / name] = value

    web_path = ROOT / "backend/src/Goblin.Web/appsettings.json"
    tooling_path = ROOT / "backend/tools/Goblin.Database/appsettings.json"
    settings = {}
    for path, values in {
        web_path: {
            "Goblin": connection(
                "app", Path("/etc/goblin-postgres"), host="goblin-postgres"
            )
        },
        tooling_path: {
            "Goblin": connection("app", ROOT / ".goblin-postgres/app", port=args.port),
            "GoblinAdmin": connection(
                "admin", ROOT / ".goblin-postgres/admin", port=args.port
            ),
        },
    }.items():
        configuration = json.loads(path.read_text()) if path.exists() else {}
        configuration.setdefault("ConnectionStrings", {}).update(values)
        settings[path] = (json.dumps(configuration, indent=2) + "\n").encode()

    for directory in (
        ROOT / ".goblin-postgres",
        ROOT / ".goblin-postgres/app",
        ROOT / ".goblin-postgres/admin",
    ):
        directory.mkdir(mode=0o700, exist_ok=True)
        directory.chmod(0o700)
        if os.geteuid() == 0 and "SUDO_UID" in os.environ and "SUDO_GID" in os.environ:
            os.chown(
                directory, int(os.environ["SUDO_UID"]), int(os.environ["SUDO_GID"])
            )
    for path, contents in {**certificates, **settings}.items():
        private_write(path, contents)
    print(
        "Exported client certificates and updated password-free web/tooling appsettings.json files."
    )


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError):
        sys.exit(
            "Cannot export PostgreSQL configuration. Check client Secrets and local file permissions."
        )
