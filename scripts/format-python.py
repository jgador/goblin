#!/usr/bin/env python3
"""Format Python and regenerate Azure artifacts when embedded sources change."""

from pathlib import Path
import json
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
EMBEDDED_SOURCES = (
    ROOT / "deploy/azure/setup/__main__.py",
    ROOT / "deploy/azure/hash-password.py",
)


def azure_artifacts_current() -> bool:
    bundle = subprocess.run(
        [sys.executable, "deploy/azure/build-setup-bundle.py", "--check"],
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    if bundle.returncode != 0:
        return False
    try:
        azure = ROOT / "deploy/azure"
        values = [
            (azure / "setup-bundle.b64").read_text().strip(),
            (azure / "setup-bundle.sha256").read_text().strip(),
            (azure / "hash-password.py").read_text(),
        ]
        for name in ("azuredeploy.json", "azuredeploy.portal.json"):
            template = json.dumps(json.loads((azure / name).read_text()))
            if any(json.dumps(value)[1:-1] not in template for value in values):
                return False
    except (OSError, ValueError):
        return False
    return True


def main() -> int:
    ruff = [sys.executable, "-m", "ruff", "format"]
    # Check prerequisites before formatting sources that need Bicep regeneration.
    embedded_check = subprocess.run(
        [*ruff, "--check", *map(str, EMBEDDED_SOURCES)],
        cwd=ROOT,
        stdout=subprocess.DEVNULL,
    )
    if embedded_check.returncode not in (0, 1):
        return embedded_check.returncode

    local_bicep = ROOT / ".venv/bin/bicep"
    bicep = shutil.which("bicep") or (
        str(local_bicep) if local_bicep.is_file() else None
    )
    regenerate = embedded_check.returncode == 1 or not azure_artifacts_current()
    if regenerate and bicep is None:
        print(
            "Python formatting requires regenerating the Azure artifacts. Install the "
            "Bicep CLI on PATH or at .venv/bin/bicep so their deployment artifacts "
            "can be regenerated. See docs/formatting.md.",
            file=sys.stderr,
        )
        return 1

    formatted = subprocess.run([*ruff, "."], cwd=ROOT)
    if regenerate:
        # Ruff can format valid files even if another file has a syntax error.
        subprocess.run(
            [sys.executable, "deploy/azure/build-setup-bundle.py"],
            cwd=ROOT,
            check=True,
        )
        for name in ("main", "portal"):
            output = "azuredeploy.json" if name == "main" else "azuredeploy.portal.json"
            subprocess.run(
                [
                    bicep,
                    "build",
                    f"deploy/azure/{name}.bicep",
                    "--outfile",
                    f"deploy/azure/{output}",
                ],
                cwd=ROOT,
                check=True,
            )
    return formatted.returncode


if __name__ == "__main__":
    sys.exit(main())
