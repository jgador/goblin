#!/usr/bin/env python3
"""Validate the Run 005 compact context on the frozen held-out evidence set."""
import hashlib
import json
import math
import resource
import sqlite3
import statistics
import tempfile
import time
from pathlib import Path

from run_experiment import WORD_RE, compact_text, detect_supersessions, fts_query, load_jsonl

ROOT = Path(__file__).resolve().parent


def terms(text):
    return {token.lower() for token in WORD_RE.findall(text)}


def evaluate(db, queries, suppressed_ids, compact):
    rr, recall, hit1, evidence, chars, latencies, details = [], [], [], [], [], [], []
    cpu_start = time.process_time()
    for query in queries:
        started = time.perf_counter()
        rows = db.execute(
            "SELECT id, text, bm25(memory) AS score FROM memory WHERE memory MATCH ? ORDER BY score LIMIT 5",
            (fts_query(query["query"]),),
        ).fetchall()
        rows = [row for row in rows if row[0] not in suppressed_ids]
        rendered = [compact_text(row[1]) if compact else row[1] for row in rows]
        latencies.append((time.perf_counter() - started) * 1000)
        ids = [row[0] for row in rows]
        relevant = set(query["relevant"])
        rank = next((i + 1 for i, doc_id in enumerate(ids) if doc_id in relevant), None)
        rr.append(0.0 if rank is None else 1.0 / rank)
        recall.append(len(relevant.intersection(ids)) / len(relevant))
        hit1.append(1.0 if ids and ids[0] in relevant else 0.0)
        preserved = set(query["evidence_terms"]).issubset(terms(" ".join(rendered)))
        evidence.append(1.0 if preserved else 0.0)
        size = sum(len(text) for text in rendered)
        chars.append(size)
        details.append({"query": query["id"], "top": ids, "first_relevant_rank": rank, "evidence_preserved": preserved, "context_chars": size})
    token_counts = [math.ceil(size / 4) for size in chars]
    return {
        "query_count": len(queries),
        "mrr_at_5": round(statistics.fmean(rr), 6),
        "recall_at_5": round(statistics.fmean(recall), 6),
        "hit_at_1": round(statistics.fmean(hit1), 6),
        "evidence_at_5": round(statistics.fmean(evidence), 6),
        "context_chars_mean": round(statistics.fmean(chars), 2),
        "context_tokens_approx_mean": round(statistics.fmean(token_counts), 2),
        "latency_ms_mean": round(statistics.fmean(latencies), 6),
        "latency_ms_p95": round(sorted(latencies)[max(0, math.ceil(0.95 * len(latencies)) - 1)], 6),
        "cpu_seconds": round(time.process_time() - cpu_start, 6),
        "peak_rss_mb": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 3),
        "details": details,
    }


def main():
    corpus_path = ROOT / "corpus.jsonl"
    original_queries_path = ROOT / "queries.jsonl"
    heldout_path = ROOT / "heldout_queries.jsonl"
    corpus = load_jsonl(corpus_path)
    heldout = load_jsonl(heldout_path)
    supersessions = detect_supersessions(corpus)
    artifacts = ROOT.parents[1] / ".artifacts" / "memory-loop"
    artifacts.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="goblin-memory-heldout-", dir=artifacts) as td:
        db_path = Path(td) / "memory.db"
        db = sqlite3.connect(db_path)
        db.execute("CREATE VIRTUAL TABLE memory USING fts5(id UNINDEXED, text, source UNINDEXED, ts UNINDEXED, tokenize='unicode61')")
        db.executemany("INSERT INTO memory(id, text, source, ts) VALUES (?, ?, ?, ?)", [(d["id"], d["text"], d["source"], d["ts"]) for d in corpus])
        db.commit()
        full = evaluate(db, heldout, set(supersessions), False)
        compact = evaluate(db, heldout, set(supersessions), True)
        db_size = db_path.stat().st_size
        db.close()
    result = {
        "run": 6,
        "method": "heldout-answer-evidence-validation",
        "hypothesis": "Run 005 compact context sketches preserve answer-bearing evidence on unseen queries while retaining their context reduction.",
        "benchmark_sha256": hashlib.sha256(corpus_path.read_bytes() + b"\0" + original_queries_path.read_bytes()).hexdigest(),
        "heldout_sha256": hashlib.sha256(corpus_path.read_bytes() + b"\0" + heldout_path.read_bytes()).hexdigest(),
        "heldout_queries": len(heldout),
        "db_size_bytes": db_size,
        "full_context": full,
        "compact_context": compact,
        "context_reduction_percent": round((1 - compact["context_tokens_approx_mean"] / full["context_tokens_approx_mean"]) * 100, 2),
    }
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
