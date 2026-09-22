import { Settings } from "../settings/settings.js";
import { SystemResources } from "../settings/system.js";
import type { Repository } from "../settings/github.js";
import { icon, escapeHtml as e } from "./presentation.js";
import type { Work, View, Conversation } from "./contracts.js";
import {
    renderProgress,
    renderDecisions,
    renderOutputs,
    renderActivity,
    renderConversation,
    matchesWork,
    relativeTime,
} from "./surface.js";
type Agent = { id: string; name: string };
type Connection = {
    id: string;
    runtime: string;
    name: string;
    availability: string;
};
type Pending = { path: string; body: Record<string, unknown> };
type IdentityKind = "Work" | "Command" | "Conversation" | "Message";
let work: View[] = [],
    conversations: Conversation[] = [],
    agents: Agent[] = [],
    connections: Connection[] = [];
let runtimes: { runtime: string; repositoryExecution: boolean }[] = [],
    connectionsLoading = false,
    connectionsLoaded = false;
const query = new URLSearchParams(location.search);
let selected =
        query.get("item") ??
        sessionStorage.getItem("goblin.selectedWork") ??
        "",
    activeChat = "";
let view: "work" | "chat" | "new" =
        location.pathname.startsWith("/work") && selected ? "work" : "new",
    filter = "all",
    search = "",
    conversationsOpen = false;
let selectInitialWork = !query.has("new") && !query.has("conversation");
const mobile = matchMedia("(max-width: 760px)");
const compact = matchMedia("(max-width: 1199px)");
let activityOpen = false,
    conversationExpanded = false;
let sidebarCollapsed =
    mobile.matches ||
    localStorage.getItem("goblin.sidebarCollapsed") === "true";
let sessionChecked = false,
    workUnavailable = false;
let initialSettings = query.get("settings");
const drafts = new Map<string, string>();
const setupDrafts = new Map<
    string,
    { values: [string, string][]; open: boolean }
>();
const setupErrors = new Map<string, Record<string, string>>();
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
const system = new SystemResources();
const settings = new Settings(system);
window.addEventListener("goblin-workspace-locked", () => {
    authenticated = false;
    forgetWorkspace();
    render();
});
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
    `<button class="${primary ? "primary" : "secondary"}" data-action="${action}" ${sending || ((pending || workUnavailable) && !["resend", "dismiss", "new-work", "changes"].includes(action)) ? "disabled" : ""}>${e(text)}</button>`;
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
    connectionsLoaded = false;
    repositories = [];
    github = null;
    draft = "";
    drafts.clear();
    setupDrafts.clear();
    setupErrors.clear();
    loaded = false;
    system.reset();
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
            void settings.open(
                ["codex", "github", "system", "cluster"].includes(target)
                    ? target
                    : "connections",
            );
        }
        void refreshConnections();
        void system.refresh();
        const results = await Promise.allSettled([
            api<View[]>("/api/work"),
            api<Conversation[]>("/api/conversations"),
            api<Agent[]>("/api/agents"),
            api<typeof runtimes>("/api/runtimes"),
        ]);
        const [items, chats, people, capabilities] = results;
        if (items.status === "fulfilled") {
            work = items.value;
            if (selectInitialWork) {
                selectInitialWork = false;
                const initial =
                    work.find((x) => x.work.id === selected) ??
                    work.toSorted(
                        (a, b) =>
                            Date.parse(b.updatedAt) - Date.parse(a.updatedAt),
                    )[0];
                if (initial) navigate("work", initial.work.id);
            }
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
        render(true);
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
    connectionsLoaded = true;
    render(true);
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
            conversationExpanded = false;
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
    return `<div class="composer-wrap"><form class="composer" data-form="${kind}"><label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="3" maxlength="4000" placeholder="${e(placeholder)}" required ${sending ? "disabled" : ""}>${e(draft)}</textarea><div class="composer-footer">${kind === "new" ? connectionButton() : `<span class="composer-note">${kind === "chat" ? "Saved as a conversation · Track as Work to start Goblin" : changing ? "Requests changes to the current result" : current()?.work.attention?.reason === "InputRequired" ? "Answers the pending question" : current()?.work.status === "Ready" ? "Saves context for this work · Start work when ready" : "Saves context with this work"}</span>`}<button class="send" type="submit" aria-label="${kind === "new" ? "Create work" : "Send message"}" ${sending || pending || workUnavailable ? "disabled" : ""}>${icon("up")}</button></div></form>${kind === "new" ? '<p class="composer-hint">Save your idea, then start when you’re ready.</p>' : ""}</div>`;
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
    selectInitialWork = false;
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
    } else history.replaceState(null, "", "/?new=work");
    draft = drafts.get(draftKey()) ?? "";
    changing = false;
    conversationExpanded = false;
    activityOpen = false;
    if (mobile.matches) sidebarCollapsed = true;
}
function renderSidebar() {
    const matches = work.filter(({ work: w }) =>
        matchesWork(
            w,
            search,
            conversations,
            agents.find((a) => a.id === w.agentId)?.name ?? "",
        ),
    );
    const inView = (w: Work) =>
        filter === "attention"
            ? attention(w)
            : filter === "done"
              ? w.status === "Completed"
              : filter === "assigned"
                ? !!w.agentId && !["Completed", "Cancelled"].includes(w.status)
                : true;
    const shown = matches
        .filter((x) => search || inView(x.work))
        .toSorted(
            (a, b) =>
                Date.parse(filter === "recent" ? b.updatedAt : b.createdAt) -
                Date.parse(filter === "recent" ? a.updatedAt : a.createdAt),
        );
    const chats = conversations.filter(
        (c) =>
            !c.workId &&
            [c.title, ...c.messages.map((m) => m.text)].some((text) =>
                text.toLocaleLowerCase().includes(search.toLocaleLowerCase()),
            ),
    );
    return `<aside id="workspace-sidebar" class="sidebar" aria-label="Workspace" ${sidebarCollapsed || (compact.matches && activityOpen) ? "inert" : ""} ${mobile.matches && !sidebarCollapsed ? 'role="dialog" aria-modal="true"' : ""}>
        <div class="sidebar-heading"><h2>Work</h2><button id="hide-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Hide sidebar" aria-expanded="true" aria-controls="workspace-sidebar">${icon("sidebar")}</button></div>
        <nav class="work-views" aria-label="Work views">${[
            ["all", "All Work", "work", work.length],
            [
                "assigned",
                "Assigned to Goblin",
                "spark",
                work.filter(
                    (x) =>
                        !!x.work.agentId &&
                        !["Completed", "Cancelled"].includes(x.work.status),
                ).length,
            ],
            [
                "attention",
                "Needs Attention",
                "wait",
                work.filter((x) => attention(x.work)).length,
            ],
            ["recent", "Recently Updated", "clock", ""],
            [
                "done",
                "Completed",
                "circleCheck",
                work.filter((x) => x.work.status === "Completed").length,
            ],
        ]
            .map(
                ([value, name, glyph, count]) =>
                    `<button class="filter ${filter === value ? "active" : ""}" data-action="filter" data-value="${value}" aria-pressed="${filter === value}">${icon(String(glyph))}<span>${name}</span>${count !== "" ? `<small>${count}</small>` : ""}</button>`,
            )
            .join("")}</nav>
        <div class="sidebar-history" data-scroll="sidebar"><div class="history-heading"><h3>${search ? "Search results" : filter === "recent" ? "Latest updates" : "Your work"}</h3><span>${shown.length}</span></div>
        <nav class="work-history" aria-label="Recent work">${shown.map(({ work: w, updatedAt }) => `<button class="work-card ${view === "work" && selected === w.id ? "selected" : ""}" data-action="select-work" data-id="${w.id}" aria-current="${view === "work" && selected === w.id ? "page" : "false"}" title="${e(w.objective)}"><span class="work-indicator ${statusClass(w)}">${icon(attention(w) ? "wait" : w.status === "Completed" ? "circleCheck" : w.status === "InProgress" ? "activity" : "clock")}</span><span class="work-card-copy"><span class="work-title">${e(w.objective)}</span><span class="work-card-meta">${e(label(w.status))} · <time datetime="${e(updatedAt ?? "")}">${e(relativeTime(updatedAt))}</time></span></span></button>`).join("") || `<p class="sidebar-empty">${search || filter !== "all" ? "No matching work." : loaded ? "Your work will appear here." : "Loading your work…"}</p>`}</nav>
        <div class="conversation-heading"><button class="history-disclosure" data-action="view-chat" aria-expanded="${conversationsOpen}" aria-controls="conversation-history">${icon("chat")}Conversations${icon("chevron")}</button><button class="icon-button" data-action="new-chat" aria-label="New conversation">${icon("plus")}</button></div>
        <nav id="conversation-history" class="work-history" aria-label="Saved conversations" ${conversationsOpen || search ? "" : "hidden"}>${chats.map((c) => `<button class="work-card ${view === "chat" && activeChat === c.id ? "selected" : ""}" data-action="select-chat" data-id="${c.id}" aria-current="${view === "chat" && activeChat === c.id ? "page" : "false"}"><span class="work-title">${e(c.title)}</span></button>`).join("") || '<p class="sidebar-empty">Save ideas and context here.</p>'}</nav></div>
        <button class="sidebar-system" data-action="settings-system" data-system-summary aria-label="System resources">${system.summary()}</button><div class="sidebar-bottom"><button class="nav-button sidebar-settings" data-action="settings">${icon("settings")}<span>Settings</span></button><button class="icon-button" data-action="lock" aria-label="Lock workspace" title="Lock workspace">${icon("lock")}</button></div></aside>`;
}
function renderHeader() {
    return `<header class="workspace-header" ${(mobile.matches && !sidebarCollapsed) || (compact.matches && activityOpen) ? "inert" : ""}><div class="header-left"><button id="show-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Show sidebar" aria-expanded="false" aria-controls="workspace-sidebar" ${sidebarCollapsed ? "" : "hidden"}>${icon("sidebar")}</button><a class="brand" href="/" data-action="new-work"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a></div><label class="global-search">${icon("search")}<input id="work-search" type="search" placeholder="Search work, decisions, outputs…" aria-label="Search work and conversations" value="${e(search)}" autocomplete="off"></label><div class="header-actions"><button class="secondary global-ask" data-action="new-chat">${icon("chat")}<span>Ask Goblin</span></button><button class="primary" data-action="new-work">${icon("plus")}<span>New work</span></button><button class="icon-button" data-action="refresh" aria-label="Refresh" title="Refresh">${icon("refresh")}</button></div>${renderSearchResults()}</header>`;
}
function renderSearchResults() {
    if (!search || !sidebarCollapsed) return "";
    const items = work.filter((x) =>
        matchesWork(
            x.work,
            search,
            conversations,
            agents.find((a) => a.id === x.work.agentId)?.name ?? "",
        ),
    );
    const chats = conversations.filter(
        (c) =>
            !c.workId &&
            [c.title, ...c.messages.map((m) => m.text)].some((text) =>
                text.toLocaleLowerCase().includes(search.toLocaleLowerCase()),
            ),
    );
    return `<section class="search-results" aria-label="Search results"><div class="section-heading"><h3>Search results</h3><button class="text-button" data-action="clear-search">Clear search</button></div>${items.map(({ work: w }) => `<button class="work-card" data-action="search-work" data-id="${w.id}">${icon("work")}<span class="work-title">${e(w.objective)}</span></button>`).join("")}${chats.map((c) => `<button class="work-card" data-action="search-chat" data-id="${c.id}">${icon("chat")}<span class="work-title">${e(c.title)}</span></button>`).join("")}${!items.length && !chats.length ? '<p class="section-empty">No matching work or conversations.</p>' : ""}</section>`;
}
function renderActivityPanel() {
    return `<aside id="work-activity" class="activity-panel ${activityOpen ? "is-open" : ""}" aria-label="Activity" ${compact.matches ? (activityOpen ? 'role="dialog" aria-modal="true"' : "inert") : ""} ${mobile.matches && !sidebarCollapsed ? "inert" : ""}><div class="activity-heading"><h2>${icon("activity")}Activity</h2><button id="close-activity" class="icon-button" data-action="toggle-activity" aria-label="Close activity" ${compact.matches ? "" : "hidden"}>${icon("close")}</button></div><div class="activity-body" data-scroll="activity">${renderActivity(view === "work" ? current()?.work : undefined)}</div></aside>`;
}
function renderHome() {
    const available = connections.some((c) => c.availability === "Available");
    const disconnected =
        !connections.length ||
        connections.every((c) => c.availability === "Disconnected");
    return `<section class="home"><div class="home-content"><img class="home-portrait" src="/assets/branding/icon.svg" alt=""><h1>What should we work on?</h1><p class="home-description">A little help for your next big thing.</p>${composer("new", "Describe the intended outcome…")}<div class="connection-nudge" ${available || !connectionsLoaded ? "hidden" : ""}><span>${disconnected ? "Connect an AI provider when you’re ready to start." : "Your AI connection may need attention."}</span><button data-action="settings">${disconnected ? "Connect an AI provider" : "Manage connections"}${icon("arrow")}</button></div></div></section>`;
}
function refreshHome(html: string) {
    const next = document.createElement("template");
    next.innerHTML = html;
    // Keep the home, portrait, composer and header mounted. Only server-backed
    // regions that actually changed need replacing during a refresh.
    for (const selector of [
        ".sidebar",
        ".workspace-notices",
        ".connection-choice",
        ".connection-nudge",
    ]) {
        const existing = root.querySelector(selector)!;
        const updated = next.content.querySelector(selector)!;
        if (!existing.isEqualNode(updated)) existing.replaceWith(updated);
    }
    const reply = root.querySelector<HTMLTextAreaElement>("#reply")!;
    if (reply.value !== draft) reply.value = draft;
    reply.disabled = sending;
    root.querySelector<HTMLButtonElement>(".send")!.disabled =
        sending || !!pending || workUnavailable;
}
function render(preserveHome = false) {
    const active =
        document.activeElement instanceof HTMLElement &&
        root.contains(document.activeElement)
            ? document.activeElement
            : null;
    const activeButton = active?.closest<HTMLButtonElement>(
        "button[data-action]",
    );
    const buttonData = activeButton ? { ...activeButton.dataset } : null;
    const context = draftKey();
    const sameContext = context === renderedContext;
    // Keep the native picker and its keyboard interaction alive during polling.
    // Loaded state is applied on the next render after the user leaves the select.
    if (active?.closest("select") && sameContext && authenticated && !sending)
        return;
    if (
        authenticated &&
        renderedContext.startsWith("work:") &&
        root.querySelector(".work-setup")
    )
        setupDrafts.set(renderedContext, {
            values: Array.from(
                root.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
                    ".work-setup input:not([readonly]),.work-setup select",
                ),
            ).map((x) => [x.id, x.value]),
            open:
                root.querySelector<HTMLDetailsElement>("#repository-options")
                    ?.open ?? false,
        });
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
    const activeId = active?.id;
    const activeSummary = active?.closest("summary")?.parentElement?.id;
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
        root.innerHTML = `<main class="unlock-page"><a class="brand" href="/"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><section class="unlock-card"><h1>${sessionChecked ? "Open your workspace" : "Opening your workspace…"}</h1>${sessionChecked ? `<p>Enter the password chosen when this Goblin workspace was set up.</p><form data-form="unlock"><label for="password">Goblin password</label><input class="field-control" id="password" name="password" type="password" autocomplete="current-password" required maxlength="128"><button class="primary" type="submit">Open workspace${icon("arrow")}</button></form>` : ""}${error ? `<p class="command-notice" role="alert">${e(error)}</p>` : ""}</section><p class="unlock-note">Your self-hosted AI coworker</p></main>`;
    } else {
        const modal =
            (mobile.matches && !sidebarCollapsed) ||
            (compact.matches && activityOpen);
        const html = `<a class="skip-link" href="#main-content">Skip to main content</a><div class="app-shell ${sidebarCollapsed ? "sidebar-collapsed" : ""}">${renderHeader()}${renderSidebar()}${mobile.matches && !sidebarCollapsed ? '<button class="sidebar-backdrop" data-action="toggle-sidebar" aria-label="Close navigation" tabindex="-1"></button>' : ""}<main id="main-content" class="main-shell" tabindex="-1" ${modal ? "inert" : ""}>
            <div class="workspace-notices">${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${pending ? `<div class="command-notice" role="status">${sending ? "Saving command…" : "Command unconfirmed. Inspect the saved state or resend this same command."}${!sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}</div>
            ${view === "chat" ? renderChat() : view === "work" && current() ? `<section class="detail" aria-label="Selected work">${renderDetail()}</section>` : renderHome()}</main>${renderActivityPanel()}${compact.matches && activityOpen ? '<button class="activity-backdrop" data-action="toggle-activity" aria-label="Close activity panel" tabindex="-1"></button>' : ""}</div>`;
        if (
            preserveHome &&
            sameContext &&
            view === "new" &&
            root.querySelector(".home")
        )
            refreshHome(html);
        else root.innerHTML = html;
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
    const setup = setupDrafts.get(context);
    for (const [id, value] of setup?.values ?? []) {
        const field = document.getElementById(id) as
            HTMLInputElement | HTMLSelectElement | null;
        if (field) field.value = value;
    }
    const repositoryOptions = root.querySelector<HTMLDetailsElement>(
        "#repository-options",
    );
    if (repositoryOptions && setup?.open) repositoryOptions.open = true;
    updateRepositoryBranch();
    showSetupErrors();
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
    if (sameContext && !focus && !buttonData) {
        const replacement = activeSummary
            ? document
                  .getElementById(activeSummary)
                  ?.querySelector<HTMLElement>("summary")
            : activeId
              ? document.getElementById(activeId)
              : null;
        if (replacement && !replacement.closest("[inert]"))
            replacement.focus({ preventScroll: true });
    }
    for (const [name, top] of scrolls) {
        const element = root.querySelector<HTMLElement>(
            `[data-scroll="${name}"]`,
        );
        if (element && (sameContext || name === "sidebar"))
            element.scrollTop = top;
    }
}
function renderDetail() {
    const item = current()!;
    const w = item.work,
        attempt = w.attempts.at(-1);
    const latestResult = w.results.find((r) => r.attemptId === attempt?.id);
    return `<div class="detail-body" id="detail-content" data-scroll="detail"><header class="detail-heading"><div class="work-context"><span>Work ${e(w.id)}</span><button id="show-activity" class="text-button activity-toggle" data-action="toggle-activity" aria-expanded="${activityOpen}" aria-controls="work-activity">${icon("activity")}Activity</button></div><div class="title-row"><h2 id="work-title-heading" tabindex="-1">${e(w.objective)}</h2>${status(w)}</div><div class="detail-properties"><span>${icon("spark")}${w.agentId ? `Assigned to ${e(agents.find((a) => a.id === w.agentId)?.name ?? "agent " + w.agentId)}` : "Unassigned"}</span><span>Updated ${e(relativeTime(item.updatedAt))}</span></div>${attempt?.target.repository ? `<p class="repository-detail">${icon("branch")}${e(attempt.target.repository.repository)}${attempt.target.repository.grant ? ` · ${e(attempt.target.repository.grant.branch)}` : ""}</p>` : ""}</header>
        <section class="work-section" aria-labelledby="goal-heading"><div class="section-heading"><h3 id="goal-heading">Goal</h3><button class="text-button" data-action="discuss-goal">Discuss goal${icon("arrow")}</button></div><p class="goal-text preserve-lines">${e(w.objective)}</p></section>
        <section class="work-section" aria-labelledby="progress-heading"><div class="section-heading"><h3 id="progress-heading">Progress</h3><span>Work lifecycle</span></div>${renderProgress(item)}${w.attention?.reason === "ResultReview" && latestResult ? `<button class="text-button review-result" data-action="inspect-output" data-id="result-${e(latestResult.attemptId)}">Read proposed result ${icon("arrow")}</button>` : ""}${controls(w)}</section>
        <section class="work-section" aria-labelledby="decisions-heading"><div class="section-heading"><h3 id="decisions-heading">Decisions</h3><span>${w.decisions.length || ""}</span></div>${renderDecisions(w)}</section>
        <section class="work-section outputs-section" aria-labelledby="outputs-heading"><div class="section-heading"><h3 id="outputs-heading">Recent outputs</h3></div>${renderOutputs(w)}</section></div>
        <div class="work-composer"><div class="composer-heading"><h3>Ask Goblin about this work</h3><button class="text-button" data-action="toggle-conversation" aria-expanded="${conversationExpanded}" aria-controls="work-conversation">${icon("chat")}${conversationExpanded ? "Hide conversation" : "Conversation"}</button></div><section class="work-conversation" id="work-conversation" data-scroll="conversation" aria-label="Conversation about this Work" ${conversationExpanded ? "" : "hidden"}>${conversationExpanded ? renderConversation(w) : ""}</section>${composer("work", changing ? "What would you like Goblin to change?" : w.attention?.reason === "InputRequired" ? "Answer Goblin’s question…" : "Add context to this work…")}</div>`;
}
function controls(w: Work) {
    if (w.attention?.reason === "CleanupRequired")
        return `<div class="decision"><h3>Needs attention</h3><p>The outcome is saved, but Goblin could not finish cleaning up the execution. Reconcile it before continuing.</p>${button("reconcile", "Reconcile execution", true)}</div>`;
    if (w.attempts.at(-1)?.cleanupPending)
        return `<div class="decision"><p>Saving the outcome and finishing execution cleanup…</p></div>`;
    let content = "";
    if (w.status === "Ready")
        content = !w.agentId
            ? `<form class="work-setup" data-form="assign"><p>Choose the agent responsible for this work.</p><label for="agent">Agent</label><select class="field-control select-control" id="agent" name="agent" required ${agents.length ? "" : "disabled"}><button type="button"><selectedcontent></selectedcontent></button>${agents.map((a) => `<option value="${a.id}">${e(a.name)}</option>`).join("") || '<option value="">No agents available</option>'}</select>${agents.length ? `<button id="assign-agent" class="primary" type="submit" ${sending || pending ? "disabled" : ""}>Assign agent</button>` : '<p class="field-hint" role="status">No agents are available. Refresh to check again.</p>'}</form>`
            : `<p>Ready when you are.</p>${button("execute", "Start work", true)}<details id="repository-options"><summary>${icon("chevron")}Repository changes</summary><p>Use an isolated sandbox for changes to a known GitHub repository.</p><form class="work-setup" data-form="repository" novalidate><label for="repository">Repository</label><select class="field-control select-control" id="repository" name="repository" required aria-describedby="repository-error"><button type="button"><selectedcontent></selectedcontent></button><option value="">${repositories.some((r) => r.enabled) ? "Choose an enabled repository" : connectionsLoading ? "Loading repositories…" : "No repositories enabled"}</option>${repositories
                  .filter((r) => r.enabled)
                  .map(
                      (r) =>
                          `<option value="${e(r.name)}">${e(r.name)}</option>`,
                  )
                  .join(
                      "",
                  )}</select><p class="field-error" id="repository-error" hidden></p><button type="button" class="text-button" data-action="settings-github">Manage repositories${icon("arrow")}</button><label for="repository-branch">Default branch</label><input class="field-control" id="repository-branch" readonly placeholder="Choose a repository first" aria-describedby="branch-hint"><p id="branch-hint" class="field-hint">New attempts start from the repository’s default branch. Follow-up attempts use the saved checkpoint. Changes are saved to a separate Goblin branch.</p><div class="identity-fields"><div><label for="git-name">Agent Git name</label><input class="field-control" id="git-name" name="git-name" value="Goblin" required aria-describedby="git-name-error"><p class="field-error" id="git-name-error" hidden></p></div><div><label for="git-email">Agent Git email</label><input class="field-control" id="git-email" name="git-email" type="email" placeholder="goblin@example.com" required aria-describedby="git-email-error"><p class="field-error" id="git-email-error" hidden></p></div></div><p class="field-hint">Used as the author identity for this agent’s commits.</p>${runtimes.some((r) => r.repositoryExecution) ? `<button id="start-repository" type="submit" class="primary" ${sending || pending ? "disabled" : ""}>Start repository work</button>` : '<p role="status">Repository execution is unavailable on this Goblin.</p>'}</form></details>`;
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
function updateRepositoryBranch() {
    const repository = root.querySelector<HTMLSelectElement>("#repository");
    const branch = root.querySelector<HTMLInputElement>("#repository-branch");
    if (branch)
        branch.value =
            repositories.find((r) => r.name === repository?.value)
                ?.defaultBranch ?? "";
}
function showSetupErrors() {
    const errors = setupErrors.get(renderedContext) ?? {};
    for (const id of ["repository", "git-name", "git-email"]) {
        const field = document.getElementById(id);
        const message = document.getElementById(id + "-error");
        if (!field || !message) continue;
        field.setAttribute("aria-invalid", String(!!errors[id]));
        message.textContent = errors[id] ?? "";
        message.hidden = !errors[id];
    }
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
        action === "settings-system" ||
        action === "settings-github"
    ) {
        await settings.open(
            action === "settings-system"
                ? "system"
                : action === "settings-github"
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
        activityOpen = false;
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
    if (action === "toggle-activity") {
        activityOpen = !activityOpen;
        if (mobile.matches) sidebarCollapsed = true;
        render();
        document
            .getElementById(activityOpen ? "close-activity" : "show-activity")
            ?.focus();
        return;
    }
    if (action === "toggle-conversation")
        conversationExpanded = !conversationExpanded;
    if (action === "discuss-goal") {
        draft ||= "I'd like to clarify the goal: ";
        render();
        document.getElementById("reply")?.focus();
        return;
    }
    if (action === "inspect-output") {
        activityOpen = false;
        render();
        const output = document.getElementById(
            target.dataset.id!,
        ) as HTMLDetailsElement | null;
        if (output) {
            const all = output.closest<HTMLDetailsElement>("#all-outputs");
            if (all) all.open = true;
            output.open = true;
            output.scrollIntoView({ block: "nearest" });
            output.querySelector("summary")?.focus({ preventScroll: true });
        }
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
    if (action === "clear-search") search = "";
    if (action === "search-work" || action === "search-chat") {
        search = "";
        navigate(
            action === "search-work" ? "work" : "chat",
            target.dataset.id!,
        );
        render();
        root.querySelector<HTMLElement>(
            action === "search-work" ? ".detail h2" : "#reply",
        )?.focus();
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

    if (action === "filter") filter = value!;
    if (action === "changes") changing = true;
    if (action === "execute") await command("Execute");
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
    if (action === "changes") document.getElementById("reply")?.focus();
    if (action === "clear-search")
        document.getElementById("work-search")?.focus();
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
    if (kind === "assign") {
        await command("Assign", { agentId: data.get("agent") });
        return;
    }
    if (kind === "repository") {
        const repository = String(data.get("repository") ?? "");
        const gitAuthorName = String(data.get("git-name") ?? "").trim();
        const gitAuthorEmail = String(data.get("git-email") ?? "").trim();
        const errors: Record<string, string> = {};
        if (!repository) errors.repository = "Choose an enabled repository.";
        if (!gitAuthorName)
            errors["git-name"] = "Enter a name for the agent’s commits.";
        const email =
            event.target.querySelector<HTMLInputElement>("#git-email")!;
        if (!gitAuthorEmail || email.validity.typeMismatch)
            errors["git-email"] =
                "Enter a valid email for the agent’s commits.";
        setupErrors.set(renderedContext, errors);
        showSetupErrors();
        if (Object.keys(errors).length) {
            document.getElementById(Object.keys(errors)[0])?.focus();
            return;
        }
        await command("Execute", {
            repository: { repository, gitAuthorName, gitAuthorEmail },
        });
        return;
    }
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
        event.target instanceof HTMLInputElement &&
        event.target.closest(".work-setup")
    ) {
        const errors = setupErrors.get(renderedContext);
        if (errors) delete errors[event.target.id];
        showSetupErrors();
    }
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
document.addEventListener("change", (event) => {
    if (
        event.target instanceof HTMLSelectElement &&
        event.target.id === "repository"
    ) {
        const errors = setupErrors.get(renderedContext);
        if (errors) delete errors.repository;
        updateRepositoryBranch();
        showSetupErrors();
    }
});
function trapFocus(event: KeyboardEvent, selector: string) {
    const targets = Array.from(
        root.querySelectorAll<HTMLElement>(
            `${selector} a, ${selector} button, ${selector} input, ${selector} summary`,
        ),
    ).filter((x) => x.getClientRects().length && !x.matches(":disabled"));
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
document.addEventListener("keydown", (event) => {
    if (settings.isOpen) return;
    if (
        event.key === "Escape" &&
        search &&
        !(mobile.matches && !sidebarCollapsed) &&
        !(compact.matches && activityOpen)
    ) {
        search = "";
        render();
        document.getElementById("work-search")?.focus();
        return;
    }
    if (compact.matches && activityOpen) {
        if (event.key === "Escape") {
            event.preventDefault();
            activityOpen = false;
            render();
            document.getElementById("show-activity")?.focus();
        }
        if (event.key === "Tab") trapFocus(event, "#work-activity");
        return;
    }
    if (mobile.matches && !sidebarCollapsed) {
        if (event.key === "Escape") {
            event.preventDefault();
            sidebarCollapsed = true;
            render();
            document.getElementById("show-sidebar")?.focus();
        }
        if (event.key === "Tab") trapFocus(event, ".sidebar");
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
});
let wasMobile = mobile.matches,
    wasCompact = compact.matches;
function updateResponsiveLayout() {
    // A resize can cross both breakpoints. Apply their new state together once.
    if (wasMobile === mobile.matches && wasCompact === compact.matches) return;
    if (wasMobile !== mobile.matches)
        sidebarCollapsed =
            mobile.matches ||
            localStorage.getItem("goblin.sidebarCollapsed") === "true";
    wasMobile = mobile.matches;
    wasCompact = compact.matches;
    activityOpen = false;
    render();
}
compact.addEventListener("change", updateResponsiveLayout);
mobile.addEventListener("change", updateResponsiveLayout);
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
