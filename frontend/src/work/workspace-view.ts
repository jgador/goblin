import {
    AttentionReason,
    ConnectionAvailability,
    FailureKind,
    GitRepositoryAuthorizationStatus,
    WorkEventKind,
    WorkTitleLimit,
    WorkStatus,
} from "../api/values.js";
import { renderKnowledge } from "../knowledge/page.js";
import { settingsHref, settingsSectionName } from "../settings/navigation.js";
import { formatTimestamp as time } from "../settings/timezone.js";
import { renderBoard, renderInboxControls, workStatus } from "./board.js";
import type { BoardLayout, ListStatus } from "./board.js";
import type { View, Work } from "./contracts.js";
import { renderModelPicker } from "./model-picker.js";
import type { WorkspaceNavigation } from "./navigation.js";
import {
    connectionStatus,
    escapeHtml as e,
    failureLabels,
    icon,
    workTitle,
} from "./presentation.js";
import { isRepositoryHandoff } from "./repository-setup.js";
import {
    matchesWork,
    renderActivity,
    renderConversation,
    renderDecisions,
    renderExecutions,
    renderOutputs,
    renderProgress,
    reviewableResult,
} from "./surface.js";
import type { WorkspaceController } from "./workspace.js";
import { canGenerateWorkTitle } from "./workspace.js";

type WorkspaceState = WorkspaceController["state"];

function matchingWork(workspace: WorkspaceState, query: string) {
    const { work, conversations, agents } = workspace;
    return work
        .filter(
            ({ work }) =>
                !query ||
                matchesWork(
                    work,
                    query,
                    conversations,
                    agents.find((agent) => agent.id === work.agentId)?.name ??
                        "",
                ),
        )
        .toSorted((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
}

export function renderSearch(workspace: WorkspaceState, search: string) {
    const query = search.trim().toLocaleLowerCase();
    if (!query)
        return {
            status: "Search saved objectives, results, and messages.",
            results: "",
        };
    const work = matchingWork(workspace, query);
    const chats = workspace.conversations.filter(
        ({ workId, title, messages }) =>
            !workId &&
            [title, ...messages.map(({ text }) => text)].some((text) =>
                text.toLocaleLowerCase().includes(query),
            ),
    );
    const unavailable = [
        workspace.workUnavailable ? "Work" : "",
        workspace.conversationsUnavailable ? "Conversations" : "",
    ].filter(Boolean);
    const count = work.length + chats.length;
    const status = unavailable.length
        ? `${unavailable.join(" and ")} could not be refreshed.${count ? ` Showing ${count} available ${count === 1 ? "result" : "results"}.` : ""}`
        : count
          ? `${count} ${count === 1 ? "result" : "results"}`
          : workspace.loaded
            ? "No matching work or conversations."
            : "Loading workspace results…";
    const option = (
        kind: "work" | "chat",
        id: string,
        title: string,
        context: string,
    ) => {
        const key = `search-${kind}-${e(id)}`;
        return `<div id="${key}" class="search-option" role="option" aria-selected="false" aria-labelledby="${key}-title" aria-describedby="${key}-context" data-action="search-${kind}" data-id="${e(id)}">${icon(kind)}
            <span><strong id="${key}-title">${e(title)}</strong><small id="${key}-context">${e(context)}</small></span>
        </div>`;
    };
    const group = (kind: string, title: string, options: string[]) =>
        options.length
            ? `<div class="search-group" role="group" aria-labelledby="search-group-${kind}"><h3 id="search-group-${kind}">${title} <span aria-hidden="true">${options.length}</span></h3>${options.join("")}</div>`
            : "";
    return {
        status,
        results:
            group(
                "work",
                "Work",
                work.map(({ work: item }) =>
                    option(
                        "work",
                        item.id,
                        workTitle(item),
                        `Work ${item.id} · ${workStatus(item)}`,
                    ),
                ),
            ) +
            group(
                "chat",
                "Conversations",
                chats.map((chat) =>
                    option(
                        "chat",
                        chat.id,
                        chat.title,
                        `${chat.messages.length} ${chat.messages.length === 1 ? "saved message" : "saved messages"}`,
                    ),
                ),
            ),
    };
}

type WorkspaceViewInput = {
    navigation: Pick<
        WorkspaceNavigation,
        | "view"
        | "selected"
        | "activeChat"
        | "isDirectory"
        | "settingsSection"
        | "settingsReturn"
    >;
    workspace: WorkspaceState;
    mobile: boolean;
    sidebarCollapsed: boolean;
    activityOpen: boolean;
    showChanges: boolean;
    changesOpen: boolean;
    changesPreviewOpen: boolean;
    conversationsOpen: boolean;
    listStatus: ListStatus;
    boardLayout: BoardLayout;
    inboxSearch: string;
    refreshInterval: number;
    refreshIntervals: readonly number[];
    draft: string;
    changing: boolean;
    authenticated: boolean;
    sessionChecked: boolean;
    sessionFailures: number;
    timezoneError: string;
    error: string;
    submission: {
        sending: boolean;
        pending: boolean;
        generatingTitle: boolean;
    };
    titleEdit?: { workId: string; original: string | null; value: string };
    model: Parameters<typeof renderModelPicker>[0] | undefined;
    canExecute: boolean;
};

export function renderWorkspace(input: WorkspaceViewInput) {
    const {
        navigation,
        workspace,
        mobile,
        sidebarCollapsed,
        activityOpen,
        showChanges,
        changesOpen,
        changesPreviewOpen,
        conversationsOpen,
        listStatus,
        boardLayout,
        inboxSearch,
        refreshInterval,
        refreshIntervals,
        draft,
        changing,
        authenticated,
        sessionChecked,
        sessionFailures,
        timezoneError,
        error,
        submission,
        titleEdit,
        model,
        canExecute,
    } = input;
    const { view, selected, activeChat, isDirectory } = navigation;
    const inbox = view === "board";
    const {
        work,
        conversations,
        agents,
        connections,
        runtimes,
        repositories,
        loaded,
        workUnavailable,
        connectionsLoaded,
        connectionsUnavailable,
    } = workspace;
    const item = work.find(({ work }) => work.id === selected);
    const review = view === "work" ? reviewableResult(item?.work) : undefined;
    const workAgent = (work: Work) =>
        agents.find((agent) => agent.id === work.agentId);
    const workConnection = (work: Work) =>
        connections.find(
            (connection) => connection.id === workAgent(work)?.connectionId,
        );
    const codexAvailability =
        connections.find((connection) => connection.runtime === "codex")
            ?.availability ?? ConnectionAvailability.Disconnected;
    const canCreate = canGenerateWorkTitle(workspace);
    const chats = conversations.filter((chat) => !chat.workId);
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
        `<span class="status-pill ${statusClass(w)}">${icon(attention(w) ? "wait" : w.status === WorkStatus.Completed ? "check" : "activity")}${e(workStatus(w))}</span>`;
    const button = (
        action: string,
        text: string,
        primary = false,
        disabled = false,
    ) =>
        `<button class="${primary ? "primary" : "secondary"}" data-action="${action}" ${disabled || submission.sending || ((submission.pending || workUnavailable) && !["resend", "dismiss", "new-work"].includes(action)) ? "disabled" : ""}>${e(text)}</button>`;
    function message(author: string, text: string, at?: string) {
        return `<div class="message"><div class="${author === "Goblin" ? "bot-avatar" : "avatar"}">${author === "Goblin" ? '<img src="/assets/branding/icon.svg" alt="">' : "You"}</div><div><div class="message-name">${e(author)}<time>${at ? e(time(at)) : ""}</time></div><div class="message-text"><p class="preserve-lines">${e(text)}</p></div></div></div>`;
    }
    function connectionMenu() {
        return `<button id="codex-connection-trigger" class="connection-choice" type="button" aria-label="Codex connection" popovertarget="codex-connection-menu"><img src="/assets/providers/codex.png" width="24" height="24" alt=""><span>Codex</span></button>
            <div id="codex-connection-menu" class="connection-menu" popover>
                <p class="connection-menu-status" role="status">${currentConnectionStatus(codexAvailability)}</p>
                <a href="${settingsHref("codex")}" data-action="settings-codex" data-settings-return-focus="codex-connection-trigger">${icon("settings")}Manage Codex connection</a>
            </div>`;
    }
    function currentConnectionStatus(availability?: ConnectionAvailability) {
        if (!connectionsLoaded) return "Checking connection…";
        return connectionStatus(
            connectionsUnavailable ? undefined : availability,
        );
    }
    function composer(
        kind: "new" | "chat" | "work",
        placeholder: string,
        work?: Work,
    ) {
        const prompt = kind !== "work";
        const closed =
            work?.status === WorkStatus.Completed ||
            work?.status === WorkStatus.Cancelled;
        const sendLabel =
            kind === "new"
                ? "Create work"
                : kind === "chat"
                  ? "Save message"
                  : changing
                    ? "Send change request"
                    : closed
                      ? "Add note"
                      : work?.attention?.reason ===
                          AttentionReason.InputRequired
                        ? "Send answer"
                        : "Save context";
        const help = closed ? "Saving a note keeps this Work closed." : "";
        const input = `<label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="${prompt ? 1 : 2}" maxlength="4000" placeholder="${e(placeholder)}" required ${prompt || help ? 'aria-describedby="composer-help"' : ""} ${submission.sending ? "disabled" : ""}>${e(draft)}</textarea>`;
        const blocked =
            submission.sending ||
            submission.pending ||
            workUnavailable ||
            (kind === "new" && !canCreate);
        const send = `<button class="send" type="submit" ${blocked ? 'data-blocked="true"' : ""} ${blocked || !draft.trim() ? "disabled" : ""}>${sendLabel}${icon(prompt && kind === "new" ? "plus" : "up")}</button>`;
        if (prompt)
            return `<div class="composer-wrap"><form class="composer prompt-composer" data-form="${kind}">${input}<div class="composer-footer"><div class="composer-footer-start">${kind === "new" ? connectionMenu() : ""}</div>${send}</div></form><p id="composer-help" class="composer-hint">${kind === "new" ? "Codex writes a title for your goal. Start execution from the Ready item." : "Save messages in this conversation. Choose Track this work to turn it into a Work item."}</p></div>`;
        const workTools = `${work && (work.results.length || work.artifacts.length) ? `<button class="composer-chip" type="button" data-action="view-outputs" aria-controls="work-outputs">${icon("file")}Outputs</button>` : ""}${changing ? `<button class="composer-chip" type="button" data-action="changes" ${submission.sending ? "disabled" : ""}>${icon("close")}Cancel change request</button>` : ""}${help ? `<p id="composer-help" class="composer-hint">${help}</p>` : ""}`;
        return `<div class="composer-wrap"><form class="composer" data-form="work">${input}<div class="composer-footer"><div class="composer-footer-start">${workTools}</div>${send}</div></form></div>`;
    }
    function renderWorkspaceMenu() {
        return `<details id="workspace-menu" class="workspace-menu">
            <summary id="workspace-menu-trigger" aria-label="Workspace menu" title="Workspace menu">${icon("settings")}<span>Goblin workspace</span>${icon("chevron", "workspace-menu-chevron")}</summary>
            <div class="workspace-menu-items">
                <button data-action="settings">${icon("settings")}Settings</button>
                <button data-action="settings-system">${icon("activity")}System</button>
                <button data-action="lock">${icon("lock")}Lock workspace</button>
            </div>
        </details>`;
    }
    function renderSidebar() {
        const attentionCount = work.filter(({ work: item }) =>
            attention(item),
        ).length;
        return `<aside id="workspace-sidebar" class="sidebar" aria-label="Workspace" ${sidebarCollapsed || activityOpen ? "inert" : ""} ${mobile && !sidebarCollapsed ? 'role="dialog" aria-modal="true"' : ""}>
            <div class="sidebar-heading">
                <a class="brand" href="/" data-action="browse-work"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a>
                <button id="hide-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Hide sidebar" aria-expanded="true" aria-controls="workspace-sidebar">${icon("sidebar")}</button>
            </div>
            <nav class="sidebar-start" aria-label="Work navigation">
                <button class="nav-button" data-action="new-work">${icon("plus")}New work</button>
                <button id="nav-work-search" class="nav-button" data-action="open-search" aria-haspopup="dialog" aria-keyshortcuts="Control+K Meta+K">${icon("search")}<span>Search</span><kbd aria-hidden="true">Ctrl K</kbd></button>
                <button class="nav-button ${inbox ? "active" : ""}" data-action="browse-work" ${inbox ? 'aria-current="page"' : ""} ${attentionCount ? 'aria-describedby="work-inbox-attention"' : ""}>${icon("inbox")}<span>Work inbox</span>${attentionCount ? `<span class="inbox-nav-count" aria-hidden="true">${attentionCount}</span>` : ""}</button>
                ${attentionCount ? `<span class="sr-only" id="work-inbox-attention">${attentionCount} ${attentionCount === 1 ? "Work item needs" : "Work items need"} attention</span>` : ""}
                <a id="nav-knowledge" class="nav-button ${view === "knowledge" ? "active" : ""}" href="/knowledge" data-action="knowledge" ${view === "knowledge" ? 'aria-current="page"' : ""}>${icon("knowledge")}Knowledge</a>
            </nav>
            <div class="sidebar-history" data-scroll="sidebar">
                <button class="history-disclosure" data-action="view-chat" aria-expanded="${conversationsOpen}" aria-controls="conversation-history">Conversations${icon("chevron")}</button>
                <button class="nav-button" data-action="new-chat">${icon("compose")}New chat</button>
                <nav id="conversation-history" class="work-history" aria-label="Saved conversations" ${conversationsOpen ? "" : "hidden"}>${chats.map((chat) => `<button class="work-card ${view === "chat" && activeChat === chat.id ? "selected" : ""}" data-action="select-chat" data-id="${e(chat.id)}" aria-current="${view === "chat" && activeChat === chat.id ? "page" : "false"}">${icon("chat")}<span class="work-title">${e(chat.title)}</span></button>`).join("") || '<p class="sidebar-empty">No saved conversations.</p>'}</nav>
            </div>
            ${sidebarCollapsed ? "" : renderWorkspaceMenu()}
        </aside>`;
    }
    function renderHeader() {
        const intervalLabel = (seconds: number) =>
            seconds ? `${seconds} seconds` : "Off";
        const refresh = `<details id="workspace-refresh" class="workspace-refresh${inbox ? " inbox-refresh" : ""}">
            <summary id="workspace-refresh-trigger" class="icon-button refresh-trigger" aria-label="Auto-refresh interval, ${intervalLabel(refreshInterval)}" title="Auto-refresh interval">${icon("clock")}<span>${refreshInterval ? `${refreshInterval}s` : "Off"}</span>${icon("chevron", "refresh-chevron")}</summary>
            <div class="refresh-menu">
                <fieldset><legend>Auto-refresh interval</legend>
                    <div class="refresh-options">${refreshIntervals
                        .map(
                            (seconds) =>
                                `<label><input id="workspace-refresh-${seconds}" type="radio" name="workspace-refresh-interval" value="${seconds}" ${seconds === refreshInterval ? "checked" : ""}>${intervalLabel(seconds)}</label>`,
                        )
                        .join("")}</div>
                </fieldset>
                <button type="button" data-action="refresh">${icon("refresh")}Refresh now</button>
            </div>
        </details>`;
        const changes =
            showChanges && !review
                ? `<button id="show-changes" class="icon-button changes-toggle" type="button" data-action="view-changes" aria-label="Changes" title="Changes" aria-expanded="${changesPreviewOpen || changesOpen}" aria-controls="${changesOpen ? "work-changes-host" : "work-changes-preview-host"}">${icon("listControls")}</button>`
                : "";
        const section = {
            settings: "Settings",
            knowledge: "Knowledge",
            board: "Work inbox",
            work: "Work",
            new: "New work",
            chat: activeChat ? "Conversation" : "New chat",
        }[view];
        const returnLabel = {
            board: "inbox",
            new: "New work",
            knowledge: "Knowledge",
            work: "Work",
            chat: navigation.settingsReturn.id ? "conversation" : "New chat",
            settings: "Settings",
        }[navigation.settingsReturn.view];
        const breadcrumb =
            view === "work"
                ? `<button class="text-button work-return" data-action="browse-work">${icon("back")}Back to inbox</button>`
                : `<div class="workspace-breadcrumb"><span>Workspace</span>${icon("chevron")}<strong>${section}</strong>${view === "settings" ? `${icon("chevron")}<span>${e(settingsSectionName(navigation.settingsSection))}</span>` : ""}</div>`;
        const actions =
            view === "settings"
                ? `<button class="text-button settings-return" data-action="return-settings">${icon("back")}Back to ${returnLabel}</button>`
                : inbox
                  ? renderInboxControls(boardLayout, listStatus)
                  : !isDirectory
                    ? `${refresh}${changes}`
                    : "";
        return `<header class="workspace-header${inbox ? " inbox-header" : ""}" ${(mobile && !sidebarCollapsed) || activityOpen ? "inert" : ""}>
            ${
                sidebarCollapsed
                    ? `<div class="header-navigation">
                <button id="show-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Show sidebar" aria-expanded="false" aria-controls="workspace-sidebar">${icon("sidebar")}</button>
                <button id="show-work-search" class="icon-button" data-action="open-search" aria-label="Search" title="Search" aria-haspopup="dialog" aria-keyshortcuts="Control+K Meta+K">${icon("search")}</button>
                ${renderWorkspaceMenu()}
            </div>`
                    : ""
            }
            ${breadcrumb}
            ${inbox ? refresh : ""}
            <div class="header-actions">${actions}</div>
        </header>`;
    }
    function renderBoardView() {
        return renderBoard({
            total: work.length,
            matches: matchingWork(
                workspace,
                inboxSearch.trim().toLocaleLowerCase(),
            ),
            loaded,
            workUnavailable,
            listStatus,
            layout: boardLayout,
            search: inboxSearch,
        });
    }
    function renderActivityPanel() {
        return `<aside id="work-activity" class="activity-panel ${activityOpen ? "is-open" : ""}" aria-label="Activity timeline" ${activityOpen ? 'role="dialog" aria-modal="true"' : "inert"}><div class="activity-heading"><h2>${icon("activity")}Activity timeline</h2><button id="close-activity" class="icon-button" data-action="toggle-activity" aria-label="Close activity">${icon("close")}</button></div><div class="activity-body" data-scroll="activity">${renderActivity(view === "work" ? item?.work : undefined)}</div></aside>`;
    }
    function renderHome() {
        return `<section class="home" aria-labelledby="home-title"><div class="home-content"><h1 id="home-title" tabindex="-1">New work</h1>${composer("new", "Describe the work")}${connectionNoticeForTitle("new")}</div></section>`;
    }
    function connectionNoticeForTitle(id: "new" | "chat") {
        const showNotice =
            connectionsLoaded &&
            (connectionsUnavailable ||
                codexAvailability !== ConnectionAvailability.Available);
        const disconnected =
            !connectionsUnavailable &&
            codexAvailability === ConnectionAvailability.Disconnected;
        const notice = disconnected
            ? "Codex is not connected."
            : `Codex · ${currentConnectionStatus(codexAvailability)}`;
        return `<div class="connection-nudge" ${showNotice ? "" : "hidden"}>
                <p role="status">${icon("wait")}${notice}</p>
                <a id="${id === "new" ? "new-work-codex-settings" : "chat-work-codex-settings"}" href="${settingsHref("codex")}" data-action="settings-codex" data-settings-return-focus="${id === "new" ? "codex-connection-trigger" : "chat-work-codex-settings"}">${disconnected ? "Connect Codex" : "Open Codex settings"}${icon("arrow")}</a>
            </div>`;
    }
    function renderWorkTitle(work: Work) {
        const title = e(workTitle(work));
        if (titleEdit?.workId !== work.id)
            return `<div class="work-title-row"><h1 id="work-title-heading" tabindex="-1">${title}</h1><button id="rename-work" class="icon-button" type="button" data-action="rename-work" aria-label="Rename work" title="Rename work" ${submission.sending || submission.pending || workUnavailable ? "disabled" : ""}>${icon("pencil")}</button></div>`;
        const length = Array.from(titleEdit.value.trim()).length;
        const conflict = titleEdit.original !== (work.title ?? null);
        const blocked =
            submission.sending ||
            submission.pending ||
            workUnavailable ||
            conflict;
        return `<h1 id="work-title-heading" class="sr-only" tabindex="-1">${title}</h1>
            <form class="work-title-edit" data-form="rename-work">
                <label class="sr-only" for="work-title-input">Work title</label>
                <input class="field-control" id="work-title-input" name="title" type="text" autocomplete="off" required value="${e(titleEdit.value)}" aria-describedby="work-title-count${conflict ? " work-title-conflict" : ""}" aria-invalid="${length > WorkTitleLimit}" ${submission.sending ? "readonly" : ""}>
                <div class="work-title-edit-actions">
                    <span id="work-title-count" class="field-hint">${length} / ${WorkTitleLimit}</span>
                    <button class="secondary" type="button" data-action="cancel-rename" ${submission.sending ? "disabled" : ""}>Cancel</button>
                    <button class="primary" type="submit" ${blocked ? 'data-blocked="true"' : ""} ${blocked || !length || length > WorkTitleLimit ? "disabled" : ""}>Save</button>
                </div>
                ${conflict ? '<p id="work-title-conflict" class="field-hint" role="status">The title changed to “' + title + "”. Reopen Rename to edit the latest title.</p>" : ""}
            </form>`;
    }
    function renderWorkReferences(item: View) {
        const w = item.work,
            attempt = w.attempts.at(-1);
        const outputCount = w.results.length + w.artifacts.length;
        return `<details id="work-outputs" class="work-reference"><summary><span>Saved outputs <span class="output-count">${outputCount}</span></span>${icon("chevron", "disclosure-icon")}</summary><section class="work-reference-content" aria-label="Outputs">${renderOutputs(w)}</section></details>
        <details id="work-reference" class="work-reference"><summary>Execution details${icon("chevron", "disclosure-icon")}</summary><div class="work-reference-content work-support">
        ${w.decisions.length ? `<section class="work-section" aria-labelledby="decisions-heading"><div class="section-content"><div class="section-heading"><h3 id="decisions-heading">Decisions</h3><span class="section-count">${w.decisions.length}</span></div>${renderDecisions(w)}</div></section>` : ""}
        <section class="work-section progress-section" aria-labelledby="progress-heading"><div class="section-content"><div class="section-heading"><h3 id="progress-heading">Progress</h3></div>${renderProgress(item)}</div></section>
        ${w.attempts.length ? `<section class="work-section executions-section" aria-labelledby="executions-heading"><div class="section-content"><div class="section-heading"><h3 id="executions-heading">Executions</h3><span class="section-count">${w.attempts.length}</span></div>${renderExecutions(w)}</div></section>` : ""}
        <section class="work-section work-info" aria-labelledby="work-info-heading"><div class="section-content"><div class="section-heading"><h3 id="work-info-heading">Details</h3></div><dl class="work-properties"><dt>Created</dt><dd>${e(time(item.createdAt))}</dd><dt><button class="text-button" data-action="settings-agents">Agent ${icon("arrow")}</button></dt><dd>${e(workAgent(w)?.name ?? "Goblin")}</dd>${attempt?.target.repository ? `<dt>Repository</dt><dd class="preserve-lines">${e(attempt.target.repository.repository)}${attempt.target.repository.grant ? `<span class="repository-branch">${icon("branch")}${e(attempt.target.repository.grant.branch)}</span>` : ""}</dd>` : ""}</dl><div class="work-info-actions"><button class="text-button" data-action="discuss-goal">${icon("chat")}Discuss goal</button><button class="text-button" data-action="settings-github">${icon("github")}GitHub</button></div>${review ? cancelWork(w) : ""}</div></section>
        </div></details>`;
    }
    function renderSelectedWork(item: View) {
        const w = item.work;
        const reply = changing
            ? "What would you like Goblin to change?"
            : w.attention?.reason === AttentionReason.InputRequired
              ? "Answer Goblin’s question…"
              : w.status === WorkStatus.Completed ||
                  w.status === WorkStatus.Cancelled
                ? "Write a note…"
                : "Add context to this work…";
        const patchHost = '<div id="work-changes-preview-host"></div>';
        const reviewArea = review
            ? `<section class="review-decision" aria-labelledby="review-heading"><h2 id="review-heading">Review result</h2>
                ${patchHost}
                <p>Approving the result completes this Work.${w.workspace ? " Goblin then deletes its local workspace files." : ""}</p>
                <div class="work-state-actions">${button("approve", "Approve & complete", true)}${button("changes", "Request changes", false, changing)}</div></section>`
            : "";
        return `<div class="work-selection${changesOpen ? " work-review-layout" : ""}">
            <section class="detail" aria-label="Selected work">
                <div class="selected-work-top">
                    <header class="selected-work-header">
                        <div class="selected-work-identity">${renderWorkTitle(w)}<div class="work-heading-meta">${w.title ? `<span>Work ${e(w.id)}</span>` : ""}${status(w)}</div></div>
                        <div class="selected-work-actions">
                            <button class="icon-button" data-action="share-work" aria-label="Share" title="Share work">${icon("share")}</button>
                            <button id="show-activity" class="text-button activity-trigger" data-action="toggle-activity" aria-label="Activity timeline" aria-expanded="${activityOpen}" aria-controls="work-activity">${icon("activity")}Activity</button>
                        </div>
                    </header>
                    ${review ? "" : patchHost}
                </div>
                <div class="detail-body work-thread" data-scroll="detail">
                    <div class="work-thread-content">
                        <section id="work-conversation" class="work-conversation" aria-label="Conversation about this Work">
                            <details id="work-objective" class="work-objective">
                                <summary>Full objective${icon("chevron", "disclosure-icon")}</summary>
                                <p class="work-objective-full preserve-lines">${e(w.objective)}</p>
                            </details>
                            ${renderConversation(w, workAgent(w)?.name ?? "Goblin", review, reviewArea)}
                        </section>
                        ${controls(w)}
                        <div class="work-references">${renderWorkReferences(item)}</div>
                    </div>
                </div>
                <div class="work-composer">${composer("work", reply, w)}</div>
            </section>
            <div id="work-changes-host" ${changesOpen ? "" : "hidden"}></div>
        </div>`;
    }
    function gitHubRepositorySetup(w: Work, required = false) {
        const handoff = isRepositoryHandoff(w);
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
            w.workspace?.repository ??
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
                : approval?.status ===
                    GitRepositoryAuthorizationStatus.Invalidated
                  ? "<p>Context changed. Review repository access again.</p>"
                  : approval?.status === GitRepositoryAuthorizationStatus.Denied
                    ? "<p>Repository access was declined. You can review a different request.</p>"
                    : "";
        return `${preview}<details id="github-repository-options" data-approval="${approval ? `${approval.id}:${approval.status}` : ""}" ${required && !pendingApproval ? "open" : ""}>
        <summary>${icon("chevron")}${pendingApproval ? "Change repository or actions" : required || handoff ? "Repository access" : w.workspace ? "Repository settings" : "Add repository"}</summary>
        <p>Choose a GitHub repository for this Work. Goblin uses the repository’s default branch and your connected GitHub commit identity unless you specify them.</p>
        ${suggestions.length > 1 ? `<p class="field-hint">Several repositories match: ${e(suggestions.join(", "))}. Choose the full name.</p>` : ""}
        <form class="work-setup github-repository-form" data-form="github-repository" novalidate>
            <div><label for="github-repository">Repository</label>
                <input class="field-control" id="github-repository" name="github-repository" list="known-github-repositories" value="${e(name)}" placeholder="owner/repository or GitHub URL" ${w.workspace ? "readonly" : "required"} aria-describedby="github-repository-error">
                <datalist id="known-github-repositories">${repositories.map((r) => `<option value="${e(r.name)}">${r.enabled ? "Enabled" : "Requires enablement"}</option>`).join("")}</datalist>
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
            ${runtimes.some((r) => r.repositoryExecution) ? `<p class="field-hint">An enabled repository starts Work. Other repositories require approval first.</p><div class="github-repository-actions"><button id="start-github-repository" type="submit" class="primary" ${submission.sending || submission.pending || (!handoff && !canExecute) ? "disabled" : ""}>Use repository</button></div>` : '<p role="status">Repository execution is unavailable on this Goblin.</p>'}
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
        return `<div class="work-executor"><img src="/assets/branding/icon.svg" alt=""><div class="executor-identity"><strong>${e(workAgent(w)?.name ?? "Goblin")}</strong>${runtime ? `<span>Uses ${e(runtime)}</span>` : ""}</div>${available ? (model ? renderModelPicker(model) : "") : ""}</div>${available || w.attention?.reason === AttentionReason.Failure ? "" : `<p role="status">${notice}</p>${connectionsLoaded ? button("settings-codex", disconnected ? "Connect Codex" : "Manage Codex connection", true) : ""}`}`;
    }
    function repositoryScope(w: Work) {
        const previous = w.attempts.findLast(
            (attempt) =>
                attempt.target.repository &&
                (!w.workspace ||
                    attempt.target.repository.repository ===
                        w.workspace.repository),
        )?.target.repository;
        const repository = w.workspace?.repository ?? previous?.repository;
        if (!repository)
            return '<p class="repository-scope">No repository attached</p>';
        const grant = previous?.grant;
        const delivery = grant?.allowPullRequest
            ? "Push and open a draft PR"
            : grant?.allowPush
              ? "Push changes"
              : "Keep changes local";
        return `<dl class="execution-properties repository-scope">
            <dt>${w.workspace ? "Repository" : "Previous repository"}</dt><dd>${e(repository)}</dd>
            ${grant ? `<dt>Previous base branch</dt><dd>${e(grant.baseBranch)}</dd><dt>Previous delivery</dt><dd>${delivery}</dd>` : ""}
        </dl>`;
    }
    function failureRecovery(w: Work) {
        const attempt = w.attempts.at(-1);
        const failure = w.attention?.failure ?? attempt?.failure;
        const related =
            failure === FailureKind.ConnectionUnavailable ||
            failure === FailureKind.RuntimeDisconnected;
        const failedAt =
            attempt?.finishedAt ??
            w.history.findLast(
                (event) =>
                    event.kind === WorkEventKind.ExecutionFailed &&
                    (!attempt || event.attemptId === attempt.id),
            )?.occurredAt;
        const availability = workConnection(w)?.availability;
        const available = availability === ConnectionAvailability.Available;
        const manage = `<a id="recovery-codex" class="${available ? "secondary" : "primary"}" href="${settingsHref("codex")}" data-action="settings-codex">Manage Codex connection</a>`;
        const activity = `<button id="recovery-activity" class="secondary" data-action="toggle-activity" aria-expanded="${activityOpen}" aria-controls="work-activity">View activity</button>`;
        const guidance =
            failure === FailureKind.WorkspaceCapacityReached
                ? "Close Work that still has a workspace or raise the workspace limit before retrying."
                : related
                  ? available
                      ? "Codex is connected now. Check the last attempt before retrying."
                      : availability === ConnectionAvailability.Changing ||
                          availability === ConnectionAvailability.Verifying
                        ? "Wait for the connection check or change to finish before retrying."
                        : "Check the Codex connection before retrying."
                  : "Review the last attempt before deciding whether to retry.";
        return `<h3>Recover work</h3>
            <dl class="execution-properties recovery-facts"><dt>Last attempt</dt><dd>${failure ? failureLabels[failure] : "Execution failed"}${failedAt ? `<time datetime="${e(failedAt)}">${e(time(failedAt))}</time>` : ""}</dd>
            <dt>Codex now</dt><dd>${currentConnectionStatus(availability)}</dd></dl>
            <p>${guidance}</p>
            ${available ? executionSetup(w) : ""}
            <div class="work-state-actions">${!available && connectionsLoaded ? manage : ""}${available ? button("retry", "Retry work", true, !canExecute) : ""}${activity}${available && related ? manage : ""}</div>`;
    }
    function cancelWork(w: Work) {
        return `<div class="work-state-secondary">${w.workspace ? "<p>Cancelling this Work deletes its local workspace files.</p>" : ""}${button("cancel", "Cancel work")}</div>`;
    }
    function controls(w: Work) {
        if (w.attention?.reason === AttentionReason.CleanupRequired)
            return `<section class="decision work-state" aria-label="Current status"><h3>Needs attention</h3><p>The outcome is saved, but Goblin could not finish cleaning up the execution. Reconcile it before continuing.</p>${button("reconcile", "Reconcile execution", true)}</section>`;
        if (w.attempts.at(-1)?.cleanupPending)
            return `<section class="decision work-state" aria-label="Current status"><p>Saving the outcome and finishing execution cleanup…</p></section>`;
        let content = "";
        if (w.status === WorkStatus.Ready)
            content = `<h3>Ready to start</h3>${executionSetup(w)}${repositoryScope(w)}${workConnection(w)?.availability === ConnectionAvailability.Available ? `${button("execute", "Start work", true, !canExecute)}${gitHubRepositorySetup(w)}` : ""}`;
        if (review) return "";
        if (w.attention?.reason === AttentionReason.InputRequired) {
            const decision = w.decisions.at(-1);
            const inConversation = w.history.some(
                (event) =>
                    event.kind === WorkEventKind.InputRequested &&
                    event.decisionId === decision?.id &&
                    event.text,
            );
            content = `<h3>Needs your input</h3>${decision && !inConversation ? `<p>${e(decision.question)}</p>` : ""}${!w.attempts.at(-1)?.target.repository ? gitHubRepositorySetup(w) : ""}`;
        }
        if (w.attention?.reason === AttentionReason.GitRepositoryRequired)
            content = `${w.repositoryAuthorization?.status === GitRepositoryAuthorizationStatus.Pending ? "" : "<h3>Repository access needed</h3><p>Review and authorize the repository and Git actions to continue this Work.</p>"}${gitHubRepositorySetup(w, true)}`;
        if (w.attention?.reason === AttentionReason.Failure)
            content = failureRecovery(w);
        if (w.attention?.reason === AttentionReason.UncertainExecution)
            content = `<h3>Execution needs reconciliation</h3><p>Goblin cannot yet confirm the outcome. Check the execution or stop it before retrying.</p>${button("reconcile", "Reconcile execution", true)}`;
        if (
            [
                WorkStatus.Queued,
                WorkStatus.InProgress,
                WorkStatus.Cancelling,
            ].some((value) => value === w.status)
        )
            content = `<div class="progress-title">${icon("activity")} ${w.status === WorkStatus.InProgress ? "Goblin is working" : e(label(w.status))}</div><p>You can close this page. Accepted work continues independently.</p>`;
        if (
            ![
                WorkStatus.Completed,
                WorkStatus.Cancelled,
                WorkStatus.Cancelling,
            ].some((value) => value === w.status)
        )
            content += cancelWork(w);
        return content
            ? `<section class="decision work-state${w.attention?.reason === AttentionReason.Failure ? " work-recovery" : ""}" aria-label="Current status">${content}</section>`
            : "";
    }
    function renderChat() {
        const c = conversations.find((x) => x.id === activeChat);
        if (!c)
            return `<section class="home" aria-labelledby="chat-title" data-scroll="chat"><div class="home-content"><h1 id="chat-title" tabindex="-1">New chat</h1>${composer("chat", "Write a message")}</div></section>`;
        const messages = c.messages
            .map((m) =>
                message(
                    m.source
                        ? `${m.source.provider} · ${m.source.userId}`
                        : "You",
                    m.text,
                    m.createdAt,
                ),
            )
            .join("");
        const tracking = c.workId
            ? `<button class="tracked-link" data-action="select-work" data-id="${c.workId}">Open tracked work ${icon("arrow")}</button>`
            : button("track", "Track this work", true, !canCreate) +
              connectionNoticeForTitle("chat");
        return `<section class="chat-workspace" aria-labelledby="chat-title"><div class="detail-body" data-scroll="chat"><div class="thread-content"><h1 id="chat-title" class="conversation-title" tabindex="-1">${e(c.title)}</h1>${messages}${tracking}</div></div>${composer("chat", "Write a message")}</section>`;
    }
    const connectionNotice =
        sessionFailures && (sessionChecked || sessionFailures > 1)
            ? '<p class="command-notice" role="status">Could not reach Goblin. Reconnecting…</p>'
            : "";
    if (!authenticated) {
        return `<main class="unlock-page"><a class="brand" href="/"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><section class="unlock-card"><h1>${sessionChecked ? "Open your workspace" : "Opening your workspace…"}</h1>${sessionChecked ? `<p>Enter the password chosen when this Goblin workspace was set up.</p><form data-form="unlock"><label for="password">Goblin password</label><input class="field-control" id="password" name="password" type="password" autocomplete="current-password" required maxlength="128"><button class="primary" type="submit">Open workspace${icon("arrow")}</button></form>` : ""}${connectionNotice}${error ? `<p class="command-notice" role="alert">${e(error)}</p>` : ""}</section></main>`;
    }
    const modal = (mobile && !sidebarCollapsed) || activityOpen;
    return `<a class="skip-link" href="#main-content">Skip to main content</a><div class="app-shell ${sidebarCollapsed ? "sidebar-collapsed" : ""}">${renderHeader()}${renderSidebar()}${mobile && !sidebarCollapsed ? '<button class="sidebar-backdrop" data-action="toggle-sidebar" aria-label="Close navigation" tabindex="-1"></button>' : ""}<main id="main-content" class="main-shell" tabindex="-1" ${modal ? "inert" : ""}>
            <div class="workspace-notices">${connectionNotice}${timezoneError ? `<div class="command-notice" role="status">${e(timezoneError)}</div>` : ""}${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${submission.pending ? `<div class="command-notice" role="status">${submission.sending ? (submission.generatingTitle ? "Generating title…" : "Saving command…") : "Command unconfirmed. Inspect the saved state or resend this same command."}${!submission.sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}</div>
            ${view === "settings" ? '<div id="settings-host"></div>' : view === "knowledge" ? renderKnowledge() : view === "chat" ? renderChat() : view === "work" ? (item ? renderSelectedWork(item) : `<p class="board-empty" role="status">${workUnavailable ? "Work unavailable." : "Loading work…"}</p>`) : view === "new" ? renderHome() : renderBoardView()}</main>${renderActivityPanel()}${activityOpen ? '<button class="activity-backdrop" data-action="toggle-activity" aria-label="Close activity panel" tabindex="-1"></button>' : ""}</div>`;
}
