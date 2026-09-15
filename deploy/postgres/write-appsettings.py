"""Initialize matching database credentials and write application configuration."""

import base64
import json
import os
from pathlib import Path
import re
import secrets
import sys
import tempfile


root = Path(__file__).resolve().parents[2]


def configured_app_password():
    configuration = json.loads((root / "backend/src/Goblin.Web/appsettings.json").read_text())
    connection_string = configuration["ConnectionStrings"]["Goblin"]
    # Parse whole ADO.NET key/value pairs so semicolons inside quoted values
    # cannot be mistaken for another setting. Both quote styles double escapes.
    setting = re.compile(r'''\s*([^=;]+?)\s*=\s*(?:"((?:[^"]|"")*)"|'((?:[^']|'')*)'|([^;"']*))\s*(?:;|$)''')
    password = None
    remainder = connection_string.lstrip("; \t\r\n")
    while remainder:
        match = setting.match(remainder)
        if match is None:
            raise ValueError("Invalid ConnectionStrings:Goblin in the web appsettings.json.")
        key, double_quoted, single_quoted, unquoted = match.groups()
        value = (double_quoted.replace('""', '"') if double_quoted is not None
                 else single_quoted.replace("''", "'") if single_quoted is not None
                 else unquoted.strip())
        if key.strip().lower() in ("password", "pwd"):
            password = value
        remainder = remainder[match.end():].lstrip("; \t\r\n")
    if not password:
        raise ValueError("Set Password in ConnectionStrings:Goblin before PostgreSQL setup.")
    return password


if len(sys.argv) == 3 and sys.argv[1] == "--create-secret" and sys.argv[2] in ("app", "admin"):
    role = sys.argv[2]
    password = configured_app_password() if role == "app" else secrets.token_hex(32)
    json.dump({"apiVersion": "v1", "kind": "Secret", "type": "Opaque",
               "metadata": {"name": "goblin-postgres-" + role, "namespace": "goblin"},
               "stringData": {"password": password}}, sys.stdout)
    sys.exit(0)
if len(sys.argv) != 1:
    sys.exit("Usage: write-appsettings.py [--create-secret app|admin]")

cluster_secrets = {item["metadata"]["name"]: item["data"] for item in json.load(sys.stdin)["items"]}


def connection(role, host="localhost"):
    password = base64.b64decode(cluster_secrets[f"goblin-postgres-{role}"]["password"], validate=True).decode()
    # ADO.NET connection strings escape a double quote by doubling it.
    password = password.replace('"', '""')
    return f'Host={host};Port=5432;Database=goblin;Username=goblin_{role};Password="{password}"'


connections = {
    root / "backend/src/Goblin.Web/appsettings.json": {"Goblin": connection("app", "goblin-postgres")},
    root / "backend/tools/Goblin.Database/appsettings.json": {
        "Goblin": connection("app"), "GoblinAdmin": connection("admin")
    },
}
settings = {}
for path, values in connections.items():
    configuration = json.loads(path.read_text()) if path.exists() else {}
    configuration.setdefault("ConnectionStrings", {}).update(values)
    settings[path] = json.dumps(configuration, indent=2) + "\n"

for path, contents in settings.items():
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=".appsettings-", suffix=".tmp")
    try:
        with os.fdopen(descriptor, "w") as output:
            if os.geteuid() == 0 and "SUDO_UID" in os.environ and "SUDO_GID" in os.environ:
                os.fchown(output.fileno(), int(os.environ["SUDO_UID"]), int(os.environ["SUDO_GID"]))
            output.write(contents)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)

print("Updated web and database tooling appsettings.json files.")
