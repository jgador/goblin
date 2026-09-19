import { rm } from "node:fs/promises";

// Run directly with Node 24 to remove stale browser assets before compilation.
await rm(new URL("../dist/", import.meta.url), { recursive: true, force: true });
