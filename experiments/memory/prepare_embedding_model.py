#!/usr/bin/env python3
"""Download checksum-pinned embedding weights; inference is offline in the runner."""
import argparse
import hashlib
import json
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", choices=["nomic", "bge", "e5", "minilm", "embeddinggemma2"], default="nomic")
    parser.add_argument("--model-dir", type=Path)
    args = parser.parse_args()
    if args.model_dir is None:
        model_dirs = {"nomic": "nomic", "bge": "bge-small", "e5": "e5-small", "minilm": "minilm", "embeddinggemma2": "embeddinggemma2"}
        args.model_dir = ROOT.parents[1] / ".artifacts" / "memory-loop" / model_dirs[args.model]
    args.model_dir.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ROOT / f"{args.model}_model.json").read_text())
    for name, expected in manifest["files"].items():
        target = args.model_dir / Path(name).name
        if target.exists() and digest(target) == expected["sha256"]:
            continue
        url = f"https://huggingface.co/{manifest['model']}/resolve/{manifest['revision']}/{name}"
        partial = target.with_suffix(".part")
        with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as stream:
            while chunk := response.read(1024 * 1024):
                stream.write(chunk)
        if partial.stat().st_size != expected["bytes"] or digest(partial) != expected["sha256"]:
            partial.unlink()
            raise ValueError(f"Download checksum/size mismatch: {name}")
        partial.replace(target)
        print(f"Verified {name}: {expected['bytes']} bytes")


if __name__ == "__main__":
    main()
