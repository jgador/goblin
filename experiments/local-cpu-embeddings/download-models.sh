#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p models
bge_revision=5c38ec7c405ec4b44b94cc5a9bb96e735b38267a
int8_revision=ea104dacec62c0de699686887e3f920caeb4f3e3
fetch() {
  local target="$1" url="$2" expected="$3"
  if [[ ! -f "models/$target" ]] || ! echo "$expected  models/$target" | sha256sum --check --status; then
    curl --fail --location --retry 3 --max-time 300 "$url" --output "models/$target.download"
    echo "$expected  models/$target.download" | sha256sum --check --status
    mv "models/$target.download" "models/$target"
  fi
}
fetch vocab.txt "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/$bge_revision/vocab.txt" 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
fetch bge-fp32.onnx "https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/$bge_revision/onnx/model.onnx" 828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35
fetch bge-int8.onnx "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/$int8_revision/onnx/model_quantized.onnx" 6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4
if [[ "${1:-}" == "--nomic" ]]; then
  nomic_revision=e9b6763023c676ca8431644204f50c2b100d9aab
  fetch nomic-fp32.onnx "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/$nomic_revision/onnx/model.onnx" 147d5aa88c2101237358e17796cf3a227cead1ec304ec34b465bb08e9d952965
  fetch nomic-vocab.txt "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/$nomic_revision/vocab.txt" 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
fi
