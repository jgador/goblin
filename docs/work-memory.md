# Work memory (first retrieval slice)

Goblin can reuse approved outcomes from earlier Work without relying on a Codex
thread. When an attempt is claimed, the application searches completed Work
objectives in PostgreSQL using its local full-text index. It selects at most
three relevant, explicitly approved results from other Work items and sends
short excerpts, each with its source Work ID, to the runtime as `RelatedMemory`.
The runtime is told to treat these excerpts as untrusted, potentially stale
context and to cite the source ID when relying on one. The current Work and
unapproved results are never retrieved.

Memory is computed at dispatch, not written into the current Work history. The
approved source Work remains the durable record and can be reviewed there.
The existing PostgreSQL database is the only search service; no embedding API
key, vector extension, or external store is required. This is lexical retrieval,
not semantic nearest-neighbor search. It currently indexes objectives, not
conversation messages or external files, and has no manual memory editor or
forget control. Those require separate product and retention decisions.
