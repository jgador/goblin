import test, { type TestContext } from "node:test";
import assert from "node:assert/strict";
import { request as httpRequest } from "node:http";
import { mkdtemp, readdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startBackend, writePasswordHash, type BackendOptions } from "./backend.js";

async function start(t: TestContext, options: Partial<BackendOptions> = {}) {
  const dataDir = options.dataDir ?? await mkdtemp(join(tmpdir(), "goblin-local-login-"));
  t.after(() => rm(dataDir, { recursive: true, force: true }));
  const publicOrigin = options.publicOrigin ?? "http://localhost:8787";
  const app = await startBackend({ dataDir, publicOrigin, ...options });
  t.after(() => app.close());
  const request = (path: string, password?: string, cookie = "", headers: Record<string, string> = {}) =>
    new Promise<Response>((resolve, reject) => {
      const req = httpRequest(app.url + path, {
        method: password === undefined ? "GET" : "POST",
        headers: { Host: new URL(publicOrigin).host, Origin: publicOrigin, Cookie: cookie,
          "Content-Type": "application/json", ...headers },
      }, (res) => {
        let body = "";
        res.setEncoding("utf8").on("data", (chunk) => { body += chunk; });
        res.on("error", reject);
        res.on("end", () => resolve(new Response(body, { status: res.statusCode!, headers: Object.fromEntries(
          Object.entries(res.headers).filter(([, value]) => value !== undefined)
            .map(([name, value]) => [name, Array.isArray(value) ? value.join(",") : value!]),
        ) })));
      });
      req.on("error", reject);
      req.end(password === undefined ? undefined : JSON.stringify({ password }));
    });
  return { app, request, dataDir };
}

test("local login supplies a default password but requires a session POST", async (t) => {
  for (const host of ["localhost", "127.0.0.1", "[::1]"]) {
    await t.test(host, async (t) => {
      const { request, dataDir } = await start(t, { publicOrigin: `http://${host}:8787` });
      const session = await request("/api/session");
      assert.deepEqual(await session.json(), { authenticated: false, localDefaultPassword: "goblin" });
      assert.equal(session.headers.get("set-cookie"), null);
      assert.equal(session.headers.get("cache-control"), "no-store");
      assert.equal((await request("/api/status")).status, 401);
      assert.deepEqual((await readdir(dataDir)).sort(), ["codex", "home", "workspace"]);
      assert.equal((await request("/api/session", " goblin ")).status, 401);
      assert.equal((await request("/api/session", "goblin", "", { Origin: "https://foreign.example" })).status, 403);
      assert.equal((await request("/api/session", undefined, "", { Host: "foreign.example" })).status, 403);
      const login = await request("/api/session", "goblin");
      assert.equal(login.status, 200);
      const setCookie = login.headers.get("set-cookie")!;
      assert.match(setCookie, /HttpOnly/i);
      assert.match(setCookie, /SameSite=Strict/i);
      const cookie = setCookie.split(";")[0];
      assert.equal((await (await request("/api/session", undefined, cookie)).json()).authenticated, true);
      assert.equal((await request("/api/status", undefined, cookie)).status, 200);
      assert.equal((await request("/api/session/lock", "", cookie)).status, 200);
      assert.equal((await request("/api/status", undefined, cookie)).status, 401);
    });
  }
});

test("a configured local password replaces the default and invalidates previous sessions", async (t) => {
  const local = await start(t);
  const login = await local.request("/api/session", "goblin");
  assert.equal(login.status, 200);
  const cookie = login.headers.get("set-cookie")!.split(";")[0];
  await local.app.close();
  const passwordHashFile = join(local.dataDir, "owner-password");
  const password = "chosen-local-password";
  await writePasswordHash(passwordHashFile, password);
  const locked = await start(t, { dataDir: local.dataDir, passwordHashFile });
  assert.deepEqual(await (await locked.request("/api/session", undefined, cookie)).json(), { authenticated: false });
  assert.equal((await locked.request("/api/status", undefined, cookie)).status, 401);
  assert.equal((await locked.request("/api/session", "goblin")).status, 401);
  assert.equal((await locked.request("/api/session", password)).status, 200);
});

test("network listeners and public origins require a configured password", async (t) => {
  for (const options of [
    { listenUrl: "http://0.0.0.0:0" },
    { listenUrl: "http://[::]:0" },
    { publicOrigin: "https://goblin.example" },
    { publicOrigin: "http://goblin.example", allowInsecureHttp: true },
    { publicOrigin: "https://localhost.example" },
  ]) {
    await t.test(JSON.stringify(options), async (t) => {
      const dataDir = await mkdtemp(join(tmpdir(), "goblin-required-password-"));
      t.after(() => rm(dataDir, { recursive: true, force: true }));
      await assert.rejects(startBackend({ dataDir, ...options }), /Set GOBLIN_PASSWORD_HASH_FILE/);
      const passwordHashFile = join(dataDir, "owner-password");
      const password = "chosen-deployment-password";
      await writePasswordHash(passwordHashFile, password);
      const { request } = await start(t, { ...options, dataDir, passwordHashFile });
      assert.deepEqual(await (await request("/api/session")).json(), { authenticated: false });
      assert.equal((await request("/api/session", "goblin")).status, 401);
      assert.equal((await request("/api/session", password)).status, 200);
    });
  }
});
