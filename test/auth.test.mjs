import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile, stat, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";
import { request as httpRequest } from "node:http";
import { createApplication } from "../src/server.mjs";
import { checkApiKey } from "../src/auth.mjs";
import { PublicError } from "../src/errors.mjs";

const fixture = fileURLToPath(new URL("./fixtures/fake-codex.mjs", import.meta.url));
const origin = "http://localhost:8787";
const exampleKey = "sk-test-ONLY-A-FAKE-KEY-1234567890";

async function start(t, options = {}) {
  const dataDir = options.dataDir || await mkdtemp(join(tmpdir(), "goblin-test-"));
  const app = await createApplication({ dataDir, publicOrigin: origin,
    codexOptions: { args: [fixture, options.scenario || "manual"],
      environment: { PATH: process.env.PATH, OPENAI_API_KEY: "must-not-inherit", CODEX_API_KEY: "must-not-inherit",
        STACKIFY_AZURE_OPENAI_API_KEY: "must-not-inherit", CODEX_HOME: "/must-not-use", GOBLIN_SECRET: "must-not-inherit" },
      timeoutMs: options.timeoutMs || 1000 },
    verifyApiKey: options.verifyApiKey || (async () => "accepted"),
  });
  await new Promise((resolve) => app.server.listen(0, "127.0.0.1", resolve));
  const url = `http://127.0.0.1:${app.server.address().port}`;
  let cookie = "";
  let closed = false;
  async function close() { if (!closed) { closed = true; await app.close(); } }
  t.after(async () => { await close(); if (!options.keepData) await rm(dataDir, { recursive: true, force: true }); });
  async function request(path, body, overrides = {}) {
    return new Promise((resolveRequest, reject) => {
      const request = httpRequest(`${url}${path}`, {
        method: body === undefined ? "GET" : "POST",
        ...overrides,
        headers: { Host: "localhost:8787", Origin: origin, Cookie: cookie,
          ...(body === undefined ? {} : { "Content-Type": "application/json" }), ...overrides.headers },
      }, (response) => {
        let raw = "";
        response.setEncoding("utf8");
        response.on("data", (chunk) => { raw += chunk; });
        response.on("end", () => {
          try {
            resolveRequest({ status: response.statusCode,
              headers: new Headers(Object.entries(response.headers).map(([name, value]) => [name, Array.isArray(value) ? value.join(",") : value])),
              body: JSON.parse(raw) });
          } catch (error) { reject(error); }
        });
      });
      request.on("error", reject);
      request.end(body === undefined ? undefined : JSON.stringify(body));
    });
  }
  async function unlock() {
    const response = await request("/api/session", { token: (await readFile(app.tokenFile, "utf8")).trim() });
    assert.equal(response.status, 200);
    cookie = response.headers.get("set-cookie").split(";")[0];
    return response;
  }
  return { app, dataDir, request, unlock, close, url };
}

test("workspace access is required, cookies are private, and credential files aren't served", async (t) => {
  const ctx = await start(t);
  assert.equal((await ctx.request("/api/status")).status, 401);
  assert.equal((await ctx.request("/api/auth/chatgpt", {})).status, 401);
  assert.equal((await ctx.request("/api/session", { token: "wrong" })).status, 401);
  const session = await ctx.unlock();
  assert.match(session.headers.get("set-cookie"), /HttpOnly/);
  assert.match(session.headers.get("set-cookie"), /SameSite=Strict/);
  assert.equal((await ctx.request("/codex/auth.json")).status, 404);
  assert.equal((await ctx.request("/owner-token")).status, 404);
  assert.equal((await ctx.request("/api/rpc", { method: "turn/start" })).status, 404);
  assert.equal((await stat(ctx.app.tokenFile)).mode & 0o777, 0o600);
  assert.equal((await stat(join(ctx.dataDir, "codex"))).mode & 0o777, 0o700);
});

test("mutations reject foreign origins and unexpected hosts", async (t) => {
  const ctx = await start(t);
  await ctx.unlock();
  const foreign = await ctx.request("/api/auth/chatgpt", {}, { headers: { Origin: "https://attacker.example" } });
  assert.equal(foreign.status, 403);
  assert.equal(foreign.body.error.code, "invalid_origin");
  assert.equal((await ctx.request("/api/status", undefined, { headers: { Host: "attacker.example" } })).status, 403);
  assert.equal((await ctx.request("/api/auth/chatgpt", {}, { headers: { Origin: "null" } })).status, 403);
});

test("workspace unlock attempts are rate limited", async (t) => {
  const ctx = await start(t);
  for (let attempt = 0; attempt < 5; attempt++) assert.equal((await ctx.request("/api/session", { token: "wrong" })).status, 401);
  const blocked = await ctx.request("/api/session", { token: "wrong" });
  assert.equal(blocked.status, 429);
  assert.equal(blocked.headers.get("retry-after"), "60");
});

test("oversized and non-JSON requests are rejected before authentication changes", async (t) => {
  const ctx = await start(t);
  await ctx.unlock();
  assert.equal((await ctx.request("/api/auth/api-key", { apiKey: "x".repeat(9000) })).status, 413);
  assert.equal((await ctx.request("/api/auth/chatgpt", {}, { headers: { "Content-Type": "text/plain" } })).status, 415);
  assert.equal((await ctx.request("/api/status")).body.account, null);
});

test("ChatGPT device flow completes through Codex notifications", async (t) => {
  const ctx = await start(t, { scenario: "auto" });
  await ctx.unlock();
  const login = await ctx.request("/api/auth/chatgpt", {});
  assert.equal(login.status, 200);
  assert.equal(login.body.login.userCode, "ABCD-1234");
  assert.equal(login.body.login.verificationUrl, "https://auth.openai.com/codex/device");
  await delay(100);
  const status = await ctx.request("/api/status");
  assert.deepEqual(status.body.account, { type: "chatgpt", email: "owner@example.test", planType: "plus" });
  assert.equal(status.body.login, null);
  const env = JSON.parse(await readFile(join(ctx.dataDir, "codex/environment.json"), "utf8"));
  assert.equal(env.OPENAI_API_KEY, undefined);
  assert.equal(env.CODEX_API_KEY, undefined);
  assert.equal(env.STACKIFY_AZURE_OPENAI_API_KEY, undefined);
  assert.equal(env.GOBLIN_SECRET, undefined);
  assert.equal(env.CODEX_HOME, join(ctx.dataDir, "codex"));
});

test("a pending login can be canceled, rejects overlapping logins, and can be retried", async (t) => {
  const ctx = await start(t);
  await ctx.unlock();
  await ctx.request("/api/auth/chatgpt", {});
  assert.equal((await ctx.request("/api/auth/chatgpt", {})).status, 409);
  assert.equal((await ctx.request("/api/auth/api-key", { apiKey: exampleKey })).status, 409);
  const canceled = await ctx.request("/api/auth/cancel", {});
  assert.equal(canceled.body.login, null);
  assert.equal(canceled.body.account, null);
  await delay(20);
  assert.equal((await ctx.request("/api/status")).body.notice, null);
  assert.equal((await ctx.request("/api/auth/chatgpt", {})).status, 200);
});

test("expired login and upstream errors reveal no upstream secrets", async (t) => {
  const ctx = await start(t, { scenario: "expired" });
  await ctx.unlock();
  await ctx.request("/api/auth/chatgpt", {});
  await delay(100);
  const result = await ctx.request("/api/status");
  assert.equal(result.body.login, null);
  assert.equal(result.body.notice.kind, "error");
  assert.doesNotMatch(JSON.stringify(result.body), /secret-upstream-code/);
  const failed = await start(t, { scenario: "fail-device" });
  await failed.unlock();
  const error = await failed.request("/api/auth/chatgpt", {});
  assert.equal(error.status, 502);
  assert.doesNotMatch(JSON.stringify(error.body), /THIS-MUST-NOT-LEAK/);
});

test("the UI never receives an arbitrary upstream verification URL", async (t) => {
  const ctx = await start(t, { scenario: "invalid-url" });
  await ctx.unlock();
  const result = await ctx.request("/api/auth/chatgpt", {});
  assert.equal(result.status, 502);
  assert.equal(result.body.error.code, "unexpected_login_response");
  assert.doesNotMatch(JSON.stringify(result.body), /attacker/);
  assert.equal((await ctx.request("/api/status")).body.login, null);
});

test("API key login and logout use Codex storage without returning the key", async (t) => {
  let checkedKey;
  const ctx = await start(t, { verifyApiKey: async (key) => { checkedKey = key; return "accepted"; } });
  await ctx.unlock();
  const result = await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  assert.equal(result.status, 200);
  assert.equal(checkedKey, exampleKey);
  assert.deepEqual(result.body.account, { type: "apiKey" });
  assert.equal(result.body.verification, "accepted");
  assert.doesNotMatch(JSON.stringify(result.body), /FAKE-KEY/);
  assert.equal((await ctx.request("/api/auth/chatgpt", {})).status, 409);
  const logout = await ctx.request("/api/auth/logout", {});
  assert.equal(logout.body.account, null);
  await assert.rejects(readFile(join(ctx.dataDir, "codex/auth.json")), { code: "ENOENT" });
});

test("rejected API keys are not persisted; restricted-key verification is explicit", async (t) => {
  const invalid = await start(t, { verifyApiKey: async () => { throw new PublicError("invalid_api_key", "Rejected"); } });
  await invalid.unlock();
  assert.equal((await invalid.request("/api/auth/api-key", { apiKey: exampleKey })).status, 400);
  await assert.rejects(readFile(join(invalid.dataDir, "codex/auth.json")), { code: "ENOENT" });
  const restricted = await start(t, { verifyApiKey: async () => "unverified" });
  await restricted.unlock();
  const result = await restricted.request("/api/auth/api-key", { apiKey: exampleKey });
  assert.equal(result.body.verification, "unverified");
  assert.match(result.body.notice.message, /Model access has not been tested/);
});

test("saved authentication survives process replacement and stays isolated between workspaces", async (t) => {
  const first = await start(t, { keepData: true });
  await first.unlock();
  await first.request("/api/auth/api-key", { apiKey: exampleKey });
  await first.close();
  const restored = await start(t, { dataDir: first.dataDir });
  assert.equal((await restored.request("/api/status")).status, 401);
  await restored.unlock();
  assert.deepEqual((await restored.request("/api/status")).body.account, { type: "apiKey" });
  const separate = await start(t);
  await separate.unlock();
  assert.equal((await separate.request("/api/status")).body.account, null);
});

test("locking the preview invalidates its browser session without disconnecting Codex", async (t) => {
  const ctx = await start(t);
  await ctx.unlock();
  await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  assert.equal((await ctx.request("/api/session/lock", {})).status, 200);
  assert.equal((await ctx.request("/api/status")).status, 401);
  await ctx.unlock();
  assert.deepEqual((await ctx.request("/api/status")).body.account, { type: "apiKey" });
});

test("a stalled Codex request times out and doesn't leave a reusable pending process", async (t) => {
  const ctx = await start(t, { scenario: "hang", timeoutMs: 100 });
  await ctx.unlock();
  const result = await ctx.request("/api/status");
  assert.equal(result.status, 504);
  assert.equal(ctx.app.codex.ready, false);
});

test("API key verification uses a fixed endpoint, never follows redirects, and makes no model request", async () => {
  const check = async (status) => checkApiKey(exampleKey, async (url, options) => {
    assert.equal(url, "https://api.openai.com/v1/models");
    assert.equal(options.headers.Authorization, `Bearer ${exampleKey}`);
    assert.equal(options.redirect, "error");
    assert.equal(options.body, undefined);
    return new Response("{}", { status });
  });
  assert.equal(await check(200), "accepted");
  assert.equal(await check(403), "unverified");
  assert.equal(await check(429), "unverified");
  await assert.rejects(check(401), { code: "invalid_api_key" });
  await assert.rejects(check(503), { code: "verification_unavailable" });
  await assert.rejects(checkApiKey(exampleKey, async () => { throw new Error(exampleKey); }), (error) => {
    assert.equal(error.code, "verification_unavailable");
    assert.doesNotMatch(error.message, /FAKE-KEY/);
    return true;
  });
});

test("non-loopback HTTP origins are rejected", async () => {
  await assert.rejects(createApplication({ publicOrigin: "http://public.example.test" }), /HTTPS origin/);
});
