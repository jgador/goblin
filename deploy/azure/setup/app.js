import {
    SetupStatus,
    SetupPhase,
    SetupStepStatus,
    SetupStep,
    isSetupState,
} from "./contract-values.js";

const connection = document.querySelector("#connection");
const stages = document.querySelector("#stages");
const setupStatus = document.querySelector("#setup-status");
const logPanel = document.querySelector(".installation-log");
const stageDefinitions = [
    {
        title: "Prepare your server",
        description: "A secure foundation for your workspace.",
        complete: "Your server is ready.",
        steps: [
            SetupStep.Prepare,
            SetupStep.K3s,
            SetupStep.CertManager,
            SetupStep.Sandbox,
        ],
    },
    {
        title: "Install Goblin",
        description: "Bring your coworker’s tools together.",
        complete: "Goblin and its tools are installed.",
        steps: [SetupStep.Image, SetupStep.Prefetch, SetupStep.Import],
    },
    {
        title: "Create your workspace",
        description: "A place for your conversations and work.",
        complete: "Your workspace is in place.",
        steps: [SetupStep.Database, SetupStep.Migrate, SetupStep.Deploy],
    },
    {
        title: "Open your workspace",
        description: "A final check, then you’re ready to go.",
        complete: "Your workspace is ready to open.",
        steps: [SetupStep.Verify, SetupStep.Activate],
    },
];
function text(element, value) {
    if (element.textContent !== value) element.textContent = value;
}
const stageViews = stageDefinitions.map((definition, index) => {
    const element = document.createElement("details");
    element.className = "setup-stage";
    const summary = document.createElement("summary");
    const number = document.createElement("span");
    number.className = "stage-number";
    number.setAttribute("aria-hidden", "true");
    number.textContent = String(index + 1).padStart(2, "0");
    const copy = document.createElement("span");
    copy.className = "stage-copy";
    const title = document.createElement("span");
    title.className = "stage-title";
    title.textContent = definition.title;
    const description = document.createElement("span");
    description.className = "stage-description";
    const status = document.createElement("span");
    status.className = "stage-status";
    copy.append(title, description);
    summary.append(number, copy, status);
    const tasks = document.createElement("ul");
    tasks.className = "stage-tasks";
    tasks.setAttribute("aria-label", definition.title + " tasks");
    const rows = new Map(
        definition.steps.map((id) => {
            const row = document.createElement("li");
            const indicator = document.createElement("span");
            indicator.className = "indicator";
            indicator.setAttribute("aria-hidden", "true");
            const label = document.createElement("span");
            label.className = "label";
            const status = document.createElement("span");
            status.className = "status";
            row.append(indicator, label, status);
            tasks.append(row);
            return [id, { row, indicator, label, status }];
        }),
    );
    element.append(summary, tasks);
    stages.append(element);
    return { definition, element, number, description, status, rows };
});
const log = document.querySelector("#install-log");
const followLog = document.querySelector("#follow-log");
const logConnection = document.querySelector("#log-connection");
let latest;
let failures = 0;

function render(state) {
    if (
        latest?.logGeneration === state.logGeneration &&
        state.revision < latest.revision
    )
        return;
    const becameFailed =
        state.status === SetupStatus.Failed &&
        latest?.status !== SetupStatus.Failed;
    latest = state;
    const failed = state.status === SetupStatus.Failed;
    const ready = state.status === SetupStatus.Ready;
    const completed = state.steps.filter(
        (step) => step.status === SetupStepStatus.Complete,
    ).length;
    const running = state.steps.filter(
        (step) => step.status === SetupStepStatus.Running,
    );
    setupStatus.dataset.status = state.status;
    text(
        setupStatus,
        failed
            ? "Needs attention"
            : ready
              ? "Ready"
              : state.status === SetupStatus.Waiting
                ? "Getting ready"
                : "Installing",
    );
    logConnection.dataset.status = state.status;
    text(
        logConnection,
        failed
            ? "Stopped"
            : ready
              ? "Complete"
              : state.status === SetupStatus.Waiting
                ? "Waiting"
                : "Live",
    );
    document.querySelector("#recovery").hidden = !failed;
    text(
        document.querySelector("h1"),
        failed
            ? "Setup needs attention."
            : ready
              ? "Your workspace is ready."
              : "Getting things ready.",
    );
    text(
        document.querySelector("#intro"),
        failed
            ? "Something interrupted installation. Your progress is saved, so you can pick up where you left off."
            : ready
              ? "Everything is in place. We’re opening your Goblin workspace."
              : "We’re preparing a space for your conversations, work, and results. Goblin will open here when it’s ready.",
    );
    text(document.querySelector("#completed-count"), String(completed));
    text(document.querySelector("#total-count"), String(state.steps.length));
    const progress = document.querySelector("#overall-progress");
    progress.max = Math.max(1, state.steps.length);
    progress.value = completed;
    progress.setAttribute(
        "aria-valuetext",
        completed + " of " + state.steps.length + " tasks complete",
    );
    text(
        document.querySelector("#activity"),
        failed
            ? "Review the log below to see what needs attention."
            : ready
              ? "Opening your workspace…"
              : state.phase === SetupPhase.Activating
                ? "Connecting your workspace to this address…"
                : running.length
                  ? running.length +
                    (running.length === 1
                        ? " task running on your server."
                        : " tasks running on your server.")
                  : state.status === SetupStatus.Waiting
                    ? "Waiting for installation to begin."
                    : "Preparing the next task…",
    );
    for (const view of stageViews) {
        const members = state.steps.filter((step) =>
            view.definition.steps.includes(step.id),
        );
        const failedSteps = members.filter(
            (step) => step.status === SetupStepStatus.Failed,
        );
        const active = members.filter(
            (step) => step.status === SetupStepStatus.Running,
        );
        const done = members.filter(
            (step) => step.status === SetupStepStatus.Complete,
        ).length;
        const status = failedSteps.length
            ? SetupStepStatus.Failed
            : members.length && done === members.length
              ? SetupStepStatus.Complete
              : active.length || (done && state.status === SetupStatus.Running)
                ? SetupStepStatus.Running
                : SetupStepStatus.Waiting;
        if (
            status === SetupStepStatus.Failed &&
            view.element.dataset.status !== status
        )
            view.element.open = true;
        view.element.dataset.status = status;
        text(
            view.number,
            status === SetupStepStatus.Complete
                ? "✓"
                : status === SetupStepStatus.Failed
                  ? "!"
                  : String(stageViews.indexOf(view) + 1).padStart(2, "0"),
        );
        text(
            view.status,
            {
                [SetupStepStatus.Waiting]: "Waiting",
                [SetupStepStatus.Running]: "In progress",
                [SetupStepStatus.Complete]: "Complete",
                [SetupStepStatus.Failed]: "Attention",
            }[status],
        );
        text(
            view.description,
            failedSteps.length
                ? failedSteps.map((step) => step.label).join(" · ")
                : active.length
                  ? active.map((step) => step.label).join(" · ")
                  : status === SetupStepStatus.Complete
                    ? view.definition.complete
                    : done
                      ? done + " of " + members.length + " tasks complete."
                      : view.definition.description,
        );
        for (const [id, elements] of view.rows) {
            const step = members.find((step) => step.id === id);
            elements.row.hidden = !step;
            if (!step) continue;
            elements.row.dataset.status = step.status;
            text(
                elements.indicator,
                step.status === SetupStepStatus.Complete
                    ? "✓"
                    : step.status === SetupStepStatus.Failed
                      ? "!"
                      : "",
            );
            text(elements.label, step.label);
            text(
                elements.status,
                {
                    [SetupStepStatus.Waiting]: "Waiting",
                    [SetupStepStatus.Running]: "Running",
                    [SetupStepStatus.Complete]: "Complete",
                    [SetupStepStatus.Failed]: "Failed",
                }[step.status],
            );
        }
    }
    if (becameFailed) logPanel.open = true;
    updateElapsed();
    renderLog(state);
}

function renderLog(state) {
    if (!Array.isArray(state.logs) || !state.logs.length) return;
    const previousScroll = log.scrollTop;
    log.replaceChildren(
        ...state.logs.map((entry) => {
            const row = document.createElement("div");
            row.className = "log-entry";
            const meta = document.createElement("div");
            meta.className = "log-entry-meta";
            const time = document.createElement("time");
            time.dateTime = entry.timestamp;
            time.textContent = new Date(entry.timestamp).toLocaleTimeString(
                [],
                { hour12: false },
            );
            const attempt = document.createElement("span");
            attempt.textContent = `Attempt ${entry.attempt}`;
            meta.append(time, attempt);
            if (entry.step) {
                const step = document.createElement("span");
                step.className = "log-step";
                step.textContent =
                    state.steps.find((step) => step.id === entry.step)?.label ??
                    entry.step;
                meta.append(step);
            }
            const output = document.createElement("p");
            output.className = "log-message";
            output.textContent = entry.message;
            row.append(meta, output);
            return row;
        }),
    );
    log.scrollTop = followLog.checked ? log.scrollHeight : previousScroll;
}
logPanel.addEventListener("toggle", () => {
    if (logPanel.open && followLog.checked) log.scrollTop = log.scrollHeight;
});
log.addEventListener("scroll", () => {
    if (log.scrollHeight - log.scrollTop - log.clientHeight > 20)
        followLog.checked = false;
});
followLog.addEventListener("change", () => {
    if (followLog.checked) log.scrollTop = log.scrollHeight;
});

const events = new EventSource("/setup/events");
events.addEventListener("progress", (event) => {
    try {
        const state = JSON.parse(event.data);
        if (!isSetupState(state)) return;
        render(state);
    } catch {
        logConnection.textContent = "Reconnecting…";
        delete logConnection.dataset.status;
    }
});
events.addEventListener("error", () => {
    // Bounded server batches close normally. Show a gap only when status also
    // cannot be reached; EventSource resumes automatically with its last event ID.
    if (failures) {
        logConnection.textContent = "Reconnecting…";
        delete logConnection.dataset.status;
    }
});
window.addEventListener("pagehide", () => events.close());

function updateElapsed() {
    if (!latest) return;
    const end =
        latest.status === SetupStatus.Failed ||
        latest.status === SetupStatus.Ready
            ? Date.parse(latest.updatedAt)
            : Date.now();
    const seconds = Math.max(
        0,
        Math.floor((end - Date.parse(latest.startedAt)) / 1000),
    );
    document.querySelector("#elapsed").textContent =
        `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

async function poll() {
    try {
        const response = await fetch("/setup/status", {
            cache: "no-store",
            signal: AbortSignal.timeout(5000),
        });
        if (!response.ok) throw new Error("Status unavailable");
        const state = await response.json();
        if (!isSetupState(state)) throw new Error("Unexpected status");
        if (state.publicUrl) {
            const target = new URL(state.publicUrl);
            if (
                !["http:", "https:"].includes(target.protocol) ||
                target.username ||
                target.password
            )
                throw new Error("Unexpected workspace URL");
            // Move an IP/localhost visit to the configured origin while setup is still
            // serving it. Progress and readiness checks then use the final ingress host,
            // even if the browser misses the last status update before the handoff.
            if (target.origin !== location.origin) {
                location.replace(target.origin + "/");
                return;
            }
        }
        render(state);
        failures = 0;
        connection.textContent = "";
    } catch {
        failures++;
        text(setupStatus, "Reconnecting");
        delete setupStatus.dataset.status;
        connection.textContent =
            latest?.phase === SetupPhase.Activating
                ? "Connecting to Goblin…"
                : "Reconnecting… Installation continues on your server.";
        // The Kubernetes application exposes this endpoint; the setup UI never does.
        // Check across two polls so a transient route change does not trigger a reload loop.
        try {
            const response = await fetch("/readyz", {
                cache: "no-store",
                signal: AbortSignal.timeout(5000),
            });
            if (
                response.ok &&
                (await response.json()).ready === true &&
                failures >= 2
            )
                location.replace("/");
        } catch {
            /* A short connection gap is expected while port 80 changes owners. */
        }
    } finally {
        setTimeout(poll, 3000);
    }
}
setInterval(updateElapsed, 1000);
poll();
