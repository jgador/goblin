#!/usr/bin/env python3
"""Download pinned E5 FP32 files and reproducibly generate generic dynamic INT8 weights."""
import argparse
import json
import shutil
import urllib.request
from pathlib import Path

from onnxruntime.quantization import QuantType, quantize_dynamic

from prepare_embedding_model import digest

ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT.parents[1] / ".artifacts" / "memory-loop"


def download(url, target, expected):
    if target.exists() and target.stat().st_size == expected["bytes"] and digest(target) == expected["sha256"]:
        return
    partial = target.with_suffix(".part")
    with urllib.request.urlopen(url, timeout=60) as response, partial.open("wb") as stream:
        while chunk := response.read(1024 * 1024):
            stream.write(chunk)
    if partial.stat().st_size != expected["bytes"] or digest(partial) != expected["sha256"]:
        partial.unlink()
        raise ValueError(f"Download checksum/size mismatch: {target.name}")
    partial.replace(target)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, default=ARTIFACTS / "e5-source")
    parser.add_argument("--model-dir", type=Path, default=ARTIFACTS / "e5-int8")
    args = parser.parse_args()
    args.source_dir.mkdir(parents=True, exist_ok=True)
    args.model_dir.mkdir(parents=True, exist_ok=True)
    source = json.loads((ROOT / "e5_model.json").read_text())
    output = json.loads((ROOT / "e5_int8_model.json").read_text())
    for name, expected in source["files"].items():
        target = args.source_dir / Path(name).name
        url = f"https://huggingface.co/{source['model']}/resolve/{source['revision']}/{name}"
        download(url, target, expected)
    target_model = args.model_dir / "model.onnx"
    expected_model = output["files"]["model.onnx"]
    if not (target_model.exists() and target_model.stat().st_size == expected_model["bytes"] and digest(target_model) == expected_model["sha256"]):
        quantize_dynamic(args.source_dir / "model.onnx", target_model, weight_type=QuantType.QInt8)
    if target_model.stat().st_size != expected_model["bytes"] or digest(target_model) != expected_model["sha256"]:
        raise ValueError("Generated INT8 model checksum/size mismatch; verify pinned quantization dependencies")
    shutil.copyfile(args.source_dir / "tokenizer.json", args.model_dir / "tokenizer.json")
    expected_tokenizer = output["files"]["tokenizer.json"]
    if digest(args.model_dir / "tokenizer.json") != expected_tokenizer["sha256"]:
        raise ValueError("Tokenizer checksum mismatch")
    print(f"Verified dynamic INT8 model: {expected_model['bytes']} bytes")


if __name__ == "__main__":
    main()
