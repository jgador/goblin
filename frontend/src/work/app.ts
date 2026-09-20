import { icon, escapeHtml as e } from "./presentation.js";
type Attempt = {
    id: string;
    status: string;
    target: { runtime: string };
    session?: { model?: string };
    failure?: string;
    cleanupPending?: boolean;
};
type Work = {
    id: string;
    objective: string;
    status: string;
    agentId?: string;
    attention?: { reason: string; failure?: string };
    attempts: Attempt[];
    history: {
        sequence: string;
        kind: string;
        text?: string;
        failure?: string;
        occurredAt: string;
    }[];
    decisions: { id: string; question: string; answer?: string }[];
    results: { attemptId: string; text: string; approvedAt?: string }[];
    artifacts: { name: string; reference: string }[];
};
type View = {
    version: string;
    createdAt: string;
    updatedAt: string;
    work: Work;
};
type Conversation = {
    id: string;
    title: string;
    workId?: string;
    messages: { id: string; text: string; createdAt: string }[];
};
type Agent = { id: string; name: string };
type Connection = { id: string; name: string; availability: string };
type Pending = { path: string; body: Record<string, unknown> };
type IdentityKind = "Work" | "Command" | "Conversation" | "Message";
type Tab = "conversation" | "activity" | "outputs";
let work: View[] = [],
    conversations: Conversation[] = [],
    agents: Agent[] = [],
    connections: Connection[] = [];
let runtimes: { runtime: string; repositoryExecution: boolean }[] = [],
    connectionsLoading = false;
let selected = sessionStorage.getItem("goblin.selectedWork") ?? "",
    activeChat = "";
let view: "work" | "chat" | "new" = "work",
    tab: Tab = "conversation",
    filter = "all",
    detailOpen = false;
let github: {
    configured: boolean;
    login?: string;
    userCode?: string;
    verificationUrl?: string;
    notice?: string;
} | null = null;
let authenticated = false,
    loaded = false,
    error = "",
    sending = false,
    changing = false,
    loading = false;
let pending: Pending | null = JSON.parse(
        sessionStorage.getItem("goblin.pendingCommand") ?? "null",
    ),
    draft = "";
let renderedContext = "";
const root = document.querySelector<HTMLDivElement>("#app")!;
const current = () => work.find((x) => x.work.id === selected);
const time = (value: string) => new Date(value).toLocaleString();
const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");
const attention = (w: Work) => w.status === "NeedsAttention";
const statusClass = (w: Work) =>
    w.status === "Completed"
        ? "done"
        : attention(w)
          ? w.attention?.reason === "ResultReview"
              ? "review"
              : "waiting"
          : w.status === "InProgress"
            ? "working"
            : "paused";
const status = (w: Work) =>
    `<span class="status-pill ${statusClass(w)}">${icon(attention(w) ? "wait" : w.status === "Completed" ? "check" : "activity")}${e(label(w.attention?.reason ?? w.status))}</span>`;
const button = (action: string, text: string, primary = false) =>
    `<button class="${primary ? "primary" : "secondary"}" data-action="${action}" ${sending ? "disabled" : ""}>${e(text)}</button>`;
async function api<T>(path: string, body?: unknown): Promise<T> {
    const response = await fetch(
        path,
        body === undefined
            ? { cache: "no-store", signal: AbortSignal.timeout(6000) }
            : {
                  method: "POST",
                  headers: { "Content-Type": "application/json" },
                  body: JSON.stringify(body),
              },
    );
    const result = await response.json();
    if (response.status === 401) authenticated = false;
    if (!response.ok)
        throw new Error(
            result.error?.message ?? "The request could not be confirmed.",
        );
    return result as T;
}
async function refresh(preserveError = false) {
    if (loading || sending) return;
    loading = true;
    try {
        authenticated = (await api<{ authenticated: boolean }>("/api/session"))
            .authenticated;
        if (authenticated) {
            [work, conversations, agents, runtimes] = await Promise.all([
                api<View[]>("/api/work"),
                api<Conversation[]>("/api/conversations"),
                api<Agent[]>("/api/agents"),
                api<typeof runtimes>("/api/runtimes"),
            ]);
            if (!work.some((x) => x.work.id === selected))
                selected = work[0]?.work.id ?? "";
            if (!pending && !preserveError) error = "";
            loaded = true;
            void refreshConnections();
        }
    } catch (failure) {
        error = (failure as Error).message;
    } finally {
        loading = false;
        render();
    }
}
async function refreshConnections() {
    if (connectionsLoading) return;
    connectionsLoading = true;
    const [runtime, repository] = await Promise.allSettled([
        api<Connection[]>("/api/connections"),
        api<NonNullable<typeof github>>("/api/github"),
    ]);
    if (runtime.status === "fulfilled") connections = runtime.value;
    else
        connections = connections.map((c) => ({
            ...c,
            availability: "Status unavailable",
        }));
    if (repository.status === "fulfilled") github = repository.value;
    else if (github)
        github = { ...github, notice: "GitHub status could not be refreshed." };
    connectionsLoading = false;
    render();
}
async function send(
    path: string,
    body: Record<string, unknown> | (() => Promise<Record<string, unknown>>),
    repeat = false,
) {
    if (sending || (pending && !repeat)) return false;
    sending = true;
    error = "";
    let confirmed = false;
    render();
    try {
        const command = typeof body === "function" ? await body() : body;
        pending = { path, body: command };
        sessionStorage.setItem(
            "goblin.pendingCommand",
            JSON.stringify(pending),
        );
        render();
        await api(path, command);
        pending = null;
        sessionStorage.removeItem("goblin.pendingCommand");
        draft = "";
        changing = false;
        confirmed = true;
    } catch (failure) {
        error = (failure as Error).message;
    } finally {
        sending = false;
        await refresh(!confirmed);
    }
    return confirmed;
}
async function reserve(...kinds: IdentityKind[]): Promise<string[]> {
    const result = await api<{ ids: string[] }>("/api/identities", { kinds });
    return result.ids;
}
async function command(action: string, extra: Record<string, unknown> = {}) {
    const item = current();
    if (item)
        await send("/api/work/commands", async () => {
            const [commandId] = await reserve("Command");
            return {
                commandId,
                workId: item.work.id,
                action,
                expectedVersion: item.version,
                ...extra,
            };
        });
}
function message(author: string, text: string, at?: string) {
    return `<div class="message"><div class="${author === "Goblin" ? "bot-avatar" : "avatar"}">${author === "Goblin" ? '<img src="/assets/branding/icon.svg" alt="">' : "You"}</div><div><div class="message-name">${e(author)}<time>${at ? e(time(at)) : ""}</time></div><div class="message-text"><p class="preserve-lines">${e(text)}</p></div></div></div>`;
}
function composer(kind: string, placeholder: string) {
    return `<div class="composer-wrap"><form class="composer" data-form="${kind}"><label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="3" maxlength="4000" placeholder="${e(placeholder)}" required ${sending ? "disabled" : ""}>${e(draft)}</textarea><div class="composer-footer"><span class="composer-note">${icon("link")}Your context stays here</span><button class="send" type="submit" aria-label="Send message" ${sending || pending ? "disabled" : ""}>${icon("up")}</button></div></form></div>`;
}
function render() {
    const context = view + ":" + selected + ":" + activeChat;
    const preserved =
        context === renderedContext
            ? Array.from(
                  document.querySelectorAll<
                      HTMLInputElement | HTMLSelectElement
                  >("input[id],select[id]"),
              ).map((x) => [x.id, x.value] as const)
            : [];
    const openDetails =
        context === renderedContext &&
        !!document.querySelector("details[open]");
    const focus =
            document.activeElement instanceof HTMLTextAreaElement ||
            document.activeElement instanceof HTMLInputElement
                ? document.activeElement
                : null,
        cursor = focus?.selectionStart;
    const focusId = focus?.id;
    renderedContext = context;
    if (focus instanceof HTMLTextAreaElement) draft = focus.value;
    const scroll = document.querySelector(".detail-body")?.scrollTop ?? 0;
    root.innerHTML = `<div class="app-shell"><aside class="sidebar"><div class="brand"><img src="/assets/branding/icon.svg" alt="Goblin"><span>goblin</span></div><button class="new-chat" data-action="new-work">${icon("plus")}New work</button><nav class="nav" aria-label="Main navigation"><button class="nav-button ${view !== "chat" ? "active" : ""}" data-action="view-work">${icon("work")}Work<span class="nav-count">${work.filter((x) => attention(x.work)).length}</span></button><button class="nav-button ${view === "chat" ? "active" : ""}" data-action="view-chat">${icon("chat")}Conversations</button></nav><div class="sidebar-note"><div class="section-label">Your workspace</div><p>A place for the things<br>we’re working on together.</p></div><div class="sidebar-bottom"><a class="connection-link" href="/">Connection settings</a><p>${connections.map((c) => `${e(c.name)} · ${e(c.availability)}`).join("<br>")}</p>${github ? `<div class="github-connection"><strong>GitHub</strong><p>${e(github.login ?? (github.configured ? "Not connected" : "OAuth app not configured"))}</p>${github.notice ? `<p>${e(github.notice)}</p>` : ""}${github.userCode ? `<p>Code: <strong>${e(github.userCode)}</strong></p><a href="https://github.com/login/device" target="_blank" rel="noreferrer">Finish GitHub sign-in</a>` : github.login ? button("github-disconnect", "Disconnect GitHub") : github.configured ? button("github-connect", "Connect GitHub") : ""}</div>` : ""}</div></aside><main class="main-shell"><div class="preview-bar"><div class="preview-left"><button class="mobile-menu" data-action="view-work">${icon("work")}goblin</button><button class="mobile-menu" data-action="view-chat">${icon("chat")}Conversations</button><span class="preview-label">Your work</span></div><div class="preview-right"><a class="connection-link" href="/">Connections</a><button class="reset" data-action="refresh">${icon("refresh")}Refresh</button></div></div>${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${pending ? `<div class="command-notice" role="status">${sending ? "Saving command…" : "Command unconfirmed. Inspect the saved state or resend this same command."}${!sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}${!authenticated ? `<section class="chat-empty"><div class="goblin-portrait"><img src="/assets/branding/icon.svg" alt="Goblin"></div><h1>Unlock your workspace</h1><p>Your work and its history stay here.</p><form data-form="unlock"><label for="password">Goblin password</label><input id="password" name="password" type="password" autocomplete="current-password" required><button class="primary">Unlock</button></form></section>` : view === "chat" ? renderChat() : view === "new" ? `<section class="chat-workspace"><div class="chat-scroll"><div class="chat-inner"><div class="chat-empty"><div class="goblin-portrait"><img src="/assets/branding/icon.svg" alt="Goblin"></div><h1>What should we work on?</h1><p>Start with the outcome. Assign an agent when you’re ready.</p></div></div></div>${composer("new", "Describe the intended outcome…")}</section>` : renderWork()}</main></div>`;
    for (const [id, value] of preserved) {
        const field = document.getElementById(id) as
            HTMLInputElement | HTMLSelectElement | null;
        if (field) field.value = value;
    }
    const details = document.querySelector("details");
    if (details && openDetails) details.open = true;
    if (focusId) {
        const replacement = document.getElementById(focusId) as
            HTMLTextAreaElement | HTMLInputElement | null;
        replacement?.focus();
        if (
            cursor != null &&
            replacement &&
            (replacement instanceof HTMLTextAreaElement ||
                ["text", "password"].includes(replacement.type))
        )
            replacement.setSelectionRange(cursor, cursor);
    }
    const body = document.querySelector(".detail-body");
    if (body) body.scrollTop = scroll;
}
function renderWork() {
    const shown = work.filter((x) =>
        filter === "attention"
            ? attention(x.work)
            : filter === "done"
              ? x.work.status === "Completed"
              : true,
    );
    return `<div class="workspace ${detailOpen ? "detail-open" : ""}"><section class="work-list" aria-label="Tracked work"><div class="list-heading"><div><h1>Work</h1><p>${work.length} tracked · ${work.filter((x) => attention(x.work)).length} need you</p></div><button class="icon-button" data-action="new-work" aria-label="Create work">${icon("plus")}</button></div><div class="filter-row" role="group" aria-label="Filter work">${[
        ["all", "All work"],
        ["attention", "Needs you"],
        ["done", "Done"],
    ]
        .map(
            ([value, name]) =>
                `<button class="filter ${filter === value ? "active" : ""}" data-action="filter" data-value="${value}" aria-pressed="${filter === value}">${name}</button>`,
        )
        .join(
            "",
        )}</div><div class="list-scroll">${shown.map(({ work: w }) => `<button class="work-card ${selected === w.id ? "selected" : ""}" data-action="select-work" data-id="${w.id}" aria-pressed="${selected === w.id}"><div class="card-top">${status(w)}</div><h3>${e(w.objective)}</h3><p>${e(label(w.attention?.failure ?? w.status))}</p><div class="work-card-foot"><img src="/assets/branding/icon.svg" alt="">${e(agents.find((a) => a.id === w.agentId)?.name ?? "Unassigned")}<span class="card-work-id">${w.id.slice(0, 8)}</span></div></button>`).join("") || `<div class="empty-state">${icon("work")}<h3>${loaded ? "No work here yet" : "Loading work…"}</h3><p>Create work whenever there’s something to carry forward.</p></div>`}</div></section><section class="detail" aria-label="Work details">${renderDetail()}</section></div>`;
}
function renderDetail() {
    const item = current();
    if (!item)
        return `<div class="empty-state">${icon("work")}<h3>A place for your next outcome</h3>${button("new-work", "Create work", true)}</div>`;
    const w = item.work,
        attempt = w.attempts.at(-1);
    let body = "";
    if (tab === "activity")
        body = `<div class="timeline">${w.history.map((h) => `<div class="timeline-item"><div class="timeline-icon">${icon("activity")}</div><h4>${e(label(h.kind))}</h4><p class="preserve-lines">${e(h.text ?? (h.failure ? label(h.failure) : ""))}</p><time>${e(time(h.occurredAt))}</time></div>`).join("")}</div><h3>Executions</h3>${w.attempts.map((a) => `<p>${e(a.target.runtime)} · ${e(a.session?.model ?? "Model not reported")} · ${e(label(a.status))}<br><small>${e(a.id)}</small></p>`).join("")}`;
    else if (tab === "outputs")
        body =
            w.results
                .map(
                    (r) =>
                        `<article class="output-card"><div class="output-card-header">${icon("file")}<h3>${r.approvedAt ? "Approved result" : "Proposed result"}</h3></div><div class="output-content preserve-lines">${e(r.text)}</div>${r.approvedAt ? `<p>Approved ${e(time(r.approvedAt))}</p>` : ""}</article>`,
                )
                .join("") +
                w.artifacts
                    .map(
                        (a) =>
                            `<p>${/^https:\/\/github\.com\//.test(a.reference) ? `<a href="${e(a.reference)}" rel="noreferrer">${e(a.name)}</a>` : e(a.name)}</p>`,
                    )
                    .join("") ||
            `<div class="empty-state">${icon("file")}<p>Results will appear here.</p></div>`;
    else
        body =
            message("You", w.objective, item.createdAt) +
            w.history
                .filter(
                    (h) =>
                        h.text &&
                        [
                            "ContextAdded",
                            "InputProvided",
                            "ChangesRequested",
                            "ResultProposed",
                            "InputRequested",
                        ].includes(h.kind),
                )
                .map((h) =>
                    message(
                        ["ResultProposed", "InputRequested"].includes(h.kind)
                            ? "Goblin"
                            : "You",
                        h.text!,
                        h.occurredAt,
                    ),
                )
                .join("");
    return `<header class="detail-heading"><div class="detail-meta"><div class="detail-meta-left"><button class="mobile-back" data-action="back">${icon("back")}Work</button><span>Work · ${w.id.slice(0, 8)}</span></div><span class="saved">${icon("check")}Saved</span></div><div class="title-row"><h2>${e(w.objective)}</h2>${status(w)}</div><div class="detail-properties"><span class="assignee"><img src="/assets/branding/icon.svg" alt="">${e(agents.find((a) => a.id === w.agentId)?.name ?? "Unassigned")}</span><span>${attempt ? `${e(attempt.target.runtime)}${attempt.session?.model ? " · " + e(attempt.session.model) : ""}` : "No execution yet"}</span></div><div class="tabs" role="tablist" aria-label="Work detail views">${(["conversation", "activity", "outputs"] as Tab[]).map((t) => `<button class="tab ${tab === t ? "active" : ""}" id="tab-${t}" role="tab" aria-selected="${tab === t}" aria-controls="detail-content" tabindex="${tab === t ? 0 : -1}" data-action="tab" data-value="${t}">${icon(t === "conversation" ? "chat" : t === "activity" ? "activity" : "file")}${t[0].toUpperCase() + t.slice(1)}</button>`).join("")}</div></header><div class="detail-body" id="detail-content" role="tabpanel" aria-labelledby="tab-${tab}">${controls(w)}${body}</div>${tab === "conversation" ? composer("work", changing ? "What would you like Goblin to change?" : w.attention?.reason === "InputRequired" ? "Answer Goblin’s question…" : "Add context to this work…") : ""}`;
}
function controls(w: Work) {
    if (w.attention?.reason === "CleanupRequired")
        return `<div class="decision"><h3>Needs attention</h3><p>The outcome is saved, but Goblin could not finish cleaning up the execution. Reconcile it before continuing.</p>${button("reconcile", "Reconcile execution", true)}</div>`;
    if (w.attempts.at(-1)?.cleanupPending)
        return `<div class="decision"><p>Saving the outcome and finishing execution cleanup…</p></div>`;
    let content = "";
    if (w.status === "Ready")
        content = !w.agentId
            ? `<p>Choose the agent responsible for this work.</p><label for="agent">Agent</label><select id="agent">${agents.map((a) => `<option value="${a.id}">${e(a.name)}</option>`).join("")}</select>${button("assign", "Assign agent", true)}`
            : `<p>Ready when you are.</p>${button("execute", "Start work", true)}<details><summary>Repository changes</summary><p>Use an isolated sandbox for changes to a known GitHub repository.</p><label>Repository <input id="repository" placeholder="owner/repository"></label><label>Agent Git name <input id="git-name" value="Goblin"></label><label>Agent Git email <input id="git-email" type="email"></label>${runtimes.some((r) => r.repositoryExecution) ? button("repository-execute", "Start repository work") : "<p>Repository execution is unavailable on this Goblin.</p>"}</details>`;
    if (w.attention?.reason === "ResultReview")
        content = `<h3>Ready for your review</h3><p>You decide when the outcome is complete.</p>${button("approve", "Approve & complete", true)}${button("changes", "Ask for changes")}`;
    if (w.attention?.reason === "InputRequired")
        content = `<h3>Needs your input</h3><p>${e(w.decisions.at(-1)?.question)}</p>`;
    if (w.attention?.reason === "Failure")
        content = `<h3>Needs attention</h3><p>${e(label(w.attention.failure ?? "Execution failed"))}. Check the connection and execution history before retrying.</p>${button("retry", "Retry work", true)}`;
    if (w.attention?.reason === "UncertainExecution")
        content = `<h3>Execution needs reconciliation</h3><p>Goblin cannot yet confirm the outcome. Check the execution or stop it before retrying.</p>${button("reconcile", "Reconcile execution", true)}`;
    if (["Queued", "InProgress", "Cancelling"].includes(w.status))
        content = `<div class="progress-title">${icon("activity")} ${e(label(w.status))}</div><p>You can close this page. Accepted work continues independently.</p>`;
    if (!["Completed", "Cancelled", "Cancelling"].includes(w.status))
        content += button("cancel", "Cancel work");
    return content ? `<div class="decision">${content}</div>` : "";
}
function renderChat() {
    const c = conversations.find((x) => x.id === activeChat);
    return `<div class="workspace"><section class="work-list conversation-rail"><div class="list-heading"><h1>Conversations</h1><button class="icon-button" data-action="new-chat" aria-label="New conversation">${icon("plus")}</button></div><div class="list-scroll">${conversations.map((c) => `<button class="conversation-list-button ${c.id === activeChat ? "selected" : ""}" data-action="select-chat" data-id="${c.id}">${e(c.title)}</button>`).join("")}</div></section><section class="chat-workspace"><header class="chat-top">${icon("chat")}Conversation</header><div class="chat-scroll"><div class="chat-inner">${c ? c.messages.map((m) => message("You", m.text, m.createdAt)).join("") + (c.workId ? `<button class="tracked-link" data-action="select-work" data-id="${c.workId}">Open tracked work ${icon("arrow")}</button>` : `<div class="track-offer"><h3>Give this an outcome to carry forward</h3>${button("track", "Track this work", true)}</div>`) : `<div class="chat-empty"><h1>A little room to think</h1><p>Keep context here, then track it as Work when you want Goblin to execute it.</p></div>`}</div></div>${composer("chat", "Add to the conversation…")}</section></div>`;
}
document.addEventListener("click", async (event) => {
    if (!(event.target instanceof Element)) return;
    const target = event.target.closest<HTMLElement>("[data-action]");
    if (!target) return;
    const action = target.dataset.action,
        value = target.dataset.value;
    if (action === "github-connect" || action === "github-disconnect") {
        try {
            github = await api(
                "/api/github/" +
                    (action === "github-connect" ? "connect" : "disconnect"),
                {},
            );
        } catch (failure) {
            error = (failure as Error).message;
        }
        render();
        return;
    }
    if (action === "refresh") {
        await refresh();
        return;
    }
    if (action === "resend" && pending) {
        await send(pending.path, pending.body, true);
        return;
    }
    if (action === "dismiss") {
        pending = null;
        sessionStorage.removeItem("goblin.pendingCommand");
        error = "";
    }
    if (action === "new-work") {
        view = "new";
        draft = "";
    }
    if (action === "view-work") {
        view = "work";
        detailOpen = false;
    }
    if (action === "back") detailOpen = false;
    if (action === "view-chat") view = "chat";
    if (action === "new-chat") {
        view = "chat";
        activeChat = "";
        draft = "";
    }
    if (action === "select-chat") {
        activeChat = target.dataset.id!;
        view = "chat";
        draft = "";
    }
    if (action === "select-work") {
        selected = target.dataset.id!;
        view = "work";
        detailOpen = true;
        draft = "";
        sessionStorage.setItem("goblin.selectedWork", selected);
    }
    if (action === "tab") tab = value as Tab;
    if (action === "filter") filter = value!;
    if (action === "changes") {
        changing = true;
        tab = "conversation";
    }
    if (action === "assign")
        await command("Assign", {
            agentId: document.querySelector<HTMLSelectElement>("#agent")?.value,
        });
    if (action === "execute") await command("Execute");
    if (action === "repository-execute")
        await command("Execute", {
            repository: {
                repository:
                    document.querySelector<HTMLInputElement>("#repository")
                        ?.value,
                gitAuthorName:
                    document.querySelector<HTMLInputElement>("#git-name")
                        ?.value,
                gitAuthorEmail:
                    document.querySelector<HTMLInputElement>("#git-email")
                        ?.value,
            },
        });
    if (action === "retry") await command("Retry");
    if (action === "reconcile") await command("Reconcile");
    if (action === "cancel") await command("Cancel");
    if (action === "approve")
        await command("Approve", {
            attemptId: current()?.work.attempts.at(-1)?.id,
        });
    if (action === "track") {
        const c = conversations.find((x) => x.id === activeChat)!;
        const confirmed = await send(
            "/api/conversations/commands",
            async () => {
                const [workId, messageId] = await reserve("Work", "Message");
                return {
                    conversationId: c.id,
                    messageId,
                    text: null,
                    workId,
                };
            },
        );
        if (confirmed) {
            selected = conversations.find((x) => x.id === c.id)?.workId ?? "";
            view = "work";
            detailOpen = true;
        }
    }
    render();
});
document.addEventListener("submit", async (event) => {
    if (!(event.target instanceof HTMLFormElement)) return;
    event.preventDefault();
    const kind = event.target.dataset.form,
        data = new FormData(event.target);
    if (kind === "unlock") {
        try {
            await api("/api/session", { password: data.get("password") });
            await refresh();
        } catch (failure) {
            error = (failure as Error).message;
            render();
        }
        return;
    }
    const text = String(data.get("reply") ?? "").trim();
    if (!text) return;
    if (kind === "new") {
        let id = "";
        const confirmed = await send("/api/work/commands", async () => {
            const [commandId, workId] = await reserve("Command", "Work");
            id = workId;
            return { commandId, workId, action: "Create", text };
        });
        if (confirmed) {
            selected = id;
            view = "work";
            detailOpen = true;
        }
    } else if (kind === "chat") {
        await send("/api/conversations/commands", async () => {
            const [messageId, conversationId] = await reserve(
                "Message",
                ...(activeChat ? [] : ["Conversation" as const]),
            );
            activeChat ||= conversationId;
            return { conversationId: activeChat, messageId, text };
        });
    } else {
        const w = current()?.work;
        await command(
            changing
                ? "RequestChanges"
                : w?.attention?.reason === "InputRequired"
                  ? "Answer"
                  : "AddContext",
            {
                text,
                attemptId: w?.attempts.at(-1)?.id,
                decisionId: w?.decisions.at(-1)?.id,
            },
        );
    }
    render();
});
document.addEventListener("input", (event) => {
    if (event.target instanceof HTMLTextAreaElement) draft = event.target.value;
});
document.addEventListener("keydown", (event) => {
    if (
        event.target instanceof HTMLTextAreaElement &&
        event.key === "Enter" &&
        !event.shiftKey &&
        !event.isComposing
    ) {
        event.preventDefault();
        event.target.form?.requestSubmit();
    }
    if (
        event.target instanceof HTMLElement &&
        event.target.matches('[role="tab"]') &&
        ["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)
    ) {
        event.preventDefault();
        const tabs: Tab[] = ["conversation", "activity", "outputs"];
        tab =
            tabs[
                event.key === "Home"
                    ? 0
                    : event.key === "End"
                      ? 2
                      : (tabs.indexOf(tab) +
                            (event.key === "ArrowRight" ? 1 : 2)) %
                        3
            ];
        render();
        document.getElementById("tab-" + tab)?.focus();
    }
});
window.addEventListener("online", () => void refresh());
document.addEventListener("visibilitychange", () => {
    if (!document.hidden) void refresh();
});
setInterval(() => {
    if (!document.hidden) void refresh();
}, 3000);
render();
void refresh();
