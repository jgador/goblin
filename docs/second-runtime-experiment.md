# Second-runtime integration experiment

The architecture is prepared for a second runtime; only Codex is implemented.
GitHub Copilot CLI is the proposed first bounded experiment. Validate its current
supported interface before implementation; the original CLI inspection is historical.
This choice does not enable it in production or select runtime-switching policy.

The earlier read-only inspection identified non-interactive prompt/JSONL output,
model selection, tool/path/URL permissions, streaming, and native resume as
candidates. Recheck them on the chosen pinned version. Help output does not
establish authentication, cancellation guarantees, or parity with Codex.

Build an experimental adapter behind the existing Goblin execution contract:

1. Supply a persisted Work snapshot to a new Copilot attempt in an isolated test
   repository. Give the experiment its own connection credential and record its
   runtime, model, native session reference, and Goblin attempt ID.
2. Capture JSONL progress and a final result, then kill and restart observation.
   Demonstrate duplicate-delivery fencing, failure attention, confirmed stop,
   credential removal, and continuation from the retained Work PVC/local Git HEAD through the same
   hosting contract.
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
