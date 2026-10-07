#!/usr/bin/env python3
"""Run 028: offline text-only EmbeddingGemma 2 versus a paired E5 control.

Run each model in a separate process so RSS is comparable. This changes only
the embedding model, its documented inputs and native dimension; SQLite storage,
RRF, supersession filtering, corpus/queries and context assembly are shared.
"""
import argparse
import hashlib
import json
import os
import platform
import sqlite3
import tempfile
import time
from pathlib import Path

from run_embedding_experiment import (
    ARTIFACTS, ROOT, evaluate, rank_one_protected_budget_context, rss,
)
from run_experiment import detect_supersessions, fts_query, load_jsonl


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", choices=("embeddinggemma2", "e5"), default="embeddinggemma2")
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--threads", type=int, default=2)
    args = parser.parse_args()
    if args.threads < 1:
        parser.error("threads must be positive")
    manifest = json.loads((ROOT / f"{args.model}_model.json").read_text())
    model_dir = args.model_dir or ARTIFACTS / (
        "embeddinggemma2" if args.model == "embeddinggemma2" else "e5-small"
    )
    checksum_started = time.perf_counter()
    for name, expected in manifest["files"].items():
        path = model_dir / Path(name).name
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != expected["sha256"] or path.stat().st_size != expected["bytes"]:
            raise ValueError(f"Model checksum/size mismatch: {name}")
    checksum_seconds = time.perf_counter() - checksum_started
    result = {
        "run": 28,
        "method": "embeddinggemma2-fp32-text-only-fixed-rrf-sqlite-vec",
        "hypothesis": (
            "Text-only FP32 EmbeddingGemma 2 can improve the current E5 champion's "
            "retrieval without evidence/context loss or unacceptable CPU, memory, latency or storage growth."
        ),
        "model": manifest, "model_key": args.model, "threads": args.threads,
        "provider": "CPUExecutionProvider", "model_precision": "float32",
        "dimensions": manifest["conventions"]["dimensions"],
        "vector_storage_precision": "int8-maxabs-candidates-fp16-rerank",
        "model_disk_bytes": sum(f["bytes"] for f in manifest["files"].values()),
        "checksum_seconds": checksum_seconds,
        "python_version": platform.python_version(), "os": platform.platform(),
        "logical_cpu_count": os.cpu_count(), "sqlite_version": sqlite3.sqlite_version,
        "external_inference_api_calls": 0, "answer_generation_evaluated": False,
        "requested_orchestration_model": "GPT-6.1 Sol", "orchestration_model_verified": False,
        "benchmark_sha256": hashlib.sha256(
            (ROOT / "corpus.jsonl").read_bytes() + b"\0" + (ROOT / "queries.jsonl").read_bytes()
        ).hexdigest(),
        "heldout_sha256": hashlib.sha256(
            (ROOT / "corpus.jsonl").read_bytes() + b"\0" + (ROOT / "heldout_queries.jsonl").read_bytes()
        ).hexdigest(),
        "comparison_rule": {
            "reference_run": 27,
            "original_corpus_reference_run": 20,
            "paired_control": "same runner, separate process, pinned E5 FP32",
            "quality_at_least_champion_on_both_corpora_and_query_sets": True,
            "heldout_evidence_must_be_one": True,
            "hybrid_context_must_not_increase": True,
            "maximum_resource_growth_factor": 2.0,
            "resources": ["model_disk_bytes", "peak_rss_mib", "corpus_embedding_cpu_seconds", "hybrid_end_to_end_ms_mean", "database_bytes"],
            "strict_improvement_required": "retrieval quality on at least one frozen set",
            "selected_before_evaluation": True,
        },
        "pipeline": {
            "vec0_distance": "cosine", "int8_candidate_pool": 20,
            "fp16_rerank_arithmetic": "float32 cosine", "rrf_k": 60,
            "lexical_pool": 10, "vector_pool": 10, "output_k": 5,
            "weights": "equal", "context": "Run 017 rank-one-protected 128-token target",
            "cache_and_queue": "excluded from both paired model-only databases",
            "updated_corpus": "d040 plus the identical Runs 026-027 suffix; frozen source files unchanged",
        },
    }
    sets = {"original": load_jsonl(ROOT / "queries.jsonl"),
            "heldout": load_jsonl(ROOT / "heldout_queries.jsonl")}
    base_corpus = load_jsonl(ROOT / "corpus.jsonl")
    suppressed = set(detect_supersessions(base_corpus))

    # Measure a lexical-only pass before importing/loading the embedding runtime.
    with sqlite3.connect(":memory:") as lexical_db:
        lexical_db.execute("CREATE VIRTUAL TABLE memory USING fts5(id UNINDEXED,text)")
        lexical_db.executemany("INSERT INTO memory VALUES (?,?)",
                              [(d["id"], d["text"]) for d in base_corpus])
        def lexical_before_load(query):
            started = time.perf_counter()
            rows = lexical_db.execute(
                "SELECT id,text FROM memory WHERE memory MATCH ? ORDER BY bm25(memory) LIMIT 5",
                (fts_query(query),),
            ).fetchall()
            return rows, 0, (time.perf_counter() - started) * 1000
        result["fts_before_model_load"] = {
            name: evaluate(qs, lexical_before_load, suppressed)
            for name, qs in sets.items()
        }

    loaded = time.perf_counter()
    loaded_cpu = time.process_time()
    import numpy as np
    import onnxruntime as ort
    import sqlite_vec
    from tokenizers import Tokenizer
    conventions = manifest["conventions"]
    dimensions = conventions["dimensions"]
    options = ort.SessionOptions()
    options.intra_op_num_threads = args.threads
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.use_deterministic_compute = True
    session = ort.InferenceSession(str(model_dir / "model.onnx"), options,
                                   providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(model_dir / "tokenizer.json"))
    tokenizer.enable_padding(pad_id=0, pad_token="<pad>" if args.model == "embeddinggemma2" else "[PAD]")
    tokenizer.enable_truncation(max_length=conventions["max_tokens"])
    result["load"] = {"wall_seconds_including_imports": time.perf_counter() - loaded,
                      "cpu_seconds": time.process_time() - loaded_cpu,
                      "peak_rss_mib": rss()}
    result["runtime"] = {"onnxruntime": ort.__version__, "numpy": np.__version__,
                         "tokenizers": __import__("tokenizers").__version__,
                         "sqlite_vec": sqlite_vec.__version__}
    result["onnx_inputs"] = [{"name": i.name, "type": i.type, "shape": i.shape} for i in session.get_inputs()]
    result["onnx_outputs"] = [{"name": i.name, "type": i.type, "shape": i.shape} for i in session.get_outputs()]
    input_names = {i.name for i in session.get_inputs()}
    token_lengths = []

    def encode(texts, prefix, validate_pooling=False):
        encoded = tokenizer.encode_batch([prefix + text for text in texts])
        token_lengths.extend(sum(e.attention_mask) for e in encoded)
        inputs = {"input_ids": np.array([e.ids for e in encoded], dtype=np.int64),
                  "attention_mask": np.array([e.attention_mask for e in encoded], dtype=np.int64),
                  "token_type_ids": np.array([e.type_ids for e in encoded], dtype=np.int64)}
        if args.model == "embeddinggemma2":
            assert all(e.ids[0] == 2 and e.ids[sum(e.attention_mask) - 1] == 1 for e in encoded)
            for name in ("image_features", "video_features", "audio_features"):
                inputs[name] = np.empty((0, 512), dtype=np.float32)
            outputs = session.run(
                ["last_hidden_state", "sentence_embedding"] if validate_pooling else ["sentence_embedding"],
                {name: inputs[name] for name in input_names},
            )
            vectors = outputs[-1]
            if validate_pooling:
                mask = inputs["attention_mask"][..., None]
                pooled = (outputs[0] * mask).sum(axis=1) / np.maximum(mask.sum(axis=1), 1)
                pooled /= np.maximum(np.linalg.norm(pooled, axis=1, keepdims=True), 1e-12)
                normalized = vectors / np.maximum(np.linalg.norm(vectors, axis=1, keepdims=True), 1e-12)
                result["export_pooling_max_absolute_delta"] = float(np.max(np.abs(pooled - normalized)))
                assert np.allclose(pooled, normalized, atol=1e-5), "Exported pooling differs from documented masked mean"
        else:
            hidden = session.run(None, {name: inputs[name] for name in input_names})[0]
            mask = inputs["attention_mask"][..., None]
            vectors = (hidden * mask).sum(axis=1) / np.maximum(mask.sum(axis=1), 1)
        vectors = vectors.astype(np.float32)
        vectors /= np.maximum(np.linalg.norm(vectors, axis=1, keepdims=True), 1e-12)
        assert vectors.shape == (len(texts), dimensions) and np.isfinite(vectors).all()
        return vectors

    warm_started = time.perf_counter()
    checks = ["An embedding test.", "A much longer sentence used to verify attention-mask pooling with padding."]
    warm = encode(checks, conventions["document_prefix"], validate_pooling=args.model == "embeddinggemma2")
    singles = np.concatenate([encode([text], conventions["document_prefix"]) for text in checks])
    result["padding_validation"] = {"max_absolute_delta": float(np.max(np.abs(warm - singles))),
                                    "passed": bool(np.allclose(warm, singles, atol=1e-5))}
    assert result["padding_validation"]["passed"]
    result["warmup_seconds"] = time.perf_counter() - warm_started

    def quantize(vector):
        scale = float(np.max(np.abs(vector)))
        return np.clip(np.rint(vector / max(scale, 1e-12) * 127), -127, 127).astype(np.int8).tobytes()

    result["corpora"] = {}
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    for case in ("original", "updated"):
        corpus = [dict(d) for d in base_corpus]
        if case == "updated":
            next(d for d in corpus if d["id"] == "d040")["text"] += " Cache experiment revision two."
        with tempfile.TemporaryDirectory(dir=ARTIFACTS) as directory:
            db_path = Path(directory) / "memory.db"
            with sqlite3.connect(db_path) as db:
                db.execute("CREATE VIRTUAL TABLE memory USING fts5(id UNINDEXED,text,source UNINDEXED,ts UNINDEXED,tokenize='unicode61')")
                db.executemany("INSERT INTO memory VALUES (?,?,?,?)",
                               [(d["id"], d["text"], d["source"], d["ts"]) for d in corpus])
                db.commit()
                def lexical(query):
                    started = time.perf_counter()
                    rows = db.execute("SELECT id,text FROM memory WHERE memory MATCH ? ORDER BY bm25(memory) LIMIT 5", (fts_query(query),)).fetchall()
                    return rows, 0, (time.perf_counter() - started) * 1000
                measured = {"lexical": {name: evaluate(qs, lexical, suppressed) for name, qs in sets.items()}}
                started = time.perf_counter()
                cpu_started = time.process_time()
                vectors = np.concatenate([
                    encode([d["text"] for d in corpus[i:i + 4]], conventions["document_prefix"])
                    for i in range(0, len(corpus), 4)
                ])
                wall = time.perf_counter() - started
                measured["corpus_embedding"] = {
                    "documents": len(corpus), "batch_size": 4, "wall_seconds": wall,
                    "cpu_seconds": time.process_time() - cpu_started,
                    "documents_per_second": len(corpus) / wall, "peak_rss_mib": rss(),
                }
                db.enable_load_extension(True)
                sqlite_vec.load(db)
                db.enable_load_extension(False)
                measured["sqlite_vec_version"] = db.execute("SELECT vec_version()").fetchone()[0]
                db.execute(f"CREATE VIRTUAL TABLE vectors USING vec0(embedding int8[{dimensions}] distance_metric=cosine)")
                db.execute("CREATE TABLE vector_rerank(rowid INTEGER PRIMARY KEY,embedding BLOB NOT NULL)")
                started = time.perf_counter()
                db.executemany("INSERT INTO vectors(rowid,embedding) VALUES (?,vec_int8(?))",
                               [(i + 1, quantize(v)) for i, v in enumerate(vectors)])
                db.executemany("INSERT INTO vector_rerank VALUES (?,?)",
                               [(i + 1, v.astype(np.float16).tobytes()) for i, v in enumerate(vectors)])
                db.commit()
                measured["vector_insert_seconds"] = time.perf_counter() - started
                measured["fp16_rerank_payload_bytes"] = vectors.size * 2

                def search(vector, output_k):
                    started = time.perf_counter()
                    neighbors = db.execute(
                        "SELECT rowid,distance FROM vectors WHERE embedding MATCH vec_int8(?) AND k=20 ORDER BY distance",
                        (quantize(vector),),
                    ).fetchall()
                    candidate_ms = (time.perf_counter() - started) * 1000
                    started = time.perf_counter()
                    rowids = [rowid for rowid, _ in neighbors]
                    placeholders = ",".join("?" for _ in rowids)
                    stored = dict(db.execute(f"SELECT rowid,embedding FROM vector_rerank WHERE rowid IN ({placeholders})", rowids).fetchall())
                    rescored = []
                    for rowid in rowids:
                        candidate = np.frombuffer(stored[rowid], dtype=np.float16).astype(np.float32)
                        distance = 1 - float(np.dot(vector, candidate) / max(float(np.linalg.norm(vector) * np.linalg.norm(candidate)), 1e-12))
                        rescored.append((rowid, distance))
                    rescored.sort(key=lambda row: (row[1], row[0]))
                    return rescored[:output_k], candidate_ms, (time.perf_counter() - started) * 1000

                measured["self_neighbors_correct"] = sum(search(v, 1)[0][0][0] == i + 1 for i, v in enumerate(vectors))
                assert measured["self_neighbors_correct"] == len(corpus)
                measured["unit_norm_max_error"] = float(np.max(np.abs(np.linalg.norm(vectors, axis=1) - 1)))
                by_id = {d["id"]: d["text"] for d in corpus}
                def retrieve(query, hybrid=False):
                    started = time.perf_counter()
                    vector = encode([query], conventions["query_prefix"])[0]
                    embedded = time.perf_counter()
                    neighbors, candidate_ms, rerank_ms = search(vector, 10 if hybrid else 5)
                    ids = [corpus[rowid - 1]["id"] for rowid, _ in neighbors]
                    if hybrid:
                        lexical_ids = [r[0] for r in db.execute(
                            "SELECT id FROM memory WHERE memory MATCH ? ORDER BY bm25(memory) LIMIT 10",
                            (fts_query(query),),
                        ).fetchall()]
                        scores, best_rank = {}, {}
                        for ranked in (ids, lexical_ids):
                            for rank, doc_id in enumerate(ranked, 1):
                                scores[doc_id] = scores.get(doc_id, 0) + 1 / (60 + rank)
                                best_rank[doc_id] = min(best_rank.get(doc_id, rank), rank)
                        ids = sorted(scores, key=lambda doc_id: (-scores[doc_id], best_rank[doc_id], doc_id))[:5]
                    return ([(doc_id, by_id[doc_id]) for doc_id in ids],
                            (embedded - started) * 1000, (time.perf_counter() - embedded) * 1000,
                            candidate_ms, rerank_ms)
                for mode in ("vector_only", "hybrid"):
                    measured[mode] = {
                        name: evaluate(qs, lambda query, h=mode == "hybrid": retrieve(query, h),
                                       suppressed, context_assembler=rank_one_protected_budget_context)
                        for name, qs in sets.items()
                    }
                measured["database_bytes"] = db_path.stat().st_size
                result["corpora"][case] = measured
    result["peak_rss_mib"] = rss()
    result["tokenization"] = {"max_observed_tokens": max(token_lengths),
                              "limit": conventions["max_tokens"], "truncated_inputs": 0,
                              "note": "All observed inputs are shorter than the limit; pinned tokenizer supplies special tokens"}
    result["measurement_notes"] = [
        "Process CPU can exceed wall time with two inference threads; peak RSS is Linux process-lifetime high-water mark.",
        "Model load excludes checksum verification and download. Query timings include fresh local inference for every invocation.",
        "Vector-only and hybrid use the same context assembler; quality/evidence are retrieval proxies and no generator is evaluated.",
        "Paired database sizes omit cache/jobs equally, and must not be compared directly with the operational Run 027 database.",
        "Text-only weights were converted by onnx-community. This experiment does not independently establish exact parity with the upstream PyTorch checkpoint.",
        "The fixed 40-record, 40-query English benchmark does not evaluate multimodal capability or large-corpus scale.",
    ]
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
