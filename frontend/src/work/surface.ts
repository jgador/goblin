import { WorkStatus, AttentionReason, WorkEventKind } from "../api/values.js";
import type { Work, View, Conversation } from "./contracts.js";
import { icon, escapeHtml as e } from "./presentation.js";
import { formatTimestamp } from "../settings/timezone.js";

const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");
const effortLabel = (value: string) =>
    value.slice(0, 1).toUpperCase() + value.slice(1);
const timestamp = (value?: string) =>
    value ? formatTimestamp(value) : "Not recorded";
export function relativeTime(value?: string) {
    if (!value || !Number.isFinite(Date.parse(value)))
        return "Update time unavailable";
    const minutes = Math.max(
        0,
        Math.floor((Date.now() - Date.parse(value)) / 60_000),
    );
    return minutes < 1
        ? "Just now"
        : minutes < 60
          ? `${minutes}m ago`
          : minutes < 1440
            ? `${Math.floor(minutes / 60)}h ago`
            : `${Math.floor(minutes / 1440)}d ago`;
}

// Search existing product content without pretending there is a global index.
export function matchesWork(
    w: Work,
    query: string,
    conversations: Conversation[],
    agent: string,
) {
    return [
        w.objective,
        w.id,
        agent,
        ...w.decisions.flatMap((d) => [d.question, d.answer ?? ""]),
        ...w.results.map((r) => r.text),
        ...w.artifacts.flatMap((a) => [a.name, a.reference]),
        ...w.attempts.map((a) => a.target.repository?.repository ?? ""),
        ...w.history.map((h) => h.text ?? ""),
        ...conversations
            .filter((c) => c.workId === w.id)
            .flatMap((c) => [c.title, ...c.messages.map((m) => m.text)]),
    ].some((value) =>
        value.toLocaleLowerCase().includes(query.toLocaleLowerCase()),
    );
}

export function renderProgress(item: View) {
    const w = item.work,
        attempt = w.attempts.at(-1);
    const result = w.results.find((r) => r.attemptId === attempt?.id);
    const completed = w.status === WorkStatus.Completed;
    const review = w.attention?.reason === AttentionReason.ResultReview;
    const hasResult =
        !!result && !result.requestedChanges && (review || completed);
    const executionText =
        w.status === WorkStatus.Cancelled
            ? "Work cancelled"
            : w.status === WorkStatus.Ready
              ? attempt
                  ? "Ready for another attempt"
                  : "Ready to start"
              : w.attention
                ? label(w.attention.reason)
                : label(w.status);
    const rows = [
        {
            id: "goal",
            title: "Goal captured",
            done: true,
            state: "Saved",
            evidence: `Work ${w.id} · Created ${timestamp(item.createdAt)}`,
        },
        {
            id: "execution",
            title: "Work on the outcome",
            done: hasResult,
            state: hasResult ? "Result available" : executionText,
            evidence: attempt
                ? `Attempt ${attempt.id} · ${label(attempt.status)}. ${result?.requestedChanges ? "Changes were requested on this result." : "Execution status does not by itself complete Work."}`
                : "No execution has been started.",
        },
        {
            id: "review",
            title: "Review and complete",
            done: completed,
            state: completed
                ? "Approved"
                : review
                  ? "Needs your review"
                  : "Not complete",
            evidence: completed
                ? `Result approved ${timestamp(w.results.findLast((r) => r.approvedAt)?.approvedAt)}.`
                : "Work completes only when a result is approved.",
        },
    ];
    return `<div class="progress-list">${rows.map((row) => `<details class="progress-row ${row.done ? "is-complete" : ""}" id="progress-${row.id}"><summary><span class="milestone-icon ${row.done ? "complete" : ""}">${icon(row.done ? "check" : "clock")}</span><span class="row-title">${e(row.title)}</span><span class="row-meta ${row.done ? "sr-only" : ""}">${e(row.state)}</span></summary><div class="inspection"><p>${e(row.evidence)}</p>${row.id === "execution" && attempt ? `<button class="text-button" data-action="inspect-output" data-id="execution-${e(attempt.id)}">Inspect execution ${icon("arrow")}</button>` : ""}</div></details>`).join("")}</div>`;
}

export function renderDecisions(w: Work) {
    return (
        w.decisions
            .map(
                (d) =>
                    `<details class="durable-row" id="decision-${e(d.id)}"><summary>${icon(d.answer ? "circleCheck" : "wait")}<span class="row-title">${e(d.question)}</span><span class="row-meta">${d.answer ? "Answered" : "Awaiting input"}</span>${icon("chevron", "disclosure-icon")}</summary><div class="inspection">${d.answer ? `<p class="preserve-lines">${e(d.answer)}</p>` : "<p>This question needs an answer before work can continue.</p>"}<p class="inspection-meta">${d.attemptId ? `Requested in attempt ${e(d.attemptId)} · ` : ""}${e(timestamp(d.requestedAt))}${d.answeredAt ? `<br>Answer recorded ${e(timestamp(d.answeredAt))}` : ""}</p></div></details>`,
            )
            .join("") ||
        '<p class="section-empty">No decisions recorded yet. Questions and their answers will appear here.</p>'
    );
}

export function renderOutputs(w: Work) {
    const outputs = [
        ...w.results.toReversed().map((r) => ({
            at: r.approvedAt ?? r.proposedAt,
            id: `result-${r.attemptId}`,
            icon: "file",
            title: r.approvedAt
                ? "Approved result"
                : r.requestedChanges
                  ? "Changes requested"
                  : "Proposed result",
            type: "Result",
            description: r.text,
            body: `<div class="preserve-lines">${e(r.text)}</div><p class="inspection-meta">Attempt ${e(r.attemptId)}${r.approvedAt ? ` · Approved ${e(timestamp(r.approvedAt))}` : ""}</p>${r.requestedChanges ? `<p class="preserve-lines">Requested changes: ${e(r.requestedChanges)}</p>` : ""}`,
        })),
        ...w.artifacts.toReversed().map((a) => ({
            at: a.createdAt,
            id: `artifact-${a.attemptId ?? "unknown"}-${encodeURIComponent(a.reference)}`,
            icon: "file",
            title: a.name,
            type: "Artifact",
            description: a.reference,
            body: `<p>${/^https:\/\/github\.com\//.test(a.reference) ? `<a href="${e(a.reference)}" rel="noreferrer" target="_blank">Open on GitHub ${icon("arrow")}</a>` : "Preview unavailable for this artifact."}</p><p class="inspection-meta preserve-lines">${e(a.reference)}</p>${a.attemptId ? `<p class="inspection-meta">Attempt ${e(a.attemptId)}</p>` : ""}`,
        })),
        ...w.attempts.toReversed().map((a) => ({
            at: a.finishedAt ?? a.startedAt ?? a.queuedAt,
            id: `execution-${a.id}`,
            icon: "activity",
            title: `${a.target.runtime} · ${label(a.status)}`,
            type: "Execution",
            description: "Execution log and runtime details",
            body: `<dl class="execution-properties"><dt>Attempt</dt><dd>${e(a.id)}</dd><dt>Runtime turn</dt><dd>${a.turnNumber ?? 1}</dd><dt>Agent</dt><dd>${e(a.agentId ?? "Not recorded")}</dd><dt>Runtime</dt><dd>${e(a.target.runtime)}</dd><dt>Requested model</dt><dd>${e(a.target.requestedModel ?? "Codex default")}</dd><dt>Reasoning effort</dt><dd>${e(a.target.requestedEffort ? effortLabel(a.target.requestedEffort) : "Model default")}</dd><dt>Reported model</dt><dd>${e(a.session?.model ?? "Model not reported")}</dd><dt>Started</dt><dd>${e(timestamp(a.startedAt))}</dd><dt>Finished</dt><dd>${e(timestamp(a.finishedAt))}</dd>${a.environmentReference ? `<dt>Environment</dt><dd>${e(a.environmentReference)}</dd>` : ""}${a.target.repository ? `<dt>Repository</dt><dd>${e(a.target.repository.repository)}${a.target.repository.grant ? ` · ${e(a.target.repository.grant.branch)} · @${e(a.target.repository.grant.login)}` : ""}</dd>` : ""}${a.failure ? `<dt>Failure</dt><dd>${e(label(a.failure))}</dd>` : ""}${a.cleanupPending ? "<dt>Cleanup</dt><dd>Pending</dd>" : ""}</dl>${a.priorTurns?.length ? `<details class="execution-events"><summary>Previous runtime turns (${a.priorTurns.length})</summary>${a.priorTurns.map((t) => `<p class="inspection-meta">Turn ${t.number} · ${e(t.session?.model ?? "Model not reported")} · ${e(timestamp(t.startedAt))}${t.checkpointId ? " · Checkpoint saved" : ""}</p>`).join("")}</details>` : ""}${
                w.history.filter((h) => h.attemptId === a.id).length
                    ? `<details class="execution-events" id="execution-events-${e(a.id)}"><summary>Execution history</summary>${w.history
                          .filter((h) => h.attemptId === a.id)
                          .map(
                              (h) =>
                                  `<p class="inspection-meta">${e(timestamp(h.occurredAt))} · ${e(label(h.kind))}</p>${h.text ? `<p class="preserve-lines">${e(h.text)}</p>` : ""}`,
                          )
                          .join("")}</details>`
                    : ""
            }`,
        })),
    ];
    outputs.sort(
        (a, b) => (Date.parse(b.at ?? "") || 0) - (Date.parse(a.at ?? "") || 0),
    );
    const row = (o: (typeof outputs)[number]) =>
        `<details class="durable-row output-row" id="${e(o.id)}"><summary>${icon(o.icon)}<span class="output-copy"><span class="row-title">${e(o.title)}</span><span class="output-description">${e(o.description)}</span></span><span class="row-meta">${o.type}</span>${icon("chevron", "disclosure-icon")}</summary><div class="inspection">${o.body}</div></details>`;
    return (
        outputs.slice(0, 3).map(row).join("") +
            (outputs.length > 3
                ? `<details id="all-outputs" class="all-outputs"><summary>View all ${outputs.length} outputs ${icon("chevron")}</summary>${outputs.slice(3).map(row).join("")}</details>`
                : "") ||
        '<p class="section-empty">Results, artifacts, and executions will appear here.</p>'
    );
}

// Explicit product events only. Claims, cleanup bookkeeping, and runtime progress
// reports stay in execution inspection; a runtime narrative is not a milestone.
const eventNames: Partial<Record<WorkEventKind, string>> = {
    [WorkEventKind.Created]: "Work created",
    [WorkEventKind.Assigned]: "Agent assigned",
    [WorkEventKind.ExecutionQueued]: "Work queued",
    [WorkEventKind.ExecutionStarted]: "Work started",
    [WorkEventKind.ExecutionFailed]: "Execution failed",
    [WorkEventKind.ExecutionUncertain]: "Execution needs reconciliation",
    [WorkEventKind.ExecutionStopped]: "Execution stopped",
    [WorkEventKind.RetryRequested]: "Retry requested",
    [WorkEventKind.InputRequested]: "Input requested",
    [WorkEventKind.ExecutionContinued]: "Execution continued",
    [WorkEventKind.WorkspaceSaved]: "Git checkpoint recorded",
    [WorkEventKind.WorkspaceReleased]: "Sandbox suspended",
    [WorkEventKind.InputProvided]: "Input provided",
    [WorkEventKind.GitRepositoryDenied]: "Repository access declined",
    [WorkEventKind.GitRepositoryAuthorizationInvalidated]:
        "Repository authorization needs review",
    [WorkEventKind.GitRepositoryRequested]: "Repository access requested",
    [WorkEventKind.GitRepositoryAuthorized]: "Repository access authorized",
    [WorkEventKind.ResultProposed]: "Result proposed",
    [WorkEventKind.ChangesRequested]: "Changes requested",
    [WorkEventKind.ResultApproved]: "Result approved",
    [WorkEventKind.CancellationRequested]: "Cancellation requested",
    [WorkEventKind.Cancelled]: "Work cancelled",
    [WorkEventKind.ContextAdded]: "Context added",
    [WorkEventKind.ArtifactRecorded]: "Artifact recorded",
    [WorkEventKind.CleanupRequired]: "Cleanup needs attention",
    [WorkEventKind.CleanupFailed]: "Cleanup failed",
};
const humanEvents = new Set<WorkEventKind>([
    WorkEventKind.Created,
    WorkEventKind.Assigned,
    WorkEventKind.RetryRequested,
    WorkEventKind.InputProvided,
    WorkEventKind.GitRepositoryAuthorized,
    WorkEventKind.ChangesRequested,
    WorkEventKind.ResultApproved,
    WorkEventKind.CancellationRequested,
    WorkEventKind.ContextAdded,
]);
const conversationEvents = new Set<WorkEventKind>([
    WorkEventKind.Created,
    WorkEventKind.ContextAdded,
    WorkEventKind.InputProvided,
    WorkEventKind.ChangesRequested,
    WorkEventKind.ResultProposed,
    WorkEventKind.InputRequested,
    WorkEventKind.ExecutionContinued,
    WorkEventKind.WorkspaceReleased,
]);

export function renderActivity(w?: Work) {
    if (!w)
        return (
            '<div class="activity-empty">' +
            icon("activity") +
            "<p>Select Work to see what happened around it.</p></div>"
        );
    // Linked conversation messages already become ContextAdded history records.
    // Render the durable sequence once; do not invent connector provenance.
    return (
        `<ol class="activity-timeline">${w.history
            .filter((h) => eventNames[h.kind])
            .map((h) => {
                const source = humanEvents.has(h.kind)
                    ? "Goblin Web"
                    : [
                            WorkEventKind.InputRequested,
                            WorkEventKind.ResultProposed,
                        ].some((value) => value === h.kind)
                      ? "Agent"
                      : "Goblin";
                return `<li class="activity-item"><span class="activity-icon">${icon(source === "Goblin Web" ? "chat" : source === "Agent" ? "spark" : "activity")}</span><details id="event-${e(h.sequence)}"><summary><span class="activity-source">${source}<time datetime="${e(h.occurredAt)}" title="${e(timestamp(h.occurredAt))}">${e(relativeTime(h.occurredAt))}</time></span><span class="activity-title">${e(eventNames[h.kind])}</span>${h.text ? `<span class="activity-preview">${e(h.text)}</span>` : ""}${h.failure ? `<span class="activity-preview">${e(label(h.failure))}</span>` : ""}</summary><div class="activity-context">${h.text ? `<p class="preserve-lines">${e(h.text)}</p>` : ""}<p>${e(timestamp(h.occurredAt))}</p>${h.attemptId ? `<button class="text-button" data-action="inspect-output" data-id="execution-${e(h.attemptId)}">Attempt ${e(h.attemptId)} ${icon("arrow")}</button>` : ""}</div></details></li>`;
            })
            .join("")}</ol>` +
        (w.history.some((h) => eventNames[h.kind])
            ? ""
            : '<p class="section-empty">No activity recorded yet.</p>')
    );
}

export function renderConversation(w: Work) {
    return (
        w.history
            .filter((h) => conversationEvents.has(h.kind) && h.text)
            .map(
                (h) =>
                    `<article class="context-message"><p class="message-name">${[WorkEventKind.ResultProposed, WorkEventKind.InputRequested].some((value) => value === h.kind) ? "Goblin" : "You"}<time>${e(timestamp(h.occurredAt))}</time></p><p class="preserve-lines">${e(h.text)}</p></article>`,
            )
            .join("") ||
        '<p class="section-empty">No conversation recorded yet.</p>'
    );
}
