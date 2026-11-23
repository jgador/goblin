# Local CPU embedding experiment

This standalone .NET 10 console application measures a memory pipeline separately
from Goblin's production application. It uses SQLite FTS5 and ONNX Runtime's CPU
provider, with no AI framework or hosted inference. All fixtures are synthetic.

See [REPORT.md](REPORT.md) for the sandbox measurements and recommendations.
[results/](results/) contains the 2,000-record corpus, `memory.db`, JSON summaries,
periodic resource CSVs, storage checkpoints, and retrieval results.

## Reproduce

Install the .NET 10 SDK and `curl` on Linux, then from this directory:

```sh
./download-models.sh
./run.sh
```

Use `./download-models.sh --nomic` to include the secondary Nomic FP32 model.
The model downloads are pinned by revision and SHA-256. Large model weights are
excluded from Git. Inference after downloading requires no network connection.
`packages.lock.json` locks the two direct NuGet dependencies and their transitives.
A .NET SDK download in a restricted user namespace may need
`TAR_OPTIONS=--no-same-owner` during `dotnet-install.sh` extraction.

`run.sh` preserves committed measurements, creates a new result directory under
`../../.artifacts/local-cpu-embeddings/`, and runs experiments sequentially so
benchmarks do not compete with each other. The committed measurements are from
one sandbox run, not a hardware-independent performance guarantee.

## Commands

Build once, then run the Release DLL to avoid timing SDK/build processes:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet bin/Release/net10.0/LocalCpuEmbeddings.dll environment OUTPUT
dotnet bin/Release/net10.0/LocalCpuEmbeddings.dll ingest OUTPUT
dotnet bin/Release/net10.0/LocalCpuEmbeddings.dll bench OUTPUT models/bge-int8.onnx 1 2
dotnet bin/Release/net10.0/LocalCpuEmbeddings.dll embed OUTPUT models/bge-int8.onnx 1 2
dotnet bin/Release/net10.0/LocalCpuEmbeddings.dll retrieve OUTPUT models/bge-int8.onnx 1 2
dotnet bin/Release/net10.0/LocalCpuEmbeddings.dll worker OUTPUT models/bge-int8.onnx 1 2
```

`ingest`, `bench`, and `worker` require a fresh database destination. `embed`
processes pending records in an existing database. `retrieve` requires at least
1,000 ready vectors. The two numeric arguments are batch size and ORT intra-op
threads. Add `length` as a final benchmark argument for simple length grouping. ORT inter-op threads are fixed at one and idle thread spinning is disabled.
The dedicated worker's artificial CPU load has an automatic 120-second stop and
is killed after its test phase. The worker test itself has a five-minute deadline.

`tokens OUTPUT` exports tokenization cases; `validate OUTPUT MODEL` exports 64
vectors for correctness checks. These are separate from performance measurements.

## Implementation

- `Corpus.cs`: 13 source types, varied lengths, exact identifiers, semantic pairs,
  deterministic acknowledgement filters, and fixed seed ordering.
- `MemoryStore.cs`: ingestion transaction, FTS synchronization triggers, pending
  selection, normalized float32 vector BLOBs, cosine scan, and RRF (`k=60`).
- `WordPiece.cs`: uncased BERT tokenization, special tokens, greedy subwords,
  padding handled by the embedder, and 512-token truncation.
- `Embedder.cs`: CPU ONNX session, BGE CLS pooling and L2 normalization; query
  instruction is applied only to queries. The optional Nomic path uses masked
  mean pooling, full-dimensional layer normalization, L2 normalization, and the
  required document/query prefixes (768 dimensions, a 512-token test cap).
- `Resources.cs`: 50-ms process sampling, cgroup CPU usage/limits, Linux available
  memory, optional PSI, GC heap, RSS, and thread count.
- `Program.cs`: benchmark commands, storage checkpoints, retrieval checks, and
  the capacity-aware worker loop.

The database also stores a `topic` fixture label for reproducible retrieval
assessment. Labels never enter model text or retrieval scoring. Source references
are `synthetic://` provenance. The worker marks the six explicitly matched
noise strings as `skipped_noise`, preserving their FTS entries; the storage experiment deliberately embeds every record.

The experiment does not integrate this memory into Goblin, replace its durable
Work store, test concurrent production writers, or implement a vector index.
