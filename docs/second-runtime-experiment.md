# Second-runtime integration experiment

The architecture is prepared for a second runtime; only Codex is implemented.
Use GitHub Copilot CLI as the first bounded experiment because an installed
1.0.83 CLI was available for read-only interface inspection on 2026-09-19.
This choice does not enable it in production or select runtime-switching policy.

`copilot --help` confirms non-interactive `--prompt`, `--output-format json`
(JSONL), model selection, tool/path/URL permissions, streaming controls, and
session resume. Help output establishes interface candidates, not behavioral
parity, cancellation guarantees, or working authentication. No account
credentials or authenticated Copilot task were used during that inspection.

Build an experimental adapter behind the existing Goblin execution contract:

1. Supply a persisted Work snapshot to a new Copilot attempt in an isolated test
   repository. Give the experiment its own connection credential and record its
   runtime, model, native session reference, and Goblin attempt ID.
2. Capture JSONL progress and a final result, then kill and restart observation.
   Demonstrate duplicate-delivery fencing, failure attention, confirmed stop,
   credential removal, and a saved branch through the same hosting contract.
3. Advertise only demonstrated capabilities. Treat a user question as a completed
   interaction if a stable live-input contract cannot be validated. Leave native
   session resume and live approvals disabled until independently tested.
4. In an explicitly authorized handoff experiment, pass a completed Codex branch
   and Goblin context into a new Copilot attempt. Compare the result and history
   without reading a Codex thread. Do not resume uncertain work or introduce
   fallback/retry automatically.

Success means the adapter can reuse Work rules, public Work views, and recovery
controls without importing Codex types. Revisit the contract if concrete runtime
behavior does not fit it. Publishing Copilot support and the user experience for
selecting or switching runtimes remain separate product decisions.
