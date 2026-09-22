#!/usr/bin/env python3
"""Build/check a deterministic zipapp embedded in the Azure Custom Script.

Only the allowlisted setup assets enter the bundle. No per-install configuration,
application settings, credentials, Python packages, or compiled dependencies.
"""

import argparse
import base64
import hashlib
import io
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
mode = parser.add_mutually_exclusive_group()
mode.add_argument("--check", action="store_true")
mode.add_argument(
    "--output-only",
    action="store_true",
    help="Build a local bundle without updating generated template inputs",
)
parser.add_argument(
    "--output",
    type=Path,
    help="Also emit an executable .pyz for local tests or distribution",
)
args = parser.parse_args()
if args.output_only and not args.output:
    parser.error("--output-only requires --output")
buffer = io.BytesIO(b"#!/usr/bin/env python3\n")
buffer.seek(0, 2)
names = [
    "__main__.py",
    "index.html",
    "app.js",
    "styles.css",
    "goblin-setup.service",
    "goblin-installer.service",
    "installer.sh",
    "sandbox-kustomization.yaml",
    "install-app.sh",
]
sources = {
    name: root / name if name == "install-app.sh" else root / "setup" / name
    for name in names
}
# Embed the original artwork so setup needs neither the app build nor an external asset server.
sources["icon.svg"] = root.parents[1] / "assets/branding/svg/icon-light.svg"
with zipfile.ZipFile(
    buffer, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9
) as bundle:
    for name, source in sources.items():
        info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o100644 << 16
        bundle.writestr(info, source.read_bytes())
data = buffer.getvalue()
outputs = {
    "setup-bundle.b64": base64.b64encode(data).decode() + "\n",
    "setup-bundle.sha256": hashlib.sha256(data).hexdigest() + "\n",
}
for name, content in ({} if args.output_only else outputs).items():
    path = root / name
    if args.check:
        if not path.exists() or path.read_text() != content:
            raise SystemExit(
                "Setup bundle is stale. Run python3 deploy/azure/build-setup-bundle.py"
            )
    else:
        path.write_text(content)
if args.output:
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(data)
    args.output.chmod(0o755)
print(
    f"Goblin setup bundle: {len(data)} bytes; sha256 {hashlib.sha256(data).hexdigest()}"
)
