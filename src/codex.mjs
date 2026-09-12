import { spawn } from "node:child_process";
import { EventEmitter } from "node:events";
import { fileURLToPath } from "node:url";
import { PublicError, runtimeError } from "./errors.mjs";

const launcher = fileURLToPath(new URL("../node_modules/@openai/codex/bin/codex.js", import.meta.url));
const environmentNames = [
  "PATH", "LANG", "LC_ALL", "TZ", "SSL_CERT_FILE", "SSL_CERT_DIR",
  "NODE_EXTRA_CA_CERTS", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY",
];

// This bridge deliberately exposes only authentication. It never forwards a
// browser-supplied RPC method, starts a thread, or executes an agent task.
export class Codex extends EventEmitter {
  constructor({ codexHome, home, workspace, command = process.execPath,
    args = [launcher], environment = process.env, timeoutMs = 20_000 }) {
    super();
    this.options = { codexHome, home, workspace, command, args, environment, timeoutMs };
    this.ready = false;
    this.child = null;
    this.starting = null;
    this.pending = new Map();
    this.nextId = 1;
    this.closed = false;
    this.retiring = Promise.resolve();
  }

  async start() {
    if (this.closed) throw runtimeError();
    if (this.ready) return;
    if (this.starting) return this.starting;
    this.starting = this.retiring.then(() => {
      if (this.closed) throw runtimeError();
      return this.initialize();
    }).finally(() => { this.starting = null; });
    return this.starting;
  }

  async initialize() {
    const options = this.options;
    const env = Object.fromEntries(environmentNames
      .filter((name) => options.environment[name] !== undefined)
      .map((name) => [name, options.environment[name]]));
    Object.assign(env, { HOME: options.home, CODEX_HOME: options.codexHome });
    const child = spawn(options.command, [
      ...options.args, "app-server",
      "-c", 'model_provider="openai"',
      "-c", 'cli_auth_credentials_store="file"',
      "-c", "analytics.enabled=false",
    ], { cwd: options.workspace, env, stdio: ["pipe", "pipe", "ignore"] });
    this.child = child;
    let buffer = "";
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      buffer += chunk;
      if (buffer.length > 2 * 1024 * 1024) {
        child.kill();
        this.fail(child);
        return;
      }
      let newline;
      while ((newline = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, newline);
        buffer = buffer.slice(newline + 1);
        if (!line.trim()) continue;
        try { this.receive(JSON.parse(line)); }
        catch { child.kill(); this.fail(child); }
      }
    });
    child.stdin.on("error", () => this.fail(child));
    child.on("error", () => this.fail(child));
    child.on("exit", () => this.fail(child));
    try {
      await this.request("initialize", {
        clientInfo: { name: "goblin_auth", title: "Goblin", version: "0.1.0" },
      });
      this.send({ method: "initialized", params: {} });
      this.ready = true;
    } catch (error) {
      child.kill();
      this.fail(child);
      throw error;
    }
  }

  fail(child) {
    if (this.child !== child) return;
    this.child = null;
    this.ready = false;
    // Don't let a replacement process refresh the same credentials while an
    // unhealthy predecessor is still alive.
    if (child.pid && child.exitCode === null && child.signalCode === null) {
      this.retiring = new Promise((resolveRetiring) => {
        const timeout = setTimeout(() => child.kill("SIGKILL"), 2000);
        timeout.unref();
        child.once("exit", () => { clearTimeout(timeout); resolveRetiring(); });
        child.kill("SIGTERM");
      });
    }
    for (const request of this.pending.values()) {
      clearTimeout(request.timer);
      request.reject(runtimeError());
    }
    this.pending.clear();
    this.emit("disconnected");
  }

  send(message) {
    if (!this.child?.stdin.writable) throw runtimeError();
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  request(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new PublicError("runtime_timeout", "Codex took too long to respond. Please retry.", 504));
        const child = this.child;
        if (child) { child.kill(); this.fail(child); }
      }, this.options.timeoutMs);
      timer.unref();
      this.pending.set(id, { resolve, reject, timer });
      try { this.send({ id, method, params }); }
      catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  receive(message) {
    if (message.method) {
      if (message.id !== undefined) {
        this.send({ id: message.id, error: { code: -32601, message: "Only authentication is supported." } });
      } else {
        this.emit("notification", message);
      }
      return;
    }
    const request = this.pending.get(message.id);
    if (!request) return;
    clearTimeout(request.timer);
    this.pending.delete(message.id);
    if (message.error) {
      // Upstream errors can contain URLs, codes, or tokens. Never return or log them.
      request.reject(new PublicError("codex_request_failed",
        "Codex could not complete this request. For ChatGPT, check that device-code login is enabled, then retry.", 502));
    } else {
      request.resolve(message.result);
    }
  }

  async close() {
    this.closed = true;
    const child = this.child;
    if (!child) { await this.retiring; return; }
    await new Promise((resolve) => {
      const timeout = setTimeout(() => { child.kill("SIGKILL"); resolve(); }, 2000);
      timeout.unref();
      child.once("exit", () => { clearTimeout(timeout); resolve(); });
      child.kill("SIGTERM");
    });
    this.fail(child);
  }
}
