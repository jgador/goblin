# Memory experiment state

## Current champions

Semantic-quality champion: Run 008, `fts5-nomic-rrf60-sqlite-vec`.

Lightweight no-embedding fallback: Run 005,
`sqlite-fts5-bm25-supersession-compact-sketch`.

Hypothesis: a deterministic per-record context sketch can reduce prompt context while preserving retrieval quality and the record's semantic content terms.

Run 008 is accepted for semantic retrieval quality. Against Run 005, original
MRR@5 improved from 0.941667 to 0.975, Recall@5 from 0.95 to 0.975, and
Hit@1 from 0.90 to 0.95. On the frozen held-out set, MRR@5 and Hit@1 both
improved from 0.975/0.95 to 1.0/1.0 while Recall@5 and evidence-term coverage
remained 1.0.

Run 005 remains the operational fallback when embedding cost is not acceptable.
Its process RSS was 14.965 MiB in the Run 008 environment versus 790.684 MiB
for hybrid retrieval, and its query latency was about 0.09 ms versus 33 ms.

Benchmark generation: 1
Benchmark SHA-256: `c4d740bdf833d7c9f6294fb42718354d4c764c35d71b403b325af3434a6ebd74`
Corpus: 40 documents
Queries: 20

### Champion behavior

Run 008 uses a fixed, label-independent reciprocal-rank fusion rule: equal-weight
RRF with `k=60`, combining the top 10 FTS5 and top 10 Nomic candidates before
selecting five. It retains Run 004 supersession filtering and Run 005 compact
context assembly. Full original records remain in SQLite for provenance.

The compactor removes a conservative set of high-frequency function words. It deliberately preserves negation, conjunctions, modality, temporal words, identifiers, numbers, and content terms. On this benchmark the resulting sketches retain 100% of those content terms.

The full record remains stored. The sketch is generated at context assembly time, so persistent storage remains unchanged.

### Semantic champion metrics

- Original MRR@5: 0.975
- Original Recall@5: 0.975
- Original Hit@1: 0.95
- Held-out MRR@5, Recall@5, Hit@1, evidence: 1.0 each
- Mean original context: 148.5 approximate tokens
- Mean held-out context: 156.5 approximate tokens
- Mean end-to-end retrieval/context latency: about 33 ms
- Peak process RSS: 790.684 MiB
- Model and tokenizer: 548,021,671 bytes
- SQLite with FTS5, records, and vec0: 3,227,648 bytes

Comparable process CPU and peak RSS were not exposed by the connector execution runtime for this run. The committed Python reproduction records latency, CPU, and peak RSS when run in the Work sandbox.

## Run 006 held-out validation

Run 006 tested one hypothesis: the accepted Run 005 compact sketches preserve answer-bearing evidence on unseen queries while retaining their context reduction.

The original generation-1 corpus and query benchmark was not changed. A separate 20-query held-out evidence set was added and is now frozen with SHA-256 `80d3af75bc7fe4b3b28b8f3076dae5c47d807b6ab48379af94c6ad10b4c0c855`.

On the held-out set, full Run 004-style context and compact Run 005-style context produced identical retrieval quality: MRR@5 0.975, Recall@5 1.0, and Hit@1 0.95. Both preserved all required answer evidence at top 5. Compact context fell from 167.95 to 151.95 approximate tokens, a 9.53% reduction.

Measured in the Work sandbox, full-context evaluation used 0.003078 CPU seconds with 0.118191 ms mean retrieval/context latency; compact evaluation used 0.003675 CPU seconds with 0.153664 ms mean latency. Peak process RSS was 92.285 MB for both and the SQLite database remained 45,056 bytes. These process measurements are environment-sensitive.

Run 006 is accepted as validation infrastructure only. It did not replace the
retrieval/context algorithm at that stage; Run 008 later became the semantic
champion while Run 005 remained the lightweight fallback.

## Lessons so far

- Run 001 established SQLite FTS5 + BM25 as a strong lightweight baseline.
- Run 002's reconstructed IDF/intent reranker did not improve quality.
- Run 003's generic source-authority and recency prior did not improve quality.
- Run 004 showed that lifecycle semantics can suppress stale context without discarding history.
- Run 005 showed that context assembly can be optimized independently of storage and retrieval.
- Run 006 showed that Run 005's 9%+ context reduction generalizes to a separate held-out evidence set without losing required answer evidence.
- Run 007 showed vector-only Nomic has complementary strengths but worsens top-rank accuracy.
- Run 008 showed fixed FTS5 + Nomic RRF resolves the complementary errors and improves both frozen benchmark sets, at a substantial resource cost.

## Next hypotheses

User priority updated on 2026-10-06; these are queued experiments, not completed results.

1. Run 008 completed fixed FTS5 + Nomic RRF; do not tune fusion constants against these labels.
2. Next: test `BAAI/bge-small-en-v1.5` as a separate vector and fixed-RRF challenger, using its documented inputs and the same frozen benchmarks.
3. Then explore `intfloat/e5-small-v2` and `sentence-transformers/all-MiniLM-L6-v2` in separate bounded runs.
4. Test Nomic Matryoshka dimensions or quantized ONNX only as explicitly separate resource-optimization experiments.
5. Return to sentence/clause summaries or supersession improvements after the embedding comparisons.

Use documented model-specific prefixes, pooling and normalization, pin model revisions and dependencies, and run inference locally after downloading weights. Use SQLite rather than PostgreSQL/pgvector or a separate vector database service. Measure model load and embedding costs separately from vector search: CPU, peak RSS, query latency, throughput, model size, SQLite size, quality, evidence retention and context size. Preserve FTS5 as the baseline and promote only on measured benefit. Keep weights/caches/databases out of Git. If real model downloads or inference are blocked, record the blocker and recovery instructions rather than substituting synthetic vectors. The hourly automation has been updated with this priority; GPT-6.1 Sol remains the requested orchestration model.

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

## Run 007: real Nomic embeddings with sqlite-vec

Completed CPU-only ONNX FP32 inference for `nomic-ai/nomic-embed-text-v1.5`,
revision `e9b6763023c676ca8431644204f50c2b100d9aab`, 768 dimensions, two
intra-op threads. Used documented query/document prefixes, attention-mask mean
pooling, layer normalization and L2 normalization. Local checksum-pinned files
were used during inference. All 40 corpus self-neighbors passed vec0 validation.

Rejected as a replacement champion: original MRR@5 fell 0.941667 -> 0.916667
and Hit@1 fell 0.90 -> 0.85. Recall@5 rose 0.95 -> 0.975. Held-out MRR@5
remained 0.975, Recall@5 1.0, Hit@1 0.95 and evidence-term coverage 1.0.
Original context increased 150.5 -> 151.65 approximate tokens; held-out context
increased 151.95 -> 158.35. Nomic fixed q011 (rank 3 -> 1) but worsened
q003 (1 -> 3) and q015 (1 -> 2). These complementary errors justify testing
hybrid retrieval next, not replacing FTS5.

Full resource measurements and query-level outputs are in `runs/007.json`. The
model plus tokenizer occupies 548,021,671 bytes, and the SQLite database with
FTS5/raw records plus vec0 occupies 3,227,648 bytes versus 45,056 bytes before
vectors. Keep the embedding harness for future comparisons; the default champion
runner is unchanged. This evaluates retrieval/evidence, not generated answers.

Reproduction (Python 3.12 on Linux; preparation downloads once):

```bash
python3 -m venv .artifacts/memory-loop/venv
.artifacts/memory-loop/venv/bin/pip install -r experiments/memory/embedding_requirements.txt
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_nomic_model.py
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --threads 2
```

The last command is offline. Model revisions, checksums and sizes are in
`nomic_model.json`; dependency versions are pinned separately from the stdlib-only
lexical harness. Large model files, vector databases and preparation outputs are
kept under `.artifacts/` and excluded from Git.

## Run 008: fixed FTS5 + Nomic reciprocal-rank fusion

Run 008 tested exactly one hypothesis: equal-weight RRF with `k=60` over the top
10 FTS5 and top 10 Nomic candidates. The rule was fixed before evaluation and
was not tuned using benchmark relevance labels.

The hybrid improved the only original lexical top-rank weakness it changed:
q011 moved from rank 3 to rank 1. On held-out queries, h009 moved from rank 2
to rank 1. It introduced no measured rank regression, preserved all held-out
evidence terms, reduced original mean context from 150.5 to 148.5 approximate
tokens, and increased held-out context from 151.95 to 156.5 tokens.

The resource tradeoff is deliberate and explicit. In this sandbox, hybrid mean
end-to-end time was 33.08 ms on original queries and 32.94 ms on held-out
queries; peak process RSS was 790.684 MiB. Embedding 40 documents took 2.447 s
wall time at 16.35 documents/s. These measurements include query embedding,
retrieval, fusion, supersession filtering, and compact context assembly, but no
answer-generating model.

Reproduce Run 008 after the Run 007 setup steps:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --retrieval hybrid --threads 2
```

Full measurements and per-query rankings are in `runs/008.json`. Run 008 is the
semantic-quality champion; Run 005 stays available as the low-cost fallback.
