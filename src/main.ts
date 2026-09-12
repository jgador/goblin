import { resolve } from "node:path";
import { createApplication } from "./server.js";

process.umask(0o077);
const port = Number(process.env.GOBLIN_PORT || 8787);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("GOBLIN_PORT must be a valid port.");
const publicOrigin = process.env.GOBLIN_PUBLIC_ORIGIN || `http://localhost:${port}`;
const app = await createApplication({
  dataDir: resolve(process.env.GOBLIN_DATA_DIR || ".goblin-auth"), publicOrigin,
});
app.server.listen(port, process.env.GOBLIN_HOST || "127.0.0.1", () => {
  console.log(`Goblin authentication preview: ${publicOrigin}`);
  console.log(`Workspace access code file: ${app.tokenFile}`);
});
app.server.on("error", async () => {
  console.error("The preview could not listen on its configured address. Check the host and port.");
  await app.codex.close();
  process.exitCode = 1;
});
app.codex.start().catch(() => console.error("Codex could not start. Install dependencies with npm ci, then restart the preview."));
const recovery = setInterval(() => app.codex.start().catch(() => {}), 10_000);
recovery.unref();
let stopping = false;
for (const signal of ["SIGTERM", "SIGINT"]) process.on(signal, async () => {
  if (stopping) return;
  stopping = true;
  clearInterval(recovery);
  await app.close();
});
