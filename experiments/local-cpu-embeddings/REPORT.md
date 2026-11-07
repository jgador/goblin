# Goblin local CPU embedding experiment

Measured on 2026-10-01 UTC (2026-10-02 in Manila), from Goblin master
`7da7f37049a191586b4d9584065c9cbe5777b467`, on branch
`experiment/local-cpu-embeddings`. Before publishing, the isolated experiment was
rebased onto current master `da2dc6d5d4941394e106bba6a368ec6dd72d0328`;
the measured experiment code and data were retained.

CPU-only local embeddings are practical for this workload. Start with SQLite
FTS5 and BGE-small INT8, one inference thread and batch 1 for predictable resource
use. Optional length grouping plus batch 8 raises throughput when a second core
and more memory are available. Keep lexical retrieval as a first-class path:
vector retrieval missed `PR #381`, and plain RRF was helpful for some paraphrases
but was not consistently better than lexical retrieval.

## Measured environment

| Item | Actual sandbox measurement |
|---|---|
| CPU | AMD EPYC 9V74; affinity exposes 9 logical CPUs (`0-8`); quota is **8 CPU cores**; .NET reports 8 processors |
| OS | Ubuntu 24.04.3 LTS; Linux 6.18.44 x86_64 |
| Host total RAM | 9.73 GiB |
| Host available RAM at benchmark setup | 9.00 GiB |
| Sandbox memory limit | 8.00 GiB |
| Sandbox working-set headroom at setup | 6.69 GiB |
| Available disk at setup | 24.14 GiB of 31.45 GiB |
| SDK / runtime | .NET SDK 10.0.401 / .NET 10.0.12 |
| SQLite | 3.53.3, bundled by Microsoft.Data.Sqlite |
| Linux PSI | `/proc/pressure/cpu`, `/proc/pressure/memory`, and cgroup pressure files unavailable |

Before installation, no .NET SDK was present; the initial disk reading was
26.07 GiB free, and available host RAM was 9.04 GiB. The SDK and all weights were
downloaded into the sandbox. The SDK installer extracted usable files but returned
ownership errors in this user namespace; the installed SDK was verified and built
the project successfully. Project-aware `dotnet format` encountered blocked Unix
named pipes; folder-based formatting was used instead. Neither limit prevented
local inference.

Headroom uses actual Linux readings:
`min(MemAvailable, memory.max - memory.current + inactive_file)`, clamped to zero.
This avoids treating reclaimable inactive file cache as permanently occupied RAM.
It is a working-set approximation, not an allocation guarantee. CPU capacity is
read from `cpu.max`, rather than assuming every visible logical CPU is available.

## Method and correctness

The application has exactly two direct packages: `Microsoft.Data.Sqlite 10.0.12`
and `Microsoft.ML.OnnxRuntime 1.22.1`. There is no Semantic Kernel, hosted inference,
container runtime, Kubernetes, PostgreSQL instance, vector server, or LLM reranker.
The corpus contains references to those technologies only as synthetic text.

- Each main format/batch test ran **512 records** in a fresh C# process. Batch sizes
  1, 8, 16, 32, and 64 were tested for FP32 and INT8, with two ORT intra-op threads.
  Inter-op threads were one; ORT idle spinning was disabled.
- All benchmarks used the same fixed-seed subset of the 2,000-record corpus. The
  subset is randomly selected, then processed by ID. Length-order follow-ups
  change only the processing order using `length(content)`; no input is removed.
- Load time includes the tokenizer and ONNX session. Loaded RSS is recorded before
  inference. One batch warms the session; steady measurements exclude load and
  warmup. Peak RSS includes retained native arenas, which matters for a long-lived
  process. Model downloads, restore, and compilation are outside timed regions.
- A sampler reads process RSS, CPU time, GC memory, thread count, cgroup CPU, and
  available RAM every 50 ms. CPU **100% means one fully used core**; 800% would
  exhaust the eight-core quota. Capacity CPU divides aggregate cgroup usage by
  the available core count. Peak readings are individual samples, not sustained
  requirements. Sampling, SQLite work, and .NET housekeeping are included.
- Token IDs matched the independent Hugging Face tokenizer on **2,007
  cases**, including all 2,000 records and Unicode/truncation/special-token cases.
  Python was used only as an independent validation tool, never in the application
  pipeline or timed embedding benchmarks.
- Independent Python CPU ONNX inference matched the C# pooled vectors: maximum
  absolute error below **8e-8** for BGE FP32/INT8 (64 texts each) and Nomic (8 texts).
  BGE uses CLS pooling and normalization. Nomic uses masked mean pooling, full
  768-dimensional layer normalization and normalization, plus document/query
  prefixes. All tests cap inputs at 512 tokens.
- The INT8 graph contains 48 dynamic quantization and 72 integer matrix multiply
  operators. Its vectors had mean cosine **0.99551**, minimum **0.99370**, against
  FP32 across 64 texts. Dynamic quantization is batch-dependent: singleton versus
  batch cosine was 0.99536 for the checked INT8 text. Retrieval should be revalidated
  when changing quantization or batch policy; do not assume bitwise equivalence.
- Database integrity passed; all 2,000 final rows have real 384-dimensional vectors,
  non-null embedding timestamps, and `ready` status. FTS update/delete triggers and
  vector invalidation on content edits were also checked in a temporary copy.

Weights are pinned by revision, URL, byte size, and SHA-256 in
[models/manifest.json](models/manifest.json). BGE FP32 is the BAAI export; the INT8
file is Xenova's quantized export of the same BGE model. Nomic FP32 is its official
ONNX export. Large weights are omitted from Git and reproduced with
[download-models.sh](download-models.sh).

## Model and batch measurements

All rows below use two inference threads and 512 records. `Embeddings/sec` is
real tokenization + inference + pooling throughput. It excludes database reads
and writes; complete pipeline records/sec, batch latency, median RSS, and sampled
peak CPU are in [benchmark-summary.csv](results/benchmark-summary.csv).

| Model | Format | Model size | Dimensions | Load time | RSS loaded | Peak RSS | Batch | Embeddings/sec | CPU average |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| BGE-small | FP32 | 126.93 MiB | 384 | 0.228 s | 277.2 MiB | 359.5 MiB | 1 | 43.94 | 188.6% |
| BGE-small | FP32 | 126.93 MiB | 384 | 0.265 s | 280.1 MiB | 618.3 MiB | 8 | 17.79 | 198.2% |
| BGE-small | FP32 | 126.93 MiB | 384 | 0.233 s | 279.9 MiB | 988.1 MiB | 16 | 12.38 | 198.5% |
| BGE-small | FP32 | 126.93 MiB | 384 | 0.239 s | 280.1 MiB | 1907.1 MiB | 32 | 7.68 | 198.6% |
| BGE-small | FP32 | 126.93 MiB | 384 | 0.231 s | 277.1 MiB | 3586.6 MiB | 64 | 6.06 | 199.5% |
| BGE-small | INT8 | 32.44 MiB | 384 | 0.136 s | 120.2 MiB | 197.1 MiB | 1 | 89.95 | 181.0% |
| BGE-small | INT8 | 32.44 MiB | 384 | 0.158 s | 120.8 MiB | 590.3 MiB | 8 | 34.62 | 200.6% |
| BGE-small | INT8 | 32.44 MiB | 384 | 0.137 s | 120.7 MiB | 1075.3 MiB | 16 | 23.35 | 198.5% |
| BGE-small | INT8 | 32.44 MiB | 384 | 0.143 s | 120.8 MiB | 2024.0 MiB | 32 | 15.08 | 198.6% |
| BGE-small | INT8 | 32.44 MiB | 384 | 0.153 s | 120.6 MiB | 4064.8 MiB | 64 | 11.98 | 199.1% |
| Nomic v1.5 | FP32 | 521.96 MiB | 768 | 0.773 s | 1056.7 MiB | 1117.2 MiB | 1 | 9.62 | 190.6% |

### Throughput/resource tradeoffs

These follow-ups also process 512 identical records. CPU is process utilization;
`affinity2` restricts this process to CPUs 0 and 1 without changing the sandbox's
memory or quota configuration.

| Run | Batch | Threads | Order | Records/sec | Mean batch | P95 batch | Peak RSS | CPU average |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| 1-t1 | 1 | 1 | id | 68.28 | 14.60 ms | 27.42 ms | 196.9 MiB | 112.1% |
| 1-t2 | 1 | 2 | id | 88.00 | 11.32 ms | 21.84 ms | 197.1 MiB | 181.0% |
| 1-t4 | 1 | 4 | id | 85.30 | 11.68 ms | 20.48 ms | 197.7 MiB | 239.2% |
| 8-length | 8 | 2 | length | 107.46 | 74.36 ms | 133.10 ms | 367.6 MiB | 202.5% |
| 16-length | 16 | 2 | length | 90.97 | 175.77 ms | 311.08 ms | 1075.0 MiB | 197.0% |
| 32-length | 32 | 2 | length | 79.13 | 404.19 ms | 3024.45 ms | 2015.2 MiB | 197.8% |
| 64-length | 64 | 2 | length | 50.69 | 1262.28 ms | 6829.78 ms | 3879.6 MiB | 198.4% |
| 1-t1-affinity2 | 1 | 1 | id | 70.87 | 14.07 ms | 23.69 ms | 196.1 MiB | 113.8% |

Mixed-length large batches pad every item to the longest text. The full corpus
has a median of 51 tokens, P95 114,
maximum 512, and 2 truncated records. Padding
made unordered batches slower and increased retained native memory substantially.
Grouping similar lengths raised batch-8 throughput from 34.53 to 107.46 records/sec,
with peak RSS dropping from 590.3 to 367.6 MiB. That is a meaningful improvement,
but larger grouped batches still consumed too much memory for their throughput.
Character length is only a rough proxy for token length; production grouping
should also prevent long records from starving.

**Recommended default:** BGE INT8, batch 1, one inference thread. It produced
68.28 records/sec in the fixed-subset benchmark and **72.84
records/sec** across all 2,000 memories. Two threads raised singleton throughput to
88.00 records/sec but consumed roughly 1.81 cores rather than 1.12. Four threads
consumed more CPU without improving throughput over two. Use **batch 8 with length
grouping and two threads** only when throughput justifies its larger budget.
Batch 32 is not a suitable default for these varied notes.

The complete 2,000-record embedding pass took **27.46 seconds**,
with **191.6 MiB median RSS**, **215.4
MiB peak RSS**, **104.5% average process CPU**
(about one core), and **19.0 MiB peak managed heap**.
There were at most 11 process threads. Most RSS is
native/runtime memory rather than the GC heap. Initialization was
0.152 seconds and 120.9 MiB loaded RSS;
these are fresh-process, warm-file-cache measurements, not cold-disk boot timings.

Nomic worked without another framework. Its 512-record singleton test was
9.59 records/sec with about 1.09 GiB peak RSS, substantially more expensive than
BGE-small for this resource goal. No Nomic retrieval-quality comparison or full
batch sweep was run, so this does not establish that BGE is more accurate.
Nomic's long-context capability was not measured.

## SQLite and synchronous ingestion

All 2,000 records were immediately FTS-searchable while every row was still
`pending` and no vector existed.

| Measurement | Actual result |
|---|---:|
| Ingestion, including WAL checkpoint | 79.45 ms |
| Ingestion throughput | 25,174 records/sec |
| CPU time | 85.66 ms |
| CPU average during ingestion | 107.8% |
| Process RSS after ingestion | 78.0 MiB |
| Managed heap peak in ingestion sampling | 16.6 MiB |
| FTS warmed mean / P95, 200 queries | 0.098 / 0.170 ms |
| Database before vectors | 1,318,912 bytes (1.258 MiB) |
| Database after all vectors | 4,874,240 bytes (4.648 MiB) |

Ingestion is one prepared-statement transaction, with `journal_mode=WAL` and
`synchronous=NORMAL`. This measures a bulk synchronous path, not 2,000 individually
fsynced requests or production power-loss durability. The ingestion timer is
shorter than two typical sample intervals, so its CPU time and final RSS are more
useful than interpreting its four resource samples as a sustained load profile.

## Storage

Sizes are the main SQLite file after a truncate checkpoint; WAL is excluded only
once its contents have been checkpointed. No VACUUM or vector compression is used.

| Embedded records | SQLite bytes | SQLite MiB | Raw vector bytes |
|---|---:|---:|---:|
| 0 | 1,318,912 | 1.258 | 0 |
| 500 | 2,203,648 | 2.102 | 768,000 |
| 1,000 | 3,100,672 | 2.957 | 1,536,000 |
| 2,000 | 4,874,240 | 4.648 | 3,072,000 |

Each BGE vector is **384 × 4 = 1,536 bytes** of normalized float32 data. INT8 model
weights do not make stored vectors INT8. Measured database growth was approximately
**1777.7 bytes per embedded record**, including **241.7 bytes** beyond the
raw vector for model/timestamp/status metadata, page layout, and index changes.
The complete database averages **2437.1 bytes per memory**, including original
content and FTS. These marginal numbers are observed averages, not fixed SQLite
per-row overhead rules.

| Memories | Raw vectors | Additional measured SQLite/metadata overhead | Total vector-related growth | Whole DB at this corpus mix |
|---|---:|---:|---:|---:|
| 10,000 | 14.65 MiB | 2.30 MiB | 16.95 MiB | 23.24 MiB |
| 100,000 | 146.48 MiB | 23.05 MiB | 169.53 MiB | 232.42 MiB |
| 1,000,000 | 1464.84 MiB | 230.47 MiB | 1695.31 MiB | 2324.22 MiB |

The final three columns are **linear extrapolations from 2,000 measured records**,
not measured larger databases. Original text length, SQLite page allocation, FTS
vocabulary, indexes, deletions, and WAL traffic can change them. At one million
rows a brute-force scan would read about 1.43 GiB of vector data per query; an index
or candidate restriction would need separate testing. The 2,000-row result does
not validate brute force at that scale.

## Retrieval

[RETRIEVAL.md](RETRIEVAL.md) shows the top five results and scores for **every query**
from lexical, vector, and hybrid retrieval. Full text and timings are in
[retrieval.json](results/retrieval.json), and all source text is in
[corpus.json](results/corpus.json). FTS may return fewer than five matches;
empty results remain empty.

Lexical search uses quoted OR terms after removing generic English function/question
words, with FTS5 BM25. Cosine scans every ready BLOB in C#, loading rows from SQLite
for each query. Hybrid is deterministic RRF with `k=60` over each method's top 50
candidates, with ID ties. No model reranks results. The earlier unfiltered OR
baseline is preserved in [retrieval-naive-or.json](results/retrieval-naive-or.json).

For the 17 queries, mean lexical retrieval was
1.43 ms, query embedding
5.92 ms, the complete
SQLite/vector scan and sorting 8.13 ms,
and RRF 0.10 ms. These means
include first-query JIT/cache effects; warmed exact-term FTS latency above is a
separate test.

The following are **anchor-retrieval diagnostics**, not a general relevance
benchmark. Gold IDs identify deliberately inserted facts/paraphrases; generic
queries can have additional legitimate matches outside those IDs. Recall@5 is
averaged per query. MRR is the reciprocal rank of the first anchor in the top 50.
Labels never enter model inputs or retrieval scores. This small synthetic set
cannot establish statistically significant general retrieval quality.

| Query group | FTS recall@5 | Vector recall@5 | Hybrid recall@5 | FTS MRR | Vector MRR | Hybrid MRR |
|---|---:|---:|---:|---:|---:|---:|
| realistic (7) | 61.9% | 69.0% | 57.1% | 0.860 | 0.861 | 0.864 |
| identifier (4) | 100.0% | 75.0% | 100.0% | 1.000 | 0.750 | 1.000 |
| semantic (6) | 33.3% | 47.2% | 47.2% | 0.667 | 0.615 | 0.806 |
| all (17) | 60.8% | 62.7% | 63.7% | 0.825 | 0.748 | 0.875 |

Concrete outcomes:

- **FTS wins an exact identifier:** `PR #381` returned record 8 first. Vector top
  five missed it and returned generic deployment discussions. Hybrid retained
  record 8. For `libssl3`, `migration 0048`, and `WorkStore.cs`, vectors also found
  the expected anchors, so FTS was not universally more accurate. Across the four
  identifier tests FTS/hybrid anchor recall was 100%, vector recall was 75%; this
  is an observed difference, not a significance claim from four queries.
- **Vectors win a paraphrase:** “Why couldn't the rollout finish within its allotted
  time?” found migration records 1 and 2 at vector ranks 1 and 2. FTS returned
  unrelated timing notes and missed both in its top five. Hybrid kept 1 and 2 at
  ranks 1 and 3.
- **Vectors find a vocabulary gap:** “Where are job objectives recorded
  permanently?” had no FTS matches. Vectors found durable storage record 12 at
  rank 3, though two Work-UI documentation fragments outranked it. Hybrid had the
  same order because lexical retrieval was empty.
- **A vector failure matters:** “Which dependency caused the container to fail
  during startup?” missed the SSL anchors in vector top five and preferred npm
  cache/dependency notes. FTS found SSL records 8 and 3; hybrid retained only 8
  in top five. This requested semantic example did not demonstrate a vector win.
- **RRF can demote a good lexical result:** for the automatic-PR decision query,
  FTS found both decision records 5 and 10 in top five, while vector/hybrid found
  only 5. Agreement on generic distractors can outweigh a relevant single-method
  hit. Repository authorization showed the opposite benefit: vectors found 6 and
  9, lexical missed both, and hybrid recovered 6.

Hybrid raised aggregate anchor recall only from 60.8% (FTS) to 63.7% and MRR from
0.825 to 0.875. Semantic anchor recall rose from 33.3% to 47.2%, but realistic-query
recall fell from 61.9% to 57.1%. There is value in combining methods, but **plain RRF
is not a demonstrated blanket improvement**. Keep exact-identifier results visible
and evaluate deterministic weighting/source restrictions on real Goblin data
before choosing a production hybrid policy. BGE-small is an affordable English
candidate generator, not a replacement for technical identifier search.

## Noise filtering

Exact, case-insensitive matches for `thanks`, `+1`, `LGTM`, `done`, automated
successful health checks, and build-bot acknowledgements skipped **396 / 2,000
records (19.8%)**. The worker ingested all records and marked these as
`skipped_noise`; their content remains in FTS. A `thanks` FTS query was verified.
The final storage experiment deliberately embedded all 2,000 records so its
checkpoints are comparable.

This saves 19.8% of embedding requests/vectors for this constructed noise mix;
it does **not** prove a 19.8% CPU-time saving, because short acknowledgements are
cheaper than long notes. Noise prevalence is synthetic. Do not discard an approval
or important decision merely because its text is `+1` or `LGTM`; retain source and
workflow semantics when applying filters in Goblin.

## Resource contention and background behavior

Synthetic CPU load is generated by a separate mode of the same C# application.
Four threads represent moderate load; eight represent heavy load. Threads spin
for about 45 ms and yield for 5 ms, with a 120-second automatic deadline. Load
processes are removed after each phase. No memory stress or external workloads
were injected.

A controlled comparison used the **same 512 records**, batch 1, one inference
thread, with and without the four-thread workload. There was no meaningful
throughput loss within this single-run comparison; the small increase under load
should be interpreted as run variation, not a speedup caused by contention.

| Controlled load | Records | Records/sec | Mean batch | P95 batch | Aggregate capacity CPU | Embedding process CPU |
|---|---:|---:|---:|---:|---:|---:|
| idle | 512 | 67.84 | 14.70 ms | 26.22 ms | 14.3% | 112.3% |
| moderate | 512 | 69.51 | 14.34 ms | 26.55 ms | 59.3% | 113.5% |

The capacity-aware worker separately processes successive filtered records. It
checks CPU below 80% of quota, more than 512 MiB of working-set headroom, and
memory PSI below 10% when available. Unavailable PSI does not block the worker.
On rejection it waits 250 ms. All phase events, resource samples, and pending
counts are preserved in [worker.json](results/worker-int8-b1-t1/worker.json).

A dedicated foreground thread performs a small FTS lookup every 100 ms. Its P95
query duration and scheduling lateness are real measurements, but they represent
this probe, not all of Goblin's HTTP, build, or agent workloads.

| Worker phase | Embedded | Records/sec | Paused checks | Mean batch | Aggregate capacity CPU | Foreground P95 query | Foreground P95 delay |
|---|---:|---:|---:|---:|---:|---:|---:|
| idle | 512 | 58.98 | 0 | 16.69 ms | 14.2% | 0.586 ms | 0.817 ms |
| moderate | 512 | 57.22 | 2 | 16.27 ms | 57.0% | 0.445 ms | 0.635 ms |
| heavy | 0 | 0.00 | 24 | 0.00 ms | 90.1% | 0.409 ms | 0.550 ms |
| resumed | 512 | 58.87 | 0 | 16.77 ms | 12.7% | 0.425 ms | 0.653 ms |

Under heavy load the worker did **zero embeddings**, paused on all **24 checks**
over approximately six seconds, then completed another **512 embeddings** after
load removal. It retained roughly 200 MiB RSS while paused; pausing inference
does not unload the model or release native arenas. The moderate worker phase had
two short pauses. Its successive batches differ from idle, so use the separate
identical-record comparison above when comparing throughput.

Normal small lexical queries were not materially delayed in this test. Larger
builds or agent executions may compete differently. A simple capacity guard is
worth retaining, but detailed cluster-aware scheduling is not justified here.
A stable sampling window/hysteresis and a bounded inference lane would be sensible
production refinements; this experiment does not implement those policies.

## Where to run the model

| Option | Crash isolation | RAM/model duplication | Deployment and throttling | Maintainability |
|---|---|---|---|---|
| Main Goblin process | Native inference failure can take down Goblin | One reusable session can serve document and query embeddings; smallest combined footprint | One deployment; ORT thread limit and queue/capacity guard; no independent hard process budget | Simplest initial implementation |
| Separate C# worker process | Native model failures are isolated | One model if query requests also go to worker; loading query inference in main duplicates the roughly 120 MiB loaded base plus arenas | Same executable/image can have worker mode; OS-level limits possible; query IPC/process supervision required | Useful when isolation or independent limits justify IPC |
| Separate small service | Isolated model process | One model per service replica; network query calls avoid loading it in main | Extra service lifecycle, API, access controls, and health checks; independent resource limits | More operational surface than this feature currently needs |

Start **inside Goblin**, with a single reusable session and one bounded inference
lane that can prioritize query embeddings. The conservative batch-1 profile gives
no strong measured reason to add another service immediately. If native failure
isolation or enforced independent CPU/RAM limits become requirements, use a
**separate C# process**, with both query and document inference there to avoid
model duplication. The experiment did not simulate native crashes or benchmark
IPC/service overhead. Keep SQLite memory separate from Goblin's durable Work
history; this PR changes no production persistence or permission boundary.

## Answers for Goblin

| Question | Recommendation based on this run |
|---|---|
| 1. CPU-only local embedding practical? | Yes: all 2,000 BGE INT8 embeddings completed in 27.46 seconds with about one core and 215.4 MiB peak RSS. |
| 2. Is BGE-small sufficient? | Suitable for an economical English candidate generator. It found useful paraphrases but missed technical queries; preserve FTS and validate real data before relying on it. No multilingual or hosted-model accuracy comparison was made. |
| 3. Actual C# RAM? | Roughly 120 MiB loaded, 192 MiB typical and 215 MiB peak for the complete batch-1 pass. Grouped batch 8 peaked at 368 MiB; ungrouped batch 64 reached about 4 GiB. |
| 4. Actual CPU? | About 1.04 cores during the complete one-thread pass; two-thread grouped batch 8 used about 2.03 cores. Individual samples can peak higher due to runtime/sampling effects. |
| 5. Does batching help? | Only with controlled text lengths here: grouped batch 8 reached 107/sec; unordered batch 8 slowed to 35/sec. Larger unordered batches were much worse. |
| 6. Batch size? | Default 1 with one thread for simplicity and predictable footprint. Optional grouped 8 with two threads when throughput matters. Avoid a fixed unordered 32. |
| 7. Main process or worker? | Main process first, one session and bounded queue. Separate C# process if crash isolation or independent hard limits are required; extra service is not justified by this run. |
| 8. Opportunistic processing? | A basic CPU/RAM guard is worthwhile: it paused at heavy load and resumed. No Kubernetes-aware scheduler is needed. |
| 9. Embed every memory? | No: exact acknowledgement filters avoided 19.8% of requests in this corpus while preserving FTS. Respect approval/decision semantics and deduplicate/chunk real data. |
| 10. FTS without vectors? | Very useful: immediately searchable at about 25k bulk ingestions/sec and 0.10 ms warmed exact-term searches; strongest identifier baseline. |
| 11. Hybrid materially better? | Helpful for some paraphrases and mixed queries, but aggregate gains were modest and some results worsened. Do not promise a blanket improvement from unweighted RRF. |
| 12. Dedicated vector DB justified? | No for 2,000 records: local BLOB cosine scans averaged about 8 ms including SQLite reads and sorting. Larger-scale indexing requires a separate benchmark. |
| 13. Minimum recommended machine? | Feature-only starting recommendation: 2 CPU cores and 2 GiB system RAM, reserving 512 MiB–1 GiB headroom for this conservative embedding path. Two-CPU affinity delivered 70.87/sec and about 196 MiB peak. The 2 GiB VM size is a conservative extrapolation, not a tested memory limit; full Goblin/agents/builds need additional separately measured capacity. |

## Reproduction and limitations

See [README.md](README.md), [run.sh](run.sh), the locked project, and raw
[results](results/). All retained data are synthetic. Benchmarks are single runs
on a modern shared-sandbox EPYC CPU, with warm file caches; they are not confidence
intervals or predictions for an older self-hosted CPU. Peak RSS is sampled, so very
short peaks could be missed. The sampler and database writes are included in
process CPU. The raw resource CSVs use zero placeholders in the unavailable PSI columns;
those zeros are not observations of zero pressure. PSI and memory-pressure gating
were not exercised because PSI files
were absent and available RAM stayed ample. Heavy CPU gating was exercised.

This is an isolated proof of concept, not production memory integration. There
was no Kubernetes/Docker/database service deployment, hosted embedding inference,
production traffic replay, concurrent multi-worker correctness test, or million-row
retrieval test. Synthetic labels make findings reproducible but cannot establish
real-world search quality. Longer real documentation needs a chunking strategy
instead of this experiment's 512-token truncation.

Primary implementation references, used for tokenizer/pooling configuration rather
than performance conclusions:
[BAAI BGE model card](https://huggingface.co/BAAI/bge-small-en-v1.5),
[Xenova BGE ONNX files](https://huggingface.co/Xenova/bge-small-en-v1.5/tree/ea104dacec62c0de699686887e3f920caeb4f3e3/onnx),
and [Nomic model card](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5).
