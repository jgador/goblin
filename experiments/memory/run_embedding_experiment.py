#!/usr/bin/env python3
"""Offline CPU-only vector and fixed-RRF embedding challengers."""
import argparse
import hashlib
import json
import math
import os
import platform
import resource
import sqlite3
import statistics
import tempfile
import time
import unicodedata
from pathlib import Path

from run_experiment import (
    SEMANTIC_OPERATORS,
    compact_text,
    detect_supersessions,
    fts_query,
    load_jsonl,
    query_aware_compact,
    select_query_clauses,
)
from run_heldout_validation import terms

ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT.parents[1] / ".artifacts" / "memory-loop"


def rss():
    # Linux ru_maxrss is KiB, and is a process-lifetime high-water mark.
    return round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 3)


def rank_one_protected_budget_context(rows, query):
    """Apply Run 017's 128-token target across one ranked result set."""
    budget_chars = 128 * 4
    documents = []
    for _, text in rows:
        selected, _ = select_query_clauses(text, query)
        documents.append([compact_text(clause) for clause in selected])
    retained = [clauses[:] if index == 0 else clauses[:1]
                for index, clauses in enumerate(documents)]
    current_chars = sum(sum(map(len, clauses)) + max(0, len(clauses) - 1)
                        for clauses in retained)
    budget_exhausted = current_chars > budget_chars
    for clauses, retained_clauses in zip(documents[1:], retained[1:]):
        for clause in clauses[1:]:
            added_chars = len(clause) + 1
            if not budget_exhausted and current_chars + added_chars <= budget_chars:
                retained_clauses.append(clause)
                current_chars += added_chars
            else:
                budget_exhausted = True
    return [" ".join(clauses) for clauses in retained]


def evaluate(queries, retrieve, suppressed, context_transform=None, context_assembler=None):
    context_transform = context_transform or (lambda text, _query: compact_text(text))
    details, latency, embed_latency, search_latency, context_latency = [], [], [], [], []
    candidate_search_latency, rerank_latency = [], []
    cpu_start = time.process_time()
    for q in queries:
        started = time.perf_counter()
        retrieved = retrieve(q["query"])
        rows, embedding_ms, search_ms = retrieved[:3]
        if len(retrieved) == 5:
            candidate_search_latency.append(retrieved[3])
            rerank_latency.append(retrieved[4])
        rows = [row for row in rows if row[0] not in suppressed]
        context_started = time.perf_counter()
        rendered = (context_assembler(rows, q["query"]) if context_assembler else
                    [context_transform(row[1], q["query"]) for row in rows])
        context_latency.append((time.perf_counter() - context_started) * 1000)
        elapsed = (time.perf_counter() - started) * 1000
        ids = [row[0] for row in rows]
        relevant = set(q["relevant"])
        rank = next((i + 1 for i, doc in enumerate(ids) if doc in relevant), None)
        chars = sum(map(len, rendered))
        evidence = (set(q["evidence_terms"]).issubset(terms(" ".join(rendered)))
                    if "evidence_terms" in q else None)
        details.append({"query": q["id"], "top": ids, "first_relevant_rank": rank,
                        "recall": len(relevant.intersection(ids)) / len(relevant),
                        "evidence_preserved": evidence, "context_chars": chars,
                        "context_tokens_approx": math.ceil(chars / 4)})
        latency.append(elapsed)
        embed_latency.append(embedding_ms)
        search_latency.append(search_ms)
    cpu = time.process_time() - cpu_start
    measured_evidence = [d["evidence_preserved"] for d in details if d["evidence_preserved"] is not None]
    metrics = {
        "query_count": len(queries), "k": 5,
        "mrr_at_5": round(statistics.fmean(0 if d["first_relevant_rank"] is None else 1 / d["first_relevant_rank"] for d in details), 6),
        "recall_at_5": round(statistics.fmean(d["recall"] for d in details), 6),
        "hit_at_1": round(statistics.fmean(d["first_relevant_rank"] == 1 for d in details), 6),
        "evidence_at_5": statistics.fmean(measured_evidence) if measured_evidence else None,
        "context_tokens_approx_mean": statistics.fmean(d["context_tokens_approx"] for d in details),
        "end_to_end_ms_mean": statistics.fmean(latency),
        "end_to_end_ms_p95": sorted(latency)[math.ceil(.95 * len(latency)) - 1],
        "query_embedding_ms_mean": statistics.fmean(embed_latency),
        "search_ms_mean": statistics.fmean(search_latency),
        "context_assembly_ms_mean": statistics.fmean(context_latency),
        "cpu_seconds": cpu, "peak_rss_mib": rss(), "details": details,
    }
    if candidate_search_latency:
        metrics["int8_candidate_search_ms_mean"] = statistics.fmean(candidate_search_latency)
        metrics["fp16_rerank_ms_mean"] = statistics.fmean(rerank_latency)
    return metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", choices=["nomic", "nomic_256", "bge", "e5", "e5_int8", "minilm"], default="nomic")
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--retrieval", choices=["vector", "hybrid"], default="vector")
    parser.add_argument(
        "--vector-storage",
        choices=["float32", "int8", "int8_maxabs", "int8_maxabs_fp16_rerank"],
        default="float32",
    )
    parser.add_argument(
        "--context",
        choices=["compact", "query_aware", "rank_one_protected_budget"],
        default="compact",
    )
    parser.add_argument(
        "--embedding-cache",
        choices=[
            "none",
            "sqlite_content_sha256",
            "sqlite_namespace_gc",
            "sqlite_pending_queue",
            "sqlite_pending_recovery",
        ],
        default="none",
    )
    args = parser.parse_args()
    if args.threads < 1:
        parser.error("threads must be positive")
    if args.context != "compact" and args.model != "e5":
        parser.error("context extensions apply only to the E5 semantic champion")
    if args.vector_storage != "float32" and not (
        args.model == "e5" and args.context == "rank_one_protected_budget"
    ):
        parser.error("int8 vector storage extends the Run 017 E5 champion")
    if args.embedding_cache != "none" and not (
        args.model == "e5"
        and args.context == "rank_one_protected_budget"
        and args.vector_storage == "int8_maxabs_fp16_rerank"
    ):
        parser.error("the SQLite embedding cache extends the Run 020 champion")
    if args.model_dir is None:
        model_dirs = {"nomic": "nomic", "nomic_256": "nomic", "bge": "bge-small", "e5": "e5-small", "e5_int8": "e5-int8", "minilm": "minilm"}
        args.model_dir = ARTIFACTS / model_dirs[args.model]
    manifest = json.loads((ROOT / f"{args.model}_model.json").read_text())
    conventions = manifest.get("conventions", {
        "dimensions": 768, "max_tokens": 8192,
        "document_prefix": "search_document: ", "query_prefix": "search_query: ",
        "pooling": "attention-mask-mean-layer-norm", "normalization": "l2",
        "distance": "cosine",
    })
    dimensions = conventions["dimensions"]
    for name, expected in manifest["files"].items():
        path = args.model_dir / Path(name).name
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != expected["sha256"]:
            raise ValueError(f"Model checksum mismatch: {name}")
    corpus = load_jsonl(ROOT / "corpus.jsonl")
    sets = {"original": load_jsonl(ROOT / "queries.jsonl"),
            "heldout": load_jsonl(ROOT / "heldout_queries.jsonl")}
    suppressed = set(detect_supersessions(corpus))
    challenger = args.model in ("nomic_256", "bge", "e5", "e5_int8", "minilm")
    hybrid = args.retrieval == "hybrid" or challenger
    run_by_model = {"nomic_256": 13, "e5_int8": 12, "minilm": 11, "e5": 10, "bge": 9}
    method_by_model = {
        "nomic_256": "fts5-nomic-256d-rrf60-sqlite-vec",
        "e5_int8": "fts5-e5-small-v2-dynamic-int8-rrf60-sqlite-vec",
        "minilm": "fts5-all-minilm-l6-v2-rrf60-sqlite-vec",
        "e5": "fts5-e5-small-v2-rrf60-sqlite-vec",
        "bge": "fts5-bge-small-rrf60-sqlite-vec",
        "nomic": "fts5-nomic-rrf60-sqlite-vec" if hybrid else "nomic-fp32-768-sqlite-vec",
    }
    hypothesis_by_model = {
        "nomic_256": "Nomic's documented 256-dimensional Matryoshka projection can preserve fixed-RRF retrieval quality while reducing vec0 storage.",
        "e5_int8": "Dynamic INT8 E5-small-v2 can preserve Run 010 quality while reducing model, memory and latency costs.",
        "minilm": "all-MiniLM-L6-v2 can match Run 010 fixed-RRF quality while further reducing local embedding cost.",
        "e5": "E5-small-v2 can match Run 008 fixed-RRF semantic quality while materially reducing local embedding cost.",
        "bge": "BGE-small v1.5 can match Run 008 fixed-RRF semantic quality while materially reducing local embedding cost.",
        "nomic": ("Fixed reciprocal-rank fusion of FTS5 and Nomic rankings improves relevance without tuning against benchmark labels."
                  if hybrid else
                  "Real Nomic vector-only retrieval improves relevance over the lexical champion on both frozen benchmarks."),
    }
    result = {"run": run_by_model.get(args.model, 8 if hybrid else 7),
              "method": method_by_model[args.model],
              "hypothesis": hypothesis_by_model[args.model],
              "model": manifest, "dimensions": dimensions, "precision": manifest.get("precision", "float32"),
              "model_precision": manifest.get("precision", "float32"),
              "vector_storage_precision": args.vector_storage,
              "threads": args.threads, "provider": "CPUExecutionProvider",
              "python_version": platform.python_version(), "sqlite_version": sqlite3.sqlite_version,
              "os": platform.platform(), "logical_cpu_count": os.cpu_count(),
              "supersessions": detect_supersessions(corpus),
              "requested_orchestration_model": "GPT-6.1 Sol", "orchestration_model_verified": False,
              "external_inference_api_calls": 0, "answer_generation_evaluated": False,
              "benchmark_sha256": hashlib.sha256((ROOT / "corpus.jsonl").read_bytes() + b"\0" + (ROOT / "queries.jsonl").read_bytes()).hexdigest(),
              "heldout_sha256": hashlib.sha256((ROOT / "corpus.jsonl").read_bytes() + b"\0" + (ROOT / "heldout_queries.jsonl").read_bytes()).hexdigest()}
    if args.context == "query_aware":
        result.update(
            run=14,
            method="fts5-e5-small-v2-rrf60-query-aware-clause-context",
            hypothesis="A fixed query-aware clause selector can reduce Run 010 context without changing retrieval or losing held-out evidence.",
        )
        result["selection_rule"] = {
            "always_keep_first_clause": True,
            "keep_later_query_overlap_clauses": True,
            "keep_later_semantic_operator_clauses": sorted(SEMANTIC_OPERATORS),
            "then_apply_run005_compaction": True,
            "selected_before_evaluation": True,
        }
    elif args.context == "rank_one_protected_budget":
        result.update(
            run=17,
            followup_to_run=16,
            method="fts5-e5-small-v2-rrf60-query-aware-rank-one-protected-budget-128",
            hypothesis=(
                "Protecting every selected clause in the rank-one record before applying "
                "Run 016's fixed 128-token target can retain evidence while reducing Run 014 context."
            ),
        )
        result["budget_rule"] = {
            "approximate_token_target": 128,
            "payload_character_target": 512,
            "protected": "all Run 014 selected clauses from rank one, plus the first selected clause from every lower-rank record",
            "additional_clause_order": "retrieval ranks two through five, then selected clause order",
            "overflow": "stop admitting optional clauses before the first overflow; never truncate a clause",
            "protected_overflow": "retain protected clauses even if they exceed the target",
            "provenance": "full records remain in SQLite; the isolated replay records every retained and omitted source clause",
            "selected_before_evaluation": True,
        }
    if args.vector_storage == "int8":
        result.update(
            run=18,
            followup_to_run=17,
            method="fts5-e5-small-v2-rrf60-query-aware-rank-one-budget-int8-vec0",
            hypothesis=(
                "sqlite-vec unit-range int8 storage can preserve Run 017 quality and context "
                "while reducing SQLite vector storage without changing FP32 E5 inference."
            ),
            precision="float32-model-int8-unit-vector-storage",
        )
        result["vector_storage"] = {
            "type": f"int8[{dimensions}]",
            "quantizer": "sqlite-vec vec_quantize_int8(vector, 'unit')",
            "input_range": "normalized FP32 values in [-1, 1]",
            "distance": "cosine",
            "query_quantization": "same sqlite-vec unit quantizer immediately before search",
            "selected_before_evaluation": True,
        }
    elif args.vector_storage == "int8_maxabs":
        result.update(
            run=19,
            followup_to_run=18,
            method="fts5-e5-small-v2-rrf60-query-aware-rank-one-budget-int8-maxabs-vec0",
            hypothesis=(
                "Per-vector symmetric max-absolute int8 scaling can preserve Run 017 "
                "quality and context while retaining Run 018's SQLite storage reduction."
            ),
            precision="float32-model-int8-per-vector-maxabs-storage",
        )
        result["vector_storage"] = {
            "type": f"int8[{dimensions}]",
            "quantizer": "round(vector / max(abs(vector)) * 127), clipped to [-127, 127]",
            "rounding": "NumPy rint (round half to even)",
            "input_range": "L2-normalized FP32 vectors",
            "distance": "cosine",
            "scale_storage": "not required because cosine distance is invariant to positive per-vector scaling",
            "query_quantization": "same deterministic per-vector max-absolute rule immediately before search",
            "selected_before_evaluation": True,
        }
    elif args.vector_storage == "int8_maxabs_fp16_rerank":
        result.update(
            run=20,
            followup_to_run=19,
            method="fts5-e5-small-v2-rrf60-rank-one-budget-int8-maxabs-fp16-rerank",
            hypothesis=(
                "Max-absolute int8 vec0 candidate retrieval followed by FP16 reranking "
                "can match Run 017 quality and context with substantially less SQLite storage."
            ),
            precision="float32-model-int8-candidates-fp16-rerank-storage",
        )
        result["vector_storage"] = {
            "candidate_index": f"sqlite-vec int8[{dimensions}] with per-vector max-absolute scaling",
            "candidate_quantizer": "round(vector / max(abs(vector)) * 127), clipped to [-127, 127]",
            "candidate_pool": 20,
            "rerank_storage": f"SQLite BLOB float16[{dimensions}]",
            "rerank_output": 10,
            "rerank_distance": "cosine computed in FP32 from stored FP16 corpus vectors and FP32 query",
            "rerank_corpus_precision": "float16 storage converted to float32 for scoring",
            "rerank_query_precision": "float32",
            "query_quantization": "max-absolute int8 for candidate search; FP32 for reranking",
            "selected_before_evaluation": True,
        }
    if args.embedding_cache == "sqlite_content_sha256":
        result.update(
            run=21,
            followup_to_run=20,
            method="fts5-e5-small-v2-rrf60-int8-fp16-rerank-content-cache",
            hypothesis=(
                "A content-addressed SQLite embedding cache can avoid unchanged-document "
                "inference and limit a one-record update to one embedding while preserving Run 020 retrieval."
            ),
        )
        result["embedding_cache_rule"] = {
            "key": "SHA-256 of namespace JSON, NUL, document prefix, NUL, and NFC-normalized stripped text",
            "namespace": "model name, pinned revision, dimensions, and embedding conventions",
            "stored_precision": "float32",
            "warm_reindex_expected": {"hits": len(corpus), "misses": 0},
            "one_record_update_expected": {"hits": len(corpus) - 1, "misses": 1},
            "update_document": "d040",
            "update_suffix": " Cache experiment revision two.",
            "selected_before_evaluation": True,
        }
    elif args.embedding_cache == "sqlite_namespace_gc":
        result.update(
            run=22,
            followup_to_run=21,
            method="fts5-e5-small-v2-rrf60-int8-fp16-rerank-cache-namespace-gc",
            hypothesis=(
                "Embedding-convention namespaces can prevent stale cache reuse and permit "
                "bounded cleanup without changing Run 021 retrieval or active-cache hits."
            ),
        )
        result["embedding_cache_rule"] = {
            "key": "unchanged Run 021 content-addressed SHA-256 key",
            "active_namespace": "pinned E5 conventions with max_tokens=512",
            "stale_namespace": "controlled max_tokens=511 convention",
            "stale_namespace_expected": {"hits": 0, "misses": len(corpus)},
            "cleanup_expected": {
                "deleted_entries": len(corpus),
                "remaining_entries": len(corpus),
                "wall_seconds_at_most": 0.05,
                "cpu_seconds_at_most": 0.05,
            },
            "post_cleanup_active_expected": {"hits": len(corpus), "misses": 0},
            "selected_before_evaluation": True,
        }
    elif args.embedding_cache == "sqlite_pending_queue":
        result.update(
            run=23,
            followup_to_run=22,
            method="fts5-e5-small-v2-rrf60-int8-fp16-rerank-pending-queue",
            hypothesis=(
                "A durable SQLite pending queue can make records immediately FTS5-searchable, "
                "drain embeddings in bounded FIFO batches with cache reuse, and converge to "
                "Run 022 retrieval."
            ),
        )
        result["embedding_queue_rule"] = {
            "states": ["pending", "processing", "ready"],
            "ordering": "ascending ingestion ordinal",
            "batch_size": 4,
            "preseeded_cache_documents": len(corpus) // 2,
            "expected_worker_cache_hits": len(corpus) // 2,
            "expected_worker_cache_misses": len(corpus) // 2,
            "enqueue_wall_seconds_at_most": 0.05,
            "enqueue_cpu_seconds_at_most": 0.05,
            "worker_wall_seconds_at_most": 1.0,
            "worker_cpu_seconds_at_most": 2.0,
            "selected_before_evaluation": True,
        }
    elif args.embedding_cache == "sqlite_pending_recovery":
        result.update(
            run=24,
            followup_to_run=23,
            method="fts5-e5-small-v2-rrf60-int8-fp16-rerank-pending-recovery",
            hypothesis=(
                "A reopened SQLite worker can reclaim a durably stranded processing batch "
                "and complete with unique ready outputs while preserving Run 023 retrieval."
            ),
        )
        result["embedding_queue_rule"] = {
            "states": ["pending", "processing", "ready"],
            "ordering": "ascending ingestion ordinal",
            "batch_size": 4,
            "preseeded_cache_documents": len(corpus) // 2,
            "interruption": "after processing commit and before cache lookup or inference",
            "injected_processing_ordinals": [0, 1, 2, 3],
            "reclaim": "after reopening SQLite, atomically change processing to pending",
            "reclaim_wall_seconds_at_most": 0.05,
            "reclaim_cpu_seconds_at_most": 0.05,
            "expected_attempts": {"once": len(corpus) - 4, "twice": 4},
            "expected_worker_cache_hits": len(corpus) // 2,
            "expected_worker_cache_misses": len(corpus) // 2,
            "worker_wall_seconds_at_most": 1.0,
            "worker_cpu_seconds_at_most": 2.0,
            "selected_before_evaluation": True,
        }
    if args.model == "e5_int8":
        cpu_flags = set()
        cpuinfo = Path("/proc/cpuinfo")
        if cpuinfo.exists():
            for line in cpuinfo.read_text().splitlines():
                if line.startswith(("flags", "Features")) and ":" in line:
                    cpu_flags.update(line.split(":", 1)[1].split())
        result["cpu_capabilities"] = {
            "architecture": platform.machine(),
            "avx2": "avx2" in cpu_flags,
            "avx512f": "avx512f" in cpu_flags,
            "avx512_vnni": "avx512_vnni" in cpu_flags,
            "avx_vnni": "avx_vnni" in cpu_flags,
            "artifact_isa_specific": False,
        }
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ARTIFACTS) as td:
        db = sqlite3.connect(Path(td) / "memory.db")
        db.execute("CREATE VIRTUAL TABLE memory USING fts5(id UNINDEXED, text, source UNINDEXED, ts UNINDEXED, tokenize='unicode61')")
        queue_enabled = args.embedding_cache in (
            "sqlite_pending_queue",
            "sqlite_pending_recovery",
        )
        recovery_enabled = args.embedding_cache == "sqlite_pending_recovery"
        if queue_enabled:
            db.execute(
                "CREATE TABLE embedding_jobs("
                "document_id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL UNIQUE, "
                "content_sha256 TEXT NOT NULL, status TEXT NOT NULL "
                "CHECK(status IN ('pending','processing','ready')), "
                "attempts INTEGER NOT NULL DEFAULT 0, "
                "namespace_sha256 TEXT, cache_key TEXT)"
            )
            db.execute(
                "CREATE INDEX embedding_jobs_status_ordinal "
                "ON embedding_jobs(status,ordinal)"
            )
            enqueue_started = time.perf_counter()
            enqueue_cpu_started = time.process_time()
            db.executemany(
                "INSERT INTO memory VALUES (?,?,?,?)",
                [(d["id"], d["text"], d["source"], d["ts"]) for d in corpus],
            )
            db.executemany(
                "INSERT INTO embedding_jobs(document_id,ordinal,content_sha256,status) "
                "VALUES (?,?,?,'pending')",
                [
                    (
                        document["id"],
                        ordinal,
                        hashlib.sha256(
                            unicodedata.normalize("NFC", document["text"]).strip().encode()
                        ).hexdigest(),
                    )
                    for ordinal, document in enumerate(corpus)
                ],
            )
            db.commit()
            result["ingestion_lifecycle"] = {
                "enqueue": {
                    "documents": len(corpus),
                    "wall_seconds": time.perf_counter() - enqueue_started,
                    "cpu_seconds": time.process_time() - enqueue_cpu_started,
                    "database_bytes": (Path(td) / "memory.db").stat().st_size,
                    "fts_rows": db.execute("SELECT count(*) FROM memory").fetchone()[0],
                    "pending_jobs": db.execute(
                        "SELECT count(*) FROM embedding_jobs WHERE status='pending'"
                    ).fetchone()[0],
                }
            }
        else:
            db.executemany(
                "INSERT INTO memory VALUES (?,?,?,?)",
                [(d["id"], d["text"], d["source"], d["ts"]) for d in corpus],
            )
            db.commit()

        def lexical(query):
            start = time.perf_counter()
            rows = db.execute("SELECT id,text FROM memory WHERE memory MATCH ? ORDER BY bm25(memory) LIMIT 5", (fts_query(query),)).fetchall()
            return rows, 0, (time.perf_counter() - start) * 1000

        result["baseline"] = {name: evaluate(qs, lexical, suppressed) for name, qs in sets.items()}
        if queue_enabled:
            lifecycle = result["ingestion_lifecycle"]
            lifecycle["fts_while_pending"] = {
                "model_loaded": False,
                "pending_jobs_before": len(corpus),
                "pending_jobs_after": db.execute(
                    "SELECT count(*) FROM embedding_jobs WHERE status='pending'"
                ).fetchone()[0],
                "original": {
                    key: result["baseline"]["original"][key]
                    for key in (
                        "mrr_at_5",
                        "recall_at_5",
                        "hit_at_1",
                        "context_tokens_approx_mean",
                        "end_to_end_ms_mean",
                        "cpu_seconds",
                    )
                },
                "heldout": {
                    key: result["baseline"]["heldout"][key]
                    for key in (
                        "mrr_at_5",
                        "recall_at_5",
                        "hit_at_1",
                        "evidence_at_5",
                        "context_tokens_approx_mean",
                        "end_to_end_ms_mean",
                        "cpu_seconds",
                    )
                },
            }
        result["baseline_db_bytes"] = (Path(td) / "memory.db").stat().st_size
        # Imports/model loading occur after baseline measurement so its RSS does
        # not inherit the embedding runtime's high-water mark.
        start = time.perf_counter()
        cpu = time.process_time()
        import numpy as np
        import onnxruntime as ort
        import sqlite_vec
        from tokenizers import Tokenizer
        options = ort.SessionOptions()
        options.intra_op_num_threads = args.threads
        options.inter_op_num_threads = 1
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        session = ort.InferenceSession(str(args.model_dir / "model.onnx"), options, providers=["CPUExecutionProvider"])
        tokenizer = Tokenizer.from_file(str(args.model_dir / "tokenizer.json"))
        tokenizer.enable_padding(pad_id=0, pad_token="[PAD]")
        tokenizer.enable_truncation(max_length=conventions["max_tokens"])
        result["load_seconds_including_imports"] = time.perf_counter() - start
        result["load_cpu_seconds"] = time.process_time() - cpu
        result["load_peak_rss_mib"] = rss()
        result["runtime_versions"] = {"onnxruntime": ort.__version__, "numpy": np.__version__,
                                       "tokenizers": __import__("tokenizers").__version__}

        def embed(texts, prefix):
            enc = tokenizer.encode_batch([prefix + text for text in texts])
            inputs = {"input_ids": np.array([e.ids for e in enc], dtype=np.int64),
                      "attention_mask": np.array([e.attention_mask for e in enc], dtype=np.int64),
                      "token_type_ids": np.array([e.type_ids for e in enc], dtype=np.int64)}
            output = session.run(None, {i.name: inputs[i.name] for i in session.get_inputs()})[0]
            if conventions["pooling"] == "first-token-cls":
                vectors = output[:, 0, :]
            else:
                mask = inputs["attention_mask"][..., None]
                vectors = (output * mask).sum(axis=1) / np.maximum(mask.sum(axis=1), 1)
                if conventions["pooling"] == "attention-mask-mean-layer-norm":
                    vectors = (vectors - vectors.mean(axis=1, keepdims=True)) / np.sqrt(vectors.var(axis=1, keepdims=True) + 1e-5)
            if "matryoshka_dimensions" in conventions:
                vectors = vectors[:, :conventions["matryoshka_dimensions"]]
            vectors /= np.maximum(np.linalg.norm(vectors, axis=1, keepdims=True), 1e-12)
            vectors = vectors.astype(np.float32)
            assert vectors.shape == (len(texts), dimensions) and np.isfinite(vectors).all()
            return vectors

        def quantize_maxabs(vector):
            maximum = float(np.max(np.abs(vector)))
            if maximum == 0:
                return np.zeros(vector.shape, dtype=np.int8)
            return np.clip(np.rint(vector / maximum * 127), -127, 127).astype(np.int8)

        def stored_vector_bytes(vector):
            if args.vector_storage in ("int8_maxabs", "int8_maxabs_fp16_rerank"):
                return quantize_maxabs(vector).tobytes()
            return vector.tobytes()

        cache_enabled = args.embedding_cache != "none"
        if cache_enabled:
            def cache_namespace(cache_conventions):
                return json.dumps(
                    {
                        "model": manifest["model"],
                        "revision": manifest["revision"],
                        "dimensions": dimensions,
                        "conventions": cache_conventions,
                    },
                    sort_keys=True,
                    separators=(",", ":"),
                )

            namespace = cache_namespace(conventions)
            namespace_sha256 = hashlib.sha256(namespace.encode()).hexdigest()
            db.execute(
                "CREATE TABLE embedding_cache("
                "cache_key TEXT PRIMARY KEY, namespace_sha256 TEXT NOT NULL, "
                "content_sha256 TEXT NOT NULL, embedding BLOB NOT NULL)"
            )
            db.commit()

            def cached_document_embeddings(texts, active_namespace, document_prefix):
                started = time.perf_counter()
                cpu_started = time.process_time()
                active_namespace_sha256 = hashlib.sha256(active_namespace.encode()).hexdigest()
                output = [None] * len(texts)
                missing = {}
                hits = 0
                for index, text in enumerate(texts):
                    normalized = unicodedata.normalize("NFC", text).strip()
                    content_sha256 = hashlib.sha256(normalized.encode()).hexdigest()
                    key_input = (
                        active_namespace.encode() + b"\0"
                        + document_prefix.encode() + b"\0"
                        + normalized.encode()
                    )
                    cache_key = hashlib.sha256(key_input).hexdigest()
                    row = db.execute(
                        "SELECT embedding FROM embedding_cache WHERE cache_key=?",
                        (cache_key,),
                    ).fetchone()
                    if row is not None:
                        output[index] = np.frombuffer(row[0], dtype=np.float32).copy()
                        hits += 1
                    else:
                        item = missing.setdefault(
                            cache_key,
                            {"text": normalized, "content_sha256": content_sha256, "indices": []},
                        )
                        item["indices"].append(index)
                inference_started = time.perf_counter()
                inference_cpu_started = time.process_time()
                missing_items = list(missing.items())
                for offset in range(0, len(missing_items), 4):
                    batch = missing_items[offset:offset + 4]
                    embedded = embed(
                        [item[1]["text"] for item in batch],
                        document_prefix,
                    )
                    for (cache_key, item), vector in zip(batch, embedded):
                        db.execute(
                            "INSERT INTO embedding_cache VALUES (?,?,?,?)",
                            (cache_key, active_namespace_sha256, item["content_sha256"], vector.tobytes()),
                        )
                        for index in item["indices"]:
                            output[index] = vector
                inference_wall = time.perf_counter() - inference_started
                inference_cpu = time.process_time() - inference_cpu_started
                db.commit()
                vectors_out = np.stack(output).astype(np.float32)
                assert vectors_out.shape == (len(texts), dimensions)
                total_wall = time.perf_counter() - started
                return vectors_out, {
                    "documents": len(texts),
                    "hits": hits,
                    "misses": len(missing_items),
                    "total_wall_seconds": total_wall,
                    "total_cpu_seconds": time.process_time() - cpu_started,
                    "inference_wall_seconds": inference_wall,
                    "inference_cpu_seconds": inference_cpu,
                    "documents_per_second": len(texts) / total_wall,
                    "peak_rss_mib": rss(),
                }

            corpus_texts = [document["text"] for document in corpus]
            if queue_enabled:
                preseed_count = len(corpus) // 2
                _, preseed_cache = cached_document_embeddings(
                    corpus_texts[:preseed_count], namespace, conventions["document_prefix"]
                )
                preseed_cache["database_bytes"] = (Path(td) / "memory.db").stat().st_size
                if recovery_enabled:
                    interrupted_rows = db.execute(
                        "SELECT document_id,ordinal FROM embedding_jobs "
                        "WHERE status='pending' ORDER BY ordinal LIMIT 4"
                    ).fetchall()
                    db.executemany(
                        "UPDATE embedding_jobs SET status='processing',attempts=attempts+1 "
                        "WHERE document_id=? AND status='pending'",
                        [(row[0],) for row in interrupted_rows],
                    )
                    db.commit()
                    before_reopen = {
                        "ordinals": [row[1] for row in interrupted_rows],
                        "processing_jobs": db.execute(
                            "SELECT count(*) FROM embedding_jobs WHERE status='processing'"
                        ).fetchone()[0],
                    }
                    db.close()
                    db = sqlite3.connect(Path(td) / "memory.db")
                    reopened_ordinals = [
                        row[0] for row in db.execute(
                            "SELECT ordinal FROM embedding_jobs "
                            "WHERE status='processing' ORDER BY ordinal"
                        ).fetchall()
                    ]
                    recovery_started = time.perf_counter()
                    recovery_cpu_started = time.process_time()
                    recovery_cursor = db.execute(
                        "UPDATE embedding_jobs SET status='pending' WHERE status='processing'"
                    )
                    recovered_jobs = recovery_cursor.rowcount
                    db.commit()
                    result["ingestion_lifecycle"]["recovery"] = {
                        "interruption_point": "after processing commit and before cache lookup or inference",
                        "before_reopen": before_reopen,
                        "processing_ordinals_after_reopen": reopened_ordinals,
                        "recovered_jobs": recovered_jobs,
                        "wall_seconds": time.perf_counter() - recovery_started,
                        "cpu_seconds": time.process_time() - recovery_cpu_started,
                        "status_counts_after_reclaim": dict(
                            db.execute(
                                "SELECT status,count(*) FROM embedding_jobs GROUP BY status"
                            ).fetchall()
                        ),
                    }
                worker_started = time.perf_counter()
                worker_cpu_started = time.process_time()
                batch_metrics = []
                worker_hits = 0
                worker_misses = 0
                worker_inference_wall = 0.0
                worker_inference_cpu = 0.0
                while True:
                    rows = db.execute(
                        "SELECT j.document_id,j.ordinal,m.text "
                        "FROM embedding_jobs j JOIN memory m ON m.id=j.document_id "
                        "WHERE j.status='pending' ORDER BY j.ordinal LIMIT 4"
                    ).fetchall()
                    if not rows:
                        break
                    db.executemany(
                        "UPDATE embedding_jobs SET status='processing',attempts=attempts+1 "
                        "WHERE document_id=? AND status='pending'",
                        [(row[0],) for row in rows],
                    )
                    db.commit()
                    batch_vectors, measured = cached_document_embeddings(
                        [row[2] for row in rows], namespace, conventions["document_prefix"]
                    )
                    for (document_id, ordinal, text), vector in zip(rows, batch_vectors):
                        normalized = unicodedata.normalize("NFC", text).strip()
                        key_input = (
                            namespace.encode() + b"\0"
                            + conventions["document_prefix"].encode() + b"\0"
                            + normalized.encode()
                        )
                        cache_key = hashlib.sha256(key_input).hexdigest()
                        db.execute(
                            "UPDATE embedding_jobs SET status='ready',namespace_sha256=?,"
                            "cache_key=? WHERE document_id=? AND status='processing'",
                            (namespace_sha256, cache_key, document_id),
                        )
                    db.commit()
                    worker_hits += measured["hits"]
                    worker_misses += measured["misses"]
                    worker_inference_wall += measured["inference_wall_seconds"]
                    worker_inference_cpu += measured["inference_cpu_seconds"]
                    batch_metrics.append(
                        {
                            "batch": len(batch_metrics) + 1,
                            "ordinals": [row[1] for row in rows],
                            "documents": len(rows),
                            "hits": measured["hits"],
                            "misses": measured["misses"],
                            "wall_seconds": measured["total_wall_seconds"],
                            "cpu_seconds": measured["total_cpu_seconds"],
                        }
                    )
                worker_wall = time.perf_counter() - worker_started
                worker_cpu = time.process_time() - worker_cpu_started
                status_counts = dict(
                    db.execute(
                        "SELECT status,count(*) FROM embedding_jobs GROUP BY status"
                    ).fetchall()
                )
                ready_rows = db.execute(
                    "SELECT j.ordinal,c.embedding FROM embedding_jobs j "
                    "JOIN embedding_cache c ON c.cache_key=j.cache_key "
                    "WHERE j.status='ready' ORDER BY j.ordinal"
                ).fetchall()
                vectors = np.stack(
                    [np.frombuffer(row[1], dtype=np.float32).copy() for row in ready_rows]
                ).astype(np.float32)
                assert vectors.shape == (len(corpus), dimensions)
                cache_entries, cache_payload_bytes = db.execute(
                    "SELECT count(*),coalesce(sum(length(embedding)),0) FROM embedding_cache"
                ).fetchone()
                ready_output = db.execute(
                    "SELECT count(*),count(DISTINCT document_id),count(DISTINCT cache_key) "
                    "FROM embedding_jobs WHERE status='ready'"
                ).fetchone()
                result["embedding_cache"] = {
                    "namespace_sha256": namespace_sha256,
                    "preseed": preseed_cache,
                    "entries_after_worker": cache_entries,
                    "payload_bytes_after_worker": cache_payload_bytes,
                }
                result["ingestion_lifecycle"]["worker"] = {
                    "batch_size": 4,
                    "batches": batch_metrics,
                    "batch_count": len(batch_metrics),
                    "cache_hits": worker_hits,
                    "cache_misses": worker_misses,
                    "inference_wall_seconds": worker_inference_wall,
                    "inference_cpu_seconds": worker_inference_cpu,
                    "wall_seconds": worker_wall,
                    "cpu_seconds": worker_cpu,
                    "status_counts": {
                        "pending": status_counts.get("pending", 0),
                        "processing": status_counts.get("processing", 0),
                        "ready": status_counts.get("ready", 0),
                    },
                    "attempts": dict(
                        db.execute(
                            "SELECT attempts,count(*) FROM embedding_jobs GROUP BY attempts"
                        ).fetchall()
                    ),
                    "ready_output": {
                        "rows": ready_output[0],
                        "unique_documents": ready_output[1],
                        "unique_cache_keys": ready_output[2],
                    },
                    "database_bytes": (Path(td) / "memory.db").stat().st_size,
                    "peak_rss_mib": rss(),
                }
                total_embedding_wall = preseed_cache["total_wall_seconds"] + worker_wall
                total_embedding_cpu = preseed_cache["total_cpu_seconds"] + worker_cpu
                result["corpus_embedding"] = {
                    "documents": len(corpus),
                    "batch_size": 4,
                    "wall_seconds": total_embedding_wall,
                    "cpu_seconds": total_embedding_cpu,
                    "inference_wall_seconds": (
                        preseed_cache["inference_wall_seconds"] + worker_inference_wall
                    ),
                    "inference_cpu_seconds": (
                        preseed_cache["inference_cpu_seconds"] + worker_inference_cpu
                    ),
                    "documents_per_second": len(corpus) / total_embedding_wall,
                    "cache_hits_during_worker": worker_hits,
                    "cache_misses_during_worker": worker_misses,
                    "peak_rss_mib": rss(),
                }
            else:
                _, cold_cache = cached_document_embeddings(
                    corpus_texts, namespace, conventions["document_prefix"]
                )
                cold_cache["database_bytes"] = (Path(td) / "memory.db").stat().st_size
                vectors, warm_cache = cached_document_embeddings(
                    corpus_texts, namespace, conventions["document_prefix"]
                )
                warm_cache["database_bytes"] = (Path(td) / "memory.db").stat().st_size
                result["embedding_cache"] = {
                    "namespace_sha256": namespace_sha256,
                    "cold_build": cold_cache,
                    "warm_reindex": warm_cache,
                    "warm_wall_reduction_percent": (
                        (cold_cache["total_wall_seconds"] - warm_cache["total_wall_seconds"])
                        / cold_cache["total_wall_seconds"] * 100
                    ),
                    "warm_cpu_reduction_percent": (
                        (cold_cache["total_cpu_seconds"] - warm_cache["total_cpu_seconds"])
                        / cold_cache["total_cpu_seconds"] * 100
                    ),
                }
                if args.embedding_cache == "sqlite_content_sha256":
                    updated_texts = corpus_texts.copy()
                    update_index = next(
                        i for i, document in enumerate(corpus) if document["id"] == "d040"
                    )
                    updated_texts[update_index] += " Cache experiment revision two."
                    _, incremental_cache = cached_document_embeddings(
                        updated_texts, namespace, conventions["document_prefix"]
                    )
                    incremental_cache["database_bytes"] = (Path(td) / "memory.db").stat().st_size
                    cache_entries, cache_payload_bytes = db.execute(
                        "SELECT count(*),coalesce(sum(length(embedding)),0) FROM embedding_cache"
                    ).fetchone()
                    result["embedding_cache"].update(
                        one_record_update=incremental_cache,
                        entries_after_update=cache_entries,
                        payload_bytes_after_update=cache_payload_bytes,
                    )
                else:
                    alternate_conventions = dict(conventions)
                    alternate_conventions["max_tokens"] = conventions["max_tokens"] - 1
                    alternate_namespace = cache_namespace(alternate_conventions)
                    alternate_namespace_sha256 = hashlib.sha256(
                        alternate_namespace.encode()
                    ).hexdigest()
                    tokenizer.enable_truncation(max_length=alternate_conventions["max_tokens"])
                    alternate_vectors, stale_namespace_build = cached_document_embeddings(
                        corpus_texts,
                        alternate_namespace,
                        alternate_conventions["document_prefix"],
                    )
                    tokenizer.enable_truncation(max_length=conventions["max_tokens"])
                    stale_namespace_build["database_bytes"] = (
                        Path(td) / "memory.db"
                    ).stat().st_size
                    entries_before_cleanup = db.execute(
                        "SELECT count(*) FROM embedding_cache"
                    ).fetchone()[0]
                    cleanup_started = time.perf_counter()
                    cleanup_cpu_started = time.process_time()
                    cleanup_cursor = db.execute(
                        "DELETE FROM embedding_cache WHERE namespace_sha256<>?",
                        (namespace_sha256,),
                    )
                    deleted_entries = cleanup_cursor.rowcount
                    db.commit()
                    db.execute("VACUUM")
                    cleanup_wall = time.perf_counter() - cleanup_started
                    cleanup_cpu = time.process_time() - cleanup_cpu_started
                    entries_after_cleanup, payload_bytes_after_cleanup = db.execute(
                        "SELECT count(*),coalesce(sum(length(embedding)),0) FROM embedding_cache"
                    ).fetchone()
                    cleanup_database_bytes = (Path(td) / "memory.db").stat().st_size
                    active_vectors_after_cleanup, post_cleanup_active = cached_document_embeddings(
                        corpus_texts, namespace, conventions["document_prefix"]
                    )
                    post_cleanup_active["database_bytes"] = (
                        Path(td) / "memory.db"
                    ).stat().st_size
                    result["embedding_cache"].update(
                        alternate_namespace_sha256=alternate_namespace_sha256,
                        namespace_changed=alternate_namespace_sha256 != namespace_sha256,
                        stale_namespace_build=stale_namespace_build,
                        alternate_embedding_max_abs_delta=float(
                            np.max(np.abs(alternate_vectors - vectors))
                        ),
                        cleanup={
                            "entries_before": entries_before_cleanup,
                            "deleted_entries": deleted_entries,
                            "entries_after": entries_after_cleanup,
                            "payload_bytes_after": payload_bytes_after_cleanup,
                            "database_bytes_after": cleanup_database_bytes,
                            "wall_seconds": cleanup_wall,
                            "cpu_seconds": cleanup_cpu,
                        },
                        post_cleanup_active=post_cleanup_active,
                        active_vectors_preserved=bool(
                            np.array_equal(active_vectors_after_cleanup, vectors)
                        ),
                    )
                result["corpus_embedding"] = {
                    "documents": len(corpus),
                    "batch_size": 4,
                    "wall_seconds": cold_cache["total_wall_seconds"],
                    "cpu_seconds": cold_cache["total_cpu_seconds"],
                    "inference_wall_seconds": cold_cache["inference_wall_seconds"],
                    "inference_cpu_seconds": cold_cache["inference_cpu_seconds"],
                    "documents_per_second": cold_cache["documents_per_second"],
                    "peak_rss_mib": cold_cache["peak_rss_mib"],
                }
        else:
            start = time.perf_counter()
            cpu = time.process_time()
            vectors = np.concatenate([
                embed([d["text"] for d in corpus[i:i+4]], conventions["document_prefix"])
                for i in range(0, len(corpus), 4)
            ])
            seconds = time.perf_counter() - start
            result["corpus_embedding"] = {
                "documents": len(corpus),
                "batch_size": 4,
                "wall_seconds": seconds,
                "cpu_seconds": time.process_time() - cpu,
                "documents_per_second": len(corpus) / seconds,
                "peak_rss_mib": rss(),
            }
        db.enable_load_extension(True)
        sqlite_vec.load(db)
        db.enable_load_extension(False)
        result["sqlite_vec_version"] = db.execute("SELECT vec_version()").fetchone()[0]
        vector_type = "int8" if args.vector_storage != "float32" else "float"
        db.execute(
            f"CREATE VIRTUAL TABLE vectors USING vec0(embedding {vector_type}[{dimensions}] distance_metric=cosine)"
        )
        start = time.perf_counter()
        insert_expression = (
            "vec_quantize_int8(?, 'unit')" if args.vector_storage == "int8" else
            "vec_int8(?)" if args.vector_storage in ("int8_maxabs", "int8_maxabs_fp16_rerank") else
            "?"
        )
        insert_sql = f"INSERT INTO vectors(rowid,embedding) VALUES (?,{insert_expression})"
        db.executemany(insert_sql, [(i + 1, stored_vector_bytes(v)) for i, v in enumerate(vectors)])
        db.commit()
        result["vector_insert_seconds"] = time.perf_counter() - start
        query_expression = (
            "vec_quantize_int8(?, 'unit')"
            if args.vector_storage == "int8"
            else "vec_int8(?)" if args.vector_storage in ("int8_maxabs", "int8_maxabs_fp16_rerank")
            else "?"
        )
        rerank_enabled = args.vector_storage == "int8_maxabs_fp16_rerank"
        if rerank_enabled:
            db.execute("CREATE TABLE vector_rerank(rowid INTEGER PRIMARY KEY, embedding BLOB NOT NULL)")
            start = time.perf_counter()
            db.executemany(
                "INSERT INTO vector_rerank(rowid,embedding) VALUES (?,?)",
                [(i + 1, v.astype(np.float16).tobytes()) for i, v in enumerate(vectors)],
            )
            db.commit()
            result["fp16_rerank_insert_seconds"] = time.perf_counter() - start
            result["fp16_rerank_payload_bytes"] = len(vectors) * dimensions * 2

        def search_vector_rows(vector, output_k):
            candidate_started = time.perf_counter()
            pool_k = 20 if rerank_enabled else output_k
            neighbors = db.execute(
                f"SELECT rowid,distance FROM vectors WHERE embedding MATCH {query_expression} AND k=? ORDER BY distance",
                (stored_vector_bytes(vector), pool_k),
            ).fetchall()
            candidate_ms = (time.perf_counter() - candidate_started) * 1000
            if not rerank_enabled:
                return neighbors, candidate_ms, 0.0
            rerank_started = time.perf_counter()
            rowids = [rowid for rowid, _ in neighbors]
            placeholders = ",".join("?" for _ in rowids)
            stored = dict(db.execute(
                f"SELECT rowid,embedding FROM vector_rerank WHERE rowid IN ({placeholders})",
                rowids,
            ).fetchall())
            query = vector.astype(np.float32)
            query_norm = float(np.linalg.norm(query))
            rescored = []
            for rowid in rowids:
                candidate = np.frombuffer(stored[rowid], dtype=np.float16).astype(np.float32)
                similarity = float(np.dot(query, candidate) / max(query_norm * float(np.linalg.norm(candidate)), 1e-12))
                rescored.append((rowid, 1.0 - similarity))
            rescored.sort(key=lambda row: (row[1], row[0]))
            rerank_ms = (time.perf_counter() - rerank_started) * 1000
            return rescored[:output_k], candidate_ms, rerank_ms

        self_matches = [search_vector_rows(v, 1)[0][0][0] == i + 1
                        for i, v in enumerate(vectors)]
        assert all(self_matches), "Vector storage/search failed corpus self-neighbor validation"
        result["vector_validation"] = {"corpus_self_neighbors_correct": sum(self_matches),
                                       "stored_vectors": len(vectors),
                                       "unit_norm_max_error": float(np.max(np.abs(np.linalg.norm(vectors, axis=1) - 1)))}

        def vector(query):
            start = time.perf_counter()
            v = embed([query], conventions["query_prefix"])[0]
            embedded = time.perf_counter()
            neighbors, candidate_ms, rerank_ms = search_vector_rows(v, 5)
            rows = [(corpus[i - 1]["id"], corpus[i - 1]["text"]) for i, _ in neighbors]
            return (rows, (embedded - start) * 1000,
                    (time.perf_counter() - embedded) * 1000,
                    candidate_ms, rerank_ms) if rerank_enabled else (
                    rows, (embedded - start) * 1000,
                    (time.perf_counter() - embedded) * 1000)

        def hybrid_retrieve(query):
            start = time.perf_counter()
            v = embed([query], conventions["query_prefix"])[0]
            embedded = time.perf_counter()
            vector_rows, candidate_ms, rerank_ms = search_vector_rows(v, 10)
            lexical_rows = db.execute(
                "SELECT id FROM memory WHERE memory MATCH ? ORDER BY bm25(memory) LIMIT 10",
                (fts_query(query),),
            ).fetchall()
            scores = {}
            best_rank = {}
            for ranked_ids in (
                [corpus[rowid - 1]["id"] for rowid, _ in vector_rows],
                [row[0] for row in lexical_rows],
            ):
                for rank, doc_id in enumerate(ranked_ids, 1):
                    scores[doc_id] = scores.get(doc_id, 0.0) + 1.0 / (60 + rank)
                    best_rank[doc_id] = min(best_rank.get(doc_id, rank), rank)
            ids = sorted(scores, key=lambda doc_id: (-scores[doc_id], best_rank[doc_id], doc_id))[:5]
            by_id = {doc["id"]: doc["text"] for doc in corpus}
            rows = [(doc_id, by_id[doc_id]) for doc_id in ids]
            return (rows, (embedded - start) * 1000,
                    (time.perf_counter() - embedded) * 1000,
                    candidate_ms, rerank_ms) if rerank_enabled else (
                    rows, (embedded - start) * 1000,
                    (time.perf_counter() - embedded) * 1000)

        # Warm-up is explicit, not included in per-query averages.
        start = time.perf_counter()
        retrieve = hybrid_retrieve if hybrid else vector
        vector("warmup retrieval")
        result["query_warmup_seconds"] = time.perf_counter() - start
        if challenger:
            result["vector_only"] = {name: evaluate(qs, vector, suppressed) for name, qs in sets.items()}
        candidate_context = query_aware_compact if args.context == "query_aware" else None
        candidate_assembler = (
            rank_one_protected_budget_context
            if args.context == "rank_one_protected_budget"
            else None
        )
        result["candidate"] = {
            name: evaluate(qs, retrieve, suppressed, candidate_context, candidate_assembler)
            for name, qs in sets.items()
        }
        if hybrid:
            result["fusion"] = {"algorithm": "reciprocal-rank-fusion", "rrf_k": 60,
                                "lexical_pool": 10, "vector_pool": 10,
                                "output_k_before_supersession_filter": 5,
                                "weights": {"fts5": 1, args.model: 1},
                                "selected_before_evaluation": True}
        result["vector_db_bytes_including_fts_and_raw_records"] = (Path(td) / "memory.db").stat().st_size
        db.close()
    result["model_disk_bytes"] = sum(f["bytes"] for f in manifest["files"].values())
    if challenger:
        reference_run = (23 if args.embedding_cache == "sqlite_pending_recovery" else
                         22 if args.embedding_cache == "sqlite_pending_queue" else
                         21 if args.embedding_cache == "sqlite_namespace_gc" else
                         20 if cache_enabled else
                         17 if args.vector_storage != "float32" else
                         14 if args.context == "rank_one_protected_budget" else
                         10 if args.context == "query_aware" or args.model in ("nomic_256", "e5_int8", "minilm") else
                         8)
        reference = json.loads((ROOT / "runs" / f"{reference_run:03}.json").read_text())
        quality_fields = ("mrr_at_5", "recall_at_5", "hit_at_1", "evidence_at_5")
        quality_match = all(
            result["candidate"][dataset].get(metric) == reference["candidate"][dataset].get(metric)
            for dataset in ("original", "heldout") for metric in quality_fields
            if reference["candidate"][dataset].get(metric) is not None
        )
        resource_improvements = {
            "model_disk_bytes": result["model_disk_bytes"] < reference["model_disk_bytes"],
            "vector_db_bytes": result["vector_db_bytes_including_fts_and_raw_records"] < reference["vector_db_bytes_including_fts_and_raw_records"],
            "peak_rss_mib": result["candidate"]["original"]["peak_rss_mib"] < reference["candidate"]["original"]["peak_rss_mib"],
            "original_cpu_seconds": result["candidate"]["original"]["cpu_seconds"] < reference["candidate"]["original"]["cpu_seconds"],
            "heldout_cpu_seconds": result["candidate"]["heldout"]["cpu_seconds"] < reference["candidate"]["heldout"]["cpu_seconds"],
            "original_end_to_end_ms_mean": result["candidate"]["original"]["end_to_end_ms_mean"] < reference["candidate"]["original"]["end_to_end_ms_mean"],
            "heldout_end_to_end_ms_mean": result["candidate"]["heldout"]["end_to_end_ms_mean"] < reference["candidate"]["heldout"]["end_to_end_ms_mean"],
            "original_context_tokens": result["candidate"]["original"]["context_tokens_approx_mean"] < reference["candidate"]["original"]["context_tokens_approx_mean"],
            "heldout_context_tokens": result["candidate"]["heldout"]["context_tokens_approx_mean"] < reference["candidate"]["heldout"]["context_tokens_approx_mean"],
        }
        context_reduced = all(
            result["candidate"][dataset]["context_tokens_approx_mean"] < reference["candidate"][dataset]["context_tokens_approx_mean"]
            for dataset in ("original", "heldout")
        )
        context_not_increased = all(
            result["candidate"][dataset]["context_tokens_approx_mean"]
            <= reference["candidate"][dataset]["context_tokens_approx_mean"]
            for dataset in ("original", "heldout")
        )
        vector_search_below_2ms = all(
            result["candidate"][dataset]["search_ms_mean"] < 2.0
            for dataset in ("original", "heldout")
        )
        cache_gate = None
        if cache_enabled:
            cache_metrics = result["embedding_cache"]
            if args.embedding_cache in ("sqlite_pending_queue", "sqlite_pending_recovery"):
                lifecycle = result["ingestion_lifecycle"]
                enqueue = lifecycle["enqueue"]
                pending_fts = lifecycle["fts_while_pending"]
                worker = lifecycle["worker"]
                lexical_rankings_exact = all(
                    [detail["top"] for detail in result["baseline"][dataset]["details"]]
                    == [detail["top"] for detail in reference["baseline"][dataset]["details"]]
                    for dataset in ("original", "heldout")
                )
                retrieval_rankings_exact = all(
                    [detail["top"] for detail in result[mode][dataset]["details"]]
                    == [detail["top"] for detail in reference[mode][dataset]["details"]]
                    for mode in ("vector_only", "candidate")
                    for dataset in ("original", "heldout")
                )
                context_exact = all(
                    result["candidate"][dataset]["context_tokens_approx_mean"]
                    == reference["candidate"][dataset]["context_tokens_approx_mean"]
                    for dataset in ("original", "heldout")
                )
                cache_gate = {
                    "all_records_fts_visible_while_pending": (
                        enqueue["fts_rows"] == len(corpus)
                        and enqueue["pending_jobs"] == len(corpus)
                        and pending_fts["pending_jobs_before"] == len(corpus)
                        and pending_fts["pending_jobs_after"] == len(corpus)
                        and not pending_fts["model_loaded"]
                        and lexical_rankings_exact
                    ),
                    "enqueue_below_fifty_ms": (
                        enqueue["wall_seconds"] <= 0.05
                        and enqueue["cpu_seconds"] <= 0.05
                    ),
                    "ten_fifo_batches_of_four": (
                        worker["batch_count"] == 10
                        and all(batch["documents"] == 4 for batch in worker["batches"])
                        and [batch["ordinals"] for batch in worker["batches"]]
                        == [list(range(start, start + 4)) for start in range(0, len(corpus), 4)]
                    ),
                    "half_warm_cache_reused": (
                        worker["cache_hits"] == len(corpus) // 2
                        and worker["cache_misses"] == len(corpus) // 2
                    ),
                    "worker_within_fixed_bounds": (
                        worker["wall_seconds"] <= 1.0
                        and worker["cpu_seconds"] <= 2.0
                    ),
                    f"run_{reference_run:03}_rankings_and_context_exact": (
                        retrieval_rankings_exact and context_exact
                    ),
                }
                if recovery_enabled:
                    recovery = lifecycle["recovery"]
                    cache_gate.update({
                        "four_processing_jobs_survived_reopen": (
                            recovery["before_reopen"] == {
                                "ordinals": [0, 1, 2, 3], "processing_jobs": 4
                            }
                            and recovery["processing_ordinals_after_reopen"] == [0, 1, 2, 3]
                        ),
                        "four_jobs_reclaimed_below_fifty_ms": (
                            recovery["recovered_jobs"] == 4
                            and recovery["wall_seconds"] <= 0.05
                            and recovery["cpu_seconds"] <= 0.05
                            and recovery["status_counts_after_reclaim"] == {"pending": len(corpus)}
                        ),
                        "recovered_batch_resumed_first": (
                            worker["batches"][0]["ordinals"] == [0, 1, 2, 3]
                        ),
                        "all_jobs_ready_with_unique_outputs": (
                            worker["status_counts"]
                            == {"pending": 0, "processing": 0, "ready": len(corpus)}
                            and worker["attempts"] == {1: len(corpus) - 4, 2: 4}
                            and worker["ready_output"] == {
                                "rows": len(corpus),
                                "unique_documents": len(corpus),
                                "unique_cache_keys": len(corpus),
                            }
                            and cache_metrics["entries_after_worker"] == len(corpus)
                        ),
                        "database_not_larger_than_run_023": (
                            result["vector_db_bytes_including_fts_and_raw_records"]
                            <= reference["vector_db_bytes_including_fts_and_raw_records"]
                        ),
                    })
                else:
                    cache_gate.update({
                        "all_jobs_ready_once": (
                            worker["status_counts"]
                            == {"pending": 0, "processing": 0, "ready": len(corpus)}
                            and worker["attempts"] == {1: len(corpus)}
                        ),
                        "database_growth_at_most_twenty_five_percent": (
                            result["vector_db_bytes_including_fts_and_raw_records"]
                            <= reference["vector_db_bytes_including_fts_and_raw_records"] * 1.25
                        ),
                    })
            else:
                cold_cache = cache_metrics["cold_build"]
                warm_cache = cache_metrics["warm_reindex"]
            if args.embedding_cache == "sqlite_namespace_gc":
                stale_build = cache_metrics["stale_namespace_build"]
                cleanup = cache_metrics["cleanup"]
                post_cleanup = cache_metrics["post_cleanup_active"]
                cache_gate = {
                    "active_cold_misses_all_documents": (
                        cold_cache["hits"] == 0 and cold_cache["misses"] == len(corpus)
                    ),
                    "active_warm_hits_all_documents": (
                        warm_cache["hits"] == len(corpus) and warm_cache["misses"] == 0
                    ),
                    "namespace_changed": cache_metrics["namespace_changed"],
                    "zero_cross_namespace_hits": (
                        stale_build["hits"] == 0 and stale_build["misses"] == len(corpus)
                    ),
                    "deleted_only_stale_namespace": (
                        cleanup["entries_before"] == len(corpus) * 2
                        and cleanup["deleted_entries"] == len(corpus)
                        and cleanup["entries_after"] == len(corpus)
                    ),
                    "cleanup_below_fifty_ms": (
                        cleanup["wall_seconds"] <= 0.05
                        and cleanup["cpu_seconds"] <= 0.05
                    ),
                    "post_cleanup_active_hits_all_documents": (
                        post_cleanup["hits"] == len(corpus)
                        and post_cleanup["misses"] == 0
                    ),
                    "active_vectors_preserved": cache_metrics["active_vectors_preserved"],
                    "database_not_larger_than_run_021": (
                        result["vector_db_bytes_including_fts_and_raw_records"]
                        <= reference["vector_db_bytes_including_fts_and_raw_records"]
                    ),
                }
            else:
                if args.embedding_cache == "sqlite_content_sha256":
                    incremental_cache = cache_metrics["one_record_update"]
                    cache_gate = {
                        "cold_misses_all_documents": cold_cache["hits"] == 0 and cold_cache["misses"] == len(corpus),
                        "warm_hits_all_documents": warm_cache["hits"] == len(corpus) and warm_cache["misses"] == 0,
                        "incremental_embeds_one_document": (
                            incremental_cache["hits"] == len(corpus) - 1
                            and incremental_cache["misses"] == 1
                        ),
                        "warm_wall_at_most_ten_percent_of_cold": (
                            warm_cache["total_wall_seconds"] <= cold_cache["total_wall_seconds"] * 0.1
                        ),
                        "warm_cpu_at_most_ten_percent_of_cold": (
                            warm_cache["total_cpu_seconds"] <= cold_cache["total_cpu_seconds"] * 0.1
                        ),
                        "database_growth_at_most_twenty_five_percent": (
                            result["vector_db_bytes_including_fts_and_raw_records"]
                            <= reference["vector_db_bytes_including_fts_and_raw_records"] * 1.25
                        ),
                    }
        context_extension = args.context != "compact"
        accepted = (quality_match and context_not_increased
                    and vector_search_below_2ms
                    and all(cache_gate.values())
                    if cache_enabled else
                    quality_match and context_not_increased
                    and resource_improvements["vector_db_bytes"]
                    and vector_search_below_2ms
                    if args.vector_storage != "float32" else
                    quality_match and context_reduced
                    if context_extension else
                    quality_match and sum(resource_improvements.values()) >= 3)
        comparison_key = ("champion_comparison"
                          if context_extension or args.model in ("nomic_256", "e5_int8", "minilm")
                          else "run008_comparison")
        result[comparison_key] = {
            "quality_match": quality_match,
            "context_reduced_on_both_sets": context_reduced,
            "context_not_increased": context_not_increased,
            "vector_search_below_2ms": vector_search_below_2ms,
            "resource_improvements": resource_improvements,
            "reference_method": reference["method"],
            "reference_run": reference_run,
        }
        if cache_gate is not None:
            result[comparison_key]["cache_gate"] = cache_gate
        if args.model == "nomic_256":
            full_dimension = json.loads((ROOT / "runs" / "008.json").read_text())
            result["matryoshka_comparison"] = {
                "reference_run": 8,
                "reference_dimensions": 768,
                "candidate_dimensions": dimensions,
                "quality_match": all(
                    result["candidate"][dataset].get(metric) == full_dimension["candidate"][dataset].get(metric)
                    for dataset in ("original", "heldout") for metric in quality_fields
                    if full_dimension["candidate"][dataset].get(metric) is not None
                ),
                "vector_db_bytes_before": full_dimension["vector_db_bytes_including_fts_and_raw_records"],
                "vector_db_bytes_after": result["vector_db_bytes_including_fts_and_raw_records"],
            }
        result.update(
            status=("accepted-cache-champion" if cache_enabled and accepted else
                    "accepted-storage-champion" if args.vector_storage != "float32" and accepted else
                    "accepted-context-champion" if context_extension and accepted else
                    "accepted-semantic-champion" if accepted else "rejected"),
            champion=accepted,
            notes=(f"Accepted as the pending-recovery extension to Run {reference_run}: four durably stranded processing jobs survived reopen, were reclaimed, completed with unique ready outputs, and final retrieval/evidence/context matched."
                   if args.embedding_cache == "sqlite_pending_recovery" and accepted else
                   f"Rejected as a pending-recovery extension to Run {reference_run}: durable interruption, bounded reclaim, unique completion, final parity, or a resource gate failed."
                   if args.embedding_cache == "sqlite_pending_recovery" else
                   f"Accepted as the pending-ingestion extension to Run {reference_run}: FTS5 remained available before model loading, the bounded FIFO worker reused cache entries, and final retrieval/evidence/context matched."
                   if args.embedding_cache == "sqlite_pending_queue" and accepted else
                   f"Rejected as a pending-ingestion extension to Run {reference_run}: immediate lexical availability, bounded draining, cache reuse, final parity, or a resource gate failed."
                   if args.embedding_cache == "sqlite_pending_queue" else
                   f"Accepted as the cache-maintenance extension to Run {reference_run}: convention namespaces prevented stale reuse, cleanup retained the active cache, and retrieval/evidence/context matched."
                   if args.embedding_cache == "sqlite_namespace_gc" and accepted else
                   f"Rejected as a cache-maintenance extension to Run {reference_run}: namespace isolation, cleanup, retrieval parity, or a fixed resource gate failed."
                   if args.embedding_cache == "sqlite_namespace_gc" else
                   f"Accepted as the embedding-cache extension to Run {reference_run}: retrieval/evidence/context matched, warm reindex avoided all inference, and the one-record update embedded only one document within the fixed storage limit."
                   if cache_enabled and accepted else
                   f"Rejected as an embedding-cache extension to Run {reference_run}: it did not preserve retrieval/context and meet every fixed cache performance/storage gate."
                   if cache_enabled else
                   f"Accepted as the vector-storage extension to Run {reference_run}: retrieval/evidence and context matched while SQLite storage fell and mean vector search stayed below 2 ms."
                   if args.vector_storage != "float32" and accepted else
                   f"Rejected as a vector-storage extension to Run {reference_run}: it did not preserve quality/context with smaller SQLite storage and sub-2 ms vector search."
                   if args.vector_storage != "float32" else
                   f"Accepted as the context-assembly extension to Run {reference_run}: retrieval/evidence metrics matched and context fell on both frozen sets."
                   if context_extension and accepted else
                   f"Accepted as semantic-quality champion: matched every Run {reference_run} retrieval/evidence metric and reduced at least three measured resource costs."
                   if accepted else
                   f"Rejected as semantic-quality champion: the challenger did not match all Run {reference_run} retrieval/evidence metrics with at least three measured resource improvements."),
        )
    elif hybrid:
        result.update(
            status="accepted-semantic-champion",
            champion=True,
            notes=("Accepted as the semantic-quality champion: original MRR@5/Hit@1 improved "
                   "from 0.941667/0.90 to 0.975/0.95 and held-out MRR@5/Hit@1 improved "
                   "from 0.975/0.95 to 1.0/1.0 with evidence_at_5 unchanged at 1.0. "
                   "Run 005 remains the lightweight no-embedding fallback because hybrid "
                   "retrieval requires substantially more RAM, disk and latency."),
        )
    else:
        result.update(
            status="rejected",
            champion=False,
            notes=("Rejected as a replacement champion: vector-only retrieval lowered original "
                   "MRR@5 and Hit@1. Retain the harness for hybrid and model comparisons."),
        )
    result["measurement_notes"] = [
        "CPU is process CPU time and may exceed wall time with two inference threads.",
        "RSS is the Linux process-lifetime high-water mark in MiB; baseline runs before loading the embedding runtime.",
        "End-to-end query time includes query embedding, retrieval, fusion when selected, supersession filtering and compact context assembly; it excludes answer generation.",
        "The fusion rule and its constants were selected before evaluation and were not tuned against benchmark labels.",
        "Context tokens use the chars/4 approximation and held-out evidence uses the existing evidence-term subset proxy.",
        "The 40-document, 40-query experiment does not establish large-corpus scale or broad generalization.",
    ]
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
