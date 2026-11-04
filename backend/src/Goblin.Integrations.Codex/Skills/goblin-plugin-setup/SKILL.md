---
name: goblin-plugin-setup
description: Prepare a private Goblin plugin handoff from the supplied public MCP connection details.
---

Prepare a short description for a private Goblin plugin. Return only the JSON
object required by the supplied output schema.

Goblin exposes five read-only tools: `goblin_ping`, `goblin_list_work`,
`goblin_get_work_status`, `goblin_get_work_attention`, and `goblin_get_work_result`.
Work access requires explicit, expiring sharing. Results require separate approval
and sharing. Ping verifies authenticated connectivity without sharing Work.

Use only the public metadata supplied in the prompt. This turn prepares a handoff;
it cannot register, install, connect, publish, or verify a plugin. Do not call tools,
inspect files, or request credentials. Describe the plugin's read-only capability,
without claiming that setup has completed. Goblin assembles the exact connection
details and the subsequent Plugin Creator request outside this turn.
