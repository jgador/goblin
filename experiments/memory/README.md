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
