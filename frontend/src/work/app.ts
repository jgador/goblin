import {
    WorkStatus,
    AttentionReason,
    GitRepositoryAuthorizationStatus,
    WorkAction,
    IdentityKind,
    ConnectionAvailability,
} from "../api/values.js";
import { Settings } from "../settings/settings.js";
import { IntegrationsDirectory } from "../integrations/directory.js";
import { renderKnowledge } from "../knowledge/page.js";
import { requestJson, errorMessage } from "../api/client.js";
import type {
    Agent,
    Connection,
    RuntimeCapabilities,
} from "../api/workspace-contracts.js";
import { ReadScope } from "../api/read-scope.js";
import { CommandSubmission } from "./command-submission.js";
import type { PendingCommand } from "./command-submission.js";
import { SystemResources } from "../settings/system.js";
import {
    formatTimestamp as time,
    loadTimeZone,
    savedTimeZone,
    resetTimeZone,
    displayTimeZone,
} from "../settings/timezone.js";
import { TimeZoneSetup } from "../settings/timezone-picker.js";
import { ModelCatalogs } from "./model-catalog.js";
import {
    ModelSelections,
    selectedModel,
    availableEfforts,
    effortStops,
    effortLabel,
} from "./model-selection.js";
import { renderModelPicker } from "./model-picker.js";
import type { GitRepositoryInfo } from "../settings/github.js";
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
const workspaceReads = new ReadScope();
let work: View[] = [],
    conversations: Conversation[] = [],
    agents: Agent[] = [],
    connections: Connection[] = [];
let runtimes: RuntimeCapabilities[] = [],
    connectionsLoading = false,
    connectionsLoaded = false;
const query = new URLSearchParams(location.search);
let selected =
        query.get("item") ??
        sessionStorage.getItem("goblin.selectedWork") ??
        "",
    activeChat = "";
type WorkspaceView = "work" | "chat" | "new" | "integrations" | "knowledge";
const pageFromPath = (): "integrations" | "knowledge" | undefined =>
    /^\/integrations\/?$/.test(location.pathname)
        ? "integrations"
        : /^\/knowledge\/?$/.test(location.pathname)
          ? "knowledge"
          : undefined;
let view: WorkspaceView =
        pageFromPath() ??
        (location.pathname.startsWith("/work") && selected ? "work" : "new"),
    filter = "all",
    search = "",
    conversationsOpen = false;
let selectInitialWork =
    !pageFromPath() && !query.has("new") && !query.has("conversation");
const isDirectoryPage = () => view === "integrations" || view === "knowledge";
const mobile = matchMedia("(max-width: 760px)");
const compact = matchMedia("(max-width: 1199px)");
let activityOpen = false,
    conversationExpanded = false;
let sidebarCollapsed =
    mobile.matches ||
    localStorage.getItem("goblin.sidebarCollapsed") === "true";
let sessionChecked = false,
    workUnavailable = false;
let timezoneError = "";
let initialSettings = query.get("settings");
const drafts = new Map<string, string>();
const setupDrafts = new Map<
    string,
    {
        values: [string, string][];
        open: boolean;
        advancedOpen: boolean;
        approval: string;
    }
>();
const setupErrors = new Map<string, Record<string, string>>();
const modelSelections = new ModelSelections(sessionStorage);
const modelCatalogs = new ModelCatalogs(
    api,
    (connectionId, requestedModel) => {
        if (!authenticated) {
            modelCatalogs.clear();
            return;
        }
        const w = composerWork();
        const { catalog, error } = modelCatalogs.get(connectionId);
        if (modelConnection(w) === connectionId && catalog && !error)
            modelSelections.reconcile(modelContext(w), catalog, requestedModel);
        render();
        ensureModels();
    },
    (connectionId) => {
        const choice = modelSelection(composerWork());
        if (!authenticated || choice.connectionId !== connectionId) return;
        return { limit: choice.expanded ? 10 : 3, selectedModel: choice.model };
    },
);
let modelPickerOpen = false,
    modelListOpen = false,
    modelSliderDragging = false;
const draftKey = () =>
    view === "work"
        ? `work:${selected}`
        : view === "chat"
          ? `chat:${activeChat}`
          : view;

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
    changing = false,
    loading = false;
let draft = "";
const submission = new CommandSubmission(
    sessionStorage,
    ({ path, body }) => api(path, body),
    () => render(),
);
error = submission.recoveryNotice;
let renderedContext = "";
const root = document.querySelector<HTMLDivElement>("#app")!;
const system = new SystemResources();
const settings = new Settings(system);
const integrations = new IntegrationsDirectory(
    () => {
        if (authenticated && view === "integrations") render();
    },
    (provider) => void settings.open(provider),
);
const timezoneSetup = new TimeZoneSetup();
window.addEventListener("goblin-timezone-changed", () => render(true));
window.addEventListener("goblin-workspace-locked", () => {
    authenticated = false;
    initialSettings = query.get("settings");
    forgetWorkspace();
    render();
});
let gitHubRepositories: GitRepositoryInfo[] = [];
window.addEventListener("goblin-connections-changed", () => {
    modelCatalogs.clear();
    void refreshConnections();
    if (authenticated && view === "integrations") void integrations.refresh();
});
const current = () => work.find((x) => x.work.id === selected);
function composerWork(): Work | undefined {
    if (view === "work") return current()?.work;
    if (view === "chat") {
        const linked = conversations.find((c) => c.id === activeChat)?.workId;
        return work.find((x) => x.work.id === linked)?.work;
    }
}
const workAgent = (w?: Work) => agents.find((a) => a.id === w?.agentId);
const workConnection = (w?: Work) =>
    connections.find((c) => c.id === workAgent(w)?.connectionId);
const modelContext = (w?: Work) => ({ work: w, agent: workAgent(w) });
const modelSelection = (w?: Work) => modelSelections.get(modelContext(w));
const modelConnection = (w?: Work) => modelSelection(w).connectionId;
function modelPayload(w: Work) {
    const choice = modelSelection(w);
    const source = connections.find((c) => c.id === choice.connectionId);
    if (source && source.runtime !== "codex") return {};
    return {
        modelSelectionProvided: true,
        model: choice.model || null,
        reasoningEffort: choice.effort || null,
    };
}
function modelCanSubmit(w: Work) {
    if (workConnection(w)?.availability !== ConnectionAvailability.Available)
        return false;
    const choice = modelSelection(w);
    const source = connections.find((c) => c.id === choice.connectionId);
    if (source && source.runtime !== "codex") return true;
    if (!choice.model && !choice.effort) return true;
    const catalog = modelCatalogs.get(choice.connectionId).catalog;
    const model = selectedModel(catalog, choice.model);
    return (
        !!model &&
        (!choice.effort ||
            model.supportedReasoningEfforts.includes(choice.effort))
    );
}
function ensureModels() {
    const w = composerWork();
    if (
        !w ||
        (w.status !== WorkStatus.Ready &&
            w.attention?.reason !== AttentionReason.Failure)
    )
        return;
    const connectionId = modelConnection(w);
    if (
        !connectionId ||
        workConnection(w)?.availability !== ConnectionAvailability.Available
    )
        return;
    const source = connections.find((c) => c.id === connectionId);
    if (source && source.runtime !== "codex") return;
    const choice = modelSelection(w);
    void modelCatalogs.load(connectionId, {
        limit: choice.expanded ? 10 : 3,
        selectedModel: choice.model,
    });
}
const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");
const attention = (w: Work) => w.status === WorkStatus.NeedsAttention;
const statusClass = (w: Work) =>
    w.status === WorkStatus.Completed
        ? "done"
        : attention(w)
          ? w.attention?.reason === AttentionReason.ResultReview
              ? "review"
              : "waiting"
          : w.status === WorkStatus.InProgress
            ? "working"
            : "paused";
const status = (w: Work) =>
    `<span class="status-pill ${statusClass(w)}">${icon(attention(w) ? "wait" : w.status === WorkStatus.Completed ? "check" : "activity")}${e(label(w.attention?.reason ?? w.status))}</span>`;
const button = (
    action: string,
    text: string,
    primary = false,
    disabled = false,
) =>
    `<button class="${primary ? "primary" : "secondary"}" data-action="${action}" ${disabled || submission.sending || ((submission.pending || workUnavailable) && !["resend", "dismiss", "new-work", "changes"].includes(action)) ? "disabled" : ""}>${e(text)}</button>`;
async function api<T>(path: string, body?: unknown): Promise<T> {
    return requestJson<T>(path, {
        body,
        signal: body === undefined ? workspaceReads.signal : undefined,
        onUnauthorized: () =>
            window.dispatchEvent(new Event("goblin-workspace-locked")),
    });
}
function forgetWorkspace() {
    workspaceReads.reset();
    loading = false;
    connectionsLoading = false;
    timezoneSetup.reset();
    resetTimeZone();
    timezoneError = "";
    work = [];
    conversations = [];
    agents = [];
    connections = [];
    runtimes = [];
    connectionsLoaded = false;
    gitHubRepositories = [];
    github = null;
    draft = "";
    drafts.clear();
    setupDrafts.clear();
    setupErrors.clear();
    modelCatalogs.clear();
    modelSelections.clear();
    modelPickerOpen = false;
    modelListOpen = false;
    modelSliderDragging = false;
    loaded = false;
    system.reset();
    settings.reset();
    integrations.reset();
}
async function refresh(preserveError = false) {
    if (loading || submission.sending) return;
    loading = true;
    const signal = workspaceReads.signal;
    try {
        const session = await api<{ authenticated: boolean }>("/api/session");
        signal.throwIfAborted();
        authenticated = session.authenticated;
        sessionChecked = true;
        if (!authenticated) {
            forgetWorkspace();
            render();
            return;
        }
        try {
            await loadTimeZone(signal);
            timezoneError = "";
        } catch {
            signal.throwIfAborted();
            timezoneError = `Workspace timezone could not be loaded. Displayed times currently use ${displayTimeZone()}. Open Time & date in Settings to try again.`;
        }
        if (savedTimeZone() === null) {
            timezoneSetup.open(() => void refresh());
            return;
        }
        timezoneSetup.reset();
        if (view === "integrations") void integrations.refresh();
        if (query.get("returnTo") === "logs") {
            if (timezoneError) return;
            location.replace("/logs/select/vmui/" + location.hash);
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
                [
                    "codex",
                    "github",
                    "system",
                    "cluster",
                    "logs",
                    "timezone",
                    "integrations",
                    "slack",
                ].includes(target)
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
        signal.throwIfAborted();
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
            if (
                !submission.pending &&
                !preserveError &&
                error !== submission.recoveryNotice
            )
                error = "";
        } else {
            workUnavailable = true;
            if (!submission.pending && !preserveError)
                error =
                    "Work could not be loaded. You can still manage connections in Settings.";
        }
        if (chats.status === "fulfilled") conversations = chats.value;
        if (people.status === "fulfilled") agents = people.value;
        if (capabilities.status === "fulfilled") runtimes = capabilities.value;
    } catch (failure) {
        if (!signal.aborted)
            error = errorMessage(failure, "Work could not be loaded.");
    } finally {
        if (!signal.aborted) {
            if (!authenticated) forgetWorkspace();
            loading = false;
            render(true);
        }
    }
}
async function refreshConnections() {
    if (!authenticated) return;
    if (connectionsLoading) return;
    connectionsLoading = true;
    const signal = workspaceReads.signal;
    const [runtime, gitHubConnection, enabled] = await Promise.allSettled([
        api<Connection[]>("/api/connections"),
        api<NonNullable<typeof github>>("/api/github"),
        api<GitRepositoryInfo[]>("/api/github/repositories"),
    ]);
    if (signal.aborted || !authenticated) return;
    if (runtime.status === "fulfilled") {
        const previous = new Map(
            connections.map((c) => [c.id, c.availability]),
        );
        connections = runtime.value;
        for (const connection of connections)
            if (
                connection.availability === ConnectionAvailability.Available &&
                previous.get(connection.id) !== ConnectionAvailability.Available
            ) {
                modelCatalogs.invalidate(connection.id);
            }
    } else
        connections = connections.map((c) => ({
            ...c,
            availability: ConnectionAvailability.Unavailable,
        }));
    if (gitHubConnection.status === "fulfilled")
        github = gitHubConnection.value;
    else if (github)
        github = { ...github, notice: "GitHub status could not be refreshed." };
    if (enabled.status === "fulfilled") gitHubRepositories = enabled.value;
    connectionsLoading = false;
    connectionsLoaded = true;
    render(true);
}
async function send(
    path: PendingCommand["path"],
    body: Record<string, unknown> | (() => Promise<Record<string, unknown>>),
    repeat = false,
) {
    if (submission.sending || (submission.pending && !repeat)) return false;
    error = "";
    let confirmed = false;
    const submittedDraft = draftKey();
    const signal = workspaceReads.signal;
    try {
        const saved = repeat
            ? await submission.resend()
            : await submission.submit(async () => {
                  const prepared =
                      typeof body === "function" ? await body() : body;
                  signal.throwIfAborted();
                  return { path, body: prepared };
              });
        if (!saved) return false;
        if (signal.aborted) return false;
        const command = saved.body;
        draft = "";
        changing = false;
        confirmed = true;
        drafts.delete(submittedDraft);
        if (
            path === "/api/work/commands" &&
            command.action === WorkAction.Create
        ) {
            selected = String(command.workId);
            view = "work";
            conversationExpanded = false;
            rememberWork();
        }
    } catch (failure) {
        if (!signal.aborted)
            error = errorMessage(
                failure,
                "The request could not be confirmed.",
            );
    } finally {
        if (!signal.aborted) await refresh(!confirmed);
    }
    return confirmed;
}
async function reserve(...kinds: IdentityKind[]): Promise<string[]> {
    const result = await api<{ ids: string[] }>("/api/identities", { kinds });
    return result.ids;
}
async function command(
    action: WorkAction,
    extra: Record<string, unknown> = {},
) {
    const item = current();
    if (item)
        await send("/api/work/commands", async () => {
            const [commandId] = await reserve(IdentityKind.Command);
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
    const agent = agents.find((a) => a.isDefault);
    const connection = connections.find((c) => c.id === agent?.connectionId);
    return `<button class="connection-choice" type="button" data-action="settings" title="Manage AI connections">${icon("spark")}${e(connection?.name ?? "AI connections")}${icon("chevron")}</button>`;
}
function composer(kind: string, placeholder: string) {
    const note =
        kind === "chat"
            ? "Saved as a conversation · Track as Work to start Goblin"
            : changing
              ? "Requests changes to the current result"
              : current()?.work.attention?.reason ===
                  AttentionReason.InputRequired
                ? "Answers the pending question"
                : current()?.work.status === WorkStatus.Ready
                  ? "Saves context for this work · Start work when ready"
                  : "Saves context with this work";
    return `<div class="composer-wrap"><form class="composer" data-form="${kind}"><label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="3" maxlength="4000" placeholder="${e(placeholder)}" ${kind !== "new" ? 'aria-describedby="composer-note"' : ""} required ${submission.sending ? "disabled" : ""}>${e(draft)}</textarea><div class="composer-footer"><div class="composer-footer-start">${kind === "new" ? connectionButton() : kind === "work" ? `<button class="composer-add icon-button" type="button" data-action="toggle-conversation" aria-label="Conversation" aria-expanded="${conversationExpanded}" aria-controls="work-conversation" title="View conversation">${icon("chat")}</button><button class="composer-chip" type="button" data-action="settings-github">${icon("github")}GitHub</button><button class="composer-chip" type="button" data-action="view-outputs">${icon("file")}Outputs</button><button class="composer-chip" type="button" data-action="settings-agents">${icon("user")}Agent</button>` : `<span id="composer-note" class="composer-note">${note}</span>`}</div><button class="send" type="submit" aria-label="${kind === "new" ? "Create work" : "Send message"}" ${submission.sending || submission.pending || workUnavailable ? "disabled" : ""}>${icon("up")}</button></div>${kind === "work" ? `<p id="composer-note" class="composer-note">${note}</p>` : ""}</form>${kind === "new" ? '<p class="composer-hint">Save your idea, then start when you’re ready.</p>' : ""}</div>`;
}
function rememberWork() {
    sessionStorage.setItem("goblin.selectedWork", selected);
    history.replaceState(
        null,
        "",
        `/work?item=${encodeURIComponent(selected)}`,
    );
}
function navigate(
    next: WorkspaceView,
    id = "",
    historyMode: "auto" | "restore" = "auto",
) {
    selectInitialWork = false;
    drafts.set(draftKey(), draft);
    if (
        historyMode === "auto" &&
        next !== view &&
        (isDirectoryPage() || next === "integrations" || next === "knowledge")
    )
        history.pushState(null, "", location.href);
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
    } else if (next === "integrations" || next === "knowledge") {
        history.replaceState(null, "", `/${next}`);
        search = "";
        if (authenticated && next === "integrations")
            void integrations.refresh();
    } else history.replaceState(null, "", "/?new=work");
    draft = drafts.get(draftKey()) ?? "";
    changing = false;
    conversationExpanded = false;
    activityOpen = false;
    modelPickerOpen = false;
    modelListOpen = false;
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
              ? w.status === WorkStatus.Completed
              : filter === "assigned"
                ? !!w.agentId &&
                  ![WorkStatus.Completed, WorkStatus.Cancelled].some(
                      (value) => value === w.status,
                  )
                : true;
    const shown = matches
        .filter((x) => search || inView(x.work))
        .toSorted((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
    const chats = conversations.filter(
        (c) =>
            !c.workId &&
            [c.title, ...c.messages.map((m) => m.text)].some((text) =>
                text.toLocaleLowerCase().includes(search.toLocaleLowerCase()),
            ),
    );
    return `<aside id="workspace-sidebar" class="sidebar" aria-label="Workspace" ${sidebarCollapsed || activityOpen ? "inert" : ""} ${mobile.matches && !sidebarCollapsed ? 'role="dialog" aria-modal="true"' : ""}>
        <div class="sidebar-heading"><a class="brand" href="/" data-action="new-work"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><button id="hide-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Hide sidebar" aria-expanded="true" aria-controls="workspace-sidebar">${icon("sidebar")}</button></div>
        <button class="new-chat" data-action="new-work">${icon("plus")}New work</button>
        <nav class="primary-navigation" aria-label="Workspace navigation"><h2><button class="nav-button ${isDirectoryPage() ? "" : "active"}" data-action="browse-work" aria-current="${isDirectoryPage() ? "false" : "page"}">${icon("sidebar")}Work</button></h2><button class="nav-button" data-action="settings-agents">${icon("agents")}Agents</button><a id="nav-knowledge" class="nav-button ${view === "knowledge" ? "active" : ""}" href="/knowledge" data-action="knowledge" aria-current="${view === "knowledge" ? "page" : "false"}">${icon("knowledge")}Knowledge</a><a id="nav-integrations" class="nav-button ${view === "integrations" ? "active" : ""}" href="/integrations" data-action="integrations" aria-current="${view === "integrations" ? "page" : "false"}">${icon("integrations")}Integrations</a><button class="nav-button sidebar-settings" data-action="settings">${icon("settings")}Settings</button></nav>
        <nav class="work-views" aria-label="Work views">${[
            ["all", "All work", "work", work.length],
            [
                "assigned",
                "My work",
                "user",
                work.filter(
                    (x) =>
                        !!x.work.agentId &&
                        ![WorkStatus.Completed, WorkStatus.Cancelled].some(
                            (value) => value === x.work.status,
                        ),
                ).length,
            ],
            [
                "attention",
                "Needs attention",
                "wait",
                work.filter((x) => attention(x.work)).length,
            ],
            [
                "done",
                WorkStatus.Completed,
                "circleCheck",
                work.filter((x) => x.work.status === WorkStatus.Completed)
                    .length,
            ],
        ]
            .map(
                ([value, name, glyph, count]) =>
                    `<button class="filter ${filter === value ? "active" : ""}" data-action="filter" data-value="${value}" aria-pressed="${filter === value}">${icon(String(glyph))}<span>${name}</span>${count !== "" ? `<small>${count}</small>` : ""}</button>`,
            )
            .join("")}</nav>
        <div class="sidebar-history" data-scroll="sidebar"><div class="history-heading"><h3>${search ? "Search results" : "Your work"}</h3><span>${shown.length}</span></div>
        <nav class="work-history" aria-label="Recent work">${shown.map(({ work: w, updatedAt }) => `<button class="work-card ${view === "work" && selected === w.id ? "selected" : ""}" data-action="select-work" data-id="${w.id}" aria-current="${view === "work" && selected === w.id ? "page" : "false"}" title="${e(w.objective)}"><span class="work-card-copy"><span class="work-title">${e(w.objective)}</span><span class="work-card-meta"><span>${e(workAgent(w)?.name ?? "Goblin")}</span><time datetime="${e(updatedAt ?? "")}">${e(relativeTime(updatedAt))}</time></span></span><span class="work-indicator ${statusClass(w)}" aria-label="${e(label(w.attention?.reason ?? w.status))}" title="${e(label(w.attention?.reason ?? w.status))}">${icon(attention(w) ? "wait" : w.status === WorkStatus.Completed ? "circleCheck" : w.status === WorkStatus.InProgress ? "activity" : "clock")}</span></button>`).join("") || `<p class="sidebar-empty">${search || filter !== "all" ? "No matching work." : loaded ? "Your work will appear here." : "Loading your work…"}</p>`}</nav>
        <div class="conversation-heading"><button class="history-disclosure" data-action="view-chat" aria-expanded="${conversationsOpen}" aria-controls="conversation-history">${icon("chat")}Conversations${icon("chevron")}</button><button class="icon-button" data-action="new-chat" aria-label="New conversation">${icon("plus")}</button></div>
        <nav id="conversation-history" class="work-history" aria-label="Saved conversations" ${conversationsOpen || search ? "" : "hidden"}>${chats.map((c) => `<button class="work-card ${view === "chat" && activeChat === c.id ? "selected" : ""}" data-action="select-chat" data-id="${c.id}" aria-current="${view === "chat" && activeChat === c.id ? "page" : "false"}"><span class="work-title">${e(c.title)}</span></button>`).join("") || '<p class="sidebar-empty">Save ideas and context here.</p>'}</nav></div>
        <button class="sidebar-system" data-action="settings-system" data-system-summary aria-label="System resources">${system.summary()}</button></aside>`;
}
function renderHeader() {
    return `<header class="workspace-header" ${(mobile.matches && !sidebarCollapsed) || activityOpen ? "inert" : ""}><div class="header-left"><button id="show-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Show sidebar" aria-expanded="false" aria-controls="workspace-sidebar" ${sidebarCollapsed ? "" : "hidden"}>${icon("sidebar")}</button></div>${isDirectoryPage() ? `<span class="workspace-section-label">Workspace <span aria-hidden="true">/</span> ${view === "integrations" ? "Integrations" : "Knowledge"}</span>` : `<label class="global-search">${icon("search")}<input id="work-search" type="search" placeholder="Search work, decisions, outputs…" aria-label="Search work and conversations" value="${e(search)}" autocomplete="off"><span class="search-shortcut" aria-hidden="true"><kbd>Ctrl</kbd><kbd>K</kbd></span></label>`}<div class="header-actions"><button class="icon-button global-ask" data-action="new-chat" aria-label="Ask Goblin" title="Ask Goblin">${icon("chat")}</button><button class="icon-button" data-action="refresh" aria-label="Refresh" title="Refresh">${icon("refresh")}</button><details id="workspace-menu" class="action-menu"><summary class="workspace-avatar" aria-label="Workspace menu">${icon("user")}</summary><div class="action-menu-items"><button data-action="settings">${icon("settings")}Settings</button><button data-action="lock">${icon("lock")}Lock workspace</button></div></details></div>${renderSearchResults()}</header>`;
}
function renderSearchResults() {
    if (isDirectoryPage() || !search || !sidebarCollapsed) return "";
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
    return `<aside id="work-activity" class="activity-panel ${activityOpen ? "is-open" : ""}" aria-label="Activity" ${activityOpen ? 'role="dialog" aria-modal="true"' : "inert"}><div class="activity-heading"><h2>${icon("activity")}Activity</h2><button id="close-activity" class="icon-button" data-action="toggle-activity" aria-label="Close activity">${icon("close")}</button></div><div class="activity-body" data-scroll="activity">${renderActivity(view === "work" ? current()?.work : undefined)}</div></aside>`;
}
function renderHome() {
    const available = connections.some(
        (c) => c.availability === ConnectionAvailability.Available,
    );
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
    reply.disabled = submission.sending;
    root.querySelector<HTMLButtonElement>(".send")!.disabled =
        submission.sending || !!submission.pending || workUnavailable;
}
function render(preserveHome = false, forceSelect = false) {
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
    const authorization = current()?.work.repositoryAuthorization;
    const approvalKey = authorization
        ? `${authorization.id}:${authorization.status}`
        : "";
    const previousApproval =
        root.querySelector<HTMLElement>("#github-repository-options")?.dataset
            .approval ?? "";
    const gitHubAuthorizationChanged =
        sameContext && approvalKey !== previousApproval;
    if (!sameContext) {
        modelPickerOpen = false;
        modelListOpen = false;
    }
    // Keep the native picker and its keyboard interaction alive during polling.
    // Loaded state is applied on the next render after the user leaves the select.
    if (
        (active?.closest("select") || modelSliderDragging) &&
        sameContext &&
        authenticated &&
        !submission.sending &&
        !forceSelect &&
        !gitHubAuthorizationChanged
    )
        return;
    if (
        authenticated &&
        renderedContext.startsWith("work:") &&
        root.querySelector(".work-setup")
    )
        setupDrafts.set(renderedContext, {
            approval: previousApproval,
            values: Array.from(
                root.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
                    ".work-setup input:not([readonly]),.work-setup select",
                ),
            ).map((x) => [x.id, x.value]),
            open:
                root.querySelector<HTMLDetailsElement>(
                    "#github-repository-options",
                )?.open ?? false,
            advancedOpen:
                root.querySelector<HTMLDetailsElement>(
                    "#github-repository-advanced",
                )?.open ?? false,
        });
    const preserved = sameContext
        ? Array.from(
              root.querySelectorAll<HTMLInputElement | HTMLSelectElement>(
                  "input[id],select[id]",
              ),
          )
              .filter(
                  (x) =>
                      !(
                          gitHubAuthorizationChanged &&
                          x.closest('[data-form="github-repository"]')
                      ) &&
                      ![
                          "work-model",
                          "work-effort",
                          "model-connection",
                      ].includes(x.id),
              )
              .map((x) => [x.id, x.value] as const)
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
        view === "integrations"
            ? "Integrations · Goblin"
            : view === "knowledge"
              ? "Knowledge · Goblin"
              : view === "work" && authenticated && current()
                ? current()!.work.objective.slice(0, 70) + " · Goblin"
                : "Goblin";
    if (!authenticated) {
        root.innerHTML = `<main class="unlock-page"><a class="brand" href="/"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><section class="unlock-card"><h1>${sessionChecked ? "Open your workspace" : "Opening your workspace…"}</h1>${sessionChecked ? `<p>Enter the password chosen when this Goblin workspace was set up.</p><form data-form="unlock"><label for="password">Goblin password</label><input class="field-control" id="password" name="password" type="password" autocomplete="current-password" required maxlength="128"><button class="primary" type="submit">Open workspace${icon("arrow")}</button></form>` : ""}${error ? `<p class="command-notice" role="alert">${e(error)}</p>` : ""}</section><p class="unlock-note">Your self-hosted AI coworker</p></main>`;
    } else {
        const modal = (mobile.matches && !sidebarCollapsed) || activityOpen;
        const html = `<a class="skip-link" href="#main-content">Skip to main content</a><div class="app-shell ${sidebarCollapsed ? "sidebar-collapsed" : ""}">${renderHeader()}${renderSidebar()}${mobile.matches && !sidebarCollapsed ? '<button class="sidebar-backdrop" data-action="toggle-sidebar" aria-label="Close navigation" tabindex="-1"></button>' : ""}<main id="main-content" class="main-shell" tabindex="-1" ${modal ? "inert" : ""}>
            <div class="workspace-notices">${timezoneError ? `<div class="command-notice" role="status">${e(timezoneError)}</div>` : ""}${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${submission.pending ? `<div class="command-notice" role="status">${submission.sending ? "Saving command…" : "Command unconfirmed. Inspect the saved state or resend this same command."}${!submission.sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}</div>
            ${view === "integrations" ? integrations.render() : view === "knowledge" ? renderKnowledge() : view === "chat" ? renderChat() : view === "work" && current() ? `<section class="detail" aria-label="Selected work">${renderDetail()}</section>` : renderHome()}</main>${renderActivityPanel()}${activityOpen ? '<button class="activity-backdrop" data-action="toggle-activity" aria-label="Close activity panel" tabindex="-1"></button>' : ""}</div>`;
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
    const savedSetup = setupDrafts.get(context);
    const setup = savedSetup?.approval === approvalKey ? savedSetup : undefined;
    for (const [id, value] of setup?.values ?? []) {
        const field = document.getElementById(id) as
            HTMLInputElement | HTMLSelectElement | null;
        if (field) field.value = value;
    }
    const gitHubRepositoryOptions = root.querySelector<HTMLDetailsElement>(
        "#github-repository-options",
    );
    if (gitHubRepositoryOptions && setup?.open)
        gitHubRepositoryOptions.open = true;
    const advancedRepositoryOptions = root.querySelector<HTMLDetailsElement>(
        "#github-repository-advanced",
    );
    if (advancedRepositoryOptions && setup?.advancedOpen)
        advancedRepositoryOptions.open = true;
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
                    b.dataset.provider === buttonData.provider &&
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
    if (authenticated) ensureModels();
}
function renderDetail() {
    const item = current()!;
    const w = item.work,
        attempt = w.attempts.at(-1);
    const latestResult = w.results.find((r) => r.attemptId === attempt?.id);
    return `<div class="detail-body" id="detail-content" data-scroll="detail"><header class="detail-heading"><div class="work-context"><button class="text-button back-to-work" data-action="browse-work">${icon("back")}Back to work</button><span>Work ${e(w.id)}</span></div><div class="title-row"><h2 id="work-title-heading" tabindex="-1">${e(w.objective)}</h2><div class="detail-actions"><button class="secondary" data-action="share-work">${icon("share")}Share</button><button id="show-activity" class="icon-button activity-toggle" data-action="toggle-activity" aria-label="Activity" title="Activity" aria-expanded="${activityOpen}" aria-controls="work-activity">${icon("more")}</button></div></div><div class="detail-properties"><span>${icon("user")}${e(workAgent(w)?.name ?? "Goblin")}</span><span>${icon("spark")}Updated ${e(relativeTime(item.updatedAt))}</span>${status(w)}</div>${attempt?.target.repository ? `<p class="git-repository-detail">${icon("branch")}${e(attempt.target.repository.repository)}${attempt.target.repository.grant ? ` · ${e(attempt.target.repository.grant.branch)}` : ""}</p>` : ""}</header>
        <section class="work-section goal-section" aria-labelledby="goal-heading">${icon("goal", "section-icon")}<div class="section-content"><div class="section-heading"><h3 id="goal-heading">Goal</h3><button class="text-button goal-discuss" data-action="discuss-goal" aria-label="Discuss goal" title="Discuss goal">${icon("chat")}</button></div><p class="goal-text preserve-lines">${e(w.objective)}</p></div></section>
        <section class="work-section" aria-labelledby="progress-heading">${icon("work", "section-icon")}<div class="section-content"><div class="section-heading"><h3 id="progress-heading">Progress</h3></div>${renderProgress(item)}${w.attention?.reason === AttentionReason.ResultReview && latestResult ? `<button class="text-button review-result" data-action="inspect-output" data-id="result-${e(latestResult.attemptId)}">Read proposed result ${icon("arrow")}</button>` : ""}${controls(w)}</div></section>
        ${w.decisions.length ? `<section class="work-section" aria-labelledby="decisions-heading">${icon("wait", "section-icon")}<div class="section-content"><div class="section-heading"><h3 id="decisions-heading">Decisions</h3><span>${w.decisions.length}</span></div>${renderDecisions(w)}</div></section>` : ""}
        <section id="work-outputs" class="work-section outputs-section" aria-labelledby="outputs-heading">${icon("cube", "section-icon")}<div class="section-content"><div class="section-heading"><div><h3 id="outputs-heading">Outputs</h3><p class="section-description">Results, artifacts, and executions from this work.</p></div>${w.results.length + w.artifacts.length + w.attempts.length > 3 ? '<button class="secondary" data-action="view-outputs">View all ' + icon("arrow") + "</button>" : ""}</div>${renderOutputs(w)}</div></section></div>
        <div class="work-composer"><section class="work-conversation" id="work-conversation" data-scroll="conversation" aria-label="Conversation about this Work" ${conversationExpanded ? "" : "hidden"}>${conversationExpanded ? `<div class="composer-heading"><h3>Conversation</h3><button class="text-button" data-action="toggle-conversation" aria-expanded="true" aria-controls="work-conversation">Hide conversation</button></div>${renderConversation(w)}` : ""}</section>${composer("work", changing ? "What would you like Goblin to change?" : w.attention?.reason === AttentionReason.InputRequired ? "Answer Goblin’s question…" : "Add context to this work…")}</div>`;
}
function modelControls(w?: Work) {
    const choice = modelSelection(w);
    const state = modelCatalogs.get(choice.connectionId);
    return renderModelPicker({
        choice,
        ...state,
        connectionId: choice.connectionId,
        runtime: connections.find((c) => c.id === choice.connectionId)?.runtime,
        pickerOpen: modelPickerOpen,
        listOpen: modelListOpen,
    });
}
function gitHubRepositorySetup(w: Work, required = false) {
    const handoff = [
        AttentionReason.GitRepositoryRequired,
        AttentionReason.InputRequired,
    ].some((reason) => reason === w.attention?.reason);
    const approval = w.repositoryAuthorization;
    const proposed = approval?.target.repository;
    const grant =
        approval?.status === GitRepositoryAuthorizationStatus.Invalidated
            ? undefined
            : proposed?.grant;
    const pendingApproval =
        approval?.status === GitRepositoryAuthorizationStatus.Pending;
    const suggestions = w.repositoryRequest?.repositories ?? [];
    const name =
        proposed?.repository ??
        (suggestions.length === 1 ? suggestions[0] : "");
    const preview =
        pendingApproval && grant
            ? `<section class="github-repository-authorization" aria-label="GitHub authorization">
                <header><h3>Authorize GitHub access</h3><p>Review the repository and Git actions for this Work.</p></header>
                <dl class="execution-properties github-repository-authorization-details">
                    <dt>Repository</dt><dd>${e(proposed?.repository)}</dd>
                    <dt>GitHub account</dt><dd>${e(grant.login)}</dd>
                    <dt>Base branch</dt><dd>${e(grant.baseBranch)}</dd>
                    <dt>Work branch</dt><dd>${e(grant.branch)}</dd>
                    <dt>Git author</dt><dd>${e(proposed?.gitAuthorName)} &lt;${e(proposed?.gitAuthorEmail)}&gt;</dd>
                </dl>
                <div class="github-repository-permissions"><h4>Git actions</h4><ul>
                    <li>Fetch repository and create a local work branch.</li>
                    <li>${grant.allowPush ? "Push changes to the work branch." : "Keep changes in this Work without pushing."}</li>
                    <li>${grant.allowPullRequest ? "Open a draft pull request." : "No pull request."}</li>
                </ul></div>
                ${approval.enableRepository ? '<p class="github-repository-authorization-note">This also enables the repository in Goblin for future requests. Requests naming an enabled repository can start directly.</p>' : ""}
                <div class="github-repository-actions">${button("authorize-github-repository", approval.enableRepository ? "Enable repository & authorize this Work" : "Authorize this Work", true)}${button("deny-github-repository", "Decline")}</div>
            </section>`
            : approval?.status === GitRepositoryAuthorizationStatus.Invalidated
              ? "<p>Context changed. Review repository access again.</p>"
              : approval?.status === GitRepositoryAuthorizationStatus.Denied
                ? "<p>Repository access was declined. You can review a different request.</p>"
                : "";
    return `${preview}<details id="github-repository-options" data-approval="${approval ? `${approval.id}:${approval.status}` : ""}" ${required && !pendingApproval ? "open" : ""}>
        <summary>${icon("chevron")}${pendingApproval ? "Change repository or actions" : "Repository access"}</summary>
        <p>Choose an enabled repository to continue. Goblin uses its default branch and your connected GitHub commit identity. Changes stay local unless your request asks to publish them.</p>
        ${suggestions.length > 1 ? `<p class="field-hint">Several repositories match: ${e(suggestions.join(", "))}. Choose the full name.</p>` : ""}
        <form class="work-setup github-repository-form" data-form="github-repository" novalidate>
            <div><label for="github-repository">Repository</label>
                <input class="field-control" id="github-repository" name="github-repository" list="known-github-repositories" value="${e(name)}" placeholder="owner/repository or GitHub URL" required aria-describedby="github-repository-error">
                <datalist id="known-github-repositories">${gitHubRepositories.map((r) => `<option value="${e(r.name)}">${r.enabled ? "Enabled" : "Requires enablement"}</option>`).join("")}</datalist>
                <p class="field-error" id="github-repository-error" hidden></p>
                <button type="button" class="text-button" data-action="settings-github">Manage repositories${icon("arrow")}</button>
            </div>
            <details id="github-repository-advanced"><summary>Branch, delivery, and commit identity</summary>
            <div><label for="github-repository-branch">Base branch</label>
                <input class="field-control" id="github-repository-branch" name="base-branch" value="${e(grant?.baseBranch ?? "")}" placeholder="From your request or repository default">
            </div>
            <div><label for="delivery-policy">Git actions</label>
                <select class="field-control select-control" id="delivery-policy" name="delivery-policy">
                    <button type="button"><selectedcontent></selectedcontent></button>
                    <option value="message" ${!grant ? "selected" : ""}>Use the actions in my request</option>
                    <option value="local" ${grant && !grant.allowPush ? "selected" : ""}>Keep changes local</option>
                    <option value="push" ${grant?.allowPush && !grant.allowPullRequest ? "selected" : ""}>Push changes</option>
                    <option value="pr" ${grant?.allowPullRequest ? "selected" : ""}>Push and open a draft PR</option>
                </select>
            </div>
            <div class="identity-fields">
                <div><label for="git-name">Git commit name (optional)</label><input class="field-control" id="git-name" name="git-name" value="${e(proposed?.gitAuthorName ?? "")}" placeholder="Connected GitHub account" aria-describedby="git-name-error"><p class="field-error" id="git-name-error" hidden></p></div>
                <div><label for="git-email">Git commit email (optional)</label><input class="field-control" id="git-email" name="git-email" type="email" value="${e(proposed?.gitAuthorEmail ?? "")}" placeholder="GitHub no-reply email" aria-describedby="git-email-error"><p class="field-error" id="git-email-error" hidden></p></div>
            </div>
            </details>
            ${runtimes.some((r) => r.repositoryExecution) ? `<div class="github-repository-actions"><button id="start-github-repository" type="submit" class="primary" ${submission.sending || submission.pending || (!handoff && !modelCanSubmit(w)) ? "disabled" : ""}>Use repository</button></div>` : '<p role="status">Repository execution is unavailable on this Goblin.</p>'}
        </form>
    </details>`;
}
function executionSetup(w: Work) {
    const connection = workConnection(w);
    const available =
        connection?.availability === ConnectionAvailability.Available;
    const disconnected =
        connection?.availability === ConnectionAvailability.Disconnected;
    const runtime =
        connection?.runtime === "codex" ? "Codex" : connection?.name;
    const notice = !connectionsLoaded
        ? "Checking the coding agent connection…"
        : disconnected
          ? "Connect Codex so Goblin can start working."
          : !connection
            ? "Goblin’s coding agent connection is unavailable."
            : connection.availability === ConnectionAvailability.Changing
              ? "The Codex connection is changing. You can start work when it’s ready."
              : connection.availability === ConnectionAvailability.Verifying
                ? "Codex is being checked. You can start work when the check finishes."
                : "Codex needs attention before Goblin can start working.";
    return `<div class="work-executor"><img src="/assets/branding/icon.svg" alt=""><div class="executor-identity"><strong>${e(workAgent(w)?.name ?? "Goblin")}</strong>${runtime ? `<span>Uses ${e(runtime)}</span>` : ""}</div>${available ? modelControls(w) : ""}</div>${available ? "" : `<p role="status">${notice}</p>${connectionsLoaded ? button("settings-codex", disconnected ? "Connect Codex" : "Manage Codex connection", true) : ""}`}`;
}
function controls(w: Work) {
    if (w.attention?.reason === AttentionReason.CleanupRequired)
        return `<div class="decision"><h3>Needs attention</h3><p>The outcome is saved, but Goblin could not finish cleaning up the execution. Reconcile it before continuing.</p>${button("reconcile", "Reconcile execution", true)}</div>`;
    if (w.attempts.at(-1)?.cleanupPending)
        return `<div class="decision"><p>Saving the outcome and finishing execution cleanup…</p></div>`;
    let content = "";
    if (w.status === WorkStatus.Ready)
        content = `${executionSetup(w)}${workConnection(w)?.availability === ConnectionAvailability.Available ? `<p>Ready when you are.</p>${button("execute", "Start work", true, !modelCanSubmit(w))}${gitHubRepositorySetup(w)}` : ""}`;
    if (w.attention?.reason === AttentionReason.ResultReview)
        content = `<h3>Ready for your review</h3><p>You decide when the outcome is complete.</p>${button("approve", "Approve & complete", true)}${button("changes", "Ask for changes")}`;
    if (w.attention?.reason === AttentionReason.InputRequired)
        content = `<h3>Needs your input</h3><p>${e(w.decisions.at(-1)?.question)}</p>${!w.attempts.at(-1)?.target.repository ? gitHubRepositorySetup(w) : ""}`;
    if (w.attention?.reason === AttentionReason.GitRepositoryRequired)
        content = `${w.repositoryAuthorization?.status === GitRepositoryAuthorizationStatus.Pending ? "" : "<h3>Repository access needed</h3><p>Review and authorize the repository and Git actions to continue this Work.</p>"}${gitHubRepositorySetup(w, true)}`;
    if (w.attention?.reason === AttentionReason.Failure)
        content = `<h3>Needs attention</h3><p>${e(label(w.attention.failure ?? "Execution failed"))}. Check the connection and execution history before retrying.</p>${executionSetup(w)}${workConnection(w)?.availability === ConnectionAvailability.Available ? button("retry", "Retry work", true, !modelCanSubmit(w)) : ""}`;
    if (w.attention?.reason === AttentionReason.UncertainExecution)
        content = `<h3>Execution needs reconciliation</h3><p>Goblin cannot yet confirm the outcome. Check the execution or stop it before retrying.</p>${button("reconcile", "Reconcile execution", true)}`;
    if (
        [WorkStatus.Queued, WorkStatus.InProgress, WorkStatus.Cancelling].some(
            (value) => value === w.status,
        )
    )
        content = `<div class="progress-title">${icon("activity")} ${w.status === WorkStatus.InProgress ? "Goblin is working" : e(label(w.status))}</div><p>You can close this page. Accepted work continues independently.</p>`;
    if (
        ![
            WorkStatus.Completed,
            WorkStatus.Cancelled,
            WorkStatus.Cancelling,
        ].some((value) => value === w.status)
    )
        content += button("cancel", "Cancel work");
    return content ? `<div class="decision">${content}</div>` : "";
}
function showSetupErrors() {
    const errors = setupErrors.get(renderedContext) ?? {};
    for (const id of ["github-repository", "git-name", "git-email"]) {
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
    return `<section class="chat-workspace"><div class="detail-body" data-scroll="chat"><div class="thread-content">${c ? `<h1 class="conversation-title">${e(c.title)}</h1>` + c.messages.map((m) => message(m.source ? `${m.source.provider} · ${m.source.userId}` : "You", m.text, m.createdAt)).join("") + (c.workId ? `<button class="tracked-link" data-action="select-work" data-id="${c.workId}">Open tracked work ${icon("arrow")}</button>` : `<div class="decision"><h3>Ready to take this forward?</h3><p>Track this conversation as Work when you want Goblin to execute it.</p>${button("track", "Track this work", true)}</div>`) : `<div class="conversation-empty"><h1>A little room to think</h1><p>Save ideas and context here. Track them as Work when you’re ready for Goblin to start.</p></div>`}</div></div>${composer("chat", "Add to the conversation…")}</section>`;
}
function setModelEffort(value: string, updateOnly = false) {
    const w = composerWork();
    const choice = modelSelection(w);
    if (
        !modelSelections.setEffort(
            modelContext(w),
            value,
            modelCatalogs.get(choice.connectionId).catalog,
        )
    )
        return;
    if (!updateOnly) {
        render(false, true);
        return;
    }
    const effort = effortLabel(value);
    const trigger = root.querySelector<HTMLButtonElement>(
        ".model-picker-trigger",
    );
    const modelName = root.querySelector<HTMLElement>(
        ".model-picker-trigger-name",
    )?.textContent;
    if (trigger && modelName)
        trigger.setAttribute(
            "aria-label",
            `Options: ${modelName}, reasoning effort: ${effort}`,
        );
    for (const element of root.querySelectorAll<HTMLElement>(".model-effort"))
        element.textContent = effort;
    for (const button of root.querySelectorAll<HTMLButtonElement>(
        ".model-effort-labels button",
    ))
        button.setAttribute(
            "aria-pressed",
            String(button.dataset.value === value),
        );
    const range = root.querySelector<HTMLInputElement>("#work-effort");
    if (range) {
        const index = effortStops.findIndex((stop) => stop.value === value);
        range.value = String(index);
        range.setAttribute("aria-valuetext", effort);
        const visual = root.querySelector<HTMLElement>(".model-effort-visual");
        if (visual) visual.dataset.level = String(index);
    }
    for (const action of ["execute", "retry"]) {
        const button = root.querySelector<HTMLButtonElement>(
            `[data-action="${action}"]`,
        );
        if (button) button.disabled = !w || !modelCanSubmit(w);
    }
    const gitHubRepositoryButton = root.querySelector<HTMLButtonElement>(
        "#start-github-repository",
    );
    if (gitHubRepositoryButton)
        gitHubRepositoryButton.disabled =
            !w ||
            !modelCanSubmit(w) ||
            submission.sending ||
            !!submission.pending;
}
document.addEventListener("click", async (event) => {
    if (!(event.target instanceof Element)) return;
    if (!event.target.closest("#workspace-menu"))
        root.querySelector<HTMLDetailsElement>(
            "#workspace-menu",
        )?.removeAttribute("open");
    if (modelPickerOpen && !event.target.closest(".model-picker")) {
        modelPickerOpen = false;
        modelListOpen = false;
        root.querySelector("#model-popover")?.remove();
        root.querySelector(".model-picker-trigger")?.setAttribute(
            "aria-expanded",
            "false",
        );
    }
    const target = event.target.closest<HTMLElement>("[data-action]");
    if (!target) return;
    if (target instanceof HTMLAnchorElement) {
        if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey)
            return;
        event.preventDefault();
    }
    const action = target.dataset.action,
        value = target.dataset.value;
    if (action === "integrations" || action === "knowledge") {
        navigate(action);
        render();
        document.getElementById(action + "-title")?.focus();
        return;
    }
    if (action === "add-integration") {
        integrations.openPicker();
        return;
    }
    if (action === "manage-integration") {
        const provider = target.dataset.provider;
        if (provider === "github" || provider === "slack")
            await settings.open(provider);
        return;
    }
    if (action === "refresh-integrations") {
        await integrations.refresh();
        return;
    }
    if (action === "integration-tab") {
        integrations.tab = value === "connected" ? "connected" : "all";
        render();
        return;
    }
    if (action === "reset-integrations") {
        integrations.search = "";
        integrations.tab = "all";
        render();
        const input = root.querySelector<HTMLInputElement>(
            "#integration-search",
        );
        if (input) {
            input.value = "";
            input.focus();
        }
        return;
    }
    if (action === "toggle-model-picker") {
        modelPickerOpen = !modelPickerOpen;
        modelListOpen = false;
        render(false, true);
        root.querySelector("#model-popover")?.scrollIntoView({
            block: "nearest",
        });
        return;
    }
    if (action === "open-model-menu") {
        modelListOpen = true;
        render(false, true);
        root.querySelector<HTMLElement>(".model-menu-back")?.focus();
        return;
    }
    if (action === "model-menu-back") {
        modelListOpen = false;
        render(false, true);
        root.querySelector<HTMLElement>(".model-row")?.focus();
        return;
    }
    if (action === "select-model") {
        modelSelections.selectModel(modelContext(composerWork()), value ?? "");
        modelListOpen = false;
        render(false, true);
        root.querySelector<HTMLElement>(".model-row")?.focus();
        return;
    }
    if (action === "set-effort") {
        if (value) setModelEffort(value);
        return;
    }
    if (
        action === "settings" ||
        action === "settings-agents" ||
        action === "settings-integrations" ||
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
                    : action === "settings-agents"
                      ? "agents"
                      : action === "settings-integrations"
                        ? "integrations"
                        : "connections",
        );
        return;
    }
    if (action === "refresh") {
        await refresh();
        return;
    }
    if (action === "share-work" && current()) {
        const toast = document.getElementById("toast")!;
        try {
            await navigator.clipboard.writeText(location.href);
            toast.textContent =
                "Work link copied. Workspace access is required to open it.";
        } catch {
            toast.textContent =
                "Copy this page’s address to share the work link.";
        }
        setTimeout(() => {
            toast.textContent = "";
        }, 5000);
        return;
    }
    if (action === "browse-work") {
        const fromDirectory = isDirectoryPage();
        if (fromDirectory) navigate(current() ? "work" : "new", selected);
        filter = "all";
        search = "";
        sidebarCollapsed = fromDirectory && mobile.matches;
        activityOpen = false;
        render();
        root.querySelector<HTMLElement>(
            fromDirectory
                ? "#work-title-heading, #reply"
                : '.work-card[aria-current="page"]',
        )?.focus();
        return;
    }
    if (action === "view-outputs") {
        const all = root.querySelector<HTMLDetailsElement>("#all-outputs");
        if (all) all.open = true;
        document
            .getElementById("work-outputs")
            ?.scrollIntoView({ block: "start" });
        root.querySelector<HTMLElement>("#work-outputs summary")?.focus({
            preventScroll: true,
        });
        return;
    }
    if (action === "show-more-models") {
        const w = composerWork();
        const connectionId = modelConnection(w);
        modelSelections.expand(modelContext(w));
        const choice = modelSelection(w);
        if (connectionId)
            void modelCatalogs.load(connectionId, {
                limit: 10,
                selectedModel: choice.model,
            });
        render();
        root.querySelector<HTMLElement>(".model-menu-back")?.focus();
        return;
    }
    if (action === "refresh-models") {
        const choice = modelSelection(composerWork());
        if (choice.connectionId)
            await modelCatalogs.refresh(choice.connectionId, {
                limit: choice.expanded ? 10 : 3,
                selectedModel: choice.model,
            });
        render();
        return;
    }
    if (action === "resend" && submission.pending) {
        await send(submission.pending.path, submission.pending.body, true);
        return;
    }
    if (action === "dismiss") {
        submission.dismiss();
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
            error = errorMessage(failure, "Could not reach Goblin. Try again.");
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

    if (action === "filter") {
        if (isDirectoryPage()) navigate(current() ? "work" : "new", selected);
        filter = value!;
    }
    if (action === "changes") changing = true;
    if (action === "execute" && current())
        await command(WorkAction.Execute, modelPayload(current()!.work));
    if (action === "retry" && current())
        await command(WorkAction.Retry, modelPayload(current()!.work));
    if (
        action === "authorize-github-repository" ||
        action === "deny-github-repository"
    )
        await command(
            action === "authorize-github-repository"
                ? WorkAction.AuthorizeGitRepository
                : WorkAction.DenyGitRepository,
            {
                authorizationId: current()?.work.repositoryAuthorization?.id,
            },
        );
    if (action === "reconcile") await command(WorkAction.Reconcile);
    if (action === "cancel") await command(WorkAction.Cancel);
    if (action === "approve")
        await command(WorkAction.Approve, {
            attemptId: current()?.work.attempts.at(-1)?.id,
        });
    if (action === "track") {
        const c = conversations.find((x) => x.id === activeChat)!;
        const confirmed = await send(
            "/api/conversations/commands",
            async () => {
                const [workId, messageId] = await reserve(
                    IdentityKind.Work,
                    IdentityKind.Message,
                );
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
    if (kind === "github-repository") {
        let gitHubRepository = String(
            data.get("github-repository") ?? "",
        ).trim();
        const url = gitHubRepository.match(
            /^(?:https?:\/\/)?github\.com\/([^/]+\/[^/]+)/i,
        );
        if (url) gitHubRepository = url[1].replace(/\.git$/, "");
        const gitAuthorName = String(data.get("git-name") ?? "").trim();
        const gitAuthorEmail = String(data.get("git-email") ?? "").trim();
        const errors: Record<string, string> = {};
        if (!/^[a-zA-Z0-9_.-]+\/[a-zA-Z0-9_.-]+$/.test(gitHubRepository))
            errors["github-repository"] =
                "Enter the full owner/repository name or GitHub URL.";
        const email =
            event.target.querySelector<HTMLInputElement>("#git-email")!;
        if (gitAuthorEmail && email.validity.typeMismatch)
            errors["git-email"] =
                "Enter a valid email for the agent’s commits.";
        setupErrors.set(renderedContext, errors);
        showSetupErrors();
        if (Object.keys(errors).length) {
            document.getElementById(Object.keys(errors)[0])?.focus();
            return;
        }
        const w = current()!.work;
        const handoff = [
            AttentionReason.GitRepositoryRequired,
            AttentionReason.InputRequired,
        ].some((reason) => reason === w.attention?.reason);
        const policy = String(data.get("delivery-policy") ?? "message");
        const baseBranch = String(data.get("base-branch") ?? "").trim() || null;
        if (policy === "message" && baseBranch) {
            errors["github-repository"] =
                "Choose Git actions when specifying a base branch.";
            setupErrors.set(renderedContext, errors);
            showSetupErrors();
            return;
        }
        await command(
            handoff ? WorkAction.PrepareGitRepository : WorkAction.Execute,
            {
                ...(policy === "message"
                    ? {}
                    : {
                          delivery: {
                              baseBranch,
                              push: policy === "push" || policy === "pr",
                              openPullRequest: policy === "pr",
                          },
                      }),
                repository: {
                    repository: gitHubRepository,
                    ...(gitAuthorName ? { gitAuthorName } : {}),
                    ...(gitAuthorEmail ? { gitAuthorEmail } : {}),
                },
                ...(handoff ? {} : modelPayload(w)),
            },
        );
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
            error = errorMessage(failure, "Could not reach Goblin. Try again.");
            render();
        }
        return;
    }
    const text = String(data.get("reply") ?? "").trim();
    if (!text) return;
    if (kind === "new") {
        let id = "";
        const confirmed = await send("/api/work/commands", async () => {
            const [commandId, workId] = await reserve(
                IdentityKind.Command,
                IdentityKind.Work,
            );
            id = workId;
            return { commandId, workId, action: WorkAction.Create, text };
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
                    IdentityKind.Message,
                    ...(activeChat ? [] : [IdentityKind.Conversation]),
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
                ? WorkAction.RequestChanges
                : w?.attention?.reason === AttentionReason.InputRequired
                  ? WorkAction.Answer
                  : WorkAction.AddContext,
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
        event.target.id === "integration-search"
    ) {
        integrations.search = event.target.value;
        render();
        return;
    }
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-effort"
    ) {
        const choice = modelSelection(composerWork());
        const available = availableEfforts(
            modelCatalogs.get(choice.connectionId).catalog,
            choice.model,
        );
        if (available.length) {
            const requested = Number(event.target.value);
            const nearest = available.reduce((best, stop) =>
                Math.abs(stop.index - requested) <
                Math.abs(best.index - requested)
                    ? stop
                    : best,
            );
            setModelEffort(nearest.value, true);
        }
        return;
    }
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
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-effort"
    ) {
        if (!modelSliderDragging) render(false, true);
        return;
    }
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "github-repository"
    ) {
        const errors = setupErrors.get(renderedContext);
        if (errors) delete errors["github-repository"];
        showSetupErrors();
    }
});
document.addEventListener("pointerdown", (event) => {
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-effort"
    )
        modelSliderDragging = true;
});
document.addEventListener("pointerup", () => {
    modelSliderDragging = false;
});
document.addEventListener("pointercancel", () => {
    modelSliderDragging = false;
});
document.addEventListener("focusin", (event) => {
    if (
        modelPickerOpen &&
        event.target instanceof Element &&
        !event.target.closest(".model-picker")
    ) {
        modelPickerOpen = false;
        modelListOpen = false;
        root.querySelector("#model-popover")?.remove();
        root.querySelector(".model-picker-trigger")?.setAttribute(
            "aria-expanded",
            "false",
        );
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
    if (settings.isOpen || integrations.isPickerOpen) return;
    if (
        view === "integrations" &&
        event.target instanceof HTMLElement &&
        event.target.closest('[role="tablist"]') &&
        ["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)
    ) {
        event.preventDefault();
        integrations.tab =
            event.key === "Home"
                ? "all"
                : event.key === "End"
                  ? "connected"
                  : integrations.tab === "all"
                    ? "connected"
                    : "all";
        render();
        document
            .getElementById("integrations-tab-" + integrations.tab)
            ?.focus();
        return;
    }
    const workspaceMenu =
        root.querySelector<HTMLDetailsElement>("#workspace-menu");
    if (event.key === "Escape" && workspaceMenu?.open) {
        event.preventDefault();
        workspaceMenu.open = false;
        workspaceMenu.querySelector<HTMLElement>("summary")?.focus();
        return;
    }
    if (
        (event.ctrlKey || event.metaKey) &&
        event.key.toLowerCase() === "k" &&
        authenticated &&
        !activityOpen &&
        !(mobile.matches && !sidebarCollapsed)
    ) {
        event.preventDefault();
        document
            .getElementById(
                view === "integrations"
                    ? "integration-search"
                    : view === "knowledge"
                      ? "knowledge-search"
                      : "work-search",
            )
            ?.focus();
        return;
    }
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-effort" &&
        [
            "ArrowLeft",
            "ArrowDown",
            "ArrowRight",
            "ArrowUp",
            "Home",
            "End",
        ].includes(event.key)
    ) {
        const choice = modelSelection(composerWork());
        const available = availableEfforts(
            modelCatalogs.get(choice.connectionId).catalog,
            choice.model,
        );
        if (available.length) {
            event.preventDefault();
            const currentIndex = Number(event.target.value);
            const next =
                event.key === "Home"
                    ? available[0]
                    : event.key === "End"
                      ? available.at(-1)!
                      : ["ArrowRight", "ArrowUp"].includes(event.key)
                        ? (available.find(
                              (stop) => stop.index > currentIndex,
                          ) ?? available.at(-1)!)
                        : (available.findLast(
                              (stop) => stop.index < currentIndex,
                          ) ?? available[0]);
            setModelEffort(next.value, true);
        }
        return;
    }
    if (event.key === "Escape" && modelPickerOpen) {
        event.preventDefault();
        modelPickerOpen = false;
        modelListOpen = false;
        render(false, true);
        root.querySelector<HTMLElement>(".model-picker-trigger")?.focus();
        return;
    }
    if (
        event.key === "Escape" &&
        search &&
        !(mobile.matches && !sidebarCollapsed) &&
        !activityOpen
    ) {
        search = "";
        render();
        document.getElementById("work-search")?.focus();
        return;
    }
    if (activityOpen) {
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
    render(false, true);
}
compact.addEventListener("change", updateResponsiveLayout);
mobile.addEventListener("change", updateResponsiveLayout);
window.addEventListener("popstate", () => {
    const params = new URLSearchParams(location.search);
    const section = pageFromPath();
    const item = params.get("item");
    const conversation = params.get("conversation");
    navigate(
        section ?? (item ? "work" : conversation ? "chat" : "new"),
        item ?? conversation ?? "",
        "restore",
    );
    render();
    root.querySelector<HTMLElement>(
        ".directory-heading h1, #work-title-heading, #reply",
    )?.focus();
});
window.addEventListener("online", () => void refresh());
document.addEventListener("visibilitychange", () => {
    if (!document.hidden) void refresh();
});
setInterval(() => {
    if (!document.hidden) void refresh();
}, 3000);
if (!pageFromPath() && query.has("conversation")) {
    view = "chat";
    activeChat = query.get("conversation") ?? "";
    conversationsOpen = true;
}
render();
void refresh();
