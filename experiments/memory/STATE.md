# Memory experiment state

## Current champion

Run 005: `sqlite-fts5-bm25-supersession-compact-sketch`

Hypothesis: a deterministic per-record context sketch can reduce prompt context while preserving retrieval quality and the record's semantic content terms.

Status: accepted on benchmark generation 1. Retrieval remains MRR@5 0.941667, Recall@5 0.95, and Hit@1 0.90. Mean approximate returned context falls from 166.1 tokens in Run 004 to 150.5 tokens in Run 005, a 9.39% reduction.

Benchmark generation: 1
Benchmark SHA-256: `c4d740bdf833d7c9f6294fb42718354d4c764c35d71b403b325af3434a6ebd74`
Corpus: 40 documents
Queries: 20

### Champion behavior

Run 005 retains the Run 004 supersession filter and keeps full original records in SQLite for retrieval and provenance. Only the context handed to a downstream model is compacted.

The compactor removes a conservative set of high-frequency function words. It deliberately preserves negation, conjunctions, modality, temporal words, identifiers, numbers, and content terms. On this benchmark the resulting sketches retain 100% of those content terms.

The full record remains stored. The sketch is generated at context assembly time, so persistent storage remains unchanged.

### Champion metrics

- MRR@5: 0.941667
- Recall@5: 0.95
- Hit@1: 0.90
- Mean returned context: about 150.5 tokens
- Context reduction versus Run 004: 9.39%
- Content-term retention proxy: 1.0
- SQLite database size: 45,056 bytes
- Extra persistent summary storage: 0 bytes
- Compaction microbenchmark: about 5.96 microseconds per record in the connector V8 runtime

Comparable process CPU and peak RSS were not exposed by the connector execution runtime for this run. The committed Python reproduction records latency, CPU, and peak RSS when run in the Work sandbox.

## Lessons so far

- Run 001 established SQLite FTS5 + BM25 as a strong lightweight baseline.
- Run 002's reconstructed IDF/intent reranker did not improve quality.
- Run 003's generic source-authority and recency prior did not improve quality.
- Run 004 showed that lifecycle semantics can suppress stale context without discarding history.
- Run 005 shows that context assembly can be optimized independently of storage and retrieval. Conservative deterministic sketches cut another 9.39% of approximate context while preserving the benchmark's retrieval scores and all non-function content terms.

## Next hypotheses

1. Add a held-out answer/evidence benchmark before making compaction more aggressive.
2. Test sentence- or clause-level compact summaries against Run 005 instead of removing more individual words.
3. Add CPU-only local embeddings as a challenger to the lexical champion.
4. Try lexical + vector hybrid retrieval only after a vector-only result exists.
5. Expand supersession semantics only with generic evidence, not query-specific rules.

## Reproduction

```bash
python3 experiments/memory/run_experiment.py --json
```

The default reproduces Run 005. Previous accepted stages remain available:

```bash
python3 experiments/memory/run_experiment.py --method supersession --json
python3 experiments/memory/run_experiment.py --method bm25 --json
```
