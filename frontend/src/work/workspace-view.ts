import {
    AttentionReason,
    ConnectionAvailability,
    GitRepositoryAuthorizationStatus,
    WorkEventKind,
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
import { icon, escapeHtml as e } from "./presentation.js";
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

type WorkspaceViewInput = {
    navigation: Pick<
        WorkspaceNavigation,
        "view" | "selected" | "activeChat" | "isDirectory" | "settingsSection"
    >;
    workspace: WorkspaceController["state"];
    mobile: boolean;
    sidebarCollapsed: boolean;
    activityOpen: boolean;
    showChanges: boolean;
    changesOpen: boolean;
    changesPreviewOpen: boolean;
    conversationsOpen: boolean;
    listStatus: ListStatus;
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
    } = workspace;
    const item = work.find(({ work }) => work.id === selected);
    const review = view === "work" && reviewableResult(item?.work);
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
        const status = connectionsLoaded
            ? (defaultConnection?.availability ??
              ConnectionAvailability.Unavailable)
            : "Checking connection…";
        return `<button id="codex-connection-trigger" class="connection-choice" type="button" popovertarget="codex-connection-menu"><img src="/assets/providers/codex.png" width="24" height="24" alt=""><span class="connection-name">${e(defaultConnection?.name ?? "Codex")}</span>${icon("chevron")}</button>
            <div id="codex-connection-menu" class="connection-menu" popover>
                <p class="connection-menu-status" role="status">${e(status)}</p>
                <a href="${settingsHref("codex")}" data-action="settings-codex" data-settings-return-focus="codex-connection-trigger">${icon("settings")}Manage Codex connection</a>
            </div>`;
    }
    function composer(
        kind: "new" | "chat" | "work",
        placeholder = "Ask Goblin",
    ) {
        const prompt = kind !== "work";
        const sendLabel =
            kind === "new"
                ? "Create work"
                : kind === "chat"
                  ? "Save message"
                  : changing
                    ? "Send change request"
                    : "Send message";
        const input = `<label class="sr-only" for="reply">${e(placeholder)}</label><textarea id="reply" name="reply" rows="${prompt ? 1 : 3}" maxlength="4000" placeholder="${e(placeholder)}" required ${submission.sending ? "disabled" : ""}>${e(draft)}</textarea>`;
        const send = `<button class="send" type="submit" aria-label="${sendLabel}" ${prompt ? `title="${sendLabel}"` : ""} ${submission.sending || submission.pending || workUnavailable ? "disabled" : ""}>${icon("up")}</button>`;
        if (prompt)
            return `<div class="composer-wrap"><form class="composer prompt-composer" data-form="${kind}">${input}${kind === "new" ? connectionMenu() : ""}${send}</form></div>`;
        const workTools = `<button class="composer-chip" type="button" data-action="view-outputs" aria-controls="work-outputs">${icon("file")}Outputs</button>${review ? `<button class="composer-chip review-mode ${changing ? "selected" : ""}" type="button" data-action="changes" ${submission.sending ? "disabled" : ""}>${icon(changing ? "close" : "chat")}${changing ? "Cancel change request" : "Ask for changes"}</button>` : ""}`;
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
                <button id="nav-work-search" class="nav-button" data-action="open-search" aria-keyshortcuts="Control+K Meta+K">${icon("search")}<span>Search</span><kbd aria-hidden="true">Ctrl K</kbd></button>
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
        const changes = showChanges
            ? `<button id="show-changes" class="icon-button changes-toggle" type="button" data-action="view-changes" aria-label="Changes" title="Changes" aria-expanded="${changesPreviewOpen || changesOpen}" aria-controls="${changesOpen ? "work-changes-host" : "work-changes-preview-host"}">${icon("listControls")}</button>`
            : "";
        const section =
            view === "settings"
                ? "Settings"
                : view === "knowledge"
                  ? "Knowledge"
                  : inbox
                    ? "Work inbox"
                    : "Work";
        const breadcrumb = `<div class="workspace-breadcrumb"><span>Workspace</span>${icon("chevron")}<strong>${section}</strong>${view === "settings" ? `${icon("chevron")}<span>${e(settingsSectionName(navigation.settingsSection))}</span>` : ""}</div>`;
        const actions =
            view === "settings"
                ? `<button class="text-button settings-return" data-action="return-settings">${icon("back")}Back to work</button>`
                : inbox
                  ? renderInboxControls(boardLayout, listStatus)
                  : !isDirectory
                    ? `<button class="icon-button" data-action="refresh" aria-label="Refresh" title="Refresh">${icon("refresh")}</button>${changes}`
                    : "";
        return `<header class="workspace-header${inbox ? " inbox-header" : ""}" ${(mobile && !sidebarCollapsed) || activityOpen ? "inert" : ""}>
            ${
                sidebarCollapsed
                    ? `<div class="header-navigation">
                <button id="show-sidebar" class="icon-button" data-action="toggle-sidebar" aria-label="Show sidebar" aria-expanded="false" aria-controls="workspace-sidebar">${icon("sidebar")}</button>
                <button id="show-work-search" class="icon-button" data-action="open-search" aria-label="Search" title="Search" aria-keyshortcuts="Control+K Meta+K">${icon("search")}</button>
                ${renderWorkspaceMenu()}
            </div>`
                    : ""
            }
            ${breadcrumb}
            ${inbox ? `<button class="icon-button inbox-refresh" data-action="refresh" aria-label="Refresh" title="Refresh">${icon("refresh")}</button>` : ""}
            <div class="header-actions">${actions}</div>
            <div id="workspace-search" class="workspace-search" ${!isDirectory && (searchOpen || search) ? "" : "hidden"}>
                <label class="global-search">${icon("search")}<input id="work-search" type="search" placeholder="Search work and conversations" aria-label="Search work and conversations" value="${e(search)}" autocomplete="off"></label>
                <div class="workspace-search-results">${renderSearchResults()}</div>
            </div>
        </header>`;
    }
    function renderSearchResults() {
        if (isDirectory || !query || !searchOpen) return "";
        const items = inbox ? [] : matches;
        const matchingChats = chats.filter(({ title, messages }) =>
            [title, ...messages.map(({ text }) => text)].some((text) =>
                text.toLocaleLowerCase().includes(query),
            ),
        );
        if (inbox && !matchingChats.length) return "";
        const heading = inbox ? "Matching conversations" : "Search results";
        return `<section class="search-results" aria-label="${heading}"><div class="section-heading"><h3>${heading}</h3><button class="text-button" data-action="clear-search">Clear search</button></div>${items.map(({ work: w }) => `<button class="work-card" data-action="search-work" data-id="${e(w.id)}">${icon("work")}<span class="work-title">${e(w.objective)}</span></button>`).join("")}${matchingChats.map((chat) => `<button class="work-card" data-action="search-chat" data-id="${e(chat.id)}">${icon("chat")}<span class="work-title">${e(chat.title)}</span></button>`).join("")}${!items.length && !matchingChats.length ? '<p class="section-empty">No matching work or conversations.</p>' : ""}</section>`;
    }
    function renderBoardView() {
        return renderBoard({
            total: ordered.length,
            matches,
            loaded,
            workUnavailable,
            listStatus,
            layout: boardLayout,
            search: query,
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
        return `<section class="home" aria-labelledby="home-title"><div class="home-content"><h1 id="home-title">What should we work on?</h1>${composer("new")}<div class="connection-nudge" ${available || !connectionsLoaded ? "hidden" : ""}><button data-action="settings">${disconnected ? "Connect an AI provider" : "Manage connections"}${icon("arrow")}</button></div></div></section>`;
    }
    function renderWorkReferences(item: View) {
        const w = item.work,
            attempt = w.attempts.at(-1);
        return `<details id="work-outputs" class="work-reference"><summary>Saved outputs${icon("chevron", "disclosure-icon")}</summary><section class="work-reference-content" aria-label="Outputs">${renderOutputs(w)}</section></details>
        <details id="work-reference" class="work-reference"><summary>Work details${icon("chevron", "disclosure-icon")}</summary><div class="work-reference-content work-support">
        ${w.decisions.length ? `<section class="work-section" aria-labelledby="decisions-heading"><div class="section-content"><div class="section-heading"><h3 id="decisions-heading">Decisions</h3><span class="section-count">${w.decisions.length}</span></div>${renderDecisions(w)}</div></section>` : ""}
        <section class="work-section progress-section" aria-labelledby="progress-heading"><div class="section-content"><div class="section-heading"><h3 id="progress-heading">Progress</h3></div>${renderProgress(item)}</div></section>
        ${w.attempts.length ? `<section class="work-section executions-section" aria-labelledby="executions-heading"><div class="section-content"><div class="section-heading"><h3 id="executions-heading">Executions</h3><span class="section-count">${w.attempts.length}</span></div>${renderExecutions(w)}</div></section>` : ""}
        <section class="work-section work-info" aria-labelledby="work-info-heading"><div class="section-content"><div class="section-heading"><h3 id="work-info-heading">Details</h3></div><dl class="work-properties"><dt>Created</dt><dd>${e(time(item.createdAt))}</dd><dt><button class="text-button" data-action="settings-agents">Agent ${icon("arrow")}</button></dt><dd>${e(workAgent(w)?.name ?? "Goblin")}</dd>${attempt?.target.repository ? `<dt>Repository</dt><dd class="preserve-lines">${e(attempt.target.repository.repository)}${attempt.target.repository.grant ? `<span class="repository-branch">${icon("branch")}${e(attempt.target.repository.grant.branch)}</span>` : ""}</dd>` : ""}</dl><div class="work-info-actions"><button class="text-button" data-action="discuss-goal">${icon("chat")}Discuss goal</button><button class="text-button" data-action="settings-github">${icon("github")}GitHub</button></div>${review ? `<div class="work-state-secondary">${button("cancel", "Cancel work")}</div>` : ""}</div></section>
        </div></details>`;
    }
    function renderSelectedWork(item: View) {
        const w = item.work;
        const reply = changing
            ? "What would you like Goblin to change?"
            : w.attention?.reason === AttentionReason.InputRequired
              ? "Answer Goblin’s question…"
              : "Add context to this work…";
        return `<div class="work-selection${changesOpen ? " work-review-layout" : ""}">
            <section class="detail" aria-label="Selected work">
                <div class="selected-work-top">
                    <header class="selected-work-header">
                        <button class="icon-button" data-action="browse-work" aria-label="Back to work" title="Back to work">${icon("back")}</button>
                        <div class="selected-work-identity"><h2 id="work-title-heading" tabindex="-1">Work ${e(w.id)}</h2>${status(w)}</div>
                        <div class="selected-work-actions">
                            <button class="icon-button" data-action="share-work" aria-label="Share" title="Share work">${icon("share")}</button>
                            <button id="show-activity" class="icon-button" data-action="toggle-activity" aria-label="Activity" title="Activity" aria-expanded="${activityOpen}" aria-controls="work-activity">${icon("activity")}</button>
                        </div>
                    </header>
                    <div id="work-changes-preview-host"></div>
                </div>
                <div class="detail-body work-thread" data-scroll="detail">
                    <div class="work-thread-content">
                        <section id="work-conversation" class="work-conversation" aria-label="Conversation about this Work">
                            <details id="work-objective" class="work-objective">
                                <summary aria-label="Work objective"><span class="work-objective-preview">${e(w.objective)}</span><span class="work-objective-heading">Work objective</span>${icon("chevron", "disclosure-icon")}</summary>
                                <p class="work-objective-full preserve-lines">${e(w.objective)}</p>
                            </details>
                            ${renderConversation(w, workAgent(w)?.name ?? "Goblin", button("approve", "Approve & complete"))}
                        </section>
                        ${controls(w)}
                        <div class="work-references">${renderWorkReferences(item)}</div>
                    </div>
                </div>
                <div class="work-composer">${composer("work", reply)}</div>
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
            content = `<h3>Ready to start</h3>${executionSetup(w)}${workConnection(w)?.availability === ConnectionAvailability.Available ? `${button("execute", "Start work", true, !canExecute)}${gitHubRepositorySetup(w)}` : ""}`;
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
        if (!c)
            return `<section class="home" aria-labelledby="chat-title" data-scroll="chat"><div class="home-content"><h1 id="chat-title">New chat</h1>${composer("chat")}</div></section>`;
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
            : button("track", "Track this work", true);
        return `<section class="chat-workspace" aria-labelledby="chat-title"><div class="detail-body" data-scroll="chat"><div class="thread-content"><h1 id="chat-title" class="conversation-title">${e(c.title)}</h1>${messages}${tracking}</div></div>${composer("chat")}</section>`;
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
            <div class="workspace-notices">${connectionNotice}${timezoneError ? `<div class="command-notice" role="status">${e(timezoneError)}</div>` : ""}${error ? `<div class="command-notice" role="alert">${e(error)}</div>` : ""}${submission.pending ? `<div class="command-notice" role="status">${submission.sending ? "Saving command…" : "Command unconfirmed. Inspect the saved state or resend this same command."}${!submission.sending ? button("resend", "Resend command") + button("dismiss", "Keep saved state") : ""}</div>` : ""}</div>
            ${view === "settings" ? '<div id="settings-host"></div>' : view === "knowledge" ? renderKnowledge() : view === "chat" ? renderChat() : view === "work" ? (item ? renderSelectedWork(item) : `<p class="board-empty" role="status">${workUnavailable ? "Work unavailable." : "Loading work…"}</p>`) : view === "new" ? renderHome() : renderBoardView()}</main>${renderActivityPanel()}${activityOpen ? '<button class="activity-backdrop" data-action="toggle-activity" aria-label="Close activity panel" tabindex="-1"></button>' : ""}</div>`;
}
