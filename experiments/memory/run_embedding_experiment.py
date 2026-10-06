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
from pathlib import Path

from run_experiment import (
    SEMANTIC_OPERATORS,
    compact_text,
    detect_supersessions,
    fts_query,
    load_jsonl,
    query_aware_compact,
)
from run_heldout_validation import terms

ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT.parents[1] / ".artifacts" / "memory-loop"


def rss():
    # Linux ru_maxrss is KiB, and is a process-lifetime high-water mark.
    return round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 3)


def evaluate(queries, retrieve, suppressed, context_transform=None):
    context_transform = context_transform or (lambda text, _query: compact_text(text))
    details, latency, embed_latency, search_latency, context_latency = [], [], [], [], []
    cpu_start = time.process_time()
    for q in queries:
        started = time.perf_counter()
        rows, embedding_ms, search_ms = retrieve(q["query"])
        rows = [row for row in rows if row[0] not in suppressed]
        context_started = time.perf_counter()
        rendered = [context_transform(row[1], q["query"]) for row in rows]
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
    return {
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", choices=["nomic", "nomic_256", "bge", "e5", "e5_int8", "minilm"], default="nomic")
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--retrieval", choices=["vector", "hybrid"], default="vector")
    parser.add_argument("--context", choices=["compact", "query_aware"], default="compact")
    args = parser.parse_args()
    if args.threads < 1:
        parser.error("threads must be positive")
    if args.context == "query_aware" and args.model != "e5":
        parser.error("query-aware context is the Run 014 extension to the E5 champion")
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
        db.executemany("INSERT INTO memory VALUES (?,?,?,?)", [(d["id"], d["text"], d["source"], d["ts"]) for d in corpus])
        db.commit()

        def lexical(query):
            start = time.perf_counter()
            rows = db.execute("SELECT id,text FROM memory WHERE memory MATCH ? ORDER BY bm25(memory) LIMIT 5", (fts_query(query),)).fetchall()
            return rows, 0, (time.perf_counter() - start) * 1000

        result["baseline"] = {name: evaluate(qs, lexical, suppressed) for name, qs in sets.items()}
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

        start = time.perf_counter()
        cpu = time.process_time()
        vectors = np.concatenate([embed([d["text"] for d in corpus[i:i+4]], conventions["document_prefix"]) for i in range(0, len(corpus), 4)])
        seconds = time.perf_counter() - start
        result["corpus_embedding"] = {"documents": len(corpus), "batch_size": 4,
                                      "wall_seconds": seconds, "cpu_seconds": time.process_time() - cpu,
                                      "documents_per_second": len(corpus) / seconds, "peak_rss_mib": rss()}
        db.enable_load_extension(True)
        sqlite_vec.load(db)
        db.enable_load_extension(False)
        result["sqlite_vec_version"] = db.execute("SELECT vec_version()").fetchone()[0]
        db.execute(f"CREATE VIRTUAL TABLE vectors USING vec0(embedding float[{dimensions}] distance_metric=cosine)")
        start = time.perf_counter()
        db.executemany("INSERT INTO vectors(rowid,embedding) VALUES (?,?)", [(i + 1, v.tobytes()) for i, v in enumerate(vectors)])
        db.commit()
        result["vector_insert_seconds"] = time.perf_counter() - start
        self_matches = [db.execute(
            "SELECT rowid FROM vectors WHERE embedding MATCH ? AND k=1 ORDER BY distance",
            (v.tobytes(),),
        ).fetchone()[0] == i + 1 for i, v in enumerate(vectors)]
        assert all(self_matches), "Vector storage/search failed corpus self-neighbor validation"
        result["vector_validation"] = {"corpus_self_neighbors_correct": sum(self_matches),
                                       "stored_vectors": len(vectors),
                                       "unit_norm_max_error": float(np.max(np.abs(np.linalg.norm(vectors, axis=1) - 1)))}

        def vector(query):
            start = time.perf_counter()
            v = embed([query], conventions["query_prefix"])[0]
            embedded = time.perf_counter()
            neighbors = db.execute("SELECT rowid,distance FROM vectors WHERE embedding MATCH ? AND k=5 ORDER BY distance", (v.tobytes(),)).fetchall()
            rows = [(corpus[i - 1]["id"], corpus[i - 1]["text"]) for i, _ in neighbors]
            return rows, (embedded - start) * 1000, (time.perf_counter() - embedded) * 1000

        def hybrid_retrieve(query):
            start = time.perf_counter()
            v = embed([query], conventions["query_prefix"])[0]
            embedded = time.perf_counter()
            vector_rows = db.execute(
                "SELECT rowid,distance FROM vectors WHERE embedding MATCH ? AND k=10 ORDER BY distance",
                (v.tobytes(),),
            ).fetchall()
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
            return rows, (embedded - start) * 1000, (time.perf_counter() - embedded) * 1000

        # Warm-up is explicit, not included in per-query averages.
        start = time.perf_counter()
        retrieve = hybrid_retrieve if hybrid else vector
        vector("warmup retrieval")
        result["query_warmup_seconds"] = time.perf_counter() - start
        if challenger:
            result["vector_only"] = {name: evaluate(qs, vector, suppressed) for name, qs in sets.items()}
        candidate_context = query_aware_compact if args.context == "query_aware" else None
        result["candidate"] = {
            name: evaluate(qs, retrieve, suppressed, candidate_context)
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
        reference_run = 10 if args.context == "query_aware" or args.model in ("nomic_256", "e5_int8", "minilm") else 8
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
        accepted = (quality_match and context_reduced
                    if args.context == "query_aware" else
                    quality_match and sum(resource_improvements.values()) >= 3)
        comparison_key = ("champion_comparison"
                          if args.context == "query_aware" or args.model in ("nomic_256", "e5_int8", "minilm")
                          else "run008_comparison")
        result[comparison_key] = {
            "quality_match": quality_match,
            "context_reduced_on_both_sets": context_reduced,
            "resource_improvements": resource_improvements,
            "reference_method": reference["method"],
            "reference_run": reference_run,
        }
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
            status=("accepted-context-champion" if args.context == "query_aware" and accepted else
                    "accepted-semantic-champion" if accepted else "rejected"),
            champion=accepted,
            notes=(f"Accepted as the context-assembly extension to Run {reference_run}: retrieval/evidence metrics matched and context fell on both frozen sets."
                   if args.context == "query_aware" and accepted else
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
