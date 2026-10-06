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
passes the predeclared gate and is the current champion.

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8_maxabs_fp16_rerank --threads 2
```

See `runs/020.json` for complete measurements and rankings. No external
inference API or answer generator is used.
