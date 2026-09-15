import type { Account, ApiFailure, ApiRequestBodies, ApiResponses, AuthenticationState, Notice } from "../api/contracts.js";

function byId<Element extends HTMLElement>(id: string, kind: { new(): Element }): Element;
function byId(id: string): HTMLElement;
function byId(id: string, kind: typeof HTMLElement = HTMLElement): HTMLElement {
  const element = document.getElementById(id);
  if (!(element instanceof kind)) throw new Error(`Missing or invalid page element: ${id}`);
  return element;
}

for (const container of document.querySelectorAll("[data-device-login-help]")) {
  container.append(byId("device-login-help-template", HTMLTemplateElement).content.cloneNode(true));
}
let unlocked = false;
let localDefaultPassword: string | undefined;
let busy = false;
let state: AuthenticationState | null = null;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
type Panel = "loading" | "locked" | "connect" | "pending" | "connected";
let visiblePanel: Panel | undefined;
let viewVersion = 0;
let pollError = false;
type ConnectionCheck =
  | { status: "unchecked" | "checking" | "connected" }
  | { status: "failed"; title: string; description: string };
let connectionCheck: ConnectionCheck = { status: "unchecked" };

class ApiError extends Error {
  constructor(readonly code: string, message: string) { super(message); }
}

function message(text: string, kind: Notice["kind"] = "error") {
  const box = byId("message");
  box.textContent = text || "";
  box.hidden = !text;
  box.classList.toggle("info", kind === "info");
}

function showPanel(name: Panel) {
  for (const item of ["loading", "locked", "connect", "pending", "connected"]) byId(`${item}-panel`).hidden = item !== name;
  byId("lock-button").hidden = !unlocked;
  if (visiblePanel !== name) {
    visiblePanel = name;
    if (name === "locked") {
      byId("workspace-password", HTMLInputElement).value = localDefaultPassword ?? "";
      byId("workspace-password").focus();
    }
    if (name === "pending") byId("verification-link").focus();
  }
}

function lockView() {
  unlocked = false;
  state = null;
  clearTimeout(pollTimer);
  for (const id of ["device-code", "account-method", "account-detail", "connected-description"]) byId(id).textContent = "";
  byId("api-key", HTMLInputElement).value = "";
  byId("verification-link").removeAttribute("href");
  connectionCheck = { status: "unchecked" };
  renderConnectionCheck();
  showPanel("locked");
}

function renderConnectionCheck() {
  const accountName = state?.account?.type === "chatgpt" ? "ChatGPT account" : "OpenAI API key";
  const { status } = connectionCheck;
  const checking = status === "unchecked" || status === "checking";
  byId("connection-check").dataset.status = status;
  byId("connection-status").textContent = connectionCheck.status === "failed" ? connectionCheck.title
    : checking ? "Verifying connection…" : "Connected";
  byId("connection-description").textContent = connectionCheck.status === "failed" ? connectionCheck.description
    : checking ? `Checking that your ${accountName} is ready to use.` : `Your ${accountName} is ready to use.`;
  byId("connection-mark").textContent = checking ? "" : status === "connected" ? "✓" : "!";
  byId("check-connection-button").textContent = checking ? "Checking connection…"
    : status === "failed" ? "Retry check" : "Check again";
}

function connectionFailure(error: unknown): ConnectionCheck {
  const chatgpt = state?.account?.type === "chatgpt";
  switch (error instanceof ApiError ? error.code : "") {
    case "prompt_unauthorized":
      return { status: "failed", title: chatgpt ? "Sign-in expired" : "API key rejected",
        description: chatgpt ? "Disconnect Codex and sign in again to reconnect your ChatGPT account."
          : "Disconnect Codex and connect a valid API key to restore access." };
    case "prompt_limit_reached":
      return { status: "failed", title: "Usage limit reached",
        description: chatgpt ? "Your account has reached a usage or rate limit. Try again later or check your ChatGPT plan."
          : "Your API key has reached a usage or rate limit. Check your API billing and limits, then try again." };
    case "prompt_access_denied":
      return { status: "failed", title: "Model access unavailable",
        description: "This account cannot use the selected model. Check your account’s access and permissions." };
    case "prompt_timeout":
      return { status: "failed", title: "Connection check timed out",
        description: "The check didn’t finish. Try again in a moment." };
    case "prompt_cancelled":
      return { status: "failed", title: "Connection check cancelled",
        description: "The check was interrupted. Try again when you’re ready." };
    case "not_connected":
      return { status: "failed", title: "Not connected",
        description: "Sign in again to check your connection." };
    case "prompt_in_progress":
      return { status: "failed", title: "Another check is running",
        description: "Wait for the current check to finish, then try again." };
    default:
      return { status: "failed", title: "Connection check failed",
        description: "We couldn’t verify access. Check your connection and try again." };
  }
}

async function checkConnection() {
  if (!unlocked || !state?.account || busy) return;
  await action("Verifying connection…", async () => {
    const requestVersion = viewVersion;
    connectionCheck = { status: "checking" };
    renderConnectionCheck();
    try {
      // The backend only succeeds after a completed turn with a nonempty reply.
      await api("/api/prompt", { prompt: "Reply with only OK." });
      if (requestVersion !== viewVersion || !unlocked || !state?.account) return;
      connectionCheck = { status: "connected" };
    } catch (error) {
      if (requestVersion !== viewVersion || !unlocked || !state?.account) return;
      connectionCheck = connectionFailure(error);
    }
    renderConnectionCheck();
  });
}

function checkConnectionIfNeeded() {
  // Rendering a poll result must never repeat a completed or failed check.
  if (connectionCheck.status === "unchecked") void checkConnection();
}

const accountEmail = (account: Account | null | undefined) => account?.type === "chatgpt" ? account.email : null;

function render(next: AuthenticationState) {
  if (!next.account || next.account.type !== state?.account?.type || accountEmail(next.account) !== accountEmail(state?.account)) {
    connectionCheck = { status: "unchecked" };
  }
  state = next;
  if (!unlocked) { showPanel("locked"); return; }
  renderConnectionCheck();
  const account = next.account;
  if (!account) {
    for (const id of ["account-method", "account-detail", "connected-description", "connection-footnote"]) byId(id).textContent = "";
  }
  if (account) {
    const chatgpt = account.type === "chatgpt";
    byId("account-method").textContent = chatgpt ? "ChatGPT" : "OpenAI API key";
    byId("account-detail").textContent = chatgpt
      ? [account.email, account.planType].filter(Boolean).join(" · ") || "Signed in with ChatGPT"
      : "Billed to your OpenAI Platform project";
    byId("connected-description").textContent = chatgpt
      ? "Your sign-in is saved for the next time you open this workspace."
      : "Your API key is saved in this workspace. Model availability depends on your project’s permissions and billing.";
    byId("connection-footnote").textContent = chatgpt
      ? "Connection checks use a small amount of your ChatGPT usage allowance."
      : "Connection checks make a small request that counts toward your API billing.";
    showPanel("connected");
  } else if (next.login) {
    byId("device-code").textContent = next.login.userCode;
    byId("verification-link", HTMLAnchorElement).href = next.login.verificationUrl;
    showPanel("pending");
  } else {
    byId("device-code").textContent = "";
    byId("verification-link").removeAttribute("href");
    showPanel("connect");
  }
  if (next.notice) message(next.notice.message, next.notice.kind);
  checkConnectionIfNeeded();
}

async function api<Path extends keyof ApiResponses>(path: Path, body?: ApiRequestBodies[Path]): Promise<ApiResponses[Path]> {
  const requestVersion = viewVersion;
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    credentials: "same-origin",
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const result: unknown = await response.json();
  if (!response.ok) {
    const failure = result as Partial<ApiFailure> | null;
    if (failure?.error?.code === "workspace_locked" && requestVersion === viewVersion) lockView();
    throw new ApiError(failure?.error?.code || "request_failed", failure?.error?.message || "Something went wrong. Please retry.");
  }
  // Successful responses are produced by our backend using the shared contract.
  return result as ApiResponses[Path];
}

function setBusy(value: boolean, label = "Working…") {
  busy = value;
  byId("card").setAttribute("aria-busy", String(value));
  for (const button of document.querySelectorAll("button")) button.disabled = value;
  byId("working").textContent = label;
  byId("working").hidden = !value;
}

async function action(label: string, operation: () => Promise<void>) {
  if (busy) return;
  viewVersion++;
  pollError = false;
  setBusy(true, label);
  message("");
  try { await operation(); }
  catch (error) { message(error instanceof Error ? error.message : "Could not reach Goblin. Please retry."); }
  finally { setBusy(false); schedulePoll(); checkConnectionIfNeeded(); }
}

function schedulePoll() {
  clearTimeout(pollTimer);
  if (unlocked) pollTimer = setTimeout(poll, state?.login ? 2000 : 7000);
}

async function poll() {
  if (!unlocked) return;
  if (!busy && !document.hidden) {
    const requestVersion = viewVersion;
    try {
      const next = await api("/api/status");
      if (requestVersion === viewVersion && !busy && unlocked) {
        if (pollError || (state?.login && !next.login)) message("");
        pollError = false;
        render(next);
      }
    } catch (error) {
      if (requestVersion === viewVersion && unlocked) {
        pollError = true;
        if (state?.account) {
          connectionCheck = { status: "failed", title: "Connection unavailable",
            description: "Goblin couldn’t refresh your account status. Check your connection and try again." };
          renderConnectionCheck();
        } else message(error instanceof Error ? error.message : "Could not reach Goblin. Please retry.");
      }
    }
  }
  schedulePoll();
}

byId("unlock-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const password = byId("workspace-password", HTMLInputElement).value;
  byId("workspace-password", HTMLInputElement).value = "";
  action("Opening workspace…", async () => {
    await api("/api/session", { password });
    unlocked = true;
    showPanel("connect");
    render(await api("/api/status"));
  });
});

byId("chatgpt-button").addEventListener("click", () => action("Getting a sign-in code…", async () => {
  render(await api("/api/auth/chatgpt", {}));
}));

byId("api-key-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const apiKey = byId("api-key", HTMLInputElement).value;
  byId("api-key", HTMLInputElement).value = "";
  action("Checking and saving your key…", async () => {
    render(await api("/api/auth/api-key", { apiKey }));
  });
});

byId("cancel-button").addEventListener("click", () => action("Canceling sign-in…", async () => {
  render(await api("/api/auth/cancel", {}));
}));

byId("disconnect-button").addEventListener("click", () => action("Disconnecting Codex…", async () => {
  render(await api("/api/auth/logout", {}));
}));

byId("check-connection-button").addEventListener("click", () => { void checkConnection(); });

byId("lock-button").addEventListener("click", () => action("Locking workspace…", async () => {
  await api("/api/session/lock", {});
  lockView();
}));

byId("copy-code").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(byId("device-code").textContent);
    byId("copy-code").textContent = "Copied";
    setTimeout(() => { byId("copy-code").textContent = "Copy code"; }, 2000);
  } catch { message("Select and copy the code above, then paste it on OpenAI’s sign-in page.", "info"); }
});

document.addEventListener("visibilitychange", () => { if (!document.hidden && unlocked && !busy) poll(); });

try {
  const session = await api("/api/session");
  unlocked = session.authenticated;
  localDefaultPassword = session.localDefaultPassword;
  if (localDefaultPassword !== undefined) {
    byId("unlock-description").textContent = "The local password is filled in. Select Open workspace to continue.";
    byId("workspace-password", HTMLInputElement).autocomplete = "off";
  }
  if (unlocked) { showPanel("connect"); render(await api("/api/status")); }
  else showPanel("locked");
} catch (error) {
  showPanel(unlocked ? "connect" : "locked");
  message(error instanceof Error ? error.message : "Could not reach Goblin. Refresh to try again.");
}
schedulePoll();
