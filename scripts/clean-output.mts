import { rm } from "node:fs/promises";

// Run directly with Node 24 before compilation; dist contains only compiled test tooling.
await rm(new URL("../dist/", import.meta.url), { recursive: true, force: true });
