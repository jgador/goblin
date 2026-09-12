import { spawn, type ChildProcessByStdio } from "node:child_process";
import { EventEmitter } from "node:events";
import { fileURLToPath } from "node:url";
import type { Readable, Writable } from "node:stream";
import { PublicError, runtimeError } from "./errors.js";
import { isRecord } from "./json.js";
import type { CodexEvents, CodexRequests, CodexResponses } from "./protocol.js";

const launcher = fileURLToPath(import.meta.resolve("@openai/codex/bin/codex.js"));
const environmentNames = [
  "PATH", "LANG", "LC_ALL", "TZ", "SSL_CERT_FILE", "SSL_CERT_DIR",
  "NODE_EXTRA_CA_CERTS", "HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY",
];

// The preview supports authentication and short prompt tests. Disable tools
// that could execute code or access external context in its private runtime.
const disabledFeatures = [
  "shell_tool", "unified_exec", "shell_snapshot", "view_image", "image_generation",
  "apps", "plugins", "remote_plugin", "multi_agent", "hooks", "memories", "goals",
  "code_mode", "code_mode_host", "skill_search", "skill_mcp_dependency_install",
  "sleep_tool", "request_permissions_tool", "workspace_dependencies",
];

// Browser input is never used as an RPC method or runtime configuration.
export interface CodexOptions {
  codexHome: string;
  home: string;
  workspace: string;
  command?: string;
  args?: string[];
  environment?: NodeJS.ProcessEnv;
  timeoutMs?: number;
}

type CodexChild = ChildProcessByStdio<Writable, Readable, null>;
interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (reason?: unknown) => void;
  timer: NodeJS.Timeout;
  method: keyof CodexRequests;
}

export class Codex extends EventEmitter<CodexEvents> {
  readonly options: Required<CodexOptions>;
  ready: boolean;
  child: CodexChild | null;
  private starting: Promise<void> | null;
  private pending: Map<number, PendingRequest>;
  private nextId: number;
  private closed: boolean;
  private retiring: Promise<void>;

  constructor({ codexHome, home, workspace, command = process.execPath,
    args = [launcher], environment = process.env, timeoutMs = 20_000 }: CodexOptions) {
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
      ...disabledFeatures.flatMap((name) => ["-c", `features.${name}=false`]),
      "-c", 'web_search="disabled"',
      "-c", 'sandbox_mode="read-only"',
      "-c", 'approval_policy="never"',
      "-c", "project_doc_max_bytes=0",
      "-c", "skills.include_instructions=false",
      "-c", "memories.generate_memories=false",
      "-c", "memories.use_memories=false",
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

  fail(child: CodexChild) {
    if (this.child !== child) return;
    this.child = null;
    this.ready = false;
    // Don't let a replacement process refresh the same credentials while an
    // unhealthy predecessor is still alive.
    if (child.pid && child.exitCode === null && child.signalCode === null) {
      this.retiring = new Promise<void>((resolveRetiring) => {
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

  private send(message: Record<string, unknown>) {
    if (!this.child?.stdin.writable) throw runtimeError();
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  request<Method extends keyof CodexRequests>(method: Method,
    ...args: CodexRequests[Method] extends undefined ? [params?: undefined] : [params: CodexRequests[Method]]
  ): Promise<CodexResponses[Method]> {
    const [params = {}] = args;
    const id = this.nextId++;
    // The response type is asserted only at the wire boundary. Authentication
    // and prompt handlers retain their runtime checks before using the data.
    return new Promise<unknown>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new PublicError("runtime_timeout", "Codex took too long to respond. Please retry.", 504));
        const child = this.child;
        if (child) { child.kill(); this.fail(child); }
      }, this.options.timeoutMs);
      timer.unref();
      this.pending.set(id, { resolve, reject, timer, method });
      try { this.send({ id, method, params }); }
      catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error);
      }
    }) as Promise<CodexResponses[Method]>;
  }

  private receive(message: unknown) {
    if (!isRecord(message)) throw runtimeError();
    if (typeof message.method === "string") {
      if (message.id !== undefined) {
        this.send({ id: message.id, error: { code: -32601, message: "Interactive tools are disabled in this preview." } });
      } else {
        this.emit("notification", { method: message.method,
          params: isRecord(message.params) ? message.params : {} });
      }
      return;
    }
    if (typeof message.id !== "number") return;
    const request = this.pending.get(message.id);
    if (!request) return;
    clearTimeout(request.timer);
    this.pending.delete(message.id);
    if (message.error) {
      // Upstream errors can contain URLs, codes, or tokens. Never return or log them.
      request.reject(new PublicError("codex_request_failed",
        request.method === "account/login/start"
          ? "Codex could not complete this request. For ChatGPT, check that device-code login is enabled, then retry."
          : "Codex could not complete this request. Please retry.", 502));
    } else {
      request.resolve(message.result);
    }
  }

  async close() {
    this.closed = true;
    const child = this.child;
    if (!child) { await this.retiring; return; }
    await new Promise<void>((resolve) => {
      const timeout = setTimeout(() => { child.kill("SIGKILL"); resolve(); }, 2000);
      timeout.unref();
      child.once("exit", () => { clearTimeout(timeout); resolve(); });
      child.kill("SIGTERM");
    });
    this.fail(child);
  }
}
