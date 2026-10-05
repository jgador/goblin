# Memory experiment loop

This directory is the persistent state for the hourly memory/context experiment.
The benchmark is intentionally small, local, deterministic, and CPU-only so each
run can test one retrieval or context-management hypothesis cheaply.

## Stable benchmark

- `corpus.jsonl`: messy multi-source records, including duplicates, corrections,
  stale notes, decisions, operational facts, and project requirements.
- `queries.jsonl`: fixed retrieval questions and relevant document IDs.
- The benchmark hash is recorded in every run. Do not change the corpus or
  queries during ordinary hourly experiments. If the benchmark must evolve,
  treat that as a dedicated experiment and start a new benchmark generation.

## Run contract

Each run must:

1. Read `STATE.md` and `results.jsonl` first.
2. Test exactly one hypothesis against the same benchmark.
3. Measure retrieval quality, approximate context size, latency, CPU, peak RSS,
   and storage where practical.
4. Compare the candidate with the current champion.
5. Accept the candidate only when quality improves without an unacceptable
   resource regression. Otherwise keep the champion and record the failure.
6. Append one summary object to `results.jsonl` and write detailed output under
   `runs/NNN.json`.
7. Update `STATE.md` with the champion, lesson, and next hypotheses.

The benchmark does not call an LLM. GPT-6.1 Sol orchestrates the experiment, but
retrieval scoring itself is deterministic and incurs no external model/API cost.

## Reproduce the current champion

```bash
python3 experiments/memory/run_experiment.py
```

The script uses only the Python standard library and requires SQLite with FTS5.
