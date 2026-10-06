# Memory experiment state

## Current champion

Run 004: `sqlite-fts5-bm25-supersession-filter`

Hypothesis: deterministic correction/supersession linking can remove stale retrieved memories without reducing retrieval quality.

Status: accepted. The fixed generation-1 benchmark kept MRR@5 at 0.941667, Recall@5 at 0.95, and Hit@1 at 0.90 while mean returned context fell from about 169.4 to 166.1 tokens, a 1.95% reduction.

Benchmark generation: 1
Benchmark SHA-256: `c4d740bdf833d7c9f6294fb42718354d4c764c35d71b403b325af3434a6ebd74`
Corpus: 40 documents
Queries: 20

### Champion behavior

Run 004 keeps all records in SQLite for provenance but suppresses records that can be deterministically linked to a later correction or confirmation.

- `d001 -> d003`: temporary deployment note superseded by the confirmed postmortem.
- `d008 -> d007`: old automatic-retry brainstorm superseded by the explicit no-automatic-retry decision.

The detection is query-independent: a record must contain an explicit stale marker, a later candidate must contain correction/confirmation language, and significant-term overlap must meet the fixed threshold.

### Champion metrics

- MRR@5: 0.941667
- Recall@5: 0.95
- Hit@1: 0.90
- Mean returned context: about 166.1 tokens
- SQLite database size: 45,056 bytes
- q001 context reduction: 19.45%
- q002 context reduction: 14.29%

Latency, CPU, and RSS are environment-sensitive. Acceptance is based on unchanged retrieval quality plus lower returned context.

## Lessons so far

- Run 001 established SQLite FTS5 + BM25 as a strong lightweight baseline.
- Run 002's reconstructed IDF/intent reranker did not improve quality.
- Run 003's generic source-authority and recency prior did not improve quality.
- Run 004 shows that lifecycle semantics can reduce context without discarding history.

## Next hypotheses

1. Add compact per-record summaries and compare retrieval quality versus context size.
2. Add a held-out query split before introducing more tunable reranking rules.
3. Add CPU-only local embeddings as a challenger to the lexical champion.
4. Try lexical + vector hybrid retrieval only after a vector-only result exists.
5. Expand supersession semantics only with generic evidence, not query-specific rules.

## Reproduction

```bash
python3 experiments/memory/run_experiment.py --json
```

The default reproduces Run 004. For the original baseline:

```bash
python3 experiments/memory/run_experiment.py --method bm25 --json
```
