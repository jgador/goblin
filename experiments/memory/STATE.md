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

## Run 006 held-out validation

Run 006 tested one hypothesis: the accepted Run 005 compact sketches preserve answer-bearing evidence on unseen queries while retaining their context reduction.

The original generation-1 corpus and query benchmark was not changed. A separate 20-query held-out evidence set was added and is now frozen with SHA-256 `80d3af75bc7fe4b3b28b8f3076dae5c47d807b6ab48379af94c6ad10b4c0c855`.

On the held-out set, full Run 004-style context and compact Run 005-style context produced identical retrieval quality: MRR@5 0.975, Recall@5 1.0, and Hit@1 0.95. Both preserved all required answer evidence at top 5. Compact context fell from 167.95 to 151.95 approximate tokens, a 9.53% reduction.

Measured in the Work sandbox, full-context evaluation used 0.003078 CPU seconds with 0.118191 ms mean retrieval/context latency; compact evaluation used 0.003675 CPU seconds with 0.153664 ms mean latency. Peak process RSS was 92.285 MB for both and the SQLite database remained 45,056 bytes. These process measurements are environment-sensitive.

Run 006 is accepted as validation infrastructure only. It does not replace the retrieval/context algorithm, so Run 005 remains the champion.

## Lessons so far

- Run 001 established SQLite FTS5 + BM25 as a strong lightweight baseline.
- Run 002's reconstructed IDF/intent reranker did not improve quality.
- Run 003's generic source-authority and recency prior did not improve quality.
- Run 004 showed that lifecycle semantics can suppress stale context without discarding history.
- Run 005 showed that context assembly can be optimized independently of storage and retrieval.
- Run 006 showed that Run 005's 9%+ context reduction generalizes to a separate held-out evidence set without losing required answer evidence.

## Next hypotheses

1. Test sentence- or clause-level compact summaries against Run 005 on both the original benchmark and the held-out evidence set.
2. Add CPU-only local embeddings as a challenger to the lexical champion and evaluate both benchmark sets.
3. Try lexical + vector hybrid retrieval only after a vector-only result exists.
4. Expand supersession semantics only with generic evidence, not query-specific rules.

## Reproduction

```bash
python3 experiments/memory/run_experiment.py --json
```

The default reproduces Run 005. Previous accepted stages remain available:

```bash
python3 experiments/memory/run_experiment.py --method supersession --json
python3 experiments/memory/run_experiment.py --method bm25 --json
```

Reproduce Run 006 held-out evidence validation:

```bash
python3 experiments/memory/run_heldout_validation.py
```

Run 006 used GPT-6.1 Sol as the requested orchestration model. The orchestration model is not independently verifiable from the experiment harness; retrieval and evidence scoring use no LLM or external API.
