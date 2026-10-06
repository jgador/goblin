#!/usr/bin/env python3
"""Replay Run 010 rankings and compare context assembly strategies."""
import argparse
import hashlib
import json
import math
import resource
import statistics
import time
from pathlib import Path

from run_experiment import (
    SEMANTIC_OPERATORS,
    compact_text,
    load_jsonl,
    query_aware_compact,
    select_query_clauses,
    split_clauses,
)
from run_heldout_validation import terms

ROOT = Path(__file__).resolve().parent
def baseline_context(text, _query):
    clauses = split_clauses(text)
    return compact_text(text), len(clauses), len(clauses)


def query_aware_context(text, query):
    """Keep topic, query-overlap, and semantic-operator clauses, then compact."""
    selected, clauses = select_query_clauses(text, query)
    return query_aware_compact(text, query), len(selected), len(clauses)


def retrieval_metrics(queries, details):
    by_id = {detail["query"]: detail for detail in details}
    rows = []
    for query in queries:
        ids = by_id[query["id"]]["top"]
        relevant = set(query["relevant"])
        rank = next((index + 1 for index, doc_id in enumerate(ids) if doc_id in relevant), None)
        rows.append((rank, len(relevant.intersection(ids)) / len(relevant)))
    return {
        "mrr_at_5": round(statistics.fmean(0 if rank is None else 1 / rank for rank, _ in rows), 6),
        "recall_at_5": round(statistics.fmean(recall for _, recall in rows), 6),
        "hit_at_1": round(statistics.fmean(rank == 1 for rank, _ in rows), 6),
    }


def evaluate(queries, details, corpus, transform):
    by_id = {detail["query"]: detail for detail in details}
    output = []
    for query in queries:
        ids = by_id[query["id"]]["top"]
        rendered = []
        selected_clauses = total_clauses = 0
        for doc_id in ids:
            text, selected, total = transform(corpus[doc_id]["text"], query["query"])
            rendered.append(text)
            selected_clauses += selected
            total_clauses += total
        joined = " ".join(rendered)
        chars = sum(map(len, rendered))
        evidence = (set(query["evidence_terms"]).issubset(terms(joined))
                    if "evidence_terms" in query else None)
        output.append({
            "query": query["id"],
            "top": ids,
            "context_chars": chars,
            "context_tokens_approx": math.ceil(chars / 4),
            "evidence_preserved": evidence,
            "selected_clauses": selected_clauses,
            "total_clauses": total_clauses,
        })
    evidence = [row["evidence_preserved"] for row in output if row["evidence_preserved"] is not None]
    return {
        **retrieval_metrics(queries, details),
        "query_count": len(queries),
        "evidence_at_5": statistics.fmean(evidence) if evidence else None,
        "context_chars_mean": statistics.fmean(row["context_chars"] for row in output),
        "context_tokens_approx_mean": statistics.fmean(row["context_tokens_approx"] for row in output),
        "selected_clause_fraction": (
            sum(row["selected_clauses"] for row in output) /
            sum(row["total_clauses"] for row in output)
        ),
        "details": output,
    }


def measure_assembly(queries, details, corpus, transform, iterations):
    by_id = {detail["query"]: detail for detail in details}
    samples = []
    cpu_start = time.process_time()
    for _ in range(iterations):
        for query in queries:
            started = time.perf_counter()
            for doc_id in by_id[query["id"]]["top"]:
                transform(corpus[doc_id]["text"], query["query"])
            samples.append((time.perf_counter() - started) * 1000)
    return {
        "iterations": iterations,
        "assembled_queries": len(samples),
        "wall_ms_mean_per_query": statistics.fmean(samples),
        "wall_ms_p95_per_query": sorted(samples)[math.ceil(0.95 * len(samples)) - 1],
        "cpu_seconds_total": time.process_time() - cpu_start,
        "peak_rss_mib": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 3),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--iterations", type=int, default=1000)
    args = parser.parse_args()
    if args.iterations < 1:
        parser.error("iterations must be positive")

    corpus_path = ROOT / "corpus.jsonl"
    original_path = ROOT / "queries.jsonl"
    heldout_path = ROOT / "heldout_queries.jsonl"
    corpus = {doc["id"]: doc for doc in load_jsonl(corpus_path)}
    query_sets = {
        "original": load_jsonl(original_path),
        "heldout": load_jsonl(heldout_path),
    }
    champion = json.loads((ROOT / "runs" / "010.json").read_text())
    expected_benchmark = hashlib.sha256(corpus_path.read_bytes() + b"\0" + original_path.read_bytes()).hexdigest()
    expected_heldout = hashlib.sha256(corpus_path.read_bytes() + b"\0" + heldout_path.read_bytes()).hexdigest()
    if champion["benchmark_sha256"] != expected_benchmark or champion["heldout_sha256"] != expected_heldout:
        raise ValueError("Run 010 rankings do not match the frozen benchmark files")

    baseline = {}
    candidate = {}
    for name, queries in query_sets.items():
        details = champion["candidate"][name]["details"]
        baseline[name] = evaluate(queries, details, corpus, baseline_context)
        candidate[name] = evaluate(queries, details, corpus, query_aware_context)
        baseline[name]["assembly"] = measure_assembly(
            queries, details, corpus, baseline_context, args.iterations)
        candidate[name]["assembly"] = measure_assembly(
            queries, details, corpus, query_aware_context, args.iterations)

    quality_fields = ("mrr_at_5", "recall_at_5", "hit_at_1", "evidence_at_5")
    quality_match = all(
        candidate[dataset].get(metric) == baseline[dataset].get(metric)
        for dataset in ("original", "heldout") for metric in quality_fields
        if baseline[dataset].get(metric) is not None
    )
    context_reduced = all(
        candidate[dataset]["context_tokens_approx_mean"] < baseline[dataset]["context_tokens_approx_mean"]
        for dataset in ("original", "heldout")
    )
    latency_acceptable = all(
        candidate[dataset]["assembly"]["wall_ms_mean_per_query"] < 1.0
        for dataset in ("original", "heldout")
    )
    accepted = quality_match and context_reduced and latency_acceptable
    result = {
        "run": 14,
        "method": "fts5-e5-small-v2-rrf60-query-aware-clause-context",
        "hypothesis": "A fixed query-aware clause selector can reduce Run 010 context without changing retrieval or losing held-out evidence.",
        "status": "accepted-context-champion" if accepted else "rejected",
        "champion": accepted,
        "reference_run": 10,
        "retrieval_replayed": True,
        "retrieval_or_embedding_latency_measured": False,
        "selection_rule": {
            "always_keep_first_clause": True,
            "keep_later_query_overlap_clauses": True,
            "keep_later_semantic_operator_clauses": sorted(SEMANTIC_OPERATORS),
            "then_apply_run005_compaction": True,
            "selected_before_evaluation": True,
        },
        "baseline": baseline,
        "candidate": candidate,
        "comparison": {
            "quality_match": quality_match,
            "context_reduced_on_both_sets": context_reduced,
            "assembly_latency_below_1ms": latency_acceptable,
            "persistent_storage_delta_bytes": 0,
            "run010_vector_db_bytes": champion["vector_db_bytes_including_fts_and_raw_records"],
        },
        "benchmark_sha256": expected_benchmark,
        "heldout_sha256": expected_heldout,
        "external_inference_api_calls": 0,
        "answer_generation_evaluated": False,
        "requested_orchestration_model": "GPT-6.1 Sol",
        "orchestration_model_verified": False,
        "notes": (
            "Accepted as the context-assembly extension to Run 010: retrieval/evidence metrics matched, context fell on both frozen sets, and assembly remained below 1 ms per query."
            if accepted else
            "Rejected: the selector did not preserve all Run 010 retrieval/evidence metrics with lower context and acceptable assembly latency."
        ),
        "measurement_notes": [
            "Run 010 top-five document IDs are replayed, so this isolates context assembly and does not remeasure embedding or retrieval latency.",
            "Context tokens use the existing chars/4 approximation; held-out answer quality uses the existing evidence-term subset proxy.",
            "Assembly timing repeats each frozen query to reduce timer noise and excludes answer generation.",
            "Peak RSS is the Linux process-lifetime high-water mark for this standard-library replay process.",
        ],
    }
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
