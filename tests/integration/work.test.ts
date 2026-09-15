import test from "node:test";
import assert from "node:assert/strict";
import { request as httpRequest } from "node:http";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startBackend } from "../support/backend.js";

test("the work preview and its assets are public while workspace APIs remain protected", async (t) => {
  const dataDir = await mkdtemp(join(tmpdir(), "goblin-work-"));
  t.after(() => rm(dataDir, { recursive: true, force: true }));
  const app = await startBackend({ dataDir });
  t.after(() => app.close());
  const paths = new Map([
    ["/", "text/html"],
    ["/app.js", "text/javascript"],
    ["/styles.css", "text/css"],
    ["/work", "text/html"],
    ["/work/", "text/html"],
    ["/work/app.js", "text/javascript"],
    ["/work/styles.css", "text/css"],
    ["/assets/branding/icon.svg", "image/svg+xml"],
    ["/api/status", "application/json"],
  ]);
  for (const [path, contentType] of paths) {
    const result = await new Promise<{ status: number; type: string; csp: string; body: string }>((resolve, reject) => {
      const request = httpRequest(app.url + path, { headers: { Host: "localhost:8787" } }, response => {
        let body = "";
        response.setEncoding("utf8");
        response.on("data", chunk => { body += chunk; });
        response.on("end", () => resolve({
          status: response.statusCode ?? 0,
          type: response.headers["content-type"] ?? "",
          csp: String(response.headers["content-security-policy"] ?? ""),
          body,
        }));
      });
      request.on("error", reject);
      request.end();
    });
    assert.equal(result.status, path === "/api/status" ? 401 : 200, path);
    assert.ok(result.type.startsWith(contentType), path);
    assert.match(result.csp, /script-src 'self'; style-src 'self'/);
    assert.ok(result.body.length > 0);
    if (path === "/work" || path === "/work/") {
      assert.match(result.body, /src="\/work\/app\.js"/);
      assert.match(result.body, /href="\/work\/styles\.css"/);
    }
  }
});
