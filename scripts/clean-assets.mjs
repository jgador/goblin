import { rm } from "node:fs/promises";

// dist contains only compiler output; remove stale modules after source deletions.
await rm(new URL("../dist/", import.meta.url), { recursive: true, force: true });
