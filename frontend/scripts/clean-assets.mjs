import { rm } from "node:fs/promises";

// Remove stale browser assets before compiling the current source.
await rm(new URL("../dist/", import.meta.url), { recursive: true, force: true });
