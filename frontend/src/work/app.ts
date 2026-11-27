import {
    AttentionReason,
    ConnectionAvailability,
    WorkAction,
    WorkStatus,
    WorkTitleLimit,
} from "../api/values.js";
import { ApiError, errorMessage } from "../api/client.js";
import "../focus.js";
import { Settings } from "../settings/settings.js";
import { SystemResources } from "../settings/system.js";
import {
    displayTimeZone,
    loadTimeZone,
    resetTimeZone,
    savedTimeZone,
} from "../settings/timezone.js";
import { TimeZoneSetup } from "../settings/timezone-picker.js";
import { isListStatus } from "./board.js";
import type { BoardLayout, ListStatus } from "./board.js";
import { WorkChangesPanel } from "./changes.js";
import { CommandSubmission } from "./command-submission.js";
import type { PendingCommand } from "./command-submission.js";
import { WorkCommands } from "./commands.js";
import type { WorkCommandRequest } from "./commands.js";
import type {
    ModelSelectionPayload,
    SelectedWorkAction,
    Work,
} from "./contracts.js";
import { ComposerDrafts } from "./drafts.js";
import type { DraftMode, DraftSnapshot } from "./drafts.js";
import { ModelCatalogs } from "./model-catalog.js";
import {
    ModelSelections,
    selectedModel,
    availableEfforts,
    effortStops,
    effortLabel,
} from "./model-selection.js";
import { WorkspaceNavigation } from "./navigation.js";
import type { WorkspaceContext, WorkspaceView } from "./navigation.js";
import { workTitle } from "./presentation.js";
import {
    isRepositoryHandoff,
    readRepositorySetup,
    repositoryApprovalKey,
    RepositoryDrafts,
} from "./repository-setup.js";
import { reviewableResult } from "./surface.js";
import { canGenerateWorkTitle, WorkspaceController } from "./workspace.js";
import { WorkspaceDom } from "./workspace-dom.js";
import { renderSearch, renderWorkspace } from "./workspace-view.js";

const workspace = new WorkspaceController(() =>
    window.dispatchEvent(new Event("goblin-workspace-locked")),
);
const navigation = new WorkspaceNavigation();
const drafts = new ComposerDrafts();
const repositoryDrafts = new RepositoryDrafts();
const root = document.querySelector<HTMLDivElement>("#app")!;
const searchDialog =
    document.querySelector<HTMLDialogElement>("#workspace-search")!;
const searchInput =
    searchDialog.querySelector<HTMLInputElement>("#work-search")!;
const searchResults = searchDialog.querySelector<HTMLElement>(
    "#global-search-results",
)!;
const searchStatus = searchDialog.querySelector<HTMLElement>(
    "#global-search-status",
)!;
const dom = new WorkspaceDom(root, repositoryDrafts);
const workChanges = new WorkChangesPanel(api);
const commands = new WorkCommands(
    async (...kinds) =>
        (await api<{ ids: string[] }>("/api/identities", { kinds })).ids,
);
const mobile = matchMedia("(max-width: 760px)");
const compact = matchMedia("(max-width: 1199px)");
const refreshIntervalKey = "goblin.refreshIntervalSeconds";
const defaultRefreshInterval = 3;
const refreshIntervals = [defaultRefreshInterval, 5, 10, 15, 30, 60, 0];
let refreshInterval = defaultRefreshInterval;
let refreshTimer: number | undefined;
try {
    const saved = localStorage.getItem(refreshIntervalKey);
    refreshInterval =
        refreshIntervals.find((seconds) => String(seconds) === saved) ??
        defaultRefreshInterval;
} catch {
    // Use the default when browser storage is unavailable.
}
let activityOpen = false,
    conversationsOpen = navigation.view === "chat",
    listStatus: ListStatus = "all",
    boardLayout: BoardLayout = "board",
    inboxSearch = "";
let searchOpener: HTMLElement | undefined;
let searchMarkup = "";
let activityOpener = "show-activity";
let sidebarCollapsed =
    mobile.matches ||
    localStorage.getItem("goblin.sidebarCollapsed") === "true";
let sessionChecked = false,
    sessionFailures = 0,
    timezoneError = "";
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
    modelListOpen = false;
let authenticated = false,
    error = "";
let reviewAttempt: string | undefined;
let titleEdit:
    { workId: string; original: string | null; value: string } | undefined;
let activeRefresh: Promise<void> | undefined;
let commandActive = false;
type SubmissionContext = { origin: WorkspaceContext; draft?: DraftSnapshot };
let pendingContext: SubmissionContext | undefined;
const submission = new CommandSubmission(
    sessionStorage,
    ({ path, body }) => api(path, body),
    () => render(),
);
error = submission.recoveryNotice;
const system = new SystemResources();
let settingsOpener: { id?: string; action?: string; provider?: string } = {};
const settings = new Settings(system, (provider) => {
    const menu = document.querySelector<HTMLDetailsElement>("#workspace-menu");
    if (navigation.view !== "settings") {
        const trigger =
            document.activeElement instanceof HTMLElement
                ? document.activeElement.closest<HTMLElement>("[data-action]")
                : null;
        settingsOpener = menu?.contains(document.activeElement)
            ? { id: "workspace-menu-trigger" }
            : {
                  id: trigger?.dataset.settingsReturnFocus ?? trigger?.id,
                  action: trigger?.dataset.action,
                  provider: trigger?.dataset.provider,
              };
    }
    if (menu) menu.open = false;
    const previous = navigation.settingsSection;
    navigate("settings", provider);
    render();
    settings.focusSelection(previous);
});
const timezoneSetup = new TimeZoneSetup();
window.addEventListener("goblin-timezone-changed", () => render(true));
window.addEventListener("goblin-workspace-locked", () => {
    authenticated = false;
    forgetWorkspace();
    render();
});
window.addEventListener("goblin-connections-changed", () => {
    modelCatalogs.clear();
    void refreshConnections();
    if (authenticated) settings.refreshConnections();
});
const current = () =>
    workspace.state.work.find((item) => item.work.id === navigation.selected);
function composerWork(): Work | undefined {
    if (navigation.view === "work") return current()?.work;
    if (navigation.view === "chat") {
        const linked = workspace.state.conversations.find(
            (chat) => chat.id === navigation.activeChat,
        )?.workId;
        return workspace.state.work.find((item) => item.work.id === linked)
            ?.work;
    }
}
const workAgent = (work?: Work) =>
    workspace.state.agents.find((agent) => agent.id === work?.agentId);
const workConnection = (work: Work) =>
    workspace.state.connections.find(
        (connection) => connection.id === workAgent(work)?.connectionId,
    );
const modelContext = (w?: Work) => ({ work: w, agent: workAgent(w) });
const modelSelection = (w?: Work) => modelSelections.get(modelContext(w));
const modelConnection = (w?: Work) => modelSelection(w).connectionId;
function modelPayload(w: Work): ModelSelectionPayload {
    const choice = modelSelection(w);
    const source = workspace.state.connections.find(
        (c) => c.id === choice.connectionId,
    );
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
    const source = workspace.state.connections.find(
        (c) => c.id === choice.connectionId,
    );
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
    const source = workspace.state.connections.find(
        (c) => c.id === connectionId,
    );
    if (source && source.runtime !== "codex") return;
    const choice = modelSelection(w);
    void modelCatalogs.load(connectionId, {
        limit: choice.expanded ? 10 : 3,
        selectedModel: choice.model,
    });
}
function api<T>(path: string, body?: unknown) {
    return workspace.request<T>(path, body);
}

function forgetWorkspace() {
    workChanges.setWork();
    workspace.reset();
    activeRefresh = undefined;
    timezoneSetup.reset();
    resetTimeZone();
    timezoneError = "";
    drafts.clear();
    repositoryDrafts.clear();
    pendingContext = undefined;
    reviewAttempt = undefined;
    titleEdit = undefined;
    listStatus = "all";
    closeSearch(false);
    inboxSearch = "";
    modelCatalogs.clear();
    modelSelections.clear();
    modelPickerOpen = false;
    modelListOpen = false;
    dom.reset();
    system.reset();
    settings.reset();
}

function refresh(preserveError = false) {
    if (activeRefresh || commandActive) return activeRefresh;
    const task = loadWorkspace(preserveError);
    activeRefresh = task;
    const finished = () => {
        if (activeRefresh === task) activeRefresh = undefined;
    };
    void task.then(finished, finished);
    return task;
}

function refreshAutomatically() {
    if (refreshInterval > 0 || !sessionChecked) void refresh();
}

function scheduleRefresh() {
    window.clearInterval(refreshTimer);
    // Even with auto-refresh off, recover if the first session check fails.
    const seconds =
        refreshInterval || (sessionChecked ? 0 : defaultRefreshInterval);
    refreshTimer = seconds
        ? window.setInterval(() => {
              if (!document.hidden) refreshAutomatically();
          }, seconds * 1000)
        : undefined;
}

async function loadWorkspace(preserveError: boolean) {
    const signal = workspace.signal;
    try {
        const session = await api<{ authenticated: boolean }>("/api/session");
        signal.throwIfAborted();
        sessionFailures = 0;
        authenticated = session.authenticated;
        sessionChecked = true;
        if (refreshInterval === 0) scheduleRefresh();
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
        settings.refreshConnections();
        if (navigation.query.get("returnTo") === "logs") {
            if (timezoneError) return;
            location.replace("/logs/select/vmui/" + location.hash);
            return;
        }
        if (navigation.query.get("returnTo") === "headlamp") {
            location.replace("/headlamp/");
            return;
        }
        void refreshConnections();
        if (
            navigation.view === "settings" &&
            navigation.settingsSection === "system"
        )
            void system.refresh();
        const loaded = await workspace.refreshWork();
        signal.throwIfAborted();
        if (loaded) {
            if (!commandActive && navigation.view === "work" && !current())
                navigate("board", "", "restore");
            if (
                !commandActive &&
                !submission.pending &&
                !preserveError &&
                error !== submission.recoveryNotice
            )
                error = "";
        } else if (!commandActive && !submission.pending && !preserveError)
            error =
                "Work could not be loaded. You can still manage connections in Settings.";
    } catch {
        if (!signal.aborted) sessionFailures++;
    } finally {
        if (!signal.aborted) {
            if (!authenticated) forgetWorkspace();
            render(true);
        }
    }
}
async function refreshConnections() {
    if (!authenticated) return;
    const signal = workspace.signal;
    const available = await workspace.refreshConnections();
    if (!available || signal.aborted) return;
    for (const id of available) modelCatalogs.invalidate(id);
    render(true);
}

function restoredContext(command: PendingCommand): WorkspaceContext {
    if (command.path === "/api/work/commands")
        return command.body.action === WorkAction.Create
            ? "new"
            : `work:${command.body.workId}`;
    return `chat:${command.body.conversationId}`;
}

async function send(
    prepare?: () => Promise<WorkCommandRequest>,
    consumesDraft = false,
) {
    if (commandActive || (prepare && submission.pending)) return;
    const pending = submission.pending;
    const context: SubmissionContext | undefined = prepare
        ? {
              origin: navigation.context,
              draft: consumesDraft
                  ? drafts.capture(navigation.context, composerDraftMode())
                  : undefined,
          }
        : (pendingContext ??
          (pending ? { origin: restoredContext(pending) } : undefined));
    if (!context) return;
    const signal = workspace.signal;
    error = "";
    commandActive = true;
    let confirmed = false;
    let destination: WorkspaceContext | undefined;
    let renamed = false;
    try {
        const saved = prepare
            ? await submission.submit(async () => {
                  const command = await prepare();
                  signal.throwIfAborted();
                  return command;
              })
            : await submission.resend();
        if (!saved || signal.aborted) return;
        // Let an earlier poll finish before selecting a newly confirmed item.
        await activeRefresh;
        if (signal.aborted) return;
        confirmed = true;
        if (context.draft) drafts.confirm(context.draft);
        if (navigation.context === context.origin) {
            if (
                saved.path === "/api/work/commands" &&
                saved.body.action === WorkAction.Rename
            ) {
                titleEdit = undefined;
                renamed = true;
            }
            if (
                saved.path === "/api/work/commands" &&
                saved.body.action === WorkAction.RequestChanges &&
                saved.body.attemptId === reviewAttempt
            )
                reviewAttempt = undefined;
            if (
                saved.path === "/api/work/commands" &&
                saved.body.action === WorkAction.Create
            ) {
                navigate("work", saved.body.workId);
                destination = navigation.context;
            }
            if (saved.path === "/api/conversations/commands") {
                if (typeof saved.body.workId === "string") {
                    navigate("work", saved.body.workId);
                    destination = navigation.context;
                } else if (
                    navigation.activeChat !== saved.body.conversationId
                ) {
                    navigate("chat", saved.body.conversationId);
                    destination = navigation.context;
                }
            }
        }
    } catch (failure) {
        if (!signal.aborted) {
            const attempted = submission.pending;
            if (
                failure instanceof ApiError &&
                ([
                    "work_title_failed",
                    "work_title_connection_required",
                ].includes(failure.code) ||
                    (attempted?.path === "/api/work/commands" &&
                        attempted.body.action === WorkAction.Rename &&
                        ["work_changed", "invalid_work_title"].includes(
                            failure.code,
                        )))
            )
                submission.dismiss();
            if (submission.pending) pendingContext = context;
            error = errorMessage(
                failure,
                "The request could not be confirmed.",
            );
        }
    } finally {
        commandActive = false;
        if (!signal.aborted) {
            if (!submission.pending) pendingContext = undefined;
            await refresh(!confirmed);
            if (!searchDialog.open) {
                if (destination === navigation.context) focusDestination();
                if (renamed && navigation.context === context.origin)
                    document.getElementById("rename-work")?.focus();
            }
        } else render();
    }
}

async function command(action: SelectedWorkAction, consumesDraft = false) {
    const item = current();
    if (item) await send(() => commands.selected(item, action), consumesDraft);
}

function navigate(
    next: WorkspaceView,
    id = "",
    historyMode: "auto" | "restore" = "auto",
) {
    const previous = navigation.view;
    const leavingSettings = previous === "settings" && next !== "settings";
    if (leavingSettings) settings.reset();
    closeSearch(false);
    navigation.navigate(next, id, historyMode);
    reviewAttempt = undefined;
    titleEdit = undefined;
    activityOpen = false;
    modelPickerOpen = false;
    modelListOpen = false;
    if (mobile.matches) sidebarCollapsed = true;
    if (leavingSettings && authenticated) {
        modelCatalogs.clear();
        void refreshConnections();
    }
}

function closeChanges() {
    const wasReviewing = workChanges.isOpen;
    workChanges.close();
    if (wasReviewing) render(false, true);
    const trigger = document.getElementById("show-changes");
    trigger?.setAttribute("aria-expanded", "false");
    trigger?.focus();
}

function closeDiff() {
    if (!workChanges.closeReview()) return;
    render(false, true);
    workChanges.focusReviewEntry();
}

function openSearch(trigger = document.activeElement) {
    if (!searchDialog.open) {
        searchOpener = trigger instanceof HTMLElement ? trigger : undefined;
        searchDialog.showModal();
        updateSearch();
    }
    searchInput.focus();
}

function closeSearch(restoreFocus: boolean) {
    if (!searchDialog.open) return;
    searchDialog.close();
    searchInput.value = "";
    selectSearchOption();
    searchMarkup = "";
    searchResults.replaceChildren();
    searchResults.hidden = true;
    searchInput.setAttribute("aria-expanded", "false");
    const opener = searchOpener;
    searchOpener = undefined;
    if (!restoreFocus) return;
    const target = opener?.isConnected
        ? opener
        : opener?.id
          ? document.getElementById(opener.id)
          : opener?.dataset.action
            ? Array.from(
                  root.querySelectorAll<HTMLElement>("[data-action]"),
              ).find(
                  (element) =>
                      element.dataset.action === opener.dataset.action &&
                      element.dataset.id === opener.dataset.id &&
                      element.dataset.value === opener.dataset.value &&
                      !element.closest("[inert]") &&
                      element.getClientRects().length,
              )
            : null;
    if (
        target &&
        target !== document.body &&
        !target.closest("[inert]") &&
        target.getClientRects().length
    ) {
        target.focus({ preventScroll: true });
    } else {
        const fallback = activityOpen
            ? "close-activity"
            : sidebarCollapsed
              ? "show-work-search"
              : "nav-work-search";
        document.getElementById(fallback)?.focus({ preventScroll: true });
    }
}

function selectSearchOption(option?: HTMLElement) {
    searchResults
        .querySelector('[aria-selected="true"]')
        ?.setAttribute("aria-selected", "false");
    if (!option) {
        searchInput.removeAttribute("aria-activedescendant");
        return;
    }
    option.setAttribute("aria-selected", "true");
    searchInput.setAttribute("aria-activedescendant", option.id);
}

function updateSearch() {
    if (!searchDialog.open) return;
    const { status, results } = renderSearch(
        workspace.state,
        searchInput.value,
    );
    if (status !== searchStatus.textContent) searchStatus.textContent = status;
    if (results === searchMarkup) return;
    const selected = searchInput.getAttribute("aria-activedescendant");
    searchMarkup = results;
    searchResults.innerHTML = results;
    searchResults.hidden = !results;
    searchInput.setAttribute("aria-expanded", String(!!results));
    selectSearchOption(
        selected ? (document.getElementById(selected) ?? undefined) : undefined,
    );
}

function closeActivity() {
    activityOpen = false;
    render();
    (
        document.getElementById(activityOpener) ??
        document.getElementById("show-activity")
    )?.focus({ preventScroll: true });
}

function focusDestination() {
    root.querySelector<HTMLElement>(
        "#settings-title, .directory-heading h1, #work-title-heading, #chat-title, #board-heading, #home-title",
    )?.focus();
}

function selectSearchResult(option: HTMLElement) {
    navigate(
        option.dataset.action === "search-work" ? "work" : "chat",
        option.dataset.id,
    );
    render();
    focusDestination();
}

searchDialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    closeSearch(true);
});
searchDialog.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !event.isComposing) {
        event.preventDefault();
        event.stopPropagation();
        closeSearch(true);
    }
});
searchDialog.addEventListener("click", (event) => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    if (target === searchDialog || target.id === "close-search")
        closeSearch(true);
    const option = target.closest<HTMLElement>('[role="option"]');
    if (option) selectSearchResult(option);
});
searchInput.addEventListener("input", () => {
    selectSearchOption();
    updateSearch();
});
searchInput.addEventListener("keydown", (event) => {
    if (
        event.isComposing ||
        !["ArrowDown", "ArrowUp", "Enter"].includes(event.key)
    )
        return;
    const options = Array.from(
        searchResults.querySelectorAll<HTMLElement>('[role="option"]'),
    );
    if (!options.length) return;
    event.preventDefault();
    const active = options.findIndex(
        (option) =>
            option.id === searchInput.getAttribute("aria-activedescendant"),
    );
    if (event.key === "Enter") {
        selectSearchResult(options[Math.max(0, active)]);
        return;
    }
    const next =
        event.key === "ArrowDown"
            ? (active + 1) % options.length
            : active <= 0
              ? options.length - 1
              : active - 1;
    selectSearchOption(options[next]);
    options[next].scrollIntoView({ block: "nearest" });
});

function composerDraftMode(): DraftMode {
    return reviewAttempt ? `review:${reviewAttempt}` : "message";
}

function render(preserveMounted = false, forceSelect = false) {
    const { context, view, settingsSection } = navigation;
    if (context !== dom.context) {
        modelPickerOpen = false;
        modelListOpen = false;
    }
    const work = view === "work" ? current()?.work : undefined;
    const changesWorkId =
        work &&
        work.status !== WorkStatus.Completed &&
        work.status !== WorkStatus.Cancelled &&
        work.attempts.some((entry) => entry.target.repository?.grant)
            ? work.id
            : undefined;
    const changesCheckpointId = changesWorkId
        ? work?.attempts
              .map(
                  ({ checkpointId, priorTurns }) =>
                      checkpointId ??
                      priorTurns?.findLast((turn) => turn.checkpointId)
                          ?.checkpointId,
              )
              .findLast((id) => id !== undefined)
        : undefined;
    const review = reviewableResult(work);
    workChanges.setWork(changesWorkId, changesCheckpointId, !!review);
    if (review?.attemptId !== reviewAttempt) reviewAttempt = undefined;
    const draft = drafts.get(context, composerDraftMode());
    const pending = submission.pending;
    const submissionState = {
        sending: commandActive,
        pending: !!pending,
        generatingTitle:
            pending?.path === "/api/work/commands"
                ? pending.body.action === WorkAction.Create
                : typeof pending?.body.workId === "string",
    };
    const rendered = dom.render({
        context,
        approvalKey: repositoryApprovalKey(work),
        authenticated,
        preserveMounted,
        forceSelect,
        changesPanel: workChanges.isOpen ? workChanges.element : undefined,
        changesPreview: workChanges.isPreviewing
            ? workChanges.preview
            : undefined,
        draft,
        ...submissionState,
        creationUnavailable:
            workspace.state.workUnavailable ||
            !canGenerateWorkTitle(workspace.state),
        title: authenticated
            ? `${
                  {
                      settings: "Settings",
                      knowledge: "Knowledge",
                      board: "Work inbox",
                      new: "New work",
                      work: work ? workTitle(work) : "Work",
                      chat:
                          workspace.state.conversations.find(
                              (chat) => chat.id === navigation.activeChat,
                          )?.title ?? "New chat",
                  }[view]
              } · Goblin`
            : "Goblin",
        markup: () => {
            const choice = work ? modelSelection(work) : undefined;
            return renderWorkspace({
                navigation,
                workspace: workspace.state,
                mobile: mobile.matches,
                sidebarCollapsed,
                activityOpen,
                showChanges: !!changesWorkId,
                changesOpen: workChanges.isOpen,
                changesPreviewOpen: workChanges.isPreviewing,
                conversationsOpen,
                listStatus,
                boardLayout,
                inboxSearch,
                refreshInterval,
                refreshIntervals,
                draft,
                changing: !!reviewAttempt,
                authenticated,
                sessionChecked,
                sessionFailures,
                timezoneError,
                error,
                submission: submissionState,
                titleEdit,
                canExecute: !!work && modelCanSubmit(work),
                model: choice && {
                    choice,
                    ...modelCatalogs.get(choice.connectionId),
                    connectionId: choice.connectionId,
                    runtime: workspace.state.connections.find(
                        (c) => c.id === choice.connectionId,
                    )?.runtime,
                    pickerOpen: modelPickerOpen,
                    listOpen: modelListOpen,
                },
            });
        },
        settings:
            authenticated && view === "settings"
                ? {
                      detach: () => settings.element.remove(),
                      attach: () =>
                          settings.attach(
                              root.querySelector<HTMLElement>(
                                  "#settings-host",
                              )!,
                              settingsSection,
                          ),
                  }
                : undefined,
    });
    updateSearch();
    if (rendered && authenticated && view !== "settings") ensureModels();
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
            !w || !modelCanSubmit(w) || commandActive || !!submission.pending;
}
const openHeaderMenus = "#workspace-menu[open], #workspace-refresh[open]";
function closeHeaderMenusOutside(target: Element) {
    for (const menu of root.querySelectorAll<HTMLDetailsElement>(
        openHeaderMenus,
    ))
        if (!menu.contains(target)) menu.open = false;
}
function dismissHeaderMenu(menu: HTMLDetailsElement) {
    menu.open = false;
    menu.querySelector("summary")?.focus({ preventScroll: true });
}
document.addEventListener("click", async (event) => {
    if (!(event.target instanceof Element)) return;
    closeHeaderMenusOutside(event.target);
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
    if (action === "open-search") {
        openSearch(target);
        return;
    }
    if (action === "knowledge") {
        navigate(action);
        render();
        document.getElementById(action + "-title")?.focus();
        return;
    }
    if (action === "return-settings") {
        const { view, id } = navigation.settingsReturn;
        navigate(view, id);
        render();
        const opener = Array.from(
            root.querySelectorAll<HTMLElement>("[data-action]"),
        ).find(
            (element) =>
                element.dataset.action === settingsOpener.action &&
                element.dataset.provider === settingsOpener.provider &&
                element.getClientRects().length > 0,
        );
        (
            (settingsOpener.id
                ? document.getElementById(settingsOpener.id)
                : null) ??
            opener ??
            root.querySelector<HTMLElement>(
                ".directory-heading h1, #reply, #work-title-heading, #board-heading",
            )
        )?.focus();
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
                      : "connections",
        );
        return;
    }
    if (action === "refresh") {
        const menu = target.closest<HTMLDetailsElement>("#workspace-refresh");
        if (menu) dismissHeaderMenu(menu);
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
        const returning = navigation.view === "work";
        if (returning && navigation.canReturnToBoard) {
            history.back();
            return;
        }
        navigate("board");
        render();
        focusBoard(returning);
        return;
    }
    if (action === "board-layout" && (value === "board" || value === "list")) {
        boardLayout = value;
        render();
        return;
    }
    if (action === "filter-attention") {
        boardLayout = "list";
        listStatus = "attention";
        render();
        document.getElementById("inbox-status")?.focus();
        return;
    }
    if (action === "clear-inbox-search") {
        inboxSearch = "";
        render();
        document.getElementById("inbox-search")?.focus();
        return;
    }
    if (action === "view-outputs") {
        const all = root.querySelector<HTMLDetailsElement>("#all-outputs");
        if (all) all.open = true;
        const outputs = root.querySelector<HTMLDetailsElement>("#work-outputs");
        if (!outputs) return;
        outputs.open = true;
        outputs.scrollIntoView({ block: "start" });
        outputs.querySelector("summary")?.focus({
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
        await send();
        return;
    }
    if (action === "dismiss") {
        submission.dismiss();
        pendingContext = undefined;
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
        if (activityOpen) {
            closeActivity();
            return;
        }
        activityOpener = target.id;
        activityOpen = true;
        if (mobile.matches) sidebarCollapsed = true;
        render();
        document.getElementById("close-activity")?.focus();
        return;
    }
    if (action === "clarify-request") {
        reviewAttempt = undefined;
        if (!drafts.get(navigation.context))
            drafts.set(navigation.context, "I'd like to clarify my request: ");
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
            let disclosure = output.closest("details");
            while (disclosure) {
                disclosure.open = true;
                disclosure =
                    disclosure.parentElement?.closest("details") ?? null;
            }
            output.scrollIntoView({ block: "nearest" });
            output.querySelector("summary")?.focus({ preventScroll: true });
        }
        return;
    }
    if (action === "view-changes") {
        if (workChanges.isOpen || workChanges.isPreviewing) closeChanges();
        else {
            void workChanges.openPreview();
            render(false, true);
            workChanges.focus();
        }
        return;
    }
    if (action === "reload-changes") {
        void workChanges.openPreview();
        workChanges.focusReviewEntry();
        return;
    }
    if (action === "review-diff") {
        if (workChanges.openReview()) {
            render(false, true);
            workChanges.focus();
        }
        return;
    }
    if (action === "close-diff") {
        closeDiff();
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

    const work = current()?.work;
    if (action === "changes") {
        const result = reviewableResult(work);
        if (!result) return;
        const cancelling = !!reviewAttempt;
        reviewAttempt = cancelling ? undefined : result.attemptId;
        render();
        root.querySelector<HTMLElement>(
            cancelling ? '.review-decision [data-action="changes"]' : "#reply",
        )?.focus();
        return;
    }
    if (action === "execute" && work)
        await command({ action: WorkAction.Execute, ...modelPayload(work) });
    if (action === "retry" && work)
        await command({ action: WorkAction.Retry, ...modelPayload(work) });
    if (
        action === "authorize-github-repository" ||
        action === "deny-github-repository"
    ) {
        const authorizationId = work?.repositoryAuthorization?.id;
        if (authorizationId)
            await command({
                action:
                    action === "authorize-github-repository"
                        ? WorkAction.AuthorizeGitRepository
                        : WorkAction.DenyGitRepository,
                authorizationId,
            });
    }
    if (action === "reconcile") await command({ action: WorkAction.Reconcile });
    if (action === "cancel") await command({ action: WorkAction.Cancel });
    if (action === "approve") {
        const result = reviewableResult(work);
        if (result)
            await command({
                action: WorkAction.Approve,
                attemptId: result.attemptId,
            });
    }
    if (action === "rename-work" && work) {
        const original = work.title ?? null;
        titleEdit = { workId: work.id, original, value: original ?? "" };
        render();
        const field = document.getElementById(
            "work-title-input",
        ) as HTMLInputElement;
        field.focus();
        field.select();
        return;
    }
    if (action === "cancel-rename") {
        cancelRename();
        return;
    }
    if (
        action === "track" &&
        navigation.activeChat &&
        canGenerateWorkTitle(workspace.state)
    ) {
        const conversationId = navigation.activeChat;
        await send(() => commands.track(conversationId));
    }
    render();
    if (action === "select-work" || action === "select-chat")
        focusDestination();
    if (action === "new-work" || action === "new-chat")
        document.getElementById("reply")?.focus();
});
document.addEventListener("submit", async (event) => {
    if (
        !(event.target instanceof HTMLFormElement) ||
        !root.contains(event.target)
    )
        return;
    event.preventDefault();
    const form = event.target;
    const kind = form.dataset.form;
    if (kind === "rename-work") {
        const item = current();
        const text = String(new FormData(form).get("title") ?? "").trim();
        if (
            item &&
            titleEdit?.workId === item.work.id &&
            titleEdit.original === (item.work.title ?? null) &&
            text &&
            Array.from(text).length <= WorkTitleLimit
        )
            await command({ action: WorkAction.Rename, text });
        return;
    }
    if (kind === "github-repository") {
        const work = current()?.work;
        if (!work) return;
        const context = navigation.context;
        const { errors, payload } = readRepositorySetup(form);
        repositoryDrafts.setErrors(context, errors ?? {});
        repositoryDrafts.showErrors(root, context);
        if (!payload) {
            root.querySelector<HTMLElement>(
                ".work-setup [aria-invalid=true]",
            )?.focus();
            return;
        }
        await command(
            isRepositoryHandoff(work)
                ? { action: WorkAction.PrepareGitRepository, ...payload }
                : {
                      action: WorkAction.Execute,
                      ...payload,
                      ...modelPayload(work),
                  },
        );
        return;
    }
    const data = new FormData(form);
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
        if (!canGenerateWorkTitle(workspace.state)) return;
        await send(() => commands.create(text), true);
    } else if (kind === "chat") {
        const conversationId = navigation.activeChat;
        await send(() => commands.append(conversationId, text), true);
    } else if (kind === "work") {
        const work = current()?.work;
        if (
            reviewAttempt &&
            reviewableResult(work)?.attemptId !== reviewAttempt
        ) {
            render();
            return;
        }
        await command(
            {
                action: reviewAttempt
                    ? WorkAction.RequestChanges
                    : work?.attention?.reason === AttentionReason.InputRequired
                      ? WorkAction.Answer
                      : WorkAction.AddContext,
                text,
                attemptId: work?.attempts.at(-1)?.id,
                decisionId: work?.decisions.at(-1)?.id,
            },
            true,
        );
    }
    render();
});
document.addEventListener("input", (event) => {
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-title-input" &&
        titleEdit
    ) {
        titleEdit.value = event.target.value;
        const length = Array.from(titleEdit.value.trim()).length;
        const count = document.getElementById("work-title-count");
        const save = event.target.form?.querySelector<HTMLButtonElement>(
            'button[type="submit"]',
        );
        if (count) count.textContent = `${length} / ${WorkTitleLimit}`;
        event.target.setAttribute(
            "aria-invalid",
            String(length > WorkTitleLimit),
        );
        if (save)
            save.disabled =
                !!save.dataset.blocked || !length || length > WorkTitleLimit;
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
        repositoryDrafts.clearError(navigation.context, event.target.id);
        repositoryDrafts.showErrors(root, navigation.context);
    }
    if (
        event.target instanceof HTMLTextAreaElement &&
        event.target.id === "reply" &&
        root.contains(event.target)
    ) {
        drafts.set(navigation.context, event.target.value, composerDraftMode());
        const send =
            event.target.form?.querySelector<HTMLButtonElement>(".send");
        if (send)
            send.disabled =
                !!send.dataset.blocked || !event.target.value.trim();
    }
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "inbox-search"
    ) {
        inboxSearch = event.target.value;
        render(true);
    }
});
document.addEventListener("change", (event) => {
    if (
        event.target instanceof HTMLInputElement &&
        event.target.name === "workspace-refresh-interval"
    ) {
        const seconds = Number(event.target.value);
        if (!refreshIntervals.includes(seconds) || seconds === refreshInterval)
            return;
        refreshInterval = seconds;
        try {
            localStorage.setItem(refreshIntervalKey, String(seconds));
        } catch {
            // The selection still applies for this page.
        }
        scheduleRefresh();
        render();
        return;
    }
    if (event.target instanceof HTMLSelectElement) {
        const { id, value } = event.target;
        if (id === "inbox-status" && isListStatus(value)) {
            listStatus = value;
            render(true);
            return;
        }
    }
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-effort"
    ) {
        if (!dom.sliderDragging) render(false, true);
        return;
    }
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "github-repository"
    ) {
        repositoryDrafts.clearError(navigation.context, "github-repository");
        repositoryDrafts.showErrors(root, navigation.context);
    }
});
document.addEventListener("focusin", (event) => {
    if (!(event.target instanceof Element)) return;
    closeHeaderMenusOutside(event.target);
    if (modelPickerOpen && !event.target.closest(".model-picker")) {
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
            `${selector} a, ${selector} button, ${selector} input, ${selector} select, ${selector} summary`,
        ),
    ).filter((target) => {
        const disclosure = target.matches("summary")
            ? target.parentElement
            : target;
        return (
            target.getClientRects().length &&
            !target.matches(":disabled") &&
            !disclosure?.parentElement?.closest("details:not([open])")
        );
    });
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
function cancelRename() {
    titleEdit = undefined;
    render();
    document.getElementById("rename-work")?.focus();
}

document.addEventListener("keydown", (event) => {
    if (
        (event.ctrlKey || event.metaKey) &&
        event.key.toLowerCase() === "k" &&
        authenticated
    ) {
        event.preventDefault();
        document.documentElement.removeAttribute("data-pointer-focus");
        openSearch();
        return;
    }
    if (searchDialog.open) return;
    if (
        event.key === "Escape" &&
        !event.isComposing &&
        !commandActive &&
        event.target instanceof Element &&
        event.target.closest(".work-title-edit")
    ) {
        event.preventDefault();
        cancelRename();
        return;
    }
    if (
        event.key === "Escape" &&
        workChanges.isOpen &&
        event.target instanceof Element &&
        (workChanges.element.contains(event.target) ||
            event.target.id === "show-changes")
    ) {
        event.preventDefault();
        closeDiff();
        return;
    }
    if (event.key === "Escape") {
        const menu = root.querySelector<HTMLDetailsElement>(openHeaderMenus);
        if (menu) {
            event.preventDefault();
            dismissHeaderMenu(menu);
            return;
        }
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
    if (activityOpen) {
        if (event.key === "Escape") {
            event.preventDefault();
            closeActivity();
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
        const send =
            event.target.form?.querySelector<HTMLButtonElement>(".send");
        if (send && !send.disabled) event.target.form?.requestSubmit(send);
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
    const { view, id } = navigation.locationTarget();
    const fromWork = navigation.view === "work";
    navigate(view, id, "restore");
    render();
    if (view === "board") {
        focusBoard(fromWork);
        return;
    }
    focusDestination();
});
function focusBoard(restoreItem: boolean) {
    const entry = restoreItem
        ? root.querySelector<HTMLElement>(
              `.board-entry[data-id="${CSS.escape(navigation.selected)}"]`,
          )
        : null;
    (entry ?? document.getElementById("board-heading"))?.focus();
}
window.addEventListener("online", refreshAutomatically);
document.addEventListener("visibilitychange", () => {
    if (!document.hidden) refreshAutomatically();
});
scheduleRefresh();
render();
void refresh();
