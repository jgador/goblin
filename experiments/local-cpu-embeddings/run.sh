#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
run_root="../../.artifacts/local-cpu-embeddings/run-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$run_root"
dotnet restore --locked-mode
dotnet build -c Release --no-restore
application=bin/Release/net10.0/LocalCpuEmbeddings.dll
dotnet "$application" environment "$run_root/results"
dotnet "$application" ingest "$run_root/results"
for format in int8 fp32; do
  for batch in 1 8 16 32 64; do
    dotnet "$application" bench "$run_root/bench-$format-$batch-t2" "models/bge-$format.onnx" "$batch" 2
  done
done
for batch in 8 16 32 64; do
  dotnet "$application" bench "$run_root/bench-int8-$batch-length" models/bge-int8.onnx "$batch" 2 length
done
for threads in 1 4; do
  dotnet "$application" bench "$run_root/bench-int8-1-t$threads" models/bge-int8.onnx 1 "$threads"
done
if command -v taskset >/dev/null && taskset -c 0,1 true 2>/dev/null; then
  taskset -c 0,1 dotnet "$application" bench "$run_root/bench-affinity2" models/bge-int8.onnx 1 1
fi
if [[ -f models/nomic-fp32.onnx ]]; then
  dotnet "$application" bench "$run_root/bench-nomic-1-t2" models/nomic-fp32.onnx 1 2
fi
dotnet "$application" embed "$run_root/results" models/bge-int8.onnx 1 1
dotnet "$application" retrieve "$run_root/results" models/bge-int8.onnx 1 1
dotnet "$application" worker "$run_root/worker" models/bge-int8.onnx 1 1
printf 'Results: %s\n' "$run_root"
