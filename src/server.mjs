import { createServer } from "node:http";
import { randomBytes, createHash, timingSafeEqual } from "node:crypto";
import { mkdir, readFile, writeFile, chmod } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { Codex } from "./codex.mjs";
import { Authentication } from "./auth.mjs";
import { PublicError } from "./errors.mjs";

const cookieName = "goblin_auth_session";
const sessionLifetime = 12 * 60 * 60 * 1000;
const staticFiles = new Map([
  ["/", ["index.html", "text/html; charset=utf-8"]],
  ["/app.js", ["app.js", "text/javascript; charset=utf-8"]],
  ["/styles.css", ["styles.css", "text/css; charset=utf-8"]],
  ["/icon.svg", ["icon.svg", "image/svg+xml"]],
]);
const hash = (text) => createHash("sha256").update(text).digest();

async function readJson(request) {
  if (request.headers["content-type"]?.split(";")[0].trim() !== "application/json") {
    request.resume();
    throw new PublicError("invalid_content_type", "Send JSON content.", 415);
  }
  return new Promise((resolveBody, reject) => {
    let size = 0;
    const chunks = [];
    function cleanup() {
      request.off("data", onData);
      request.off("end", onEnd);
      request.off("error", onError);
      request.off("aborted", onError);
    }
    function fail(error) { cleanup(); request.resume(); reject(error); }
    function onError() { fail(new PublicError("invalid_request", "The request was interrupted.")); }
    function onData(chunk) {
      size += chunk.length;
      if (size > 8192) return fail(new PublicError("request_too_large", "The request is too large.", 413));
      chunks.push(chunk);
    }
    function onEnd() {
      cleanup();
      try {
        const body = JSON.parse(Buffer.concat(chunks).toString("utf8"));
        if (body === null || Array.isArray(body) || typeof body !== "object") throw new Error();
        resolveBody(body);
      } catch { reject(new PublicError("invalid_json", "The request could not be read.")); }
    }
    request.on("data", onData);
    request.on("end", onEnd);
    request.on("error", onError);
    request.on("aborted", onError);
  });
}

export async function createApplication({ dataDir = resolve(".goblin-auth"),
  publicOrigin = "http://localhost:8787", codexOptions = {}, verifyApiKey, promptTimeoutMs } = {}) {
  const origin = new URL(publicOrigin);
  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(origin.hostname);
  if (origin.username || origin.password || origin.pathname !== "/" || origin.search || origin.hash ||
      !["http:", "https:"].includes(origin.protocol) || (!loopback && origin.protocol !== "https:")) {
    throw new Error("GOBLIN_PUBLIC_ORIGIN must be an HTTPS origin or a loopback HTTP origin.");
  }
  const allowedOrigins = new Set([origin.origin]);
  if (loopback) {
    for (const host of ["localhost", "127.0.0.1", "[::1]"]) {
      allowedOrigins.add(`${origin.protocol}//${host}${origin.port ? `:${origin.port}` : ""}`);
    }
  }
  const allowedHosts = new Set([...allowedOrigins].map((item) => new URL(item).host));
  const paths = { data: resolve(dataDir), codexHome: resolve(dataDir, "codex"),
    home: resolve(dataDir, "home"), workspace: resolve(dataDir, "workspace") };
  for (const path of Object.values(paths)) { await mkdir(path, { recursive: true, mode: 0o700 }); await chmod(path, 0o700); }
  const tokenFile = join(paths.data, "owner-token");
  try { await writeFile(tokenFile, `${randomBytes(32).toString("base64url")}\n`, { flag: "wx", mode: 0o600 }); }
  catch (error) { if (error.code !== "EEXIST") throw error; }
  const ownerToken = (await readFile(tokenFile, "utf8")).trim();
  if (ownerToken.length < 32 || ownerToken.length > 256) throw new Error("The owner-token file is invalid.");
  await chmod(tokenFile, 0o600);
  const ownerHash = hash(ownerToken);
  const codex = new Codex({ ...paths, ...codexOptions });
  const authentication = new Authentication(codex, { verifyApiKey, promptTimeoutMs });
  const sessions = new Map();
  let failedUnlocks = [];
  const assets = new Map(await Promise.all([...staticFiles].map(async ([path, [name, type]]) =>
    [path, { type, body: await readFile(fileURLToPath(new URL(`../public/${name}`, import.meta.url))) }])));

  function sessionId(request) {
    const value = request.headers.cookie?.split(";").map((item) => item.trim())
      .find((item) => item.startsWith(`${cookieName}=`))?.slice(cookieName.length + 1);
    if (!value || !/^[\w-]{43}$/.test(value)) return null;
    const id = hash(value).toString("hex");
    if ((sessions.get(id) ?? 0) <= Date.now()) { sessions.delete(id); return null; }
    return id;
  }

  function cookie(value, maxAge) {
    return `${cookieName}=${value}; Path=/; HttpOnly; SameSite=Strict; Max-Age=${maxAge}${origin.protocol === "https:" ? "; Secure" : ""}`;
  }

  function json(response, status, body) {
    response.writeHead(status, { "Content-Type": "application/json; charset=utf-8" });
    response.end(JSON.stringify(body));
  }

  const server = createServer(async (request, response) => {
    response.setHeader("Cache-Control", "no-store");
    response.setHeader("X-Content-Type-Options", "nosniff");
    response.setHeader("Referrer-Policy", "no-referrer");
    response.setHeader("X-Frame-Options", "DENY");
    response.setHeader("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
    try {
      const path = new URL(request.url, origin).pathname;
      if (request.method === "GET" && path === "/healthz") return json(response, 200, { ok: true });
      if (request.method === "GET" && path === "/readyz") return json(response, codex.ready ? 200 : 503, { ready: codex.ready });
      if (!allowedHosts.has(request.headers.host?.toLowerCase())) {
        throw new PublicError("invalid_host", "Open the configured workspace address.", 403);
      }
      if (request.method === "POST" && !allowedOrigins.has(request.headers.origin)) {
        throw new PublicError("invalid_origin", "This request must come from the workspace page.", 403);
      }
      if (request.method === "GET" && assets.has(path)) {
        const asset = assets.get(path);
        response.writeHead(200, { "Content-Type": asset.type });
        return response.end(asset.body);
      }
      if (request.method === "GET" && path === "/api/session") {
        return json(response, 200, { authenticated: Boolean(sessionId(request)) });
      }
      if (request.method === "POST" && path === "/api/session") {
        const body = await readJson(request);
        failedUnlocks = failedUnlocks.filter((time) => time > Date.now() - 60_000);
        if (failedUnlocks.length >= 5) {
          response.setHeader("Retry-After", "60");
          throw new PublicError("too_many_attempts", "Too many attempts. Wait a minute, then try again.", 429);
        }
        if (typeof body.token !== "string" || !timingSafeEqual(hash(body.token.trim()), ownerHash)) {
          failedUnlocks.push(Date.now());
          throw new PublicError("invalid_workspace_code", "The workspace access code is incorrect.", 401);
        }
        failedUnlocks = [];
        for (const [id, expiry] of sessions) if (expiry <= Date.now()) sessions.delete(id);
        if (sessions.size >= 32) sessions.delete(sessions.keys().next().value);
        const token = randomBytes(32).toString("base64url");
        sessions.set(hash(token).toString("hex"), Date.now() + sessionLifetime);
        response.setHeader("Set-Cookie", cookie(token, sessionLifetime / 1000));
        return json(response, 200, { authenticated: true });
      }
      const session = sessionId(request);
      if (!session) throw new PublicError("workspace_locked", "Unlock the workspace to continue.", 401);
      if (request.method === "POST" && path === "/api/session/lock") {
        await readJson(request);
        sessions.delete(session);
        response.setHeader("Set-Cookie", cookie("", 0));
        return json(response, 200, { authenticated: false });
      }
      if (request.method === "GET" && path === "/api/status") {
        return json(response, 200, await authentication.status());
      }
      if (request.method === "POST") {
        const body = await readJson(request);
        if (path === "/api/prompt") {
          const controller = new AbortController();
          const abort = () => { if (!response.writableFinished) controller.abort(); };
          response.once("close", abort);
          try {
            return json(response, 200, await authentication.testPrompt(body.prompt, { signal: controller.signal }));
          } finally { response.off("close", abort); }
        }
        let state;
        if (path === "/api/auth/chatgpt") state = await authentication.loginChatGPT();
        else if (path === "/api/auth/api-key") state = await authentication.loginApiKey(body.apiKey);
        else if (path === "/api/auth/cancel") state = await authentication.cancelLogin();
        else if (path === "/api/auth/logout") state = await authentication.logout();
        else throw new PublicError("not_found", "This endpoint does not exist.", 404);
        return json(response, 200, state);
      }
      throw new PublicError("not_found", "This endpoint does not exist.", 404);
    } catch (error) {
      if (response.headersSent || response.destroyed) return;
      const safe = error instanceof PublicError ? error : new PublicError("internal_error", "The request could not be completed. Please retry.", 500);
      json(response, safe.status, { error: { code: safe.code, message: safe.message } });
    }
  });
  server.requestTimeout = 30_000;
  server.headersTimeout = 10_000;
  server.maxHeadersCount = 32;

  return {
    server, codex, authentication, tokenFile,
    async close() {
      sessions.clear();
      await new Promise((resolveClose) => { server.close(resolveClose); server.closeIdleConnections(); });
      await codex.close();
    },
  };
}
