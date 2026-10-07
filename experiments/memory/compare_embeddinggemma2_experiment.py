#!/usr/bin/env python3
"""Compare separate-process Run 028 outputs; write the full measured artifact."""
import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--summary", action="store_true", help="Emit one append-only results.jsonl row")
    args = parser.parse_args()
    candidate = json.loads(args.candidate.read_text())
    control = json.loads(args.control.read_text())
    assert candidate["model_key"] == "embeddinggemma2" and control["model_key"] == "e5"
    assert candidate["pipeline"] == control["pipeline"]
    assert candidate["benchmark_sha256"] == control["benchmark_sha256"]
    assert candidate["heldout_sha256"] == control["heldout_sha256"]
    assert candidate["threads"] == control["threads"] == 2
    parity = {}
    quality_fields = ("mrr_at_5", "recall_at_5", "hit_at_1", "evidence_at_5")
    quality_gates, context_gates, resource_ratios, changes = {}, {}, {}, {}
    for case, reference_run in (("original", 20), ("updated", 27)):
        reference = json.loads((ROOT / "runs" / f"{reference_run:03}.json").read_text())
        for mode, stored_mode in (("lexical", "baseline"), ("vector_only", "vector_only"), ("hybrid", "candidate")):
            for dataset in ("original", "heldout"):
                measured = control["corpora"][case][mode][dataset]
                stored = reference[stored_mode][dataset]
                parity[f"{case}_{mode}_{dataset}_rankings"] = (
                    [d["top"] for d in measured["details"]] == [d["top"] for d in stored["details"]]
                )
                parity[f"{case}_{mode}_{dataset}_quality"] = all(measured[k] == stored[k] for k in quality_fields)
                if mode == "hybrid":
                    parity[f"{case}_{mode}_{dataset}_context"] = measured["context_tokens_approx_mean"] == stored["context_tokens_approx_mean"]
        resource_ratios[f"{case}_corpus_embedding_cpu"] = (
            candidate["corpora"][case]["corpus_embedding"]["cpu_seconds"]
            / control["corpora"][case]["corpus_embedding"]["cpu_seconds"]
        )
        resource_ratios[f"{case}_database_bytes"] = candidate["corpora"][case]["database_bytes"] / control["corpora"][case]["database_bytes"]
        for mode in ("vector_only", "hybrid"):
            for dataset in ("original", "heldout"):
                a = candidate["corpora"][case][mode][dataset]
                b = control["corpora"][case][mode][dataset]
                for field in quality_fields:
                    if b[field] is not None:
                        quality_gates[f"{case}_{mode}_{dataset}_{field}"] = a[field] >= b[field]
                if mode == "hybrid":
                    context_gates[f"{case}_{dataset}"] = a["context_tokens_approx_mean"] <= b["context_tokens_approx_mean"]
                    resource_ratios[f"{case}_{dataset}_hybrid_end_to_end"] = a["end_to_end_ms_mean"] / b["end_to_end_ms_mean"]
                changed = []
                for new, old in zip(a["details"], b["details"]):
                    assert new["query"] == old["query"]
                    if new["top"] != old["top"]:
                        changed.append({"query": new["query"], "candidate": new["top"], "control": old["top"],
                                        "candidate_recall": new["recall"], "control_recall": old["recall"],
                                        "candidate_rank": new["first_relevant_rank"], "control_rank": old["first_relevant_rank"]})
                changes[f"{case}_{mode}_{dataset}"] = changed
    resource_ratios["model_disk_bytes"] = candidate["model_disk_bytes"] / control["model_disk_bytes"]
    resource_ratios["peak_rss_mib"] = candidate["peak_rss_mib"] / control["peak_rss_mib"]
    hybrid_quality = all(v for k, v in quality_gates.items() if "_hybrid_" in k)
    strict_hybrid_improvement = any(
        candidate["corpora"][case]["hybrid"][dataset][field] > control["corpora"][case]["hybrid"][dataset][field]
        for case in ("original", "updated") for dataset in ("original", "heldout")
        for field in quality_fields if control["corpora"][case]["hybrid"][dataset][field] is not None
    )
    gates = {
        "paired_e5_reproduces_committed_retrieval_and_context": all(parity.values()),
        "hybrid_quality_at_least_champion": hybrid_quality,
        "heldout_evidence_retained": all(candidate["corpora"][case][mode]["heldout"]["evidence_at_5"] == 1.0
                                        for case in ("original", "updated") for mode in ("vector_only", "hybrid")),
        "hybrid_context_not_increased": all(context_gates.values()),
        "resources_within_two_times_control": all(ratio <= 2.0 for ratio in resource_ratios.values()),
        "strict_hybrid_quality_improvement": strict_hybrid_improvement,
        "all_self_neighbors_and_padding_checks_pass": (
            candidate["padding_validation"]["passed"] and control["padding_validation"]["passed"]
            and all(r["self_neighbors_correct"] == 40 for x in (candidate, control) for r in x["corpora"].values())
        ),
    }
    accepted = all(gates.values())
    candidate.update(
        status="accepted-semantic-champion" if accepted else "rejected",
        champion=accepted, reference_run=27, followup_to_run=27,
        measured_at_utc=datetime.now(timezone.utc).isoformat(),
        paired_e5_control=control,
        comparison={"acceptance_gate": gates, "quality_gates": quality_gates,
                    "context_gates": context_gates, "resource_ratios": resource_ratios,
                    "control_parity": parity, "changed_rankings": changes},
        notes=(
            "EmbeddingGemma 2 text-only FP32 was evaluated locally using the pinned community ONNX export "
            "and the current fixed SQLite/RRF/context pipeline. Retain reusable infrastructure; "
            "keep E5 as default unless every predeclared replacement gate passes."
        ),
    )
    if args.summary:
        def compact(model):
            return {
                "model": model["model"]["model"],
                "revision": model["model"]["revision"],
                "dimensions": model["dimensions"], "model_precision": model["model_precision"],
                "threads": model["threads"], "runtime": model["runtime"],
                "python_version": model["python_version"], "logical_cpu_count": model["logical_cpu_count"],
                "model_disk_bytes": model["model_disk_bytes"], "load": model["load"],
                "peak_rss_mib": model["peak_rss_mib"],
                "corpora": {
                    case: {
                        "corpus_embedding": measured["corpus_embedding"],
                        "database_bytes": measured["database_bytes"],
                        "self_neighbors_correct": measured["self_neighbors_correct"],
                        **{
                            mode: {dataset: {k: v for k, v in metrics.items() if k != "details"}
                                   for dataset, metrics in measured[mode].items()}
                            for mode in ("vector_only", "hybrid")
                        },
                    }
                    for case, measured in model["corpora"].items()
                },
            }
        summary = {key: candidate[key] for key in (
            "run", "method", "hypothesis", "status", "champion", "reference_run",
            "followup_to_run", "benchmark_sha256", "heldout_sha256", "measured_at_utc",
            "external_inference_api_calls", "answer_generation_evaluated",
            "requested_orchestration_model", "orchestration_model_verified", "notes",
        )}
        summary.update(candidate=compact(candidate), paired_e5_control=compact(control),
                       acceptance_gate=gates, resource_ratios=resource_ratios,
                       measured_artifact="experiments/memory/runs/028.json")
        print(json.dumps(summary, sort_keys=True, separators=(",", ":")))
    else:
        print(json.dumps(candidate, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
