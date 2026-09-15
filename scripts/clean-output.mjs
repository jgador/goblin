import { rm } from "node:fs/promises";

// Root dist contains only compiled test tooling; remove stale modules after source deletions.
await rm(new URL("../dist/", import.meta.url), { recursive: true, force: true });
