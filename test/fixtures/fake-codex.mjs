import { createInterface } from "node:readline";
import { readFile, writeFile, unlink } from "node:fs/promises";
import { join } from "node:path";

const scenario = process.argv[2];
const root = process.env.CODEX_HOME;
const accountFile = join(root, "auth.json");
const send = (message) => process.stdout.write(`${JSON.stringify(message)}\n`);
const result = (id, value) => send({ id, result: value });
let initialized = false;
let acknowledged = false;
let login = null;
let timer;
await writeFile(join(root, "environment.json"), JSON.stringify(process.env));

async function finishLogin() {
  if (!login) return;
  const loginId = login;
  login = null;
  await writeFile(accountFile, JSON.stringify({ type: "chatgpt", email: "owner@example.test", planType: "plus" }), { mode: 0o600 });
  send({ method: "account/login/completed", params: { loginId, success: true } });
  send({ method: "account/updated", params: { authMode: "chatgpt" } });
}

for await (const line of createInterface({ input: process.stdin })) {
  const { id, method, params } = JSON.parse(line);
  if (method === "initialize") {
    initialized = true;
    result(id, { userAgent: "fake-codex/0.154.0" });
  } else if (method === "initialized") {
    acknowledged = initialized;
  } else if (!acknowledged) {
    send({ id, error: { code: -32000, message: "Initialization handshake missing" } });
  } else if (method === "account/read") {
    if (scenario === "hang") continue;
    try { await readFile(join(root, "complete-login")); await finishLogin(); } catch {}
    let account = null;
    try { account = JSON.parse(await readFile(accountFile, "utf8")); } catch {}
    if (account?.type === "apiKey") account = { type: "apiKey" };
    result(id, { account, requiresOpenaiAuth: true });
  } else if (method === "account/login/start" && params.type === "chatgptDeviceCode") {
    if (scenario === "fail-device") {
      send({ id, error: { code: 400, message: "Upstream secret sk-THIS-MUST-NOT-LEAK" } });
      continue;
    }
    login = "test-login-id";
    result(id, { type: "chatgptDeviceCode", loginId: login,
      verificationUrl: scenario === "invalid-url" ? "https://attacker.example/codex/device" : "https://auth.openai.com/codex/device",
      userCode: "ABCD-1234" });
    if (scenario === "auto") timer = setTimeout(() => finishLogin(), 40);
    if (scenario === "expired") timer = setTimeout(() => {
      send({ method: "account/login/completed", params: { loginId: login, success: false, error: "secret-upstream-code" } });
      login = null;
    }, 40);
  } else if (method === "account/login/start" && params.type === "apiKey") {
    await writeFile(accountFile, JSON.stringify({ type: "apiKey", apiKey: params.apiKey }), { mode: 0o600 });
    // Real notifications can arrive before the request response.
    send({ method: "account/login/completed", params: { loginId: null, success: true } });
    send({ method: "account/updated", params: { authMode: "apikey" } });
    result(id, { type: "apiKey" });
  } else if (method === "account/login/cancel") {
    clearTimeout(timer);
    const loginId = login;
    login = null;
    result(id, {});
    send({ method: "account/login/completed", params: { loginId, success: false } });
  } else if (method === "account/logout") {
    await unlink(accountFile).catch(() => {});
    result(id, {});
    send({ method: "account/updated", params: { authMode: null } });
  } else {
    send({ id, error: { code: -32601, message: "Unsupported method" } });
  }
}
