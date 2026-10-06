# Memory experiment state

## Current champions

Semantic-quality champion: Run 017,
`fts5-e5-small-v2-rrf60-query-aware-rank-one-protected-budget-128`.

Lightweight no-embedding fallback: Run 005,
`sqlite-fts5-bm25-supersession-compact-sketch`.

Run 017 hypothesis: protecting every selected clause in the rank-one record
before applying a fixed 128-token target can preserve evidence while reducing
Run 014 context.

Run 014 extends Run 010's accepted E5 retrieval with query-aware context
assembly. It preserves every original and held-out retrieval/evidence metric
while reducing mean assembled context on both frozen sets.

Run 015 did not replace it. The predeclared conservative cross-record
near-duplicate rule found no eligible clauses in either frozen set, so it added
comparison work without reducing context.

Run 016 did not replace Run 014. A fixed 128-token rank-aware budget reduced
context, but omitted required held-out evidence for two queries. Run 017's
rank-one protection restores that evidence and reduces context on both sets.

Run 018 did not replace Run 017. Native sqlite-vec unit-range int8 vectors cut
SQLite storage by 71.29% and preserved scored quality, but changed enough top-five
membership to increase mean assembled context on both frozen sets.

Run 005 remains the operational fallback when embedding cost is not acceptable.
Its process RSS was about 15 MiB in the Run 010 environment versus 228.238 MiB
for the latest full hybrid run. Embeddings remain the dominant cost.

Benchmark generation: 1
Benchmark SHA-256: `c4d740bdf833d7c9f6294fb42718354d4c764c35d71b403b325af3434a6ebd74`
Corpus: 40 documents
Queries: 20

### Champion behavior

Run 017 uses Run 010's fixed, label-independent reciprocal-rank fusion rule: equal-weight
RRF with `k=60`, combining the top 10 FTS5 and top 10 E5 candidates before
selecting five. It retains Run 004 supersession filtering and Run 005 compact
context assembly. E5 uses its documented `query: ` and `passage: ` prefixes,
attention-mask mean pooling, L2 normalization, 384 dimensions and cosine
distance. Full original records remain in SQLite for provenance.

At context assembly, Run 017 first applies Run 014's selector. It protects every
selected clause from the rank-one record and the first selected clause from each
lower-rank record. It then admits remaining lower-rank clauses in retrieval
order up to a 128 approximate-token target. Protected clauses may exceed the
target and clauses are never truncated. The rule was fixed before evaluation.

The compactor removes a conservative set of high-frequency function words. It deliberately preserves negation, conjunctions, modality, temporal words, identifiers, numbers, and content terms. On this benchmark the resulting sketches retain 100% of those content terms.

The full record remains stored. The sketch is generated at context assembly time, so persistent storage remains unchanged.

### Semantic champion metrics

- Original MRR@5: 0.975
- Original Recall@5: 0.975
- Original Hit@1: 0.95
- Held-out MRR@5, Recall@5, Hit@1, evidence: 1.0 each
- Mean original context: 128.80 approximate tokens
- Mean held-out context: 131.40 approximate tokens
- Mean end-to-end retrieval/context latency: 7.90 ms original, 7.04 ms held-out
- Mean context assembly latency: 0.081 ms original, 0.090 ms held-out
- Peak process RSS: 228.238 MiB
- Model and tokenizer: 133,804,864 bytes
- SQLite with FTS5, records, and vec0: 1,654,784 bytes

Measured process CPU across 20 hybrid queries was 0.314 seconds on the original
set and 0.282 seconds on held-out. Resource measurements are environment-sensitive;
the committed reproduction records load, embedding, query, search, end-to-end,
CPU and process-lifetime peak RSS separately.

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
- Run 009 showed BGE-small v1.5 substantially reduces model size, RSS and
  latency, but the unchanged fixed-RRF hybrid regressed held-out MRR@5/Hit@1
  from 1.0/1.0 to 0.975/0.95. Resource savings alone do not justify replacing
  the semantic-quality champion.
- Run 010 showed E5-small-v2 preserves all Run 008 hybrid retrieval/evidence
  metrics while substantially reducing model, memory, CPU, latency, vector
  storage and assembled context costs. Its vector-only original top-rank score
  was weaker, confirming that the fixed lexical fusion remains important.
- Run 011 showed all-MiniLM-L6-v2 is smaller and faster than E5, but its hybrid
  regressed held-out MRR@5/Hit@1 from 1.0/1.0 to 0.975/0.95 on h014. Recall@5
  and evidence retention stayed 1.0. Resource savings do not outweigh the
  frozen quality regression, so Run 010 remains champion.
- Run 012 showed portable ONNX Runtime dynamic INT8 E5 is substantially smaller,
  faster and lower-memory than FP32 E5, but q011 moved from rank 1 to rank 2.
  Original hybrid MRR@5/Hit@1 regressed from 0.975/0.95 to 0.95/0.90, so Run
  010 remains champion despite perfect held-out quality and evidence retention.
- Run 013 showed that truncating Nomic from 768 to 256 dimensions reduced the
  SQLite database by 64.97%, but q003 moved from rank 1 to rank 2. Original
  hybrid MRR@5/Hit@1 regressed from 0.975/0.95 to 0.95/0.90; the large Nomic
  model and inference costs also remained. Run 010 therefore remained champion
  at that stage.
- Run 014 showed that conservative query-aware clause selection can reduce
  original context by 5.52% and held-out context by 5.64% without changing any
  retrieval metric or losing held-out evidence. Its isolated assembly overhead
  was about 0.04 ms per query, so it extends Run 010 as the current champion.
- Run 015 showed that conservative cross-record clause deduplication has no
  opportunity after Run 014 on this benchmark: it removed zero clauses, left
  context unchanged, and increased isolated assembly from about 0.05 ms to
  0.27-0.30 ms per query. Do not loosen similarity thresholds against the
  frozen labels; that would weaken semantic guards and invite overfitting.
- Run 016 showed that a uniform 128-token context budget is too rigid. It cut
  original context by 7.70% and held-out context by 11.95%, but held-out
  evidence retention fell from 1.0 to 0.9 because second clauses from the
  rank-one records were omitted for h013 and h017. Nine held-out queries already
  exceeded the budget using only one mandatory clause per retrieved record.
- Run 017 showed that rank-one protection is the missing safety rule. It restored
  held-out evidence from 0.9 to 1.0 while still reducing Run 014 context by
  7.70% original and 9.78% held-out. All retrieval metrics and provenance
  invariants remained unchanged, so Run 017 replaces Run 014 as champion.
- Run 018 showed that sqlite-vec's unit-range int8 quantizer is exceptionally
  storage-efficient but not rank-equivalent to FP32 E5. SQLite fell from
  1,654,784 to 475,136 bytes and all scored retrieval/evidence metrics matched,
  but original context rose 1.98% and held-out context rose 0.95%. The fixed
  no-context-increase gate rejected it; Run 017 remains champion.

## Next hypotheses

User priority updated on 2026-10-06; these are queued experiments, not completed results.

1. Run 008 completed fixed FTS5 + Nomic RRF; do not tune fusion constants against these labels.
2. Run 009 completed `BAAI/bge-small-en-v1.5`; do not tune RRF constants to
   repair its single held-out top-rank miss.
3. Run 010 completed `intfloat/e5-small-v2`; it is the new semantic-quality
   champion. Do not tune the fixed fusion constants against benchmark labels.
4. Run 011 completed `sentence-transformers/all-MiniLM-L6-v2`; do not tune the
   fixed fusion rule to repair its held-out top-rank miss.
5. Run 012 completed a portable dynamic INT8 E5 experiment; do not tune the
   fixed fusion rule against q011 to conceal its original-benchmark regression.
6. Run 013 completed Nomic Matryoshka at 256 dimensions; do not tune fusion
   against q003 or try another dimension without a specific follow-up reason.
7. Run 014 completed fixed query-aware clause selection and is the current
   context-assembly extension to Run 010.
8. Run 015 completed fixed cross-record near-duplicate clause removal; the
   candidate was rejected because it found no safe duplicates.
9. Run 016 completed a fixed 128-token rank-aware budget; it was rejected after
   losing held-out evidence on h013 and h017.
10. Run 017 completed rank-one protection with the same 128-token target and is
    the current context-assembly champion.
11. Run 018 completed sqlite-vec unit-range int8 storage; it was rejected because
    quantized ranking changes increased context despite matching scored quality.
12. Next: test one specific int8 follow-up using deterministic per-vector
    symmetric max-absolute scaling before rounding and clipping to [-127, 127].
    Cosine is scale-invariant, so this should use the int8 range more effectively
    while retaining Run 018's storage benefit. Keep all other Run 017 rules fixed.

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

## Run 009: BGE-small v1.5 model substitution

Run 009 tested one hypothesis: replace Nomic with the smaller CPU-only
`BAAI/bge-small-en-v1.5` model while leaving SQLite vec0, supersession handling,
compact context assembly, and Run 008's equal-weight RRF (`k=60`, top-10 pools)
unchanged. The experiment used the model's documented 512-token tokenizer,
query instruction, no passage instruction, first-token CLS pooling, L2
normalization, 384 dimensions and cosine distance.

BGE's hybrid matched Run 008 on the original benchmark: MRR@5 0.975,
Recall@5 0.975 and Hit@1 0.95. It did not match the frozen held-out benchmark:
MRR@5/Hit@1 fell from 1.0/1.0 to 0.975/0.95, although Recall@5 and evidence
retention remained 1.0. The held-out miss was h016 at rank 2. The candidate is
therefore rejected and Run 008 remains the semantic-quality champion.

The rejected challenger was materially cheaper: model plus tokenizer was
133,804,886 bytes instead of 548,021,671; peak RSS was 234.074 MiB instead of
790.684 MiB; original hybrid end-to-end latency was 8.16 ms instead of 33.08
ms; and the SQLite database was 1,654,784 bytes instead of 3,227,648. Embedding
40 documents took 0.600 s at 66.64 documents/s. These are sandbox-specific
process measurements. Full vector-only diagnostics and per-query rankings are
in `runs/009.json`; no answer generator was evaluated.

Reproduce after installing `embedding_requirements.txt`:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model bge
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model bge --threads 2
```

The second command is offline. Revision, checksums, sizes and model conventions
are pinned in `bge_model.json`; large weights and generated databases stay under
`.artifacts/` and out of Git.

## Run 010: E5-small-v2 model substitution

Run 010 tested one hypothesis: replace Nomic with CPU-only
`intfloat/e5-small-v2` while leaving SQLite vec0, supersession handling,
compact context assembly, and Run 008's equal-weight RRF (`k=60`, top-10 pools)
unchanged. It used the documented `query: ` and `passage: ` prefixes,
512-token limit, attention-mask mean pooling, L2 normalization, 384 dimensions
and cosine distance.

The hybrid exactly matched Run 008 quality. Original MRR@5/Recall@5/Hit@1 were
0.975/0.975/0.95. Held-out MRR@5, Recall@5, Hit@1 and evidence retention were
all 1.0. Mean context also fell from 148.5 to 147.7 approximate tokens on the
original set and from 156.5 to 154.35 on held-out. Vector-only diagnostics were
mixed: original MRR@5/Hit@1 were 0.9125/0.85 despite Recall@5 of 1.0, while all
held-out quality/evidence metrics were 1.0. This supports keeping lexical fusion.

The accepted challenger reduced model plus tokenizer size from 548,021,671 to
133,804,864 bytes, peak RSS from 790.684 to 236.047 MiB, SQLite size from
3,227,648 to 1,654,784 bytes, original end-to-end latency from 33.08 to 8.09 ms,
and held-out latency from 32.94 to 8.66 ms. Embedding 40 documents took 0.567 s
at 70.60 documents/s. Full CPU, query embedding, vec0 search and per-query
results are in `runs/010.json`. This is retrieval/context evaluation, not
generated-answer evaluation.

Reproduce after installing `embedding_requirements.txt`:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model e5
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --threads 2
```

The second command is offline. Revision, checksums, sizes and conventions are
pinned in `e5_model.json`; large weights and generated databases stay under
`.artifacts/` and out of Git.

## Run 011: all-MiniLM-L6-v2 model substitution

Run 011 tested one hypothesis: replace E5 with CPU-only
`sentence-transformers/all-MiniLM-L6-v2` while preserving SQLite vec0,
supersession handling, compact context assembly, and the fixed equal-weight RRF
rule (`k=60`, top-10 pools). It used the model's documented symmetric inputs
without prefixes, 256-word-piece limit, attention-mask mean pooling, L2
normalization, 384 dimensions and cosine distance.

MiniLM's hybrid matched Run 010 on the original benchmark at MRR@5 0.975,
Recall@5 0.975 and Hit@1 0.95. It did not match the held-out benchmark:
MRR@5/Hit@1 fell from 1.0/1.0 to 0.975/0.95 because h014 ranked its relevant
record second. Held-out Recall@5 and evidence retention remained 1.0. The
candidate is rejected and Run 010 remains the semantic-quality champion.

The rejected challenger was cheaper: model plus tokenizer was 90,871,461 bytes
instead of 133,804,864; peak RSS was 193.996 MiB instead of 236.047 MiB;
original hybrid latency was 4.75 ms instead of 8.09 ms; and held-out latency
was 4.19 ms instead of 8.66 ms. SQLite size was unchanged at 1,654,784 bytes.
Original context increased from 147.7 to 149.35 approximate tokens, while
held-out context fell from 154.35 to 150.05. Embedding 40 documents took 0.312
seconds at 128.41 documents/s. Full CPU, vector-only diagnostics and per-query
results are in `runs/011.json`; no answer generator was evaluated.

Reproduce after installing `embedding_requirements.txt`:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model minilm
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model minilm --threads 2
```

The second command is offline. Revision, checksums, sizes and conventions are
pinned in `minilm_model.json`; large weights and generated databases stay under
`.artifacts/` and out of Git.

## Run 012: portable dynamic INT8 E5 precision experiment

Run 012 tested one hypothesis: dynamically quantizing the pinned E5-small-v2
ONNX weights from FP32 to signed INT8 can preserve Run 010 quality while
reducing local resource costs. The tokenizer, 384-dimensional output, E5
`query: ` and `passage: ` prefixes, attention-mask mean pooling, L2
normalization, SQLite vec0 storage, supersession behavior, compact context and
fixed equal-weight RRF rule were unchanged.

The quantized hybrid retained perfect held-out MRR@5, Recall@5, Hit@1 and
evidence retention. It did not preserve the original benchmark: q011 moved
from rank 1 to rank 2, lowering MRR@5 from 0.975 to 0.95 and Hit@1 from 0.95
to 0.90; Recall@5 remained 0.975. The candidate is rejected and Run 010 remains
the semantic-quality champion.

The resource result was nevertheless strong. Model plus tokenizer size fell
from 133,804,864 to 34,518,725 bytes; measured peak RSS fell from 236.047 to
121.637 MiB; original hybrid latency fell from 8.09 to 6.10 ms; and held-out
latency fell from 8.66 to 4.70 ms. Embedding 40 documents took 0.368 seconds at
108.72 documents/s. SQLite remained 1,654,784 bytes because stored vectors are
still normalized FP32. Measurements came from an AVX-512 VNNI-capable x86-64
host, but the generated ONNX graph is not ISA-specific; target-machine
performance still requires validation. No answer generator was evaluated.

Reproduce with the pinned quantization toolchain:

```bash
python3 -m venv .artifacts/memory-loop/venv
.artifacts/memory-loop/venv/bin/pip install -r experiments/memory/quantization_requirements.txt
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_e5_int8_model.py
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5_int8 --threads 2
```

Preparation downloads the pinned FP32 model once and deterministically creates
the local dynamic-QInt8 model. The final benchmark command is offline. Source
and generated checksums, sizes, quantization parameters, model conventions and
dependency versions are pinned in `e5_int8_model.json` and
`quantization_requirements.txt`; large artifacts remain under `.artifacts/`.

## Run 013: Nomic 256-dimensional Matryoshka projection

Run 013 tested one hypothesis: Nomic's documented 256-dimensional Matryoshka
projection can preserve the fixed Run 008 RRF retrieval quality while reducing
SQLite vec0 storage. It used the same pinned Nomic FP32 ONNX model and tokenizer,
`search_document: ` and `search_query: ` prefixes, attention-mask mean pooling,
full 768-dimensional layer normalization, truncation to the first 256 values,
then L2 normalization. Supersession handling, compact context, cosine distance
and equal-weight RRF (`k=60`, top-10 pools) were unchanged.

The projection did not preserve quality. On the original benchmark q003 moved
from rank 1 to rank 2, reducing hybrid MRR@5/Hit@1 from 0.975/0.95 at 768
dimensions to 0.95/0.90 at 256 dimensions; Recall@5 remained 0.975. Held-out
MRR@5, Recall@5, Hit@1 and evidence retention remained 1.0. Run 010 E5 remains
the semantic-quality champion.

SQLite with FTS5, original records and vec0 fell from 3,227,648 to 1,130,496
bytes, a 64.97% reduction. Model plus tokenizer remained 548,021,671 bytes and
measured peak RSS was 889.184 MiB. Embedding 40 records took 2.968 seconds at
13.48 documents/s; original and held-out hybrid latency averaged 51.68 and
50.64 ms. These process measurements are environment-sensitive and should not
be treated as controlled speed comparisons with earlier hosts. No answer
generator was evaluated.

Reproduce after installing `embedding_requirements.txt`:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_nomic_model.py
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model nomic_256 --threads 2
```

The final command is offline. Revision, checksums, dimensions, operation order
and model conventions are pinned in `nomic_256_model.json`; full measurements
and query rankings are in `runs/013.json`. Large model and database artifacts
remain under `.artifacts/` and out of Git.

## Run 014: query-aware clause context selection

Run 014 tested one fixed context hypothesis on top of Run 010 retrieval. For
each retrieved record, it always keeps the first clause, keeps later clauses
with query-term overlap or fixed semantic operators (`after`, `before`,
`except`, `instead`, `must`, `never`, `not`, `only`, `required`, `should`,
`unless`, `until`, `without`), and then applies Run 005 compaction. The rule
was selected before evaluation and does not use relevance or evidence labels.

The candidate preserved original MRR@5/Recall@5/Hit@1 at 0.975/0.975/0.95 and
all held-out retrieval and evidence metrics at 1.0. Original mean context fell
from 147.7 to 139.55 approximate tokens (5.52%); held-out context fell from
154.35 to 145.65 (5.64%). Persistent storage was unchanged at 1,654,784 bytes.

A 1,000-iteration replay of Run 010 rankings measured 0.067 ms original and
0.069 ms held-out assembly per query, about 0.04 ms slower than the old compact
sketch but below the fixed 1 ms limit. A full local E5 run separately measured
query embedding, vec0 search, context assembly and end-to-end latency; original
end-to-end latency was 4.67 ms and held-out was 4.95 ms, with 236.312 MiB peak
RSS. These process timings are environment-sensitive. Retrieval/evidence was
evaluated, not generated-answer quality.

Reproduce the isolated comparison without model weights:

```bash
python3 experiments/memory/run_context_selection_experiment.py --iterations 1000
```

Reproduce the complete pipeline after the Run 010 model preparation:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/prepare_embedding_model.py --model e5
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context query_aware --threads 2
```

The final command is offline. Full pipeline measurements, isolated assembly
timings, selection rules and per-query contexts are in `runs/014.json`.

## Run 015: cross-record near-duplicate clause removal

Run 015 replayed Run 014's fixed rankings and query-aware selected clauses, then
tested one predeclared cross-record filter. It retained the first occurrence in
retrieval order only when token-set Jaccard was at least 0.80 or smaller-set
containment was at least 0.90. Semantic operators, numbers, underscore tokens,
and uppercase identifiers had to match exactly. Any removed clause would have
merged its document and clause provenance into the retained entry.

No selected clauses met that conservative rule on either frozen set. Original
context therefore stayed at 139.55 approximate tokens and held-out context at
145.65, with all retrieval/evidence metrics unchanged and provenance invariants
passing. Mean isolated assembly rose from 0.047 to 0.274 ms on original queries
and from 0.052 to 0.302 ms on held-out queries. The candidate is rejected and
Run 014 remains champion. Loosening the thresholds after observing this result
would be label-driven and risks merging clauses with different meaning.

Reproduce without model weights or external APIs:

```bash
python3 experiments/memory/run_deduplication_experiment.py --iterations 1000
```

Full metrics and per-query duplicate/provenance results are in `runs/015.json`.
This is retrieval/evidence evaluation only; no answer generator was evaluated.

## Run 016: fixed rank-aware context budget

Run 016 replayed Run 014 and tested one predeclared 128 approximate-token
(512-character) context budget. It first retained one selected clause from every
retrieved record, then admitted later selected clauses in retrieval-rank order
until the next clause would exceed the budget. Clauses were never truncated;
mandatory clauses could exceed the target, and every retained or omitted clause
kept record, rank, and selected-clause provenance.

Original mean context fell from 139.55 to 128.80 approximate tokens (7.70%),
and held-out context fell from 145.65 to 128.25 (11.95%). Retrieval metrics were
unchanged by replay, provenance checks passed, and mean candidate assembly was
0.101 ms original and 0.092 ms held-out. Persistent storage remained 1,654,784
bytes.

The candidate is rejected because held-out evidence retention fell from 1.0 to
0.9. The budget omitted required second clauses from the rank-one records for
h013 and h017. Nine held-out queries exceeded the target using mandatory clauses
alone, demonstrating that a uniform hard target is not compatible with this
five-record context policy. Run 014 remains champion.

Reproduce without model weights or external APIs:

```bash
python3 experiments/memory/run_context_budget_experiment.py --iterations 1000
```

Full metrics and per-query retained/omitted provenance are in `runs/016.json`.
This is retrieval/evidence evaluation only; no answer generator was evaluated.

## Run 017: rank-one protected context budget

Run 017 tested the specific safety follow-up justified by Run 016. It keeps the
same 128 approximate-token target, protects every Run 014-selected clause from
the rank-one record, protects the first selected clause from every lower-rank
record, then admits lower-rank optional clauses in retrieval order. Protected
overflow is allowed, clauses are never truncated, and retained plus omitted
provenance accounts for every selected clause.

Original context fell from 139.55 to 128.80 approximate tokens (7.70%) and
held-out context fell from 145.65 to 131.40 (9.78%). Original
MRR@5/Recall@5/Hit@1 remained 0.975/0.975/0.95; all held-out retrieval and
evidence metrics remained 1.0. A 1,000-iteration isolated replay measured 0.055
ms candidate assembly on both sets and verified every provenance invariant.

The complete offline E5/SQLite pipeline used FP32, 384 dimensions and two
threads. Model and tokenizer files occupied 133,804,864 bytes; SQLite with FTS5,
records and vec0 occupied 1,654,784 bytes; peak process RSS was 228.238 MiB.
Embedding 40 records took 0.590 seconds at 67.75 records/s. Mean full-pipeline
latency was 7.90 ms original and 7.04 ms held-out, including query embedding,
hybrid retrieval, context assembly and no answer generation. Environment-sensitive
timings are not directly comparable across hosts.

Reproduce the isolated comparison without model weights:

```bash
python3 experiments/memory/run_protected_context_budget_experiment.py --iterations 1000
```

Reproduce the complete pipeline after the Run 010 model preparation:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --threads 2
```

Full pipeline measurements and per-query retained/omitted provenance are in
`runs/017.json`. No external inference API or answer generator was used.

## Run 018: sqlite-vec unit-range int8 vector storage

Run 018 kept Run 017's FP32 E5 inference, fixed FTS5/E5 RRF, supersession rules,
and protected context assembly. It changed only the vec0 column to `int8[384]`
and used sqlite-vec v0.1.9 `vec_quantize_int8(vector, 'unit')` for both corpus
and query vectors. The rule was fixed before evaluation; every corpus vector
still returned itself as its nearest neighbor.

SQLite with FTS5, records, and vec0 fell from 1,654,784 to 475,136 bytes, a
71.29% reduction. Mean vector search remained below the fixed 2 ms limit at
0.505 ms original and 0.578 ms held-out. All original and held-out scored
retrieval metrics matched Run 017, and held-out evidence remained 1.0.

The candidate is rejected because int8 ranking changes increased original mean
context from 128.80 to 131.35 tokens (1.98%) and held-out context from 131.40 to
132.65 (0.95%). This failed the predeclared no-context-increase gate even though
MRR, Recall, Hit@1, and evidence scores were unchanged. FP32 Run 017 remains the
champion. These measurements evaluate retrieval and evidence, not generated
answers.

Reproduce after the Run 010 E5 preparation:

```bash
.artifacts/memory-loop/venv/bin/python experiments/memory/run_embedding_experiment.py --model e5 --context rank_one_protected_budget --vector-storage int8 --threads 2
```

Full model, quantizer, CPU, memory, SQLite, latency, quality, context, and
per-query ranking measurements are in `runs/018.json`. No external inference
API or answer generator was used.
