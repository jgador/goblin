import {
    AttentionReason,
    ConnectionAvailability,
    WorkAction,
    WorkStatus,
} from "../api/values.js";
import { errorMessage } from "../api/client.js";
import { Settings } from "../settings/settings.js";
import { SystemResources } from "../settings/system.js";
import {
    displayTimeZone,
    loadTimeZone,
    resetTimeZone,
    savedTimeZone,
} from "../settings/timezone.js";
import { TimeZoneSetup } from "../settings/timezone-picker.js";
import { isWorkFilter } from "./board.js";
import type { BoardLayout, WorkFilter } from "./board.js";
import { WorkChangesDialog } from "./changes.js";
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
import type { DraftSnapshot } from "./drafts.js";
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
import {
    isRepositoryHandoff,
    readRepositorySetup,
    repositoryApprovalKey,
    RepositoryDrafts,
} from "./repository-setup.js";
import { WorkspaceController } from "./workspace.js";
import { WorkspaceDom } from "./workspace-dom.js";
import { renderWorkspace } from "./workspace-view.js";

const workspace = new WorkspaceController(() =>
    window.dispatchEvent(new Event("goblin-workspace-locked")),
);
const navigation = new WorkspaceNavigation();
const drafts = new ComposerDrafts();
const repositoryDrafts = new RepositoryDrafts();
const root = document.querySelector<HTMLDivElement>("#app")!;
const dom = new WorkspaceDom(root, repositoryDrafts);
const workChanges = new WorkChangesDialog(api);
const commands = new WorkCommands(
    async (...kinds) =>
        (await api<{ ids: string[] }>("/api/identities", { kinds })).ids,
);
const mobile = matchMedia("(max-width: 760px)");
const compact = matchMedia("(max-width: 1199px)");
let activityOpen = false,
    conversationExpanded = false,
    conversationsOpen = navigation.view === "chat",
    filter: WorkFilter = "all",
    boardFilter: WorkFilter = "all",
    boardLayout: BoardLayout = "list",
    searchOpen = false,
    search = "";
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
    error = "",
    changing = false;
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
    if (navigation.view !== "settings") {
        const trigger =
            document.activeElement instanceof HTMLElement
                ? document.activeElement.closest<HTMLElement>("[data-action]")
                : null;
        settingsOpener = {
            id: trigger?.id,
            action: trigger?.dataset.action,
            provider: trigger?.dataset.provider,
        };
    }
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
    workChanges.retain();
    workspace.reset();
    activeRefresh = undefined;
    timezoneSetup.reset();
    resetTimeZone();
    timezoneError = "";
    drafts.clear();
    repositoryDrafts.clear();
    pendingContext = undefined;
    changing = false;
    filter = "all";
    boardFilter = "all";
    searchOpen = false;
    search = "";
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

async function loadWorkspace(preserveError: boolean) {
    const signal = workspace.signal;
    try {
        const session = await api<{ authenticated: boolean }>("/api/session");
        signal.throwIfAborted();
        sessionFailures = 0;
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
                  ? drafts.capture(navigation.context)
                  : undefined,
          }
        : (pendingContext ??
          (pending ? { origin: restoredContext(pending) } : undefined));
    if (!context) return;
    const signal = workspace.signal;
    error = "";
    commandActive = true;
    let confirmed = false;
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
            if (context.draft) changing = false;
            if (
                saved.path === "/api/work/commands" &&
                saved.body.action === WorkAction.Create
            )
                navigate("work", saved.body.workId);
            if (saved.path === "/api/conversations/commands") {
                if (typeof saved.body.workId === "string")
                    navigate("work", saved.body.workId);
                else if (navigation.activeChat !== saved.body.conversationId)
                    navigate("chat", saved.body.conversationId);
            }
        }
    } catch (failure) {
        if (!signal.aborted) {
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
    const leavingSettings =
        navigation.view === "settings" && next !== "settings";
    if (leavingSettings) settings.reset();
    navigation.navigate(next, id, historyMode);
    searchOpen = false;
    if (navigation.isDirectory) search = "";
    changing = false;
    conversationExpanded = false;
    activityOpen = false;
    modelPickerOpen = false;
    modelListOpen = false;
    if (mobile.matches) sidebarCollapsed = true;
    if (leavingSettings && authenticated) {
        modelCatalogs.clear();
        void refreshConnections();
    }
}

function render(preserveMounted = false, forceSelect = false) {
    const { context, view, settingsSection } = navigation;
    if (context !== dom.context) {
        modelPickerOpen = false;
        modelListOpen = false;
    }
    const work = view === "work" ? current()?.work : undefined;
    workChanges.retain(
        work &&
            work.status !== WorkStatus.Completed &&
            work.status !== WorkStatus.Cancelled
            ? work.id
            : undefined,
    );
    const draft = drafts.get(context);
    const submissionState = {
        sending: commandActive,
        pending: !!submission.pending,
    };
    const rendered = dom.render({
        context,
        approvalKey: repositoryApprovalKey(work),
        authenticated,
        preserveMounted,
        forceSelect,
        draft,
        ...submissionState,
        workUnavailable: workspace.state.workUnavailable,
        title:
            view === "settings"
                ? "Settings · Goblin"
                : view === "knowledge"
                  ? "Knowledge · Goblin"
                  : view === "board" && authenticated
                    ? "Work inbox · Goblin"
                    : work && authenticated
                      ? work.objective.slice(0, 70) + " · Goblin"
                      : "Goblin",
        markup: () => {
            const choice = work ? modelSelection(work) : undefined;
            return renderWorkspace({
                navigation,
                workspace: workspace.state,
                mobile: mobile.matches,
                sidebarCollapsed,
                activityOpen,
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
                submission: submissionState,
                systemSummary: system.summary(),
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
        const returning =
            navigation.inboxSelection ||
            target.classList.contains("back-to-work");
        filter = "all";
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
    if (action === "board-filter" && value && isWorkFilter(value)) {
        boardFilter = value;
        render();
        return;
    }
    if (action === "expand-work" && navigation.inboxSelection) {
        navigation.expandWork();
        render();
        document.getElementById("work-title-heading")?.focus();
        return;
    }
    if (action === "inbox-overview") {
        document.getElementById("detail-content")?.scrollTo({ top: 0 });
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
        if (!drafts.get(navigation.context))
            drafts.set(navigation.context, "I'd like to clarify the goal: ");
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
            const all = output.closest<HTMLDetailsElement>(
                "#all-outputs, #all-executions",
            );
            if (all) all.open = true;
            output.open = true;
            output.scrollIntoView({ block: "nearest" });
            output.querySelector("summary")?.focus({ preventScroll: true });
        }
        return;
    }
    if (action === "view-changes") {
        const work = current()?.work;
        if (work) await workChanges.open(work.id);
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
    if (action === "clear-search") {
        search = "";
        searchOpen = false;
    }
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

    if (action === "changes") changing = true;
    const work = current()?.work;
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
    if (action === "approve")
        await command({
            action: WorkAction.Approve,
            attemptId: work?.attempts.at(-1)?.id,
        });
    if (action === "track" && navigation.activeChat) {
        const conversationId = navigation.activeChat;
        await send(() => commands.track(conversationId));
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
    const form = event.target;
    const kind = form.dataset.form;
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
        await send(() => commands.create(text), true);
    } else if (kind === "chat") {
        const conversationId = navigation.activeChat;
        await send(() => commands.append(conversationId, text), true);
    } else if (kind === "work") {
        const work = current()?.work;
        await command(
            {
                action: changing
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
        root.contains(event.target)
    )
        drafts.set(navigation.context, event.target.value);
    if (
        event.target instanceof HTMLInputElement &&
        event.target.id === "work-search"
    ) {
        search = event.target.value;
        searchOpen = true;
        render();
    }
});
document.addEventListener("change", (event) => {
    if (
        event.target instanceof HTMLSelectElement &&
        event.target.id === "work-view"
    ) {
        const nextFilter = event.target.value;
        if (!isWorkFilter(nextFilter)) return;
        filter = nextFilter;
        const fromDirectory = navigation.isDirectory;
        if (fromDirectory) {
            boardFilter = nextFilter;
            navigate("board");
        }
        render(false, true);
        if (fromDirectory) focusBoard(false);
        return;
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
    if (event.target.id === "work-search" && search && !searchOpen) {
        searchOpen = true;
        render(true);
    } else if (searchOpen && !event.target.closest(".workspace-header")) {
        searchOpen = false;
        root.querySelector(".search-results")?.remove();
    }
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
                navigation.view === "knowledge"
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
        searchOpen = false;
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
    const { view, id } = navigation.locationTarget();
    const fromWork = navigation.view === "work";
    navigate(view, id, "restore");
    render();
    if (view === "board") {
        focusBoard(fromWork);
        return;
    }
    root.querySelector<HTMLElement>(
        "#settings-title, .directory-heading h1, #work-title-heading, #reply",
    )?.focus();
});
function focusBoard(restoreItem: boolean) {
    const entry = restoreItem
        ? root.querySelector<HTMLElement>(
              `.board-entry[data-id="${CSS.escape(navigation.selected)}"]`,
          )
        : null;
    (entry ?? document.getElementById("board-heading"))?.focus();
}
window.addEventListener("online", () => void refresh());
document.addEventListener("visibilitychange", () => {
    if (!document.hidden) void refresh();
});
setInterval(() => {
    if (!document.hidden) void refresh();
}, 3000);
render();
void refresh();
