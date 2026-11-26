import {
    AttentionReason,
    ConnectionAvailability,
    GitRepositoryAuthorizationStatus,
    WorkStatus,
} from "../api/values.js";
import { renderKnowledge } from "../knowledge/page.js";
import { formatTimestamp as time } from "../settings/timezone.js";
import { renderBoard, workFilters, workInFilter, workStatus } from "./board.js";
import type { BoardLayout, WorkFilter } from "./board.js";
import type { View, Work } from "./contracts.js";
import { renderModelPicker } from "./model-picker.js";
import type { WorkspaceNavigation } from "./navigation.js";
import { icon, escapeHtml as e } from "./presentation.js";
import { isRepositoryHandoff } from "./repository-setup.js";
import {
    matchesWork,
    relativeTime,
    renderActivity,
    renderConversation,
    renderDecisions,
    renderExecutions,
    renderOutputs,
    renderProgress,
    renderRecentActivity,
} from "./surface.js";
import type { WorkspaceController } from "./workspace.js";

type WorkspaceViewInput = {
    navigation: Pick<
        WorkspaceNavigation,
        "view" | "selected" | "activeChat" | "isDirectory" | "inboxSelection"
    >;
    workspace: WorkspaceController["state"];
    mobile: boolean;
    sidebarCollapsed: boolean;
    activityOpen: boolean;
    changesOpen: boolean;
    conversationExpanded: boolean;
    conversationsOpen: boolean;
    filter: WorkFilter;
    boardFilter: WorkFilter;
    boardLayout: BoardLayout;
    search: string;
    searchOpen: boolean;
    draft: string;
    changing: boolean;
    authenticated: boolean;
    sessionChecked: boolean;
    sessionFailures: number;
    timezoneError: string;
    error: string;
    submission: { sending: boolean; pending: boolean };
    systemSummary: string;
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
        changesOpen,
        conversationExpanded,
        conversationsOpen,
        filter,
        boardFilter,
        boardLayout,
        search,
        searchOpen,
        draft,
        changing,
        authenticated,
        sessionChecked,
        sessionFailures,
        timezoneError,
        error,
        submission,
        systemSummary,
        model,
        canExecute,
    } = input;
    const { view, selected, activeChat, isDirectory, inboxSelection } =
        navigation;
    const inbox = view === "board" || inboxSelection;
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
    } = workspace;
    const item = work.find(({ work }) => work.id === selected);
    const workAgent = (work: Work) =>
        agents.find((agent) => agent.id === work.agentId);
    const workConnection = (work: Work) =>
        connections.find(
            (connection) => connection.id === workAgent(work)?.connectionId,
        );
    const defaultAgent = agents.find((agent) => agent.isDefault);
    const defaultConnection = connections.find(
        (connection) => connection.id === defaultAgent?.connectionId,
    );
    const ordered = work.toSorted(
        (a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt),
    );
    const query = search.trim().toLocaleLowerCase();
    const matches = query
        ? ordered.filter(({ work: w }) =>
              matchesWork(w, query, conversations, workAgent(w)?.name ?? ""),
          )
        : ordered;
    const chats = conversations.filter(
        (chat) =>
            !chat.workId &&
            (!query ||
                [
                    chat.title,
                    ...chat.messages.map((message) => message.text),
                ].some((text) => text.toLocaleLowerCase().includes(query))),
    );
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
        `<button class="${primary ? "primary" : "secondary"}" data-action="${action}" ${disabled || submission.sending || ((submission.pending || workUnavailable) && !["resend", "dismiss", "new-work", "changes"].includes(action)) ? "disabled" : ""}>${e(text)}</button>`;
    function message(author: string, text: string, at?: string) {
        return `<div class="message"><div class="${author === "Goblin" ? "bot-avatar" : "avatar"}">${author === "Goblin" ? '<img src="/assets/branding/icon.svg" alt="">' : "You"}</div><div><div class="message-name">${e(author)}<time>${at ? e(time(at)) : ""}</time></div><div class="message-text"><p class="preserve-lines">${e(text)}</p></div></div></div>`;
    }
    function connectionButton() {
        return `<button class="connection-choice" type="button" data-action="settings-codex" title="Manage Codex connection"><img src="/assets/providers/codex.png" width="24" height="24" alt=""><span class="connection-name">${e(defaultConnection?.name ?? "Codex")}</span>${icon("chevron")}</button>`;
    }
    function composer(kind: string, placeholder: string) {
        const note =
            kind === "chat"
                ? "Saved as a conversation · Track as Work to start Goblin"
                : changing
                  ? "Requests changes to the current result"
                  : item?.work.attention?.reason ===
                      AttentionReason.InputRequired
                    ? "Answers the pending question"
                    : item?.work.status === WorkStatus.Ready
                      ? "Saves context for this work · Start work when ready"
                      : "Saves context with this work";
        return `<div class="composer-wrap"><form class="composer" data-form="${kind}"><label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="3" maxlength="4000" placeholder="${e(placeholder)}" aria-describedby="${kind === "new" ? "create-work-note" : "composer-note"}" required ${submission.sending ? "disabled" : ""}>${e(draft)}</textarea><div class="composer-footer"><div class="composer-footer-start">${kind === "new" ? connectionButton() : kind === "work" ? `<button class="composer-add icon-button" type="button" data-action="toggle-conversation" aria-label="Conversation" aria-expanded="${conversationExpanded}" aria-controls="work-conversation" title="View conversation">${icon("chat")}</button><button class="composer-chip" type="button" data-action="view-outputs">${icon("file")}Outputs</button>${inboxSelection && item ? `<span class="composer-work-label">${icon("work")}Work ${e(item.work.id)}</span>` : ""}` : `<span id="composer-note" class="composer-note">${note}</span>`}</div><button class="send ${kind === "new" ? "create-work" : ""}" type="submit" aria-label="${kind === "new" ? "Create work" : "Send message"}" ${submission.sending || submission.pending || workUnavailable ? "disabled" : ""}>${kind === "new" ? `Create work${icon("arrow")}` : icon("up")}</button></div>${kind === "work" ? `<p id="composer-note" class="composer-note">${note}</p>` : ""}</form>${kind === "new" ? '<p id="create-work-note" class="composer-hint">Save a goal. Start when you’re ready.</p>' : ""}</div>`;
    }
    function renderWorkHistory() {
        const shown = matches.filter(
            ({ work: w }) => query || workInFilter(w, filter),
        );
        return `<div class="history-heading"><h2>${query ? "Matching work" : "Recent work"}</h2>
            <label class="sr-only" for="work-view">Work view</label><select id="work-view" class="field-control select-control work-view">${workFilters(
                work,
            )
                .map(
                    ({ value, name, count }) =>
                        `<option value="${value}" ${filter === value ? "selected" : ""}>${name} (${count})</option>`,
                )
                .join("")}</select></div>
            <nav class="work-history" aria-label="Recent work">${shown.map(({ work: w, updatedAt }) => `<button class="work-card ${view === "work" && selected === w.id ? "selected" : ""}" data-action="select-work" data-id="${e(w.id)}" aria-current="${view === "work" && selected === w.id ? "page" : "false"}" title="${e(w.objective)}"><span class="work-card-copy"><span class="work-title">${e(w.objective)}</span><time class="work-card-meta" datetime="${e(updatedAt)}">${e(relativeTime(updatedAt))}</time></span><span class="work-indicator ${statusClass(w)}" aria-label="${e(label(w.attention?.reason ?? w.status))}" title="${e(label(w.attention?.reason ?? w.status))}">${icon(attention(w) ? "wait" : w.status === WorkStatus.Completed ? "circleCheck" : w.status === WorkStatus.InProgress ? "progress" : "circle")}</span></button>`).join("") || `<p class="sidebar-empty">${query || filter !== "all" ? "No matching work." : loaded ? "No work yet." : workUnavailable ? "Work unavailable." : "Loading work…"}</p>`}</nav>`;
    }
    function renderSidebar() {
        const attentionCount = work.filter(({ work: item }) =>
            attention(item),
        ).length;
        const connectionLabel = defaultConnection
            ? `${defaultConnection.name} · ${label(defaultConnection.availability)}`
            : connectionsLoaded
              ? "No AI connection"
              : "Checking AI connection…";
        return `<aside id="workspace-sidebar" class="sidebar" aria-label="Workspace" ${sidebarCollapsed || activityOpen ? "inert" : ""} ${mobile && !sidebarCollapsed ? 'role="dialog" aria-modal="true"' : ""}>
            <div class="sidebar-heading"><a class="brand" href="/" data-action="browse-work"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><button id="hide-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Hide sidebar" aria-expanded="true" aria-controls="workspace-sidebar">${icon("sidebar")}</button></div>
            ${inbox ? "" : `<button class="new-chat" data-action="new-work">${icon("plus")}New work</button>`}
            <nav class="sidebar-start" aria-label="Work navigation">
                ${inbox ? `<button class="nav-button" data-action="new-chat">${icon("chat")}Ask Goblin</button>` : ""}
                <button class="nav-button ${inbox ? "active" : ""}" data-action="browse-work" aria-current="${inbox ? "page" : "false"}">${icon("inbox")}<span>Work inbox</span>${attentionCount ? `<span class="inbox-nav-count" aria-hidden="true">${attentionCount}</span>` : ""}</button>
            </nav>
            <div class="sidebar-history" data-scroll="sidebar">
                <div class="conversation-heading"><button class="history-disclosure" data-action="view-chat" aria-expanded="${conversationsOpen || !!query}" aria-controls="conversation-history">${icon("chat")}Conversations${icon("chevron")}</button><button class="icon-button" data-action="new-chat" aria-label="New conversation">${icon("plus")}</button></div>
                <nav id="conversation-history" class="work-history" aria-label="Saved conversations" ${conversationsOpen || query ? "" : "hidden"}>${chats.map((chat) => `<button class="work-card ${view === "chat" && activeChat === chat.id ? "selected" : ""}" data-action="select-chat" data-id="${e(chat.id)}" aria-current="${view === "chat" && activeChat === chat.id ? "page" : "false"}"><span class="work-title">${e(chat.title)}</span></button>`).join("") || '<p class="sidebar-empty">No saved conversations.</p>'}</nav>
                ${inbox ? "" : renderWorkHistory()}
            </div>
            <nav class="primary-navigation" aria-label="Workspace navigation"><a id="nav-knowledge" class="nav-button ${view === "knowledge" ? "active" : ""}" href="/knowledge" data-action="knowledge" aria-current="${view === "knowledge" ? "page" : "false"}">${icon("knowledge")}Knowledge</a><button id="nav-settings" class="nav-button sidebar-settings ${view === "settings" ? "active" : ""}" aria-current="${view === "settings" ? "page" : "false"}" data-action="settings">${icon("settings")}Settings</button></nav>
            ${inbox ? `<button class="inbox-connection nav-button" data-action="settings-codex">${icon("activity")}<span>${e(connectionLabel)}</span></button>` : ""}
            <button class="sidebar-system" data-action="settings-system" data-system-summary aria-label="System resources">${systemSummary}</button>
        </aside>`;
    }
    function renderHeader() {
        return `<header class="workspace-header" ${(mobile && !sidebarCollapsed) || activityOpen ? "inert" : ""}><button id="show-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Show sidebar" aria-expanded="false" aria-controls="workspace-sidebar" ${sidebarCollapsed ? "" : "hidden"}>${icon("sidebar")}</button>${isDirectory ? `<span class="workspace-section-label">Workspace <span aria-hidden="true">/</span> ${view === "settings" ? "Settings" : "Knowledge"}</span>` : `${inbox ? '<span class="workspace-section-label inbox-breadcrumb">Workspace <span aria-hidden="true">/</span> Work inbox</span>' : ""}<label class="global-search">${icon("search")}<input id="work-search" type="search" placeholder="${mobile ? "Search work…" : "Search work and conversations"}" aria-label="Search work and conversations" value="${e(search)}" autocomplete="off"><span class="search-shortcut" aria-hidden="true"><kbd>Ctrl</kbd><kbd>K</kbd></span></label>`}<div class="header-actions">${inbox ? "" : `<button class="icon-button global-ask" data-action="new-chat" aria-label="Ask Goblin" title="Ask Goblin">${icon("chat")}</button>`}<button class="icon-button" data-action="refresh" aria-label="Refresh" title="Refresh">${icon("refresh")}</button><details id="workspace-menu" class="action-menu"><summary class="workspace-avatar" aria-label="Workspace menu">${icon("user")}</summary><div class="action-menu-items"><button data-action="settings">${icon("settings")}Settings</button><button data-action="lock">${icon("lock")}Lock workspace</button></div></details></div>${renderSearchResults()}</header>`;
    }
    function renderSearchResults() {
        if (isDirectory || !query || !sidebarCollapsed || !searchOpen)
            return "";
        const items = view === "board" ? [] : matches;
        if (view === "board" && !chats.length) return "";
        const heading =
            view === "board" ? "Matching conversations" : "Search results";
        return `<section class="search-results" aria-label="${heading}"><div class="section-heading"><h3>${heading}</h3><button class="text-button" data-action="clear-search">Clear search</button></div>${items.map(({ work: w }) => `<button class="work-card" data-action="search-work" data-id="${e(w.id)}">${icon("work")}<span class="work-title">${e(w.objective)}</span></button>`).join("")}${chats.map((chat) => `<button class="work-card" data-action="search-chat" data-id="${e(chat.id)}">${icon("chat")}<span class="work-title">${e(chat.title)}</span></button>`).join("")}${!items.length && !chats.length ? '<p class="section-empty">No matching work or conversations.</p>' : ""}</section>`;
    }
    function renderBoardView() {
        return renderBoard({
            work: ordered,
            matches,
            loaded,
            workUnavailable,
            filter: boardFilter,
            layout: boardLayout,
            search: query,
            selected: inboxSelection ? selected : "",
            sidebarCollapsed,
        });
    }
    function renderActivityPanel() {
        return `<aside id="work-activity" class="activity-panel ${activityOpen ? "is-open" : ""}" aria-label="Activity" ${activityOpen ? 'role="dialog" aria-modal="true"' : "inert"}><div class="activity-heading"><h2>${icon("activity")}Activity</h2><button id="close-activity" class="icon-button" data-action="toggle-activity" aria-label="Close activity">${icon("close")}</button></div><div class="activity-body" data-scroll="activity">${renderActivity(view === "work" ? item?.work : undefined)}</div></aside>`;
    }
    function renderHome() {
        const available = connections.some(
            (c) => c.availability === ConnectionAvailability.Available,
        );
        const disconnected =
            !connections.length ||
            connections.every(
                (c) => c.availability === ConnectionAvailability.Disconnected,
            );
        return `<section class="home" aria-labelledby="home-title"><div class="home-content"><h1 id="home-title">What should we work on?</h1>${composer("new", "Describe the intended outcome…")}<div class="connection-nudge" ${available || !connectionsLoaded ? "hidden" : ""}><span>${disconnected ? "Connect an AI provider when you’re ready to start." : "Your AI connection may need attention."}</span><button data-action="settings">${disconnected ? "Connect an AI provider" : "Manage connections"}${icon("arrow")}</button></div></div></section>`;
    }
    function renderDetail(item: View) {
        const w = item.work,
            attempt = w.attempts.at(-1);
        const back = inboxSelection
            ? `<button class="icon-button" data-action="browse-work" aria-label="Back to work" title="Back to work">${icon("close")}</button>`
            : `<button class="text-button back-to-work" data-action="browse-work">${icon("back")}Back to work</button>`;
        const activityButton = `<button id="show-activity" class="${inboxSelection ? "text-button" : "secondary activity-toggle"}" data-action="toggle-activity" aria-label="Activity" aria-expanded="${activityOpen}" aria-controls="work-activity">${inboxSelection ? "" : icon("activity")}Activity</button>`;
        const changes =
            w.status !== WorkStatus.Completed &&
            w.status !== WorkStatus.Cancelled &&
            w.attempts.some((entry) => entry.target.repository?.grant)
                ? `<button id="show-changes" class="${inboxSelection ? "text-button" : "secondary"}" data-action="view-changes" aria-expanded="${changesOpen}" aria-controls="work-changes-host">${icon("branch")}Changes</button>`
                : "";
        const actions = inboxSelection
            ? `<div class="inbox-detail-actions"><button class="icon-button" data-action="share-work" aria-label="Share" title="Share work">${icon("share")}</button><button class="icon-button inbox-expand" data-action="expand-work" aria-label="Open full work" title="Open full work">${icon("external")}</button>${back}</div>`
            : `<div class="detail-actions">${changes}<button class="secondary" data-action="share-work">${icon("share")}Share</button>${activityButton}</div>`;
        const outputCount = w.results.length + w.artifacts.length;
        const sections = inboxSelection
            ? `<nav class="inbox-detail-nav" aria-label="Work detail sections"><button class="text-button" data-action="inbox-overview">Overview</button>${changes}<button class="text-button" data-action="view-outputs">Outputs${outputCount ? ` <span>${outputCount}</span>` : ""}</button>${activityButton}</nav>`
            : "";
        return `<div class="detail-body" id="detail-content" data-scroll="detail"><header class="detail-heading">
            <div class="work-context">${inboxSelection ? `<span>Work ${e(w.id)}</span>${actions}` : `${back}<span>Work ${e(w.id)}</span>`}</div>
            <div class="title-row"><h2 id="work-title-heading" tabindex="-1" class="preserve-lines">${e(w.objective)}</h2>${inboxSelection ? "" : actions}</div>
            <div class="detail-properties">${status(w)}<span>${icon("user")}${e(workAgent(w)?.name ?? "Goblin")}</span><span>Updated ${e(relativeTime(item.updatedAt))}</span></div>
            ${sections}
        </header>
        <div class="work-layout"><div class="work-main">${controls(w)}
        ${w.decisions.length ? `<section class="work-section" aria-labelledby="decisions-heading"><div class="section-content"><div class="section-heading"><h3 id="decisions-heading">Decisions</h3><span class="section-count">${w.decisions.length}</span></div>${renderDecisions(w)}</div></section>` : ""}
        <section id="work-outputs" class="work-section outputs-section" aria-labelledby="outputs-heading"><div class="section-content"><div class="section-heading"><div><h3 id="outputs-heading">Outputs</h3>${inboxSelection ? "" : '<p class="section-description">Results and artifacts from this work.</p>'}</div>${w.results.length + w.artifacts.length > 3 ? '<button class="text-button" data-action="view-outputs">View all ' + icon("arrow") + "</button>" : ""}</div>${renderOutputs(w)}</div></section>${inboxSelection ? renderRecentActivity(w) : ""}</div>
        <aside class="work-support" aria-label="Work details"><section class="work-section progress-section" aria-labelledby="progress-heading"><div class="section-content"><div class="section-heading"><h3 id="progress-heading">Progress</h3></div>${renderProgress(item)}</div></section>
        ${w.attempts.length ? `<section class="work-section executions-section" aria-labelledby="executions-heading"><div class="section-content"><div class="section-heading"><h3 id="executions-heading">Executions</h3><span class="section-count">${w.attempts.length}</span></div>${renderExecutions(w)}</div></section>` : ""}
        <section class="work-section work-info" aria-labelledby="work-info-heading"><div class="section-content"><div class="section-heading"><h3 id="work-info-heading">Details</h3></div><dl class="work-properties"><dt>Created</dt><dd>${e(time(item.createdAt))}</dd><dt><button class="text-button" data-action="settings-agents">Agent ${icon("arrow")}</button></dt><dd>${e(workAgent(w)?.name ?? "Goblin")}</dd>${attempt?.target.repository ? `<dt>Repository</dt><dd class="preserve-lines">${e(attempt.target.repository.repository)}${attempt.target.repository.grant ? `<span class="repository-branch">${icon("branch")}${e(attempt.target.repository.grant.branch)}</span>` : ""}</dd>` : ""}</dl><div class="work-info-actions"><button class="text-button" data-action="discuss-goal">${icon("chat")}Discuss goal</button><button class="text-button" data-action="settings-github">${icon("github")}GitHub</button></div></div></section></aside></div></div>
        `;
    }
    function renderWorkComposer(w: Work) {
        return `<div class="work-composer"><section class="work-conversation" id="work-conversation" data-scroll="conversation" aria-label="Conversation about this Work" ${conversationExpanded ? "" : "hidden"}>${conversationExpanded ? `<div class="composer-heading"><h3>Conversation</h3><button class="text-button" data-action="toggle-conversation" aria-expanded="true" aria-controls="work-conversation">Hide conversation</button></div>${renderConversation(w)}` : ""}</section>${composer("work", changing ? "What would you like Goblin to change?" : w.attention?.reason === AttentionReason.InputRequired ? "Answer Goblin’s question…" : "Add context to this work…")}</div>`;
    }
    function renderSelectedWork(item: View) {
        const detail = renderDetail(item);
        const workComposer = renderWorkComposer(item.work);
        if (changesOpen)
            return `<div class="work-review-layout"><section class="detail" aria-label="Selected work">${detail}${workComposer}</section><div id="work-changes-host"></div></div>`;
        return inboxSelection
            ? `<div class="inbox-split">${renderBoardView()}<aside class="detail inbox-details" aria-label="Selected work">${detail}</aside>${workComposer}</div><div id="work-changes-host" hidden></div>`
            : `<section class="detail" aria-label="Selected work">${detail}${workComposer}</section><div id="work-changes-host" hidden></div>`;
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
        <summary>${icon("chevron")}${pendingApproval ? "Change repository or actions" : "Repository access"}</summary>
        <p>Choose an enabled repository to continue. Goblin uses its default branch and your connected GitHub commit identity. Changes stay local unless your request asks to publish them.</p>
        ${suggestions.length > 1 ? `<p class="field-hint">Several repositories match: ${e(suggestions.join(", "))}. Choose the full name.</p>` : ""}
        <form class="work-setup github-repository-form" data-form="github-repository" novalidate>
            <div><label for="github-repository">Repository</label>
                <input class="field-control" id="github-repository" name="github-repository" list="known-github-repositories" value="${e(name)}" placeholder="owner/repository or GitHub URL" required aria-describedby="github-repository-error">
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
            ${runtimes.some((r) => r.repositoryExecution) ? `<div class="github-repository-actions"><button id="start-github-repository" type="submit" class="primary" ${submission.sending || submission.pending || (!handoff && !canExecute) ? "disabled" : ""}>Use repository</button></div>` : '<p role="status">Repository execution is unavailable on this Goblin.</p>'}
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
        return `<div class="work-executor"><img src="/assets/branding/icon.svg" alt=""><div class="executor-identity"><strong>${e(workAgent(w)?.name ?? "Goblin")}</strong>${runtime ? `<span>Uses ${e(runtime)}</span>` : ""}</div>${available ? (model ? renderModelPicker(model) : "") : ""}</div>${available ? "" : `<p role="status">${notice}</p>${connectionsLoaded ? button("settings-codex", disconnected ? "Connect Codex" : "Manage Codex connection", true) : ""}`}`;
    }
    function controls(w: Work) {
        if (w.attention?.reason === AttentionReason.CleanupRequired)
            return `<section class="decision work-state" aria-label="Current status"><h3>Needs attention</h3><p>The outcome is saved, but Goblin could not finish cleaning up the execution. Reconcile it before continuing.</p>${button("reconcile", "Reconcile execution", true)}</section>`;
        if (w.attempts.at(-1)?.cleanupPending)
            return `<section class="decision work-state" aria-label="Current status"><p>Saving the outcome and finishing execution cleanup…</p></section>`;
        let content = "";
        if (w.status === WorkStatus.Ready)
            content = `<h3>Ready to start</h3>${executionSetup(w)}${workConnection(w)?.availability === ConnectionAvailability.Available ? `<p>Ready when you are.</p>${button("execute", "Start work", true, !canExecute)}${gitHubRepositorySetup(w)}` : ""}`;
        if (w.attention?.reason === AttentionReason.ResultReview)
            content = `<h3>Ready for your review</h3><p>Read the proposed result below, then approve it or ask for changes.</p>${w.results.find((r) => r.attemptId === w.attempts.at(-1)?.id) ? `<button class="text-button review-result" data-action="inspect-output" data-id="result-${e(w.attempts.at(-1)?.id)}">Read proposed result ${icon("arrow")}</button>` : ""}<div class="work-state-actions">${button("approve", "Approve & complete", true)}${button("changes", "Ask for changes")}</div>`;
        if (w.attention?.reason === AttentionReason.InputRequired)
            content = `<h3>Needs your input</h3><p>${e(w.decisions.at(-1)?.question)}</p>${!w.attempts.at(-1)?.target.repository ? gitHubRepositorySetup(w) : ""}`;
        if (w.attention?.reason === AttentionReason.GitRepositoryRequired)
            content = `${w.repositoryAuthorization?.status === GitRepositoryAuthorizationStatus.Pending ? "" : "<h3>Repository access needed</h3><p>Review and authorize the repository and Git actions to continue this Work.</p>"}${gitHubRepositorySetup(w, true)}`;
        if (w.attention?.reason === AttentionReason.Failure)
            content = `<h3>Needs attention</h3><p>${e(label(w.attention.failure ?? "Execution failed"))}. Check the connection and execution history before retrying.</p>${executionSetup(w)}${workConnection(w)?.availability === ConnectionAvailability.Available ? button("retry", "Retry work", true, !canExecute) : ""}`;
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
            content += `<div class="work-state-secondary">${button("cancel", "Cancel work")}</div>`;
        return content
            ? `<section class="decision work-state" aria-label="Current status">${content}</section>`
            : "";
    }
    function renderChat() {
        const c = conversations.find((x) => x.id === activeChat);
        return `<section class="chat-workspace"><div class="detail-body" data-scroll="chat"><div class="thread-content">${c ? `<h1 class="conversation-title">${e(c.title)}</h1>` + c.messages.map((m) => message(m.source ? `${m.source.provider} · ${m.source.userId}` : "You", m.text, m.createdAt)).join("") + (c.workId ? `<button class="tracked-link" data-action="select-work" data-id="${c.workId}">Open tracked work ${icon("arrow")}</button>` : `<div class="decision"><h3>Ready to take this forward?</h3><p>Track this conversation as Work when you want Goblin to execute it.</p>${button("track", "Track this work", true)}</div>`) : `<div class="conversation-empty"><h1>A little room to think</h1><p>Save ideas and context here. Track them as Work when you’re ready for Goblin to start.</p></div>`}</div></div>${composer("chat", "Add to the conversation…")}</section>`;
    }
    const connectionNotice =
        sessionFailures && (sessionChecked || sessionFailures > 1)
            ? '<p class="command-notice" role="status">Could not reach Goblin. Reconnecting…</p>'
            : "";
    if (!authenticated) {
        return `<main class="unlock-page"><a class="brand" href="/"><img src="/assets/branding/icon.svg" alt=""><span>goblin</span></a><section class="unlock-card"><h1>${sessionChecked ? "Open your workspace" : "Opening your workspace…"}</h1>${sessionChecked ? `<p>Enter the password chosen when this Goblin workspace was set up.</p><form data-form="unlock"><label for="password">Goblin password</label><input class="field-control" id="password" name="password" type="password" autocomplete="current-password" required maxlength="128"><button class="primary" type="submit">Open workspace${icon("arrow")}</button></form>` : ""}${connectionNotice}${error ? `<p class="command-notice" role="alert">${e(error)}</p>` : ""}</section><p class="unlock-note">Your self-hosted AI coworker</p></main>`;
    }
    const modal = (mobile && !sidebarCollapsed) || activityOpen;
    return `<a class="skip-link" href="#main-content">Skip to main content</a><div class="app-shell ${sidebarCollapsed ? "sidebar-collapsed" : ""}">${renderHeader()}${renderSidebar()}${mobile && !sidebarCollapsed ? '<button class="sidebar-backdrop" data-action="toggle-sidebar" aria-label="Close navigation" tabindex="-1"></button>' : ""}<main id="main-content" class="main-shell" tabindex="-1" ${modal ? "inert" : ""}>
            <div class="workspace-notices">${connectionNotice}${timezoneError ? `<div class="command-notice" role="status">${e(timezoneError)}</div>` : ""}${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${submission.pending ? `<div class="command-notice" role="status">${submission.sending ? "Saving command…" : "Command unconfirmed. Inspect the saved state or resend this same command."}${!submission.sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}</div>
            ${view === "settings" ? '<div id="settings-host"></div>' : view === "knowledge" ? renderKnowledge() : view === "chat" ? renderChat() : view === "work" ? (item ? renderSelectedWork(item) : `<p class="board-empty" role="status">${workUnavailable ? "Work unavailable." : "Loading work…"}</p>`) : view === "new" ? renderHome() : renderBoardView()}</main>${renderActivityPanel()}${activityOpen ? '<button class="activity-backdrop" data-action="toggle-activity" aria-label="Close activity panel" tabindex="-1"></button>' : ""}</div>`;
}
