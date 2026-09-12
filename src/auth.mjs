import { PublicError, runtimeError } from "./errors.mjs";

export async function checkApiKey(apiKey, fetchImpl = fetch) {
  let response;
  try {
    response = await fetchImpl("https://api.openai.com/v1/models", {
      headers: { Authorization: `Bearer ${apiKey}` },
      redirect: "error",
      signal: AbortSignal.timeout(10_000),
    });
    await response.body?.cancel();
  } catch {
    throw new PublicError("verification_unavailable", "OpenAI could not be reached. Your key has not been saved. Please retry.", 502);
  }
  if (response.status === 401) {
    throw new PublicError("invalid_api_key", "OpenAI rejected this API key. Check the key and try again.");
  }
  if (response.ok) return "accepted";
  // Restricted keys need not have Models: Read. A 403 or rate limit does not
  // establish whether model inference is authorized, so don't call it verified.
  if (response.status === 403 || response.status === 429) return "unverified";
  throw new PublicError("verification_unavailable", "OpenAI could not verify the key. Your key has not been saved. Please retry.", 502);
}

export class Authentication {
  constructor(codex, { verifyApiKey = checkApiKey } = {}) {
    this.codex = codex;
    this.verifyApiKey = verifyApiKey;
    this.account = null;
    this.login = null;
    this.notice = null;
    this.verification = null;
    this.queue = Promise.resolve();
    codex.on("notification", (message) => {
      if (message.method === "account/login/completed") {
        this.serial(async () => {
          if (!this.login || message.params?.loginId !== this.login.id) return;
          this.login = null;
          this.notice = message.params.success ? null : {
            kind: "error", message: "Sign-in did not finish. The code may have expired or device-code login may be disabled. Please try again.",
          };
          await this.refresh();
        }).catch(() => {});
      } else if (message.method === "account/updated") {
        this.serial(() => this.refresh()).catch(() => {});
      }
    });
    codex.on("disconnected", () => {
      if (this.login) this.notice = { kind: "error", message: "Codex restarted during sign-in. Please start sign-in again." };
      this.login = null;
      this.account = null;
      this.verification = null;
    });
  }

  serial(operation) {
    const result = this.queue.then(operation);
    this.queue = result.catch(() => {});
    return result;
  }

  async refresh() {
    await this.codex.start();
    const result = await this.codex.request("account/read", { refreshToken: false });
    if (result?.requiresOpenaiAuth !== true) {
      throw new PublicError("unexpected_provider", "This preview requires Codex's OpenAI provider. Check the runtime configuration.", 503);
    }
    const account = result.account;
    if (account?.type === "chatgpt") {
      this.account = {
        type: "chatgpt",
        email: typeof account.email === "string" ? account.email : null,
        planType: typeof account.planType === "string" ? account.planType : null,
      };
    } else if (account?.type === "apiKey") {
      this.account = { type: "apiKey" };
    } else if (account == null) {
      this.account = null;
    } else {
      throw runtimeError();
    }
    if (this.account) this.login = null;
  }

  snapshot() {
    return { account: this.account, login: this.login, notice: this.notice,
      verification: this.verification, runtimeReady: this.codex.ready };
  }

  status() {
    return this.serial(async () => { await this.refresh(); return this.snapshot(); });
  }

  async requireDisconnected() {
    await this.refresh();
    if (this.account) throw new PublicError("already_connected", "Disconnect the current account before switching sign-in methods.", 409);
    if (this.login) throw new PublicError("login_in_progress", "Sign-in is already in progress. Finish or cancel it first.", 409);
  }

  loginChatGPT() {
    return this.serial(async () => {
      await this.requireDisconnected();
      this.notice = null;
      const result = await this.codex.request("account/login/start", { type: "chatgptDeviceCode" });
      let url;
      try { url = new URL(result?.verificationUrl); } catch { /* rejected below */ }
      if (result?.type !== "chatgptDeviceCode" || typeof result.loginId !== "string" ||
          typeof result.userCode !== "string" || !result.userCode || result.userCode.length > 64 ||
          !url || url.origin !== "https://auth.openai.com" || url.pathname !== "/codex/device" ||
          url.username || url.password) {
        if (typeof result?.loginId === "string") {
          await this.codex.request("account/login/cancel", { loginId: result.loginId }).catch(() => {});
        }
        throw new PublicError("unexpected_login_response", "Codex returned an unexpected sign-in response. Check the pinned Codex version.", 502);
      }
      this.login = { id: result.loginId, verificationUrl: url.href, userCode: result.userCode };
      return this.snapshot();
    });
  }

  loginApiKey(value) {
    return this.serial(async () => {
      const apiKey = typeof value === "string" ? value.trim() : "";
      if (apiKey.length < 20 || apiKey.length > 4096 || /[^\x21-\x7e]/.test(apiKey)) {
        throw new PublicError("invalid_api_key", "Enter a complete OpenAI API key without spaces.");
      }
      await this.requireDisconnected();
      const verification = await this.verifyApiKey(apiKey);
      await this.codex.request("account/login/start", { type: "apiKey", apiKey });
      this.verification = verification;
      this.notice = verification === "unverified" ? {
        kind: "info", message: "Key saved. Its permissions or rate limits prevented verification. Model access has not been tested.",
      } : null;
      await this.refresh();
      return this.snapshot();
    });
  }

  cancelLogin() {
    return this.serial(async () => {
      if (this.login) {
        const id = this.login.id;
        await this.codex.request("account/login/cancel", { loginId: id });
        this.login = null;
      }
      this.notice = null;
      await this.refresh();
      return this.snapshot();
    });
  }

  logout() {
    return this.serial(async () => {
      await this.codex.start();
      if (this.login) {
        await this.codex.request("account/login/cancel", { loginId: this.login.id });
        this.login = null;
      }
      await this.codex.request("account/logout");
      this.verification = null;
      this.notice = null;
      await this.refresh();
      return this.snapshot();
    });
  }
}
