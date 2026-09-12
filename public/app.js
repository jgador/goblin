const byId = (id) => document.getElementById(id);
for (const container of document.querySelectorAll("[data-device-login-help]")) {
  container.append(byId("device-login-help-template").content.cloneNode(true));
}
let unlocked = false;
let busy = false;
let state = null;
let pollTimer;
let visiblePanel;
let viewVersion = 0;
let pollError = false;

function message(text, kind = "error") {
  const box = byId("message");
  box.textContent = text || "";
  box.hidden = !text;
  box.classList.toggle("info", kind === "info");
}

function showPanel(name) {
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
  byId("api-key").value = "";
  byId("verification-link").removeAttribute("href");
  showPanel("locked");
}

function render(next) {
  state = next;
  if (!unlocked) { showPanel("locked"); return; }
  if (next.account) {
    const chatgpt = next.account.type === "chatgpt";
    byId("account-method").textContent = chatgpt ? "ChatGPT" : "OpenAI API key";
    byId("account-detail").textContent = chatgpt
      ? [next.account.email, next.account.planType].filter(Boolean).join(" · ") || "Signed in with ChatGPT"
      : "Billed to your OpenAI Platform project";
    byId("connected-description").textContent = chatgpt
      ? "Codex will keep your sign-in up to date. Your connection is saved for the next time you open this workspace."
      : "Your API key is saved in this workspace. Model availability depends on your project’s permissions and billing.";
    showPanel("connected");
  } else if (next.login) {
    byId("device-code").textContent = next.login.userCode;
    byId("verification-link").href = next.login.verificationUrl;
    showPanel("pending");
  } else {
    byId("device-code").textContent = "";
    byId("verification-link").removeAttribute("href");
    showPanel("connect");
  }
  if (next.notice) message(next.notice.message, next.notice.kind);
}

async function api(path, body) {
  const requestVersion = viewVersion;
  const response = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    credentials: "same-origin",
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const result = await response.json();
  if (!response.ok) {
    if (result.error?.code === "workspace_locked" && requestVersion === viewVersion) lockView();
    throw new Error(result.error?.message || "Something went wrong. Please retry.");
  }
  return result;
}

function setBusy(value, label = "Working…") {
  busy = value;
  byId("card").setAttribute("aria-busy", String(value));
  for (const button of document.querySelectorAll("button")) button.disabled = value;
  byId("working").textContent = label;
  byId("working").hidden = !value;
}

async function action(label, operation) {
  if (busy) return;
  viewVersion++;
  pollError = false;
  setBusy(true, label);
  message("");
  try { await operation(); }
  catch (error) { message(error.message || "Could not reach Goblin. Please retry."); }
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
      if (requestVersion === viewVersion) { pollError = true; message(error.message); }
    }
  }
  schedulePoll();
}

byId("unlock-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const token = byId("workspace-code").value;
  byId("workspace-code").value = "";
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
  const apiKey = byId("api-key").value;
  byId("api-key").value = "";
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

byId("lock-button").addEventListener("click", () => action("Locking preview…", async () => {
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
  unlocked = (await api("/api/session")).authenticated;
  if (unlocked) { showPanel("connect"); render(await api("/api/status")); }
  else showPanel("locked");
} catch (error) {
  showPanel(unlocked ? "connect" : "locked");
  message(error.message || "Could not reach Goblin. Refresh to try again.");
}
schedulePoll();
