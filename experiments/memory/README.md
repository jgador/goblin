# Memory experiment loop

This directory is the persistent state for the hourly memory/context experiment.
The benchmark is intentionally small, local, deterministic, and CPU-only so each
run can test one retrieval or context-management hypothesis cheaply.

## Stable benchmark

- `corpus.jsonl`: messy multi-source records, including duplicates, corrections,
  stale notes, decisions, operational facts, and project requirements.
- `queries.jsonl`: fixed retrieval questions and relevant document IDs.
- The benchmark hash is recorded in every run. Do not change the corpus or
  queries during ordinary hourly experiments. If the benchmark must evolve,
  treat that as a dedicated experiment and start a new benchmark generation.

## Run contract

Each run must:

1. Read `STATE.md` and `results.jsonl` first.
2. Test exactly one hypothesis against the same benchmark.
3. Measure retrieval quality, approximate context size, latency, CPU, peak RSS,
   and storage where practical.
4. Compare the candidate with the current champion.
5. Accept the candidate only when quality improves without an unacceptable
   resource regression. Otherwise keep the champion and record the failure.
6. Append one summary object to `results.jsonl` and write detailed output under
   `runs/NNN.json`.
7. Update `STATE.md` with the champion, lesson, and next hypotheses.

The scheduled task requests GPT-6.1 Sol rather than GPT-6 Astra for orchestration.
The benchmark itself does not call an LLM, so retrieval scoring is deterministic
and incurs no external model/API cost.

## Reproduce the lightweight fallback

```bash
python3 experiments/memory/run_experiment.py
```

The script uses only the Python standard library and requires SQLite with FTS5.

The default now reproduces Run 005: Run 004 supersession filtering plus a
conservative context-only compact sketch. Use `--method supersession` for Run 004,
`--method bm25` for the original lexical baseline, or `--method idf-intent` for
the rejected reconstructed Run 002 candidate.

## Local embedding challenger (Run 007)

Nomic Embed Text v1.5, FP32/768 dimensions, CPU ONNX Runtime, and SQLite
`sqlite-vec`/`vec0` are now reproducible. See `STATE.md` for installation and
commands, `nomic_model.json` for pinned files, and `runs/007.json` for results.
The vector-only candidate lost on original MRR/Hit@1, so Run 005 stayed the
default for that run. The optional embedding harness is retained for hybrid and small-model
comparisons. It embeds full original records; supersession filtering and compact
context assembly match the lexical champion. No PostgreSQL, containers, remote
inference, or answer-generating LLM is used.

Model conventions: https://huggingface.co/nomic-ai/nomic-embed-text-v1.5
SQLite extension: https://github.com/asg017/sqlite-vec

## Hybrid semantic champion (Run 008)

Run 008 combines FTS5 and Nomic with fixed equal-weight reciprocal-rank fusion
(`k=60`, top 10 from each retriever). It improved ranking quality on both frozen
benchmark sets and is the semantic-quality champion. Run 005 remains the
lightweight fallback because it avoids the roughly 548 MB model and roughly
791 MiB measured peak process RSS.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --retrieval hybrid --threads 2
```

See `runs/008.json` for full resource measurements and per-query rankings. This
is retrieval plus context assembly, not end-to-end answer generation.

## BGE-small challenger (Run 009)

Run 009 substitutes `BAAI/bge-small-en-v1.5` for Nomic while preserving the
fixed Run 008 fusion rule and both frozen benchmark sets. It records vector-only
diagnostics and the hybrid candidate in one model-substitution experiment. BGE
was much smaller and faster, but its hybrid lost the champion's perfect
held-out top-rank score, so it is retained as reproducible comparison
infrastructure rather than promoted.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model bge
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model bge --threads 2
```

The model revision, file checksums, 384-dimensional CLS pooling convention,
query instruction, normalization, and 512-token limit are pinned in
`bge_model.json`. See `runs/009.json` for complete measurements and rankings.

## E5-small-v2 semantic champion (Run 010)

Run 010 substitutes `intfloat/e5-small-v2` for Nomic under the unchanged fixed
RRF rule. It matches all Run 008 retrieval/evidence metrics while reducing
model size, RSS, CPU, latency, SQLite size and assembled context. It is now the
semantic-quality champion; Run 005 remains the no-embedding fallback.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model e5
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --threads 2
```

The exact revision, file checksums, 384-dimensional attention-mask mean pooling,
`query: ` and `passage: ` prefixes, L2 normalization, and 512-token limit are
pinned in `e5_model.json`. See `runs/010.json` for complete measurements and
rankings.

## MiniLM challenger (Run 011)

Run 011 substitutes `sentence-transformers/all-MiniLM-L6-v2` for E5 under the
unchanged fixed RRF rule. It is smaller and faster, but its hybrid lost Run
010's perfect held-out top-rank score. MiniLM remains reproducible comparison
infrastructure rather than the default semantic champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model minilm
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model minilm --threads 2
```

The exact revision, checksums, symmetric no-prefix inputs, 384-dimensional
attention-mask mean pooling, L2 normalization, and 256-word-piece limit are
pinned in `minilm_model.json`. See `runs/011.json` for complete measurements.

## E5 dynamic INT8 challenger (Run 012)

Run 012 dynamically quantizes the pinned E5-small-v2 FP32 ONNX model to a
portable QInt8-weight graph with ONNX Runtime. It substantially reduces model
size, measured RSS, CPU time and latency, but q011 falls from rank 1 to rank 2
under the unchanged fixed RRF rule. The candidate is therefore retained as
reproducible quantization infrastructure rather than promoted over Run 010.

```bash
python3 -m venv .artifacts/memory-loop/venv
.artifacts/memory-loop/venv/bin/pip install -r experiments/memory/quantization_requirements.txt
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_e5_int8_model.py
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5_int8 --threads 2
```

The generated model checksum, source checksum, quantization settings, runtime
versions and E5 input/pooling conventions are pinned in `e5_int8_model.json`.
See `runs/012.json` for complete vector-only and hybrid measurements. The
benchmark command runs offline and no answer-generating model is evaluated.

## Nomic 256-dimensional Matryoshka challenger (Run 013)

Run 013 truncates Nomic's layer-normalized 768-dimensional embeddings to the
first 256 dimensions before L2 normalization, following the model's documented
Matryoshka operation order. The smaller vectors cut the SQLite database by
64.97% versus Run 008, but q003 fell from rank 1 to rank 2 under the unchanged
fixed RRF rule. Run 010 E5 remains the semantic-quality champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_nomic_model.py
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model nomic_256 --threads 2
```

The final command is offline. Exact model revision, file checksums, dimensions,
prefixes, pooling and normalization order are pinned in `nomic_256_model.json`.
See `runs/013.json` for complete measurements and rankings.

## Query-aware clause context champion (Run 014)

Run 014 preserves Run 010's E5 retrieval and fixed RRF rankings, then applies a
conservative query-aware context selector. It always keeps each record's first
clause, retains later query-overlap or semantic-operator clauses, and finally
applies the existing compact sketch. Original context fell 5.52% and held-out
context fell 5.64% without changing retrieval quality or evidence retention.

Isolated context comparison without model weights:

```bash
python3 experiments/memory/run_context_selection_experiment.py --iterations 1000
```

Complete offline E5 pipeline after model preparation:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context query_aware --threads 2
```

Run 014 is the semantic-quality champion's current context-assembly strategy.
See `runs/014.json` for isolated assembly timing, full embedding/search/context
timing, selection rules and per-query evidence results.

## Conservative cross-record deduplication (Run 015)

Run 015 replayed Run 014 and tested fixed cross-record near-duplicate clause
removal with exact semantic-operator and identifier guards plus source
provenance merging. It found no eligible duplicates on either frozen set, left
context unchanged, and increased isolated assembly cost, so it was rejected.
Run 014 remains the champion; the deduplication thresholds were not relaxed
after observing benchmark results.

```bash
python3 experiments/memory/run_deduplication_experiment.py --iterations 1000
```

See `runs/015.json` for the rule, timing, provenance invariant, and per-query
results. The replay uses no model weights, remote APIs, or answer generator.

## Fixed rank-aware context budget (Run 016)

Run 016 tested a 128 approximate-token target after Run 014, retaining one
selected clause per retrieved record before admitting later clauses in rank
order. It reduced original context by 7.70% and held-out context by 11.95%, but
held-out evidence retention fell from 1.0 to 0.9 when required second clauses
were omitted for h013 and h017. The candidate was rejected and Run 014 remains
the context champion.

```bash
python3 experiments/memory/run_context_budget_experiment.py --iterations 1000
```

See `runs/016.json` for the fixed rule, timing, evidence failures, and complete
retained/omitted provenance. No model weights, remote APIs, or answer generator
are used by the replay.

## Rank-one protected context champion (Run 017)

Run 017 keeps Run 016's 128 approximate-token target but protects every selected
clause in the rank-one record before allocating optional lower-rank context. It
restored held-out evidence from 0.9 to 1.0 while reducing Run 014 context by
7.70% original and 9.78% held-out. Retrieval quality and provenance invariants
were unchanged, so Run 017 is the new context champion.

Isolated replay:

```bash
python3 experiments/memory/run_protected_context_budget_experiment.py --iterations 1000
```

Complete offline E5 pipeline:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --threads 2
```

See `runs/017.json` for full model, CPU, memory, SQLite, latency, quality,
context, and per-query provenance measurements. No external inference API or
answer generator is used.

## sqlite-vec int8 storage challenger (Run 018)

Run 018 stores normalized FP32 E5 outputs in a native sqlite-vec `int8[384]`
column using `vec_quantize_int8(vector, 'unit')`; query vectors use the same
conversion. SQLite shrank 71.29%, from 1,654,784 to 475,136 bytes, and every
scored retrieval/evidence metric matched Run 017. Quantized ranking changes
nevertheless increased mean context by 1.98% original and 0.95% held-out, so the
candidate failed its fixed no-context-increase gate. Run 017 remains champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8 --threads 2
```

See `runs/018.json` for complete measurements and rankings. FP32 model inference
is unchanged; only stored and search-query vector precision changes. No external
inference API or answer generator is used.

## Per-vector max-absolute int8 challenger (Run 019)

Run 019 scales each normalized FP32 E5 vector independently so its largest
absolute component maps to 127, then rounds and clips to native sqlite-vec
`int8[384]`. It retained Run 018's 71.29% SQLite reduction and exactly matched
Run 017 on every original hybrid top-five result and its context. Two held-out
hybrid top-five sets still changed, increasing held-out context by 0.61%, so the
fixed gate rejected the candidate and Run 017 remains champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs --threads 2
```

See `runs/019.json` for complete measurements and rankings. FP32 model inference
is unchanged; only stored and search-query vector precision changes. No external
inference API or answer generator is used.

## Int8 candidates with FP16 reranking (Run 020)

Run 020 searches a max-absolute int8 vec0 index for 20 candidates, then reranks
them to the existing top 10 using FP16 vectors stored as ordinary SQLite blobs
before unchanged RRF. It exactly matched every Run 017 vector-only and hybrid
top-five result, evidence score, and context mean on both frozen sets.

SQLite fell 69.06%, from 1,654,784 to 512,000 bytes. Candidate search plus
reranking remained near 1 ms, below the fixed 2 ms gate. End-to-end latency rose
to 9.17 ms original and 9.37 ms held-out, an explicit storage tradeoff. Run 020
passes the predeclared gate and remains the retrieval and vector-storage core.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --threads 2
```

See `runs/020.json` for complete measurements and rankings. No external
inference API or answer generator is used.

## Content-addressed SQLite embedding cache (Run 021)

Run 021 adds a reusable FP32 embedding cache to the Run 020 SQLite database.
Keys bind the pinned E5 revision, dimensions, prefixes, pooling, normalization,
token limit, and distance convention to NFC-normalized document text. The
retrieval, reranking, fusion, supersession, and context pipeline is unchanged.

The cold build produced 40 misses. An unchanged warm reindex produced 40 hits
and no inference, reducing wall time from 0.380 seconds to 0.000587 seconds
(99.85%). A deterministic edit to one record produced 39 hits and one miss in
0.00976 seconds. Every Run 020 vector-only and hybrid top-five result, quality
metric, evidence score, and context mean matched exactly. Combined SQLite size
rose from 512,000 to 606,208 bytes, an 18.40% increase within the fixed 25%
gate, so Run 021 is accepted as the operational cache champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_content_sha256 --threads 2
```

See `runs/021.json` for cold, warm, incremental-update, model, CPU, RSS,
SQLite, latency, quality, context, and per-query measurements. Model inference
is local and offline after preparation. No external inference API or answer
generator is used.

## Namespace-safe cache invalidation and cleanup (Run 022)

Run 022 tests whether Run 021's cache namespace prevents embeddings created
under different conventions from being reused. A controlled change from 512 to
511 maximum tokens produced a distinct namespace, 0 hits, and 40 misses. The
benchmark records are short enough that the resulting vectors were identical,
but the cache still correctly treated them as incompatible.

Deleting the stale namespace and vacuuming SQLite removed exactly 40 of 80
entries in 0.83 ms, retained all 40 active entries, and restored 40/40 active
cache hits. Every Run 021 vector-only and hybrid ranking, quality metric,
evidence score, and context mean matched. Combined SQLite size fell slightly
from 606,208 to 602,112 bytes, so Run 022 passes the fixed gate and becomes the
operational cache champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_namespace_gc --threads 2
```

See `runs/022.json` for namespace, cleanup, cache, model, CPU, RSS, SQLite,
latency, quality, context, and per-query measurements. No external inference
API or answer generator is used.

## Durable pending embedding lifecycle (Run 023)

Run 023 adds a SQLite `pending` / `processing` / `ready` lifecycle around the
Run 022 cache and vector pipeline. Ingestion commits the full record to FTS5 and
its pending job before the embedding runtime loads, so lexical retrieval remains
immediately available.

The enqueue transaction stored all 40 records and jobs in 0.51 ms. With a
controlled half-warm cache, a FIFO worker processed ten batches of four in
0.354 seconds, producing exactly 20 cache hits and 20 local inference misses.
All jobs reached ready after one attempt. Every Run 022 lexical, vector-only,
and hybrid ranking, quality metric, evidence score, and context mean matched.
SQLite grew from 602,112 to 630,784 bytes, or 4.76%, so Run 023 passes the fixed
gate and becomes the operational ingestion/cache champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_pending_queue --threads 2
```

See `runs/023.json` for enqueue, pending-query, per-batch worker, cache, model,
CPU, RSS, SQLite, latency, quality, context, and per-query measurements. No
external inference API or answer generator is used.

## Restart recovery for pending embeddings (Run 024)

Run 024 injects one deterministic interruption after ordinals 0 through 3 are
committed as `processing` and before any cache lookup or inference. It closes
and reopens SQLite, confirms all four jobs remain stranded, then atomically
reclaims them to `pending` before starting the unchanged FIFO worker.

Recovery took 0.484 ms wall time and reclaimed exactly four jobs. The recovered
ordinals formed the first worker batch. Completion produced 40 unique ready
documents and 40 unique cache keys, with 36 jobs attempted once and the four
interrupted jobs attempted twice. The worker retained Run 023's ten batches,
20 cache hits, 20 misses, exact lexical/vector/hybrid rankings and context, and
630,784-byte database. Run 024 passes every predeclared gate and becomes the
operational ingestion/cache champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_pending_recovery --threads 2
```

See `runs/024.json` for the durable interruption state, reopen observation,
reclaim timing, attempt distribution, output uniqueness, queue batches, cache,
model, CPU, RSS, SQLite, latency, quality, context, and per-query measurements.
No external inference API or answer generator is used.

## Pending content supersession challenger (Run 025)

Run 025 updates `d040` after initial enqueue and before model loading or worker
claim. One transaction replaces the FTS5 text, marks revision 1 superseded, and
enqueues revision 2 while retaining the old content hash as job history. The
update completed in 0.205 ms.

The lifecycle behavior passed: revision 1 had zero attempts and no vector or
cache-key attachment, revision 2 alone reached ready, and the final state held
40 unique ready documents plus one superseded historical job. The worker kept
ten FIFO batches, 20 cache hits, 20 misses, and finished in 0.259 seconds.

The candidate was rejected because the declared content change altered four
lexical, nine vector-only, and five hybrid query rankings. Hybrid quality and
held-out evidence remained unchanged, but mean held-out context increased from
131.40 to 131.85 approximate tokens. The update text was not tuned after seeing
the result, and Run 024 remained the operational champion at that stage.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_pending_supersession --threads 2
```

See `runs/025.json` for revision hashes, FTS5 provenance, cache attachments,
queue metrics, changed rankings, model, CPU, RSS, SQLite, latency, quality, and
context measurements. This evaluates retrieval and context assembly, not answer
generation.

## Synchronous updated-corpus control (Run 026)

Run 026 repeats the Run 025 `d040` pending update and builds a separate
synchronous SQLite control from the identical updated corpus. The control uses
independent local CPU embedding, the same explicit cosine `vec0` schema, int8
candidate index, FP16 reranker, FTS5 configuration, fusion, supersession filter,
and context assembler. The first measured query vector is reused across both
indexes so the comparison isolates index and lifecycle behavior; repeated query
inference remains timed and produced zero measured vector delta in this run.

The asynchronous and synchronous paths matched exactly for all original and
held-out FTS5, vector-only, and hybrid top-five rankings, retrieval/evidence
metrics, context sizes, and all 40 corpus vectors. The asynchronous database was
626,688 bytes, 0.65% smaller than Run 024 and within the fixed 5% bound. Hybrid
quality stayed at original MRR@5 0.975 / Hit@1 0.95 and perfect held-out scores
and evidence. Mean context was 128.20 original and 131.85 held-out, both within
the fixed 5% bound relative to Run 024. Run 026 is accepted as the operational
ingestion champion; the changed rankings versus Run 024 remain documented as
natural content drift, not asynchronous failure.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_pending_supersession_control --threads 2
```

See `runs/026.json` for the exact parity gates, full per-query results, lifecycle
provenance, model and dependency pins, CPU, RSS, storage, and separated worker,
control-embedding, vector-search, and end-to-end timings. No answer generator or
external inference API was evaluated.

## Two-worker atomic claims (Run 027)

Run 027 retains Run 026's updated-corpus retrieval pipeline and adds two local
workers, each with its own SQLite connection. Every four-job claim uses
`BEGIN IMMEDIATE`, FIFO selection, status-guarded updates, and a commit before
inference. Ready attachment is guarded by both processing state and durable
worker ownership.

The workers each claimed five batches and 20 records. Their claim sets had no
overlap, all 40 active jobs had exactly one attempt and one unique attachment,
and the stale revision remained superseded with zero attempts. The slowest
claim took 2.006 ms wall time. Concurrent draining took 0.292 seconds wall and
0.743 seconds process CPU with exactly 20 cache hits and 20 misses.

All Run 026 lexical, vector-only, and hybrid rankings, quality, evidence, and
context matched exactly. A fresh full-corpus replay produced byte-identical
vectors with zero maximum delta. SQLite remained 626,688 bytes. Run 027 is
accepted as the operational concurrency champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --embedding-cache sqlite_pending_two_workers --threads 2
```

See `runs/027.json` for worker attribution, claim transactions, per-batch
timings, vector replay, queue provenance, full per-query measurements, and every
acceptance gate. This evaluates retrieval and context assembly, not answer
generation.
