#!/usr/bin/env python3
"""Replay Run 014 rankings and test a fixed rank-aware context budget."""
import argparse
import hashlib
import json
import math
import resource
import statistics
import time
from pathlib import Path

from run_context_selection_experiment import retrieval_metrics
from run_experiment import compact_text, load_jsonl, select_query_clauses
from run_heldout_validation import terms

ROOT = Path(__file__).resolve().parent
BUDGET_TOKENS_APPROX = 128
BUDGET_CHARS = BUDGET_TOKENS_APPROX * 4


def selected_entries(corpus, doc_ids, query):
    documents = []
    for rank, doc_id in enumerate(doc_ids, start=1):
        selected, all_clauses = select_query_clauses(corpus[doc_id]["text"], query)
        entries = [
            {
                "text": compact_text(clause),
                "provenance": {
                    "doc_id": doc_id,
                    "retrieval_rank": rank,
                    "selected_clause_index": index,
                },
            }
            for index, clause in enumerate(selected)
        ]
        documents.append({
            "doc_id": doc_id,
            "retrieval_rank": rank,
            "entries": entries,
            "total_clauses": len(all_clauses),
        })
    return documents


def payload_chars(documents):
    """Use Run 014 accounting: separators within, but not between, records."""
    return sum(
        sum(len(entry["text"]) for entry in document["entries"])
        + max(0, len(document["entries"]) - 1)
        for document in documents
    )


def assemble(corpus, doc_ids, query, apply_budget):
    source_documents = selected_entries(corpus, doc_ids, query)
    if not apply_budget:
        retained = source_documents
        omitted = []
    else:
        retained = [
            {**document, "entries": document["entries"][:1]}
            for document in source_documents
        ]
        omitted = []
        current_chars = payload_chars(retained)
        budget_exhausted = current_chars > BUDGET_CHARS
        for document, retained_document in zip(source_documents, retained):
            for entry in document["entries"][1:]:
                added_chars = len(entry["text"]) + (1 if retained_document["entries"] else 0)
                if not budget_exhausted and current_chars + added_chars <= BUDGET_CHARS:
                    retained_document["entries"].append(entry)
                    current_chars += added_chars
                else:
                    budget_exhausted = True
                    omitted.append({
                        **entry["provenance"],
                        "chars": len(entry["text"]),
                        "reason": "fixed_budget_exhausted",
                    })

    rendered = " ".join(
        entry["text"]
        for document in retained
        for entry in document["entries"]
    )
    provenance = [
        entry["provenance"]
        for document in retained
        for entry in document["entries"]
    ]
    return {
        "rendered": rendered,
        "context_chars": payload_chars(retained),
        "retained_provenance": provenance,
        "omitted_provenance": omitted,
        "selected_clauses": sum(len(document["entries"]) for document in source_documents),
        "retained_clauses": len(provenance),
        "total_clauses": sum(document["total_clauses"] for document in source_documents),
        "mandatory_chars": payload_chars([
            {**document, "entries": document["entries"][:1]}
            for document in source_documents
        ]),
    }


def evaluate(queries, details, corpus, apply_budget):
    by_id = {detail["query"]: detail for detail in details}
    output = []
    for query in queries:
        ids = by_id[query["id"]]["top"]
        assembled = assemble(corpus, ids, query["query"], apply_budget)
        rendered = assembled.pop("rendered")
        evidence = (
            set(query["evidence_terms"]).issubset(terms(rendered))
            if "evidence_terms" in query
            else None
        )
        output.append({
            "query": query["id"],
            "top": ids,
            "context_tokens_approx": math.ceil(assembled["context_chars"] / 4),
            "evidence_preserved": evidence,
            **assembled,
        })
    evidence = [row["evidence_preserved"] for row in output if row["evidence_preserved"] is not None]
    return {
        **retrieval_metrics(queries, details),
        "query_count": len(queries),
        "evidence_at_5": statistics.fmean(evidence) if evidence else None,
        "context_chars_mean": statistics.fmean(row["context_chars"] for row in output),
        "context_tokens_approx_mean": statistics.fmean(
            row["context_tokens_approx"] for row in output
        ),
        "selected_clause_fraction": (
            sum(row["selected_clauses"] for row in output)
            / sum(row["total_clauses"] for row in output)
        ),
        "retained_clause_fraction": (
            sum(row["retained_clauses"] for row in output)
            / sum(row["selected_clauses"] for row in output)
        ),
        "clauses_omitted": sum(len(row["omitted_provenance"]) for row in output),
        "queries_over_budget_due_to_mandatory_clauses": sum(
            row["mandatory_chars"] > BUDGET_CHARS for row in output
        ),
        "details": output,
    }


def measure_assembly(queries, details, corpus, apply_budget, iterations):
    by_id = {detail["query"]: detail for detail in details}
    samples = []
    cpu_start = time.process_time()
    for _ in range(iterations):
        for query in queries:
            started = time.perf_counter()
            assemble(corpus, by_id[query["id"]]["top"], query["query"], apply_budget)
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
    champion = json.loads((ROOT / "runs" / "014.json").read_text())
    expected_benchmark = hashlib.sha256(
        corpus_path.read_bytes() + b"\0" + original_path.read_bytes()
    ).hexdigest()
    expected_heldout = hashlib.sha256(
        corpus_path.read_bytes() + b"\0" + heldout_path.read_bytes()
    ).hexdigest()
    if champion["benchmark_sha256"] != expected_benchmark or champion["heldout_sha256"] != expected_heldout:
        raise ValueError("Run 014 rankings do not match the frozen benchmark files")

    baseline = {}
    candidate = {}
    for name, queries in query_sets.items():
        details = champion["candidate"][name]["details"]
        baseline[name] = evaluate(queries, details, corpus, False)
        candidate[name] = evaluate(queries, details, corpus, True)
        baseline[name]["assembly"] = measure_assembly(
            queries, details, corpus, False, args.iterations
        )
        candidate[name]["assembly"] = measure_assembly(
            queries, details, corpus, True, args.iterations
        )

    quality_fields = ("mrr_at_5", "recall_at_5", "hit_at_1", "evidence_at_5")
    quality_match = all(
        candidate[dataset].get(metric) == baseline[dataset].get(metric)
        for dataset in ("original", "heldout")
        for metric in quality_fields
        if baseline[dataset].get(metric) is not None
    )
    context_reduced = all(
        candidate[dataset]["context_tokens_approx_mean"]
        < baseline[dataset]["context_tokens_approx_mean"]
        for dataset in ("original", "heldout")
    )
    latency_acceptable = all(
        candidate[dataset]["assembly"]["wall_ms_mean_per_query"] < 1.0
        for dataset in ("original", "heldout")
    )
    provenance_preserved = all(
        len(row["retained_provenance"]) + len(row["omitted_provenance"])
        == row["selected_clauses"]
        and len({source["doc_id"] for source in row["retained_provenance"]})
        == len(row["top"])
        for dataset in ("original", "heldout")
        for row in candidate[dataset]["details"]
    )
    accepted = quality_match and context_reduced and latency_acceptable and provenance_preserved
    result = {
        "run": 16,
        "method": "fts5-e5-small-v2-rrf60-query-aware-rank-budget-128",
        "hypothesis": (
            "A fixed 128-token rank-aware budget can reduce Run 014 context while "
            "preserving retrieval quality, held-out evidence, and provenance."
        ),
        "status": "accepted-context-champion" if accepted else "rejected",
        "champion": accepted,
        "reference_run": 14,
        "retrieval_replayed": True,
        "retrieval_or_embedding_latency_measured": False,
        "budget_rule": {
            "approximate_token_budget": BUDGET_TOKENS_APPROX,
            "payload_character_budget": BUDGET_CHARS,
            "mandatory": "first Run 014 selected clause from every retrieved record",
            "additional_clause_order": "retrieval rank, then selected clause order",
            "overflow": "stop admitting optional clauses before the first overflow; never truncate a clause",
            "mandatory_overflow": "retain mandatory clauses even if they exceed the budget",
            "provenance": "record every retained and omitted source record, rank, and selected-clause index",
            "selected_before_evaluation": True,
        },
        "baseline": baseline,
        "candidate": candidate,
        "comparison": {
            "quality_match": quality_match,
            "context_reduced_on_both_sets": context_reduced,
            "assembly_latency_below_1ms": latency_acceptable,
            "provenance_preserved": provenance_preserved,
            "persistent_storage_delta_bytes": 0,
            "run014_vector_db_bytes": champion["vector_db_bytes_including_fts_and_raw_records"],
        },
        "benchmark_sha256": expected_benchmark,
        "heldout_sha256": expected_heldout,
        "external_inference_api_calls": 0,
        "answer_generation_evaluated": False,
        "requested_orchestration_model": "GPT-6.1 Sol",
        "orchestration_model_verified": False,
        "notes": (
            "Accepted as the context-assembly extension to Run 014: quality and provenance matched, context fell on both frozen sets, and assembly remained below 1 ms per query."
            if accepted
            else "Rejected: the fixed budget did not reduce context on both frozen sets while preserving quality, provenance, and acceptable assembly latency."
        ),
        "measurement_notes": [
            "Run 014 top-five document IDs are replayed, isolating context assembly from embedding and retrieval.",
            "Context tokens use the established chars/4 approximation; held-out quality uses the frozen evidence-term subset proxy.",
            "Retrieval metrics are unchanged by construction; no answer-generating model is evaluated.",
            "Assembly timing repeats each frozen query and peak RSS is the Linux process-lifetime high-water mark.",
        ],
    }
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
