#!/usr/bin/env python3
import argparse
import hashlib
import json
import math
import re
import resource
import sqlite3
import statistics
import tempfile
import time
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent
WORD_RE = re.compile(r"[A-Za-z0-9_]+")
STOPWORDS = {
    "the", "a", "an", "and", "or", "is", "are", "was", "were", "be", "been",
    "to", "of", "in", "on", "for", "with", "as", "at", "by", "from", "after",
    "before", "this", "that", "it", "itself", "has", "have", "had", "not", "do",
    "does", "did", "should", "can", "could", "would", "may", "might", "will",
    "what", "which", "who", "where", "when", "how", "up", "than", "into",
    "through", "during", "every", "new", "old", "later", "earlier",
}
STALE_MARKERS = ("temporary note", "old brainstorm")
CORRECTION_MARKERS = ("correction", "confirmed", "postmortem")


def load_jsonl(path):
    with path.open("r", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def fts_query(text):
    terms = [m.group(0).lower() for m in WORD_RE.finditer(text)]
    # OR is intentionally recall-oriented for messy natural-language queries.
    return " OR ".join(f'"{t}"' for t in terms)


def make_reranker(corpus):
    document_terms = {
        doc["id"]: {m.group(0).lower() for m in WORD_RE.finditer(doc["text"])}
        for doc in corpus
    }
    frequencies = {}
    for terms in document_terms.values():
        for term in terms:
            frequencies[term] = frequencies.get(term, 0) + 1
    idf = {term: math.log(1 + len(corpus) / count) for term, count in frequencies.items()}

    def rerank(query, rows):
        terms = {m.group(0).lower() for m in WORD_RE.finditer(query)}
        total = sum(idf.get(term, 0) for term in terms) or 1

        def score(row):
            coverage = sum(idf.get(term, 0) for term in terms & document_terms[row[0]]) / total
            # A general exception-intent heuristic, deliberately recorded as an
            # overfitting risk until evaluated on held-out questions.
            exception_intent = "when" in terms and "remain" in terms
            exception_bonus = 0.25 if exception_intent and "exceptions" in document_terms[row[0]] else 0
            return (coverage + exception_bonus, -row[2])

        return sorted(rows, key=score, reverse=True)

    return rerank


def significant_terms(text):
    return {
        match.group(0).lower()
        for match in WORD_RE.finditer(text)
        if len(match.group(0)) >= 4 and match.group(0).lower() not in STOPWORDS
    }


def parse_ts(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def detect_supersessions(corpus, minimum_overlap=0.20):
    """Link explicitly stale notes to a later correction/confirmation.

    This is intentionally conservative: a source record must mark itself as stale,
    the replacement must use correction/confirmation language and be newer, and
    significant-term overlap must exceed a fixed threshold. All records remain in
    SQLite; only retrieval context suppresses the stale member of a detected pair.
    """
    terms = {doc["id"]: significant_terms(doc["text"]) for doc in corpus}
    links = {}
    for stale in corpus:
        stale_text = stale["text"].lower()
        if not any(marker in stale_text for marker in STALE_MARKERS):
            continue
        stale_terms = terms[stale["id"]]
        candidates = []
        for newer in corpus:
            newer_text = newer["text"].lower()
            if parse_ts(newer["ts"]) <= parse_ts(stale["ts"]):
                continue
            if not any(marker in newer_text for marker in CORRECTION_MARKERS):
                continue
            newer_terms = terms[newer["id"]]
            denominator = min(len(stale_terms), len(newer_terms))
            overlap = 0.0 if denominator == 0 else len(stale_terms & newer_terms) / denominator
            if overlap >= minimum_overlap:
                candidates.append((overlap, parse_ts(newer["ts"]), newer["id"]))
        if candidates:
            _, _, replacement = max(candidates)
            links[stale["id"]] = replacement
    return links


def evaluate(db, queries, k=5, reranker=None, suppressed_ids=None):
    suppressed_ids = suppressed_ids or set()
    latencies_ms = []
    context_chars = []
    reciprocal_ranks = []
    recall_at_k = []
    hit_at_1 = []
    rows_out = []
    cpu_start = time.process_time()

    for q in queries:
        started = time.perf_counter()
        rows = db.execute(
            "SELECT id, text, bm25(memory) AS score FROM memory WHERE memory MATCH ? ORDER BY score LIMIT ?",
            (fts_query(q["query"]), k),
        ).fetchall()
        if reranker is not None:
            rows = reranker(q["query"], rows)
        suppressed = [row[0] for row in rows if row[0] in suppressed_ids]
        rows = [row for row in rows if row[0] not in suppressed_ids]
        elapsed = (time.perf_counter() - started) * 1000
        latencies_ms.append(elapsed)
        ids = [row[0] for row in rows]
        relevant = set(q["relevant"])
        rank = next((i + 1 for i, doc_id in enumerate(ids) if doc_id in relevant), None)
        reciprocal_ranks.append(0.0 if rank is None else 1.0 / rank)
        recall_at_k.append(len(relevant.intersection(ids)) / len(relevant))
        hit_at_1.append(1.0 if ids and ids[0] in relevant else 0.0)
        chars = sum(len(row[1]) for row in rows)
        context_chars.append(chars)
        rows_out.append({
            "query": q["id"],
            "top": ids,
            "suppressed": suppressed,
            "first_relevant_rank": rank,
            "context_chars": chars,
        })

    cpu_seconds = time.process_time() - cpu_start
    approx_context_tokens = [math.ceil(chars / 4) for chars in context_chars]
    return {
        "query_count": len(queries),
        "k": k,
        "mrr_at_5": round(statistics.fmean(reciprocal_ranks), 6),
        "recall_at_5": round(statistics.fmean(recall_at_k), 6),
        "hit_at_1": round(statistics.fmean(hit_at_1), 6),
        "latency_ms_mean": round(statistics.fmean(latencies_ms), 6),
        "latency_ms_p95": round(sorted(latencies_ms)[max(0, math.ceil(0.95 * len(latencies_ms)) - 1)], 6),
        "context_chars_mean": round(statistics.fmean(context_chars), 2),
        "context_tokens_approx_mean": round(statistics.fmean(approx_context_tokens), 2),
        "cpu_seconds": round(cpu_seconds, 6),
        "peak_rss_mb": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 3),
        "details": rows_out,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", action="store_true", help="print machine-readable result")
    parser.add_argument("--method", choices=["bm25", "idf-intent", "supersession"], default="supersession")
    args = parser.parse_args()

    corpus_path = ROOT / "corpus.jsonl"
    queries_path = ROOT / "queries.jsonl"
    corpus = load_jsonl(corpus_path)
    queries = load_jsonl(queries_path)
    benchmark_sha256 = hashlib.sha256(
        corpus_path.read_bytes() + b"\0" + queries_path.read_bytes()
    ).hexdigest()

    reranker = make_reranker(corpus) if args.method == "idf-intent" else None
    supersessions = detect_supersessions(corpus) if args.method == "supersession" else {}
    artifacts = ROOT.parents[1] / ".artifacts" / "memory-loop"
    artifacts.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="goblin-memory-", dir=artifacts) as td:
        db_path = Path(td) / "memory.db"
        db = sqlite3.connect(db_path)
        db.execute(
            "CREATE VIRTUAL TABLE memory USING fts5("
            "id UNINDEXED, text, source UNINDEXED, ts UNINDEXED, tokenize='unicode61')"
        )
        db.executemany(
            "INSERT INTO memory(id, text, source, ts) VALUES (?, ?, ?, ?)",
            [(d["id"], d["text"], d["source"], d["ts"]) for d in corpus],
        )
        db.commit()
        metrics = evaluate(db, queries, reranker=reranker, suppressed_ids=set(supersessions))
        metrics["db_size_bytes"] = db_path.stat().st_size
        db.close()

    if args.method == "supersession":
        run = 4
        method = "sqlite-fts5-bm25-supersession-filter"
        hypothesis = "Deterministic correction/supersession linking can remove stale retrieved memories without reducing retrieval quality."
    elif args.method == "idf-intent":
        run = 2
        method = "sqlite-fts5-bm25-idf-intent"
        hypothesis = "IDF-weighted lexical coverage and a small exception-intent reranker improve BM25 ranking."
    else:
        run = 1
        method = "sqlite-fts5-bm25"
        hypothesis = "SQLite FTS5 with BM25 is a useful low-cost lexical baseline for messy memory retrieval."

    result = {
        "run": run,
        "method": method,
        "hypothesis": hypothesis,
        "corpus_documents": len(corpus),
        "benchmark_sha256": benchmark_sha256,
        "python_version": __import__("platform").python_version(),
        "sqlite_version": sqlite3.sqlite_version,
        **metrics,
    }
    if supersessions:
        result["supersessions"] = supersessions
    if args.json:
        print(json.dumps(result, sort_keys=True))
    else:
        print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
