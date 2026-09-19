"""Produce a merge patch for an existing Sandbox, preserving its other settings."""
import copy
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
sandbox = json.load(sys.stdin)
original = sandbox["spec"]["podTemplate"]["spec"]
spec = copy.deepcopy(original)
container = next(item for item in spec["containers"] if item["name"] == "auth")


def replace_named(items, replacement):
    for index, item in enumerate(items):
        if item["name"] == replacement["name"]:
            items[index] = replacement
            return
    items.append(replacement)


replace_named(spec.setdefault("volumes", []), {
    "name": "postgres-client",
    "secret": {"secretName": "goblin-postgres-app-tls", "defaultMode": 0o440},
})
replace_named(container.setdefault("volumeMounts", []), {
    "name": "postgres-client", "mountPath": "/etc/goblin-postgres", "readOnly": True,
})
connection = json.loads((root / "backend/src/Goblin.Web/appsettings.json").read_text())["ConnectionStrings"]["Goblin"]
# This also upgrades running images built with the former password configuration.
replace_named(container.setdefault("env", []), {"name": "ConnectionStrings__Goblin", "value": connection})
if spec != original:
    json.dump({"spec": {"podTemplate": {"spec": spec}}}, sys.stdout)
