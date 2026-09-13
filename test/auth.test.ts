import test, { type TestContext } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, stat, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { request as httpRequest, type RequestOptions } from "node:http";
import { startBackend } from "./backend.js";
const isRecord = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
import type { ApiFailure, ApiResponses } from "../shared/api.js";

const origin = "http://localhost:8787";
const exampleKey = "sk-test-ONLY-A-FAKE-KEY-1234567890";

interface TestOptions {
  dataDir?: string;
  scenario?: string;
  timeoutMs?: number;
  promptTimeoutMs?: number;
  verification?: "accepted" | "unverified" | "invalid";
  keepData?: boolean;
}

type ResponseBody<Path extends string> = Path extends keyof ApiResponses ? ApiResponses[Path] : Record<string, unknown>;
interface TestResponse<Path extends string> {
  status: number;
  headers: Headers;
  body: Record<string, unknown>;
  readonly data: ResponseBody<Path>;
  readonly error: ApiFailure["error"];
}

async function start(t: TestContext, options: TestOptions = {}) {
  const dataDir = options.dataDir || await mkdtemp(join(tmpdir(), "goblin-test-"));
  const app = await startBackend({ dataDir, publicOrigin: origin,
    scenario: options.scenario, timeoutMs: options.timeoutMs || 2000,
    verification: options.verification, promptTimeoutMs: options.promptTimeoutMs });
  const url = app.url;
  let cookie = "";
  let closed = false;
  async function close() { if (!closed) { closed = true; await app.close(); } }
  t.after(async () => { await close(); if (!options.keepData) await rm(dataDir, { recursive: true, force: true }); });
  async function request<Path extends string>(path: Path, body?: unknown, overrides: RequestOptions = {}): Promise<TestResponse<Path>> {
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
            const body: unknown = JSON.parse(raw);
            const status = response.statusCode;
            assert.ok(status !== undefined && isRecord(body));
            resolveRequest({ status,
              headers: new Headers(Object.entries(response.headers).flatMap(([name, value]): [string, string][] =>
                value === undefined ? [] : [[name, Array.isArray(value) ? value.join(",") : value]])),
              body,
              get data() {
                assert.equal(status, 200);
                assert.ok(!("error" in body));
                return body as ResponseBody<Path>;
              },
              get error() {
                assert.ok(status >= 400 && isRecord(body.error));
                assert.equal(typeof body.error.code, "string");
                assert.equal(typeof body.error.message, "string");
                return body.error as ApiFailure["error"];
              },
            });
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
    const setCookie = response.headers.get("set-cookie");
    assert.ok(setCookie);
    cookie = setCookie.split(";")[0];
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
  assert.match(session.headers.get("set-cookie") ?? "", /HttpOnly/i);
  assert.match(session.headers.get("set-cookie") ?? "", /SameSite=Strict/i);
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
  assert.equal(foreign.error.code, "invalid_origin");
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
  assert.equal((await ctx.request("/api/status")).data.account, null);
});

test("ChatGPT device flow completes through Codex notifications", async (t) => {
  const ctx = await start(t, { scenario: "auto" });
  await ctx.unlock();
  const login = await ctx.request("/api/auth/chatgpt", {});
  assert.equal(login.status, 200);
  assert.ok(login.data.login);
  assert.equal(login.data.login.userCode, "ABCD-1234");
  assert.equal(login.data.login.verificationUrl, "https://auth.openai.com/codex/device");
  await delay(100);
  const status = await ctx.request("/api/status");
  assert.deepEqual(status.data.account, { type: "chatgpt", email: "owner@example.test", planType: "plus" });
  assert.equal(status.data.login, null);
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
  assert.equal(canceled.data.login, null);
  assert.equal(canceled.data.account, null);
  await delay(20);
  assert.equal((await ctx.request("/api/status")).data.notice, null);
  assert.equal((await ctx.request("/api/auth/chatgpt", {})).status, 200);
});

test("expired login and upstream errors reveal no upstream secrets", async (t) => {
  const ctx = await start(t, { scenario: "expired" });
  await ctx.unlock();
  await ctx.request("/api/auth/chatgpt", {});
  await delay(100);
  const result = await ctx.request("/api/status");
  assert.equal(result.data.login, null);
  assert.ok(result.data.notice);
  assert.equal(result.data.notice.kind, "error");
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
  assert.equal(result.error.code, "unexpected_login_response");
  assert.doesNotMatch(JSON.stringify(result.body), /attacker/);
  assert.equal((await ctx.request("/api/status")).data.login, null);
});

test("API key login and logout use Codex storage without returning the key", async (t) => {
  const ctx = await start(t);
  await ctx.unlock();
  const result = await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  assert.equal(result.status, 200);
  assert.equal(await readFile(join(ctx.dataDir, "verified-test-key"), "utf8"), exampleKey);
  assert.deepEqual(result.data.account, { type: "apiKey" });
  assert.equal(result.data.verification, "accepted");
  assert.doesNotMatch(JSON.stringify(result.body), /FAKE-KEY/);
  assert.equal((await ctx.request("/api/auth/chatgpt", {})).status, 409);
  const logout = await ctx.request("/api/auth/logout", {});
  assert.equal(logout.data.account, null);
  await assert.rejects(readFile(join(ctx.dataDir, "codex/auth.json")), { code: "ENOENT" });
});

test("rejected API keys are not persisted; restricted-key verification is explicit", async (t) => {
  const invalid = await start(t, { verification: "invalid" });
  await invalid.unlock();
  assert.equal((await invalid.request("/api/auth/api-key", { apiKey: exampleKey })).status, 400);
  await assert.rejects(readFile(join(invalid.dataDir, "codex/auth.json")), { code: "ENOENT" });
  const restricted = await start(t, { verification: "unverified" });
  await restricted.unlock();
  const result = await restricted.request("/api/auth/api-key", { apiKey: exampleKey });
  assert.equal(result.data.verification, "unverified");
  assert.ok(result.data.notice);
  assert.match(result.data.notice.message, /Model access has not been tested/);
  assert.equal((await restricted.request("/api/prompt", { prompt: "Hello" })).status, 200);
  const verified = (await restricted.request("/api/status")).data;
  assert.equal(verified.verification, "accepted");
  assert.equal(verified.notice, null);
});

test("saved authentication survives process replacement and stays isolated between workspaces", async (t) => {
  const first = await start(t, { keepData: true });
  await first.unlock();
  await first.request("/api/auth/api-key", { apiKey: exampleKey });
  await first.close();
  const restored = await start(t, { dataDir: first.dataDir });
  assert.equal((await restored.request("/api/status")).status, 401);
  await restored.unlock();
  assert.deepEqual((await restored.request("/api/status")).data.account, { type: "apiKey" });
  const separate = await start(t);
  await separate.unlock();
  assert.equal((await separate.request("/api/status")).data.account, null);
});

test("locking the workspace invalidates its browser session without disconnecting Codex", async (t) => {
  const ctx = await start(t);
  await ctx.unlock();
  await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  assert.equal((await ctx.request("/api/session/lock", {})).status, 200);
  assert.equal((await ctx.request("/api/status")).status, 401);
  await ctx.unlock();
  assert.deepEqual((await ctx.request("/api/status")).data.account, { type: "apiKey" });
});

test("a stalled Codex request times out and doesn't leave a reusable pending process", async (t) => {
  const ctx = await start(t, { scenario: "hang", timeoutMs: 100 });
  await ctx.unlock();
  const result = await ctx.request("/api/status");
  assert.equal(result.status, 504);
  assert.equal((await ctx.request("/readyz")).status, 503);
});

test("prompts require a connected owner and use one isolated thread", async (t) => {
  const ctx = await start(t);
  assert.equal((await ctx.request("/api/prompt", { prompt: "Hello" })).status, 401);
  await ctx.unlock();
  assert.equal((await ctx.request("/api/prompt", { prompt: "Hello" })).status, 409);
  for (const prompt of ["", "x".repeat(501), "hello\u0000", null]) {
    assert.equal((await ctx.request("/api/prompt", { prompt })).status, 400);
  }
  await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  const result = await ctx.request("/api/prompt", { prompt: "Say hello in one sentence.", model: "must-not-use", method: "command/exec" });
  assert.equal(result.status, 200);
  assert.equal(result.data.reply, "Hello from the connected account.");
  assert.equal(result.data.model, "test-model");
  assert.equal(result.data.authType, "apiKey");
  assert.equal(typeof result.data.durationMs, "number");
  assert.doesNotMatch(JSON.stringify(result.body), /FAKE-KEY|Thinking|Wrong thread/);
  const calls = (await readFile(join(ctx.dataDir, "codex/prompt-requests.jsonl"), "utf8")).trim().split("\n")
    .map((line): { method: string; params: Record<string, unknown> } => JSON.parse(line));
  assert.deepEqual(calls.map((call) => call.method), ["thread/start", "turn/start", "thread/unsubscribe"]);
  assert.equal(calls[0].params.ephemeral, true);
  assert.equal(calls[0].params.sandbox, "read-only");
  assert.equal(calls[0].params.approvalPolicy, "never");
  assert.equal(calls[0].params.cwd, join(ctx.dataDir, "workspace"));
  assert.equal(calls[0].params.model, undefined);
  assert.deepEqual(calls[1].params.input, [{ type: "text", text: "Say hello in one sentence." }]);
  assert.deepEqual(calls[1].params.sandboxPolicy, { type: "readOnly", networkAccess: false });
});

test("prompts use the saved ChatGPT account and handle completion before the RPC response", async (t) => {
  const ctx = await start(t, { scenario: "auto" });
  await ctx.unlock();
  await ctx.request("/api/auth/chatgpt", {});
  await delay(100);
  const result = await ctx.request("/api/prompt", { prompt: "Hello" });
  assert.equal(result.status, 200);
  assert.equal(result.data.authType, "chatgpt");
  assert.equal(result.data.reply, "Hello from the connected account.");
});

test("failed generations never report success or expose raw errors, even after partial text", async (t) => {
  for (const scenario of ["prompt-fail", "prompt-partial-failure"]) {
    const ctx = await start(t, { scenario });
    await ctx.unlock();
    await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
    const result = await ctx.request("/api/prompt", { prompt: "Hello" });
    assert.equal(result.status, 502);
    assert.equal(result.error.code, "prompt_unauthorized");
    assert.equal(result.body.reply, undefined);
    assert.doesNotMatch(JSON.stringify(result.body), /THIS-MUST-NOT-LEAK|PRIVATE-DETAILS/);
  }
});

test("empty or excessive model output is not accepted as a successful reply", async (t) => {
  for (const [scenario, code] of [["prompt-empty", "prompt_empty_reply"], ["prompt-large", "prompt_reply_too_large"]]) {
    const ctx = await start(t, { scenario });
    await ctx.unlock();
    await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
    const result = await ctx.request("/api/prompt", { prompt: "Hello" });
    assert.equal(result.status, 502);
    assert.equal(result.error.code, code);
  }
});

test("timed-out prompts are interrupted and cleaned up while the account remains connected", async (t) => {
  const ctx = await start(t, { scenario: "prompt-timeout", promptTimeoutMs: 50 });
  await ctx.unlock();
  await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  const result = await ctx.request("/api/prompt", { prompt: "Hello" });
  assert.equal(result.status, 504);
  assert.equal(result.error.code, "prompt_timeout");
  const calls = await readFile(join(ctx.dataDir, "codex/prompt-requests.jsonl"), "utf8");
  assert.match(calls, /turn\/interrupt/);
  assert.match(calls, /thread\/unsubscribe/);
  assert.deepEqual((await ctx.request("/api/status")).data.account, { type: "apiKey" });
});

test("closing a prompt request interrupts generation instead of leaving it running", async (t) => {
  const ctx = await start(t, { scenario: "prompt-timeout" });
  await ctx.unlock();
  await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  const controller = new AbortController();
  const result = ctx.request("/api/prompt", { prompt: "Hello" }, { signal: controller.signal });
  const rejected = assert.rejects(result, { name: "AbortError" });
  // Wait until generation actually starts; a fresh .NET host may still be JIT compiling.
  const requestsFile = join(ctx.dataDir, "codex/prompt-requests.jsonl");
  for (let attempt = 0; attempt < 200; attempt++) {
    const calls = await readFile(requestsFile, "utf8").catch(() => "");
    if (calls.includes("turn/start")) break;
    await delay(10);
  }
  assert.match(await readFile(requestsFile, "utf8"), /turn\/start/);
  controller.abort();
  await rejected;
  await ctx.request("/api/status");
  const calls = await readFile(join(ctx.dataDir, "codex/prompt-requests.jsonl"), "utf8");
  assert.match(calls, /turn\/interrupt/);
  assert.match(calls, /thread\/unsubscribe/);
});

test("overlapping prompts are rejected and account changes wait for completion", async (t) => {
  const ctx = await start(t, { scenario: "prompt-delayed" });
  await ctx.unlock();
  await ctx.request("/api/auth/api-key", { apiKey: exampleKey });
  const first = ctx.request("/api/prompt", { prompt: "Hello" });
  await delay(30);
  const second = await ctx.request("/api/prompt", { prompt: "Again" });
  assert.equal(second.status, 409);
  assert.equal(second.error.code, "prompt_in_progress");
  const logout = ctx.request("/api/auth/logout", {});
  assert.equal((await first).data.authType, "apiKey");
  assert.equal((await logout).data.account, null);
});
