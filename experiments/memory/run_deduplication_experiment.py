#!/usr/bin/env python3
"""Replay Run 014 rankings and test cross-record clause deduplication."""
import argparse
import hashlib
import json
import math
import resource
import statistics
import time
from pathlib import Path

from run_context_selection_experiment import retrieval_metrics
from run_experiment import (
    SEMANTIC_OPERATORS,
    WORD_RE,
    compact_text,
    load_jsonl,
    select_query_clauses,
)
from run_heldout_validation import terms

ROOT = Path(__file__).resolve().parent
JACCARD_THRESHOLD = 0.80
CONTAINMENT_THRESHOLD = 0.90


def compact_clauses(text, query):
    selected, all_clauses = select_query_clauses(text, query)
    return [compact_text(clause) for clause in selected], len(all_clauses)


def protected_tokens(text):
    """Return tokens whose mismatch could change a clause's meaning."""
    protected = set()
    for token in WORD_RE.findall(text):
        lowered = token.lower()
        if (
            lowered in SEMANTIC_OPERATORS
            or any(character.isdigit() for character in token)
            or "_" in token
            or (len(token) >= 2 and token.isupper())
        ):
            protected.add(lowered)
    return protected


def near_duplicate(left, right):
    """Apply the fixed, conservative token-set near-duplicate rule."""
    if protected_tokens(left) != protected_tokens(right):
        return False, None
    left_tokens = {token.lower() for token in WORD_RE.findall(left)}
    right_tokens = {token.lower() for token in WORD_RE.findall(right)}
    if not left_tokens or not right_tokens:
        return False, None
    overlap = len(left_tokens & right_tokens)
    jaccard = overlap / len(left_tokens | right_tokens)
    containment = overlap / min(len(left_tokens), len(right_tokens))
    duplicate = jaccard >= JACCARD_THRESHOLD or containment >= CONTAINMENT_THRESHOLD
    return duplicate, {"jaccard": jaccard, "containment": containment}


def assemble(corpus, doc_ids, query, deduplicate):
    entries = []
    total_clauses = 0
    selected_clauses = 0
    duplicates = []
    for doc_id in doc_ids:
        clauses, document_clause_count = compact_clauses(corpus[doc_id]["text"], query)
        total_clauses += document_clause_count
        selected_clauses += len(clauses)
        for clause_index, clause in enumerate(clauses):
            source = {"doc_id": doc_id, "clause_index": clause_index}
            if deduplicate:
                match = None
                for retained_index, retained in enumerate(entries):
                    duplicate, scores = near_duplicate(clause, retained["text"])
                    if duplicate:
                        match = (retained_index, retained, scores)
                        break
                if match is not None:
                    retained_index, retained, scores = match
                    retained["provenance"].append(source)
                    duplicates.append({
                        "removed": source,
                        "retained_index": retained_index,
                        "retained_text": retained["text"],
                        "removed_text": clause,
                        **scores,
                    })
                    continue
            entries.append({"text": clause, "provenance": [source]})
    rendered = " ".join(entry["text"] for entry in entries)
    return {
        "rendered": rendered,
        "entries": entries,
        "duplicates": duplicates,
        "selected_clauses": selected_clauses,
        "retained_clauses": len(entries),
        "total_clauses": total_clauses,
    }


def evaluate(queries, details, corpus, deduplicate):
    by_id = {detail["query"]: detail for detail in details}
    output = []
    for query in queries:
        ids = by_id[query["id"]]["top"]
        assembled = assemble(corpus, ids, query["query"], deduplicate)
        rendered = assembled.pop("rendered")
        # Match Run 014's established accounting: include separators between
        # selected clauses in one record, but not synthetic separators between
        # retrieved records.
        retained_by_document = {}
        for entry in assembled["entries"]:
            source_doc = entry["provenance"][0]["doc_id"]
            retained_by_document[source_doc] = retained_by_document.get(source_doc, 0) + 1
        chars = sum(len(entry["text"]) for entry in assembled["entries"])
        chars += sum(max(0, count - 1) for count in retained_by_document.values())
        evidence = (
            set(query["evidence_terms"]).issubset(terms(rendered))
            if "evidence_terms" in query
            else None
        )
        output.append({
            "query": query["id"],
            "top": ids,
            "context_chars": chars,
            "context_tokens_approx": math.ceil(chars / 4),
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
        "duplicate_clauses_removed": sum(len(row["duplicates"]) for row in output),
        "details": output,
    }


def measure_assembly(queries, details, corpus, deduplicate, iterations):
    by_id = {detail["query"]: detail for detail in details}
    samples = []
    cpu_start = time.process_time()
    for _ in range(iterations):
        for query in queries:
            started = time.perf_counter()
            assemble(corpus, by_id[query["id"]]["top"], query["query"], deduplicate)
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
        sum(len(entry["provenance"]) for entry in row["entries"])
        == row["selected_clauses"]
        for dataset in ("original", "heldout")
        for row in candidate[dataset]["details"]
    )
    accepted = quality_match and context_reduced and latency_acceptable and provenance_preserved
    # Per-query duplicate records contain every merged source mapping. The full
    # retained-clause list is only needed for the invariant check above and is
    # omitted from the committed measurement to keep the artifact concise.
    for dataset in ("original", "heldout"):
        for row in baseline[dataset]["details"] + candidate[dataset]["details"]:
            row.pop("entries")
    result = {
        "run": 15,
        "method": "fts5-e5-small-v2-rrf60-query-aware-cross-record-dedup",
        "hypothesis": (
            "A fixed conservative cross-record near-duplicate clause filter can reduce "
            "Run 014 context while preserving quality, evidence, and source provenance."
        ),
        "status": "accepted-context-champion" if accepted else "rejected",
        "champion": accepted,
        "reference_run": 14,
        "retrieval_replayed": True,
        "retrieval_or_embedding_latency_measured": False,
        "deduplication_rule": {
            "scope": "cross-record selected clauses only",
            "keep": "first occurrence in retrieval order",
            "token_set_jaccard_threshold": JACCARD_THRESHOLD,
            "smaller_token_set_containment_threshold": CONTAINMENT_THRESHOLD,
            "protected_token_sets_must_match": True,
            "protected_tokens": "semantic operators, numbers, underscore identifiers, uppercase identifiers",
            "provenance": "merge every removed clause source into the retained clause entry",
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
            else "Rejected: the fixed conservative rule did not reduce context on both frozen sets while preserving quality, provenance, and acceptable assembly latency."
        ),
        "measurement_notes": [
            "Run 014 top-five document IDs are replayed, isolating context assembly from embedding and retrieval.",
            "Context tokens use the existing chars/4 approximation; held-out quality uses the frozen evidence-term subset proxy.",
            "Merged clause entries retain every source document and selected-clause index as provenance.",
            "Assembly timing repeats each frozen query and excludes answer generation.",
            "Peak RSS is the Linux process-lifetime high-water mark for this standard-library replay process.",
        ],
    }
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
