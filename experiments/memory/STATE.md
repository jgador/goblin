# Memory experiment state

## Current champion

Run 001: `sqlite-fts5-bm25`

Hypothesis: SQLite FTS5 with BM25 is a useful low-cost lexical baseline for messy
memory retrieval.

Status: accepted as the bootstrap champion because there was no prior champion.

Benchmark generation: 1
Benchmark SHA-256: `c4d740bdf833d7c9f6294fb42718354d4c764c35d71b403b325af3434a6ebd74`
Corpus: 40 documents
Queries: 20

### Champion metrics

- MRR@5: 0.941667
- Recall@5: 0.95
- Hit@1: 0.90
- Mean retrieval latency: 0.089905 ms
- P95 retrieval latency: 0.096322 ms
- Mean returned context: about 169.4 tokens using chars/4 approximation
- CPU time for the 20-query evaluation: 0.001863 s
- Peak process RSS: 92.164 MB
- SQLite database size: 45,056 bytes

Peak RSS includes the Python interpreter and experiment harness, so it should be
used as a relative metric between runs in the same environment rather than as the
standalone memory cost of SQLite FTS5.

## What run 001 learned

FTS5 is already strong on exact project terminology and operational facts while
remaining tiny and fast. It retrieved a relevant document first for 18 of 20
queries. The two visible weaknesses are useful targets for later runs:

- q011 (server hardware) ranked the relevant inventory record third because
  generic words such as `host`, `small`, and `vm` also occur in unrelated notes.
- q019 (dynamic System.Text.Json exceptions) ranked the general typed-POCO rule
  before the exception record, putting the specifically relevant record second.

These failures suggest that the next improvements should focus on query intent or
ranking rather than replacing lexical search immediately.

## Next hypotheses

Test only one per hourly run, in this approximate order unless new evidence makes
another experiment more useful:

1. Add lightweight term/phrase weighting so distinctive terms and exception
   language outrank generic overlaps.
2. Add source authority and recency weighting to handle stale notes versus later
   corrections without losing deterministic lexical retrieval.
3. Add deterministic deduplication/correction linking before retrieval.
4. Add compact per-record summaries and compare quality versus context size.
5. Add CPU-only local embeddings as a challenger to lexical search.
6. Try lexical + vector hybrid retrieval only after a vector-only result exists.

## Reproduction

From the repository root:

```bash
python3 experiments/memory/run_experiment.py --json
```

Expected quality for generation 1 is stable: MRR@5 0.941667, Recall@5 0.95, and
Hit@1 0.90. Latency, CPU, and RSS vary by sandbox and should be compared only to
runs made in a similar environment.

## Run 002 recovery and rerun

The original Run 002 patch was not retained in the accessible workspace or
conversation history. The prior report claimed MRR@5 and Hit@1 of 1.0, but
those measurements are not verified artifacts. A reconstruction of IDF-weighted
lexical coverage plus a small exception-intent reranker was evaluated on the
unchanged benchmark on 2026-10-06. It achieved MRR@5 0.941667, Recall@5 0.95,
and Hit@1 0.90, so it was rejected. Run 001 remains the champion.

Fresh candidate and baseline measurements, along with explicit provenance, are
stored in `runs/002.json`. This recovery does not claim to be the original patch.
The candidate only reranks the BM25 top five and cannot improve candidate recall.
The exception-intent rule risks overfitting and needs held-out validation.

Reproduce the rejected candidate:

```bash
python3 experiments/memory/run_experiment.py --method idf-intent --json
```

The default remains the accepted BM25 baseline. Next, establish held-out queries
before adding further intent rules, then test source authority and recency.
