import type { Account, ApiFailure, ApiRequestBodies, ApiResponses, AuthenticationState, Notice } from "../shared/api.js";

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
let busy = false;
let state: AuthenticationState | null = null;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
type Panel = "loading" | "locked" | "connect" | "pending" | "connected";
let visiblePanel: Panel | undefined;
let viewVersion = 0;
let pollError = false;

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
    if (name === "locked") byId("workspace-code").focus();
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
  clearPromptResult();
  byId("prompt", HTMLTextAreaElement).value = "Say hello in one sentence.";
  showPanel("locked");
}

function clearPromptResult() {
  byId("prompt-result").hidden = true;
  byId("prompt-reply").textContent = "";
  byId("prompt-details").textContent = "";
}

const accountEmail = (account: Account | null | undefined) => account?.type === "chatgpt" ? account.email : null;

function render(next: AuthenticationState) {
  if (!next.account || next.account.type !== state?.account?.type || accountEmail(next.account) !== accountEmail(state?.account)) clearPromptResult();
  state = next;
  if (!unlocked) { showPanel("locked"); return; }
  const account = next.account;
  if (account) {
    const chatgpt = account.type === "chatgpt";
    byId("account-method").textContent = chatgpt ? "ChatGPT" : "OpenAI API key";
    byId("account-detail").textContent = chatgpt
      ? [account.email, account.planType].filter(Boolean).join(" · ") || "Signed in with ChatGPT"
      : "Billed to your OpenAI Platform project";
    byId("connected-description").textContent = chatgpt
      ? "Codex will keep your sign-in up to date. Your connection is saved for the next time you open this workspace."
      : "Your API key is saved in this workspace. Model availability depends on your project’s permissions and billing.";
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
    throw new Error(failure?.error?.message || "Something went wrong. Please retry.");
  }
  // Successful responses are produced by our backend using the shared contract.
  return result as ApiResponses[Path];
}

function setBusy(value: boolean, label = "Working…") {
  busy = value;
  byId("card").setAttribute("aria-busy", String(value));
  for (const button of document.querySelectorAll("button")) button.disabled = value;
  byId("prompt", HTMLTextAreaElement).disabled = value;
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
  finally { setBusy(false); schedulePoll(); }
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
      if (requestVersion === viewVersion) { pollError = true; message(error instanceof Error ? error.message : "Could not reach Goblin. Please retry."); }
    }
  }
  schedulePoll();
}

byId("unlock-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const token = byId("workspace-code", HTMLInputElement).value;
  byId("workspace-code", HTMLInputElement).value = "";
  action("Opening workspace…", async () => {
    await api("/api/session", { token });
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

byId("prompt-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const prompt = byId("prompt", HTMLTextAreaElement).value;
  action("Waiting for Codex to reply…", async () => {
    clearPromptResult();
    const result = await api("/api/prompt", { prompt });
    byId("prompt-reply").textContent = result.reply;
    byId("prompt-details").textContent = [
      result.authType === "chatgpt" ? "ChatGPT" : "OpenAI API key",
      result.model,
      `${(result.durationMs / 1000).toFixed(1)}s`,
    ].join(" · ");
    byId("prompt-result").hidden = false;
  });
});

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
  if (session.usesPassword) {
    byId("workspace-code-label").textContent = "Goblin password";
    byId("unlock-description").textContent = "Enter the password chosen when this Goblin workspace was set up.";
    byId("unlock-footnote").textContent = "This password opens Goblin. You’ll connect your ChatGPT account or API key next.";
    const input = byId("workspace-code", HTMLInputElement);
    input.autocomplete = "current-password";
    input.maxLength = 128;
  }
  if (unlocked) { showPanel("connect"); render(await api("/api/status")); }
  else showPanel("locked");
} catch (error) {
  showPanel(unlocked ? "connect" : "locked");
  message(error instanceof Error ? error.message : "Could not reach Goblin. Refresh to try again.");
}
schedulePoll();
