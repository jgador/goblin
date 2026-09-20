import { Settings } from "../settings/settings.js";
import type { Repository } from "../settings/github.js";
import { icon, escapeHtml as e } from "./presentation.js";
type Attempt = {
    id: string;
    status: string;
    target: {
        runtime: string;
        repository?: {
            repository: string;
            grant?: { login: string; branch: string };
        };
    };
    session?: { model?: string };
    failure?: string;
    environmentReference?: string;
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
type Connection = {
    id: string;
    runtime: string;
    name: string;
    availability: string;
};
type Pending = { path: string; body: Record<string, unknown> };
type IdentityKind = "Work" | "Command" | "Conversation" | "Message";
type Tab = "conversation" | "activity" | "outputs";
let work: View[] = [],
    conversations: Conversation[] = [],
    agents: Agent[] = [],
    connections: Connection[] = [];
let runtimes: { runtime: string; repositoryExecution: boolean }[] = [],
    connectionsLoading = false;
const query = new URLSearchParams(location.search);
let selected =
        query.get("item") ??
        sessionStorage.getItem("goblin.selectedWork") ??
        "",
    activeChat = "";
let view: "work" | "chat" | "new" =
        location.pathname.startsWith("/work") && selected ? "work" : "new",
    tab: Tab = "conversation",
    filter = "all",
    search = "",
    detailsOpen = false,
    conversationsOpen = false;
const mobile = matchMedia("(max-width: 760px)");
let sidebarCollapsed =
    mobile.matches ||
    localStorage.getItem("goblin.sidebarCollapsed") === "true";
let sessionChecked = false,
    workUnavailable = false;
let initialSettings = query.get("settings");
const drafts = new Map<string, string>();
const draftKey = () =>
    view === "work"
        ? `work:${selected}`
        : view === "chat"
          ? `chat:${activeChat}`
          : "new";

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
const settings = new Settings();
let repositories: Repository[] = [];
window.addEventListener("goblin-connections-changed", () => {
    void refreshConnections();
});
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
function forgetWorkspace() {
    work = [];
    conversations = [];
    agents = [];
    connections = [];
    repositories = [];
    github = null;
    draft = "";
    drafts.clear();
    loaded = false;
    settings.reset();
}
async function refresh(preserveError = false) {
    if (loading || sending) return;
    loading = true;
    try {
        authenticated = (await api<{ authenticated: boolean }>("/api/session"))
            .authenticated;
        sessionChecked = true;
        if (!authenticated) {
            forgetWorkspace();
            return;
        }
        if (query.get("returnTo") === "headlamp") {
            location.replace("/headlamp/");
            return;
        }
        // Connection setup remains accessible even when Work storage is unavailable.
        if (initialSettings) {
            const target = initialSettings;
            initialSettings = null;
            void settings.open(target === "codex" ? "codex" : "connections");
        }
        void refreshConnections();
        const results = await Promise.allSettled([
            api<View[]>("/api/work"),
            api<Conversation[]>("/api/conversations"),
            api<Agent[]>("/api/agents"),
            api<typeof runtimes>("/api/runtimes"),
        ]);
        const [items, chats, people, capabilities] = results;
        if (items.status === "fulfilled") {
            work = items.value;
            loaded = true;
            workUnavailable = false;
            if (view === "work" && !work.some((x) => x.work.id === selected))
                view = "new";
            if (!pending && !preserveError) error = "";
        } else {
            workUnavailable = true;
            if (!pending && !preserveError)
                error =
                    "Work could not be loaded. You can still manage connections in Settings.";
        }
        if (chats.status === "fulfilled") conversations = chats.value;
        if (people.status === "fulfilled") agents = people.value;
        if (capabilities.status === "fulfilled") runtimes = capabilities.value;
    } catch (failure) {
        error = (failure as Error).message;
    } finally {
        if (!authenticated) forgetWorkspace();
        loading = false;
        render();
    }
}
async function refreshConnections() {
    if (connectionsLoading) return;
    connectionsLoading = true;
    const [runtime, repository, enabled] = await Promise.allSettled([
        api<Connection[]>("/api/connections"),
        api<NonNullable<typeof github>>("/api/github"),
        api<Repository[]>("/api/github/repositories"),
    ]);
    if (!authenticated) {
        connectionsLoading = false;
        return;
    }
    if (runtime.status === "fulfilled") connections = runtime.value;
    else
        connections = connections.map((c) => ({
            ...c,
            availability: "Status unavailable",
        }));
    if (repository.status === "fulfilled") github = repository.value;
    else if (github)
        github = { ...github, notice: "GitHub status could not be refreshed." };
    if (enabled.status === "fulfilled") repositories = enabled.value;
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
    const submittedDraft = draftKey();
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
        drafts.delete(submittedDraft);
        if (path === "/api/work/commands" && command.action === "Create") {
            selected = String(command.workId);
            view = "work";
            tab = "conversation";
            rememberWork();
        }
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
function connectionButton() {
    const connection =
        connections.find((c) => c.availability === "Available") ??
        connections[0];
    return `<button class="connection-choice" type="button" data-action="settings" title="Manage AI connections">${icon("spark")}${e(connection?.name ?? "AI connections")}${icon("chevron")}</button>`;
}
function composer(kind: string, placeholder: string) {
    return `<div class="composer-wrap"><form class="composer" data-form="${kind}"><label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="3" maxlength="4000" placeholder="${e(placeholder)}" required ${sending ? "disabled" : ""}>${e(draft)}</textarea><div class="composer-footer">${kind === "new" ? connectionButton() : '<span class="composer-note">Your context stays with this work</span>'}<button class="send" type="submit" aria-label="${kind === "new" ? "Create work" : "Send message"}" ${sending || pending || workUnavailable ? "disabled" : ""}>${icon("up")}</button></div></form>${kind === "new" ? '<p class="composer-hint">Save your idea, then start when you’re ready.</p>' : ""}</div>`;
}
function rememberWork() {
    sessionStorage.setItem("goblin.selectedWork", selected);
    history.replaceState(
        null,
        "",
        `/work?item=${encodeURIComponent(selected)}`,
    );
}
function navigate(next: typeof view, id = "") {
    drafts.set(draftKey(), draft);
    view = next;
    if (next === "work") {
        selected = id;
        rememberWork();
    } else if (next === "chat") {
        activeChat = id;
        history.replaceState(
            null,
            "",
            "/?conversation=" + encodeURIComponent(id),
        );
    } else history.replaceState(null, "", "/");
    draft = drafts.get(draftKey()) ?? "";
    changing = false;
    detailsOpen = false;
    tab = "conversation";
    if (mobile.matches) sidebarCollapsed = true;
}
function renderSidebar() {
    const shown = work.filter(
        ({ work: w }) =>
            (filter === "attention"
                ? attention(w)
                : filter === "done"
                  ? w.status === "Completed"
                  : true) &&
            w.objective
                .toLocaleLowerCase()
                .includes(search.toLocaleLowerCase()),
    );
    const chats = conversations.filter((c) =>
        c.title.toLocaleLowerCase().includes(search.toLocaleLowerCase()),
    );
    return `<aside id="workspace-sidebar" class="sidebar" aria-label="Workspace" ${sidebarCollapsed ? "inert" : ""} ${mobile.matches && !sidebarCollapsed ? 'role="dialog" aria-modal="true"' : ""}>
        <div class="sidebar-heading"><a class="brand" href="/" data-action="new-work"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><button id="hide-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Hide sidebar" aria-expanded="true" aria-controls="workspace-sidebar">${icon("sidebar")}</button></div>
        <button class="new-chat" data-action="new-work">${icon("plus")}New work</button>
        <label class="sidebar-search">${icon("search")}<input id="work-search" type="search" placeholder="Search work" aria-label="Search work and conversations" value="${e(search)}" autocomplete="off"></label>
        <div class="sidebar-history" data-scroll="sidebar"><div class="history-heading"><h2>Work</h2><span>${work.filter((x) => attention(x.work)).length} need you</span></div>
        <div class="filter-row" role="group" aria-label="Filter work">${[
            ["all", "All"],
            ["attention", "Needs you"],
            ["done", "Done"],
        ]
            .map(
                ([value, name]) =>
                    `<button class="filter ${filter === value ? "active" : ""}" data-action="filter" data-value="${value}" aria-pressed="${filter === value}">${name}</button>`,
            )
            .join("")}</div>
        <nav class="work-history" aria-label="Recent work">${shown.map(({ work: w }) => `<button class="work-card ${view === "work" && selected === w.id ? "selected" : ""}" data-action="select-work" data-id="${w.id}" aria-current="${view === "work" && selected === w.id ? "page" : "false"}" title="${e(w.objective)} · ${e(label(w.attention?.reason ?? w.status))}"><span class="work-title">${e(w.objective)}</span><span class="work-indicator ${statusClass(w)}">${icon(attention(w) ? "wait" : w.status === "Completed" ? "check" : w.status === "InProgress" ? "activity" : "clock")}<span class="sr-only">${e(label(w.attention?.reason ?? w.status))}</span></span></button>`).join("") || `<p class="sidebar-empty">${search || filter !== "all" ? "No matching work." : loaded ? "Your work will appear here." : "Loading your work…"}</p>`}</nav>
        <div class="conversation-heading"><button class="history-disclosure" data-action="view-chat" aria-expanded="${conversationsOpen}" aria-controls="conversation-history">${icon("chat")}Conversations${icon("chevron")}</button><button class="icon-button" data-action="new-chat" aria-label="New conversation">${icon("plus")}</button></div>
        <nav id="conversation-history" class="work-history" aria-label="Saved conversations" ${conversationsOpen || search ? "" : "hidden"}>${chats.map((c) => `<button class="work-card ${view === "chat" && activeChat === c.id ? "selected" : ""}" data-action="select-chat" data-id="${c.id}" aria-current="${view === "chat" && activeChat === c.id ? "page" : "false"}"><span class="work-title">${e(c.title)}</span></button>`).join("") || '<p class="sidebar-empty">Save ideas and context here.</p>'}</nav></div>
        <div class="sidebar-bottom"><button class="nav-button sidebar-settings" data-action="settings">${icon("settings")}<span>Settings</span></button><button class="icon-button" data-action="lock" aria-label="Lock workspace" title="Lock workspace">${icon("lock")}</button></div></aside>`;
}
function renderHome() {
    const available = connections.some((c) => c.availability === "Available");
    const disconnected =
        !connections.length ||
        connections.every((c) => c.availability === "Disconnected");
    return `<section class="home"><div class="home-content"><img class="home-portrait" src="/assets/branding/icon.svg" alt=""><h1>What should we work on?</h1><p class="home-description">A little help for your next big thing.</p>${composer("new", "Describe the intended outcome…")}${!available && !connectionsLoading ? `<div class="connection-nudge"><span>${disconnected ? "Connect an AI provider when you’re ready to start." : "Your AI connection may need attention."}</span><button data-action="settings">${disconnected ? "Connect an AI provider" : "Manage connections"}${icon("arrow")}</button></div>` : ""}</div></section>`;
}
function render() {
    const active =
        document.activeElement instanceof HTMLElement &&
        root.contains(document.activeElement)
            ? document.activeElement
            : null;
    const activeButton = active?.closest<HTMLButtonElement>(
        "button[data-action]",
    );
    const buttonData = activeButton ? { ...activeButton.dataset } : null;
    const context = view + ":" + selected + ":" + activeChat;
    const sameContext = context === renderedContext;
    const preserved = sameContext
        ? Array.from(
              root.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
                  "input[id],select[id]",
              ),
          ).map((x) => [x.id, x.value] as const)
        : [];
    const openDetails = sameContext
        ? Array.from(
              root.querySelectorAll<HTMLDetailsElement>("details[open][id]"),
          ).map((x) => x.id)
        : [];
    const focus =
        active instanceof HTMLTextAreaElement ||
        active instanceof HTMLInputElement
            ? active
            : null;
    const cursor = focus?.selectionStart,
        cursorEnd = focus?.selectionEnd;
    const scrolls = Array.from(
        root.querySelectorAll<HTMLElement>("[data-scroll]"),
    ).map((x) => [x.dataset.scroll, x.scrollTop] as const);
    renderedContext = context;
    document.title =
        view === "work" && authenticated && current()
            ? current()!.work.objective.slice(0, 70) + " · Goblin"
            : "Goblin";
    if (!authenticated) {
        root.innerHTML = `<main class="unlock-page"><a class="brand" href="/"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><section class="unlock-card"><h1>${sessionChecked ? "Open your workspace" : "Opening your workspace…"}</h1>${sessionChecked ? `<p>Enter the password chosen when this Goblin workspace was set up.</p><form data-form="unlock"><label for="password">Goblin password</label><input id="password" name="password" type="password" autocomplete="current-password" required maxlength="128"><button class="primary" type="submit">Open workspace${icon("arrow")}</button></form>` : ""}${error ? `<p class="command-notice" role="alert">${e(error)}</p>` : ""}</section><p class="unlock-note">Your self-hosted AI coworker</p></main>`;
    } else {
        root.innerHTML = `<a class="skip-link" href="#main-content">Skip to main content</a><div class="app-shell ${sidebarCollapsed ? "sidebar-collapsed" : ""}">${renderSidebar()}${mobile.matches && !sidebarCollapsed ? '<button class="sidebar-backdrop" data-action="toggle-sidebar" aria-label="Close navigation" tabindex="-1"></button>' : ""}<main id="main-content" class="main-shell" tabindex="-1" ${mobile.matches && !sidebarCollapsed ? "inert" : ""}>
            <header class="workspace-header"><div class="header-left"><button id="show-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Show sidebar" aria-expanded="false" aria-controls="workspace-sidebar" ${sidebarCollapsed ? "" : "hidden"}>${icon("sidebar")}</button><span class="workspace-name">${view === "chat" ? "Conversation" : "Goblin"}</span></div><div class="header-actions">${view === "work" && current() ? `<button class="quiet-button" data-action="details" aria-expanded="${detailsOpen}" aria-controls="work-details">${icon("inbox")}Details</button>` : ""}<button class="icon-button" data-action="refresh" aria-label="Refresh" title="Refresh">${icon("refresh")}</button></div></header>
            ${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${pending ? `<div class="command-notice" role="status">${sending ? "Saving command…" : "Command unconfirmed. Inspect the saved state or resend this same command."}${!sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}
            ${view === "chat" ? renderChat() : view === "work" && current() ? `<section class="detail" aria-label="Selected work">${renderDetail()}</section>` : renderHome()}</main></div>`;
    }
    for (const [id, value] of preserved) {
        const field = document.getElementById(id) as
            HTMLInputElement | HTMLSelectElement | null;
        if (field) field.value = value;
    }
    for (const id of openDetails) {
        const detail = document.getElementById(id) as HTMLDetailsElement | null;
        if (detail) detail.open = true;
    }
    if (focus?.id && sameContext) {
        const replacement = document.getElementById(focus.id) as
            HTMLInputElement | HTMLTextAreaElement | null;
        if (replacement && !replacement.closest("[inert]")) {
            replacement.focus({ preventScroll: true });
            if (
                cursor != null &&
                (replacement instanceof HTMLTextAreaElement ||
                    ["text", "search", "password"].includes(replacement.type))
            )
                replacement.setSelectionRange(cursor, cursorEnd ?? cursor);
        }
    }
    if (buttonData?.action)
        Array.from(
            root.querySelectorAll<HTMLButtonElement>("button[data-action]"),
        )
            .find(
                (b) =>
                    b.dataset.action === buttonData.action &&
                    b.dataset.id === buttonData.id &&
                    b.dataset.value === buttonData.value &&
                    !b.closest("[inert]") &&
                    b.getClientRects().length,
            )
            ?.focus({ preventScroll: true });
    for (const [name, top] of scrolls) {
        const element = root.querySelector<HTMLElement>(
            `[data-scroll="${name}"]`,
        );
        if (element && (sameContext || name === "sidebar"))
            element.scrollTop = top;
    }
}
function renderDetail() {
    const item = current();
    if (!item)
        return `<div class="empty-state">${icon("work")}<h3>A place for your next outcome</h3>${button("new-work", "Create work", true)}</div>`;
    const w = item.work,
        attempt = w.attempts.at(-1);
    let body = "";
    if (tab === "activity")
        body = `<div class="timeline">${w.history.map((h) => `<div class="timeline-item"><div class="timeline-icon">${icon("activity")}</div><h4>${e(label(h.kind))}</h4><p class="preserve-lines">${e(h.text ?? (h.failure ? label(h.failure) : ""))}</p><time>${e(time(h.occurredAt))}</time></div>`).join("")}</div><h3>Executions</h3>${w.attempts.map((a) => `<p>${e(a.target.runtime)} · ${e(a.session?.model ?? "Model not reported")} · ${e(label(a.status))}<br><small>Attempt ${e(a.id)}</small>${a.environmentReference ? `<br><small>Environment: ${e(a.environmentReference)}</small>` : ""}${a.target.repository?.grant ? `<br><small>${e(a.target.repository.repository)} · ${e(a.target.repository.grant.branch)} · @${e(a.target.repository.grant.login)}</small>` : ""}</p>`).join("")}`;
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
    const tabs = (["conversation", "activity", "outputs"] as Tab[])
        .map(
            (t) =>
                `<button class="tab ${tab === t ? "active" : ""}" id="tab-${t}" role="tab" aria-selected="${tab === t}" aria-controls="detail-content" tabindex="${tab === t ? 0 : -1}" data-action="tab" data-value="${t}">${icon(t === "conversation" ? "chat" : t === "activity" ? "activity" : "file")}${t[0].toUpperCase() + t.slice(1)}</button>`,
        )
        .join("");
    return `<header class="detail-heading"><div class="title-row"><h2 tabindex="-1">${e(w.objective)}</h2>${status(w)}</div><div id="work-details" class="work-details" ${detailsOpen ? "" : "hidden"}><div class="detail-properties"><span>${e(agents.find((a) => a.id === w.agentId)?.name ?? "Unassigned")}</span><span>Work ${e(w.id)}</span><span>${attempt ? `${e(attempt.target.runtime)}${attempt.session?.model ? " · " + e(attempt.session.model) : ""}` : "No execution yet"}</span></div>${attempt?.target.repository ? `<p class="repository-detail">${icon("branch")}${e(attempt.target.repository.repository)}${attempt.target.repository.grant ? ` · ${e(attempt.target.repository.grant.branch)}` : ""}</p>` : ""}<div class="tabs" role="tablist" aria-label="Work detail views">${tabs}</div></div></header><div class="detail-body" id="detail-content" data-scroll="detail" ${detailsOpen ? `role="tabpanel" aria-labelledby="tab-${tab}"` : 'aria-label="Work conversation"'}><div class="thread-content">${body}${tab === "conversation" ? controls(w) : ""}</div></div>${tab === "conversation" ? composer("work", changing ? "What would you like Goblin to change?" : w.attention?.reason === "InputRequired" ? "Answer Goblin’s question…" : "Add context to this work…") : ""}`;
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
            : `<p>Ready when you are.</p>${button("execute", "Start work", true)}<details id="repository-options"><summary>Repository changes</summary><p>Use an isolated sandbox for changes to a known GitHub repository.</p><label>Repository <select id="repository"><option value="">Choose an enabled repository</option>${repositories
                  .filter((r) => r.enabled)
                  .map(
                      (r) =>
                          `<option value="${e(r.name)}">${e(r.name)}</option>`,
                  )
                  .join(
                      "",
                  )}</select></label><p>Enable repositories in Settings → GitHub.</p><label>Agent Git name <input id="git-name" value="Goblin"></label><label>Agent Git email <input id="git-email" type="email"></label>${runtimes.some((r) => r.repositoryExecution) ? button("repository-execute", "Start repository work") : "<p>Repository execution is unavailable on this Goblin.</p>"}</details>`;
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
    return `<section class="chat-workspace"><div class="detail-body" data-scroll="chat"><div class="thread-content">${c ? `<h1 class="conversation-title">${e(c.title)}</h1>` + c.messages.map((m) => message("You", m.text, m.createdAt)).join("") + (c.workId ? `<button class="tracked-link" data-action="select-work" data-id="${c.workId}">Open tracked work ${icon("arrow")}</button>` : `<div class="decision"><h3>Ready to take this forward?</h3><p>Track this conversation as Work when you want Goblin to execute it.</p>${button("track", "Track this work", true)}</div>`) : `<div class="conversation-empty"><h1>A little room to think</h1><p>Save ideas and context here. Track them as Work when you’re ready to start an agent.</p></div>`}</div></div>${composer("chat", "Add to the conversation…")}</section>`;
}
document.addEventListener("click", async (event) => {
    if (!(event.target instanceof Element)) return;
    const target = event.target.closest<HTMLElement>("[data-action]");
    if (!target) return;
    if (target instanceof HTMLAnchorElement) event.preventDefault();
    const action = target.dataset.action,
        value = target.dataset.value;
    if (
        action === "settings" ||
        action === "settings-codex" ||
        action === "settings-github"
    ) {
        await settings.open(
            action === "settings-github"
                ? "github"
                : action === "settings-codex"
                  ? "codex"
                  : "connections",
        );
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
    if (action === "toggle-sidebar") {
        sidebarCollapsed = !sidebarCollapsed;
        if (!mobile.matches)
            localStorage.setItem(
                "goblin.sidebarCollapsed",
                String(sidebarCollapsed),
            );
        render();
        document
            .getElementById(sidebarCollapsed ? "show-sidebar" : "hide-sidebar")
            ?.focus();
        return;
    }
    if (action === "lock") {
        try {
            await api("/api/session/lock", {});
        } catch (failure) {
            error = (failure as Error).message;
            render();
            return;
        }
        authenticated = false;
        initialSettings = query.get("settings");
        forgetWorkspace();
        error = "";
        render();
        return;
    }
    if (action === "new-work") navigate("new");
    if (action === "view-chat") conversationsOpen = !conversationsOpen;
    if (action === "new-chat") {
        navigate("chat");
        conversationsOpen = true;
    }
    if (action === "select-chat") navigate("chat", target.dataset.id!);
    if (action === "select-work") navigate("work", target.dataset.id!);
    if (action === "details") {
        detailsOpen = !detailsOpen;
        if (!detailsOpen) tab = "conversation";
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
            rememberWork();
        }
    }
    render();
    if (
        ["select-work", "select-chat", "new-work", "new-chat"].includes(
            action ?? "",
        )
    )
        root.querySelector<HTMLElement>(
            action === "select-work" ? ".detail h2" : "#reply",
        )?.focus();
});
document.addEventListener("submit", async (event) => {
    if (
        !(event.target instanceof HTMLFormElement) ||
        !root.contains(event.target)
    )
        return;
    event.preventDefault();
    const kind = event.target.dataset.form,
        data = new FormData(event.target);
    if (kind === "unlock") {
        event.target.querySelector<HTMLInputElement>(
            "input[type=password]",
        )!.value = "";
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
            rememberWork();
        }
    } else if (kind === "chat") {
        const confirmed = await send(
            "/api/conversations/commands",
            async () => {
                const [messageId, conversationId] = await reserve(
                    "Message",
                    ...(activeChat ? [] : ["Conversation" as const]),
                );
                activeChat ||= conversationId;
                return { conversationId: activeChat, messageId, text };
            },
        );
        if (confirmed)
            history.replaceState(
                null,
                "",
                "/?conversation=" + encodeURIComponent(activeChat),
            );
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
    if (
        event.target instanceof HTMLTextAreaElement &&
        root.contains(event.target)
    )
        draft = event.target.value;
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-search"
    ) {
        search = event.target.value;
        render();
    }
});
document.addEventListener("keydown", (event) => {
    if (settings.isOpen) return;
    if (mobile.matches && !sidebarCollapsed) {
        if (event.key === "Escape") {
            event.preventDefault();
            sidebarCollapsed = true;
            render();
            document.getElementById("show-sidebar")?.focus();
        }
        if (event.key === "Tab") {
            const targets = Array.from(
                root.querySelectorAll<HTMLElement>(
                    ".sidebar a, .sidebar button, .sidebar input",
                ),
            ).filter((x) => x.getClientRects().length);
            const first = targets[0],
                last = targets.at(-1);
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last?.focus();
            }
            if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first?.focus();
            }
        }
    }
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
mobile.addEventListener("change", () => {
    sidebarCollapsed =
        mobile.matches ||
        localStorage.getItem("goblin.sidebarCollapsed") === "true";
    render();
});
window.addEventListener("online", () => void refresh());
document.addEventListener("visibilitychange", () => {
    if (!document.hidden) void refresh();
});
setInterval(() => {
    if (!document.hidden) void refresh();
}, 3000);
if (query.has("conversation")) {
    view = "chat";
    activeChat = query.get("conversation") ?? "";
    conversationsOpen = true;
}
render();
void refresh();
