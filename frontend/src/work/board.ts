import {
    AttentionReason,
    ConnectionAvailability,
    WorkStatus,
} from "../api/values.js";
import type { Agent, Connection } from "../api/workspace-contracts.js";
import type { View, Work } from "./contracts.js";
import { escapeHtml as e, icon } from "./presentation.js";
import { relativeTime } from "./surface.js";

const filters = {
    all: "All work",
    assigned: "My work",
    attention: "Needs attention",
    done: "Completed",
} as const;

export type WorkFilter = keyof typeof filters;
export type BoardLayout = "board" | "list";

export function isWorkFilter(value: string): value is WorkFilter {
    return Object.hasOwn(filters, value);
}

export function workInFilter(work: Work, filter: WorkFilter) {
    switch (filter) {
        case "attention":
            return work.status === WorkStatus.NeedsAttention;
        case "done":
            return work.status === WorkStatus.Completed;
        case "assigned":
            return (
                !!work.agentId &&
                work.status !== WorkStatus.Completed &&
                work.status !== WorkStatus.Cancelled
            );
        default:
            return true;
    }
}

export function workFilters(work: readonly View[]) {
    return (Object.keys(filters) as WorkFilter[]).map((value) => ({
        value,
        name: filters[value],
        count: work.filter((item) => workInFilter(item.work, value)).length,
    }));
}

const lanes = [
    { id: "ready", name: "Ready", icon: "circle" },
    { id: "active", name: "In progress", icon: "progress" },
    { id: "attention", name: "Needs attention", icon: "wait" },
    { id: "closed", name: "Closed", icon: "circleCheck" },
] as const;

const laneByStatus: Record<WorkStatus, (typeof lanes)[number]["id"]> = {
    Ready: "ready",
    Queued: "active",
    InProgress: "active",
    Cancelling: "active",
    NeedsAttention: "attention",
    Completed: "closed",
    Cancelled: "closed",
};

const attentionLabels: Record<AttentionReason, string> = {
    InputRequired: "Input required",
    ResultReview: "Ready for review",
    Failure: "Execution failed",
    UncertainExecution: "Needs reconciliation",
    CleanupRequired: "Cleanup required",
    RepositoryRequired: "Repository access needed",
};

function workStatus(work: Work) {
    if (work.status === WorkStatus.NeedsAttention)
        return work.attention
            ? attentionLabels[work.attention.reason]
            : "Needs attention";
    return work.status === WorkStatus.InProgress ? "In progress" : work.status;
}

function preview(work: Work) {
    if (work.attention?.reason === AttentionReason.InputRequired)
        return work.decisions.findLast((decision) => !decision.answer)
            ?.question;
    if (
        work.attention?.reason === AttentionReason.ResultReview ||
        work.status === WorkStatus.Completed
    )
        return work.results.findLast(
            (result) =>
                result.attemptId === work.attempts.at(-1)?.id &&
                !result.requestedChanges,
        )?.text;
}

function status(item: View) {
    const { work } = item;
    const tone =
        work.attention?.reason === AttentionReason.ResultReview
            ? "review"
            : work.status === WorkStatus.Cancelled
              ? "ready"
              : laneByStatus[work.status];
    return `<span id="board-status-${e(work.id)}" class="board-status" data-tone="${tone}">${e(workStatus(work))}</span>`;
}

function updated(item: View) {
    return `<time datetime="${e(item.updatedAt)}">${e(relativeTime(item.updatedAt))}</time>`;
}

function card(item: View) {
    const { work } = item;
    const detail = preview(work)?.trim();
    const excerpt =
        detail && (detail.length > 200 ? detail.slice(0, 200) + "…" : detail);
    const id = e(work.id);
    return `<li><button class="board-card board-entry" data-action="select-work" data-id="${id}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id}${excerpt ? ` board-preview-${id}` : ""}">
        <span id="board-title-${id}" class="board-card-title" title="${e(work.objective)}">${e(work.objective)}</span>
        ${excerpt ? `<span id="board-preview-${id}" class="board-card-preview">${e(excerpt)}</span>` : ""}
        <span class="board-card-meta">${status(item)}${updated(item)}</span>
    </button></li>`;
}

function rows(items: readonly View[]) {
    return `<ul class="board-list" role="list" aria-label="Work list">${items
        .map((item) => {
            const id = e(item.work.id);
            const title = e(item.work.objective);
            return `<li><button class="board-entry board-list-item" data-action="select-work" data-id="${id}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id} board-updated-${id}">
            ${icon("work")}<span id="board-title-${id}" class="board-list-title" title="${title}">${title}</span>
            ${status(item)}<span id="board-updated-${id}"><span class="sr-only">Updated </span>${updated(item)}</span>
        </button></li>`;
        })
        .join("")}</ul>`;
}

function columns(items: readonly View[]) {
    const groups = lanes
        .map((lane) => ({
            ...lane,
            items: items.filter(
                ({ work }) => laneByStatus[work.status] === lane.id,
            ),
        }))
        .filter((lane) => lane.items.length);
    return `<div class="board-lanes" data-columns="${groups.length}">
        ${groups
            .map(
                ({
                    id,
                    name,
                    icon: symbol,
                    items,
                }) => `<section class="board-lane" data-lane="${id}" aria-labelledby="lane-${id}"><div class="board-lane-heading">
                <h2 id="lane-${id}">${icon(symbol)}${name}</h2><span class="board-lane-count">${items.length}</span>
            </div><ul>${items.map(card).join("")}</ul></section>`,
            )
            .join("")}
    </div>`;
}

const connectionLabels: Record<ConnectionAvailability, string> = {
    Available: "Connected",
    Disconnected: "Disconnected",
    Unavailable: "Unavailable",
    Changing: "Updating connection",
    Verifying: "Checking connection",
};

type BoardInput = {
    work: readonly View[];
    matches: readonly View[];
    loaded: boolean;
    workUnavailable: boolean;
    connectionsLoaded: boolean;
    agent?: Agent;
    connection?: Connection;
    filter: WorkFilter;
    layout: BoardLayout;
    search: string;
    sidebarCollapsed: boolean;
};

export function renderBoard({
    work,
    matches,
    loaded,
    workUnavailable,
    connectionsLoaded,
    agent,
    connection,
    filter,
    layout,
    search,
    sidebarCollapsed,
}: BoardInput) {
    const shown = matches.filter(({ work }) => workInFilter(work, filter));
    const next = work.find(({ work }) => workInFilter(work, "attention"))?.work;
    const connectionLabel = connection
        ? `${connection.name} · ${connectionLabels[connection.availability]}`
        : connectionsLoaded
          ? "No AI connection"
          : "Checking AI connection…";
    const count = `${shown.length} ${shown.length === 1 ? "work item" : "work items"}`;
    const empty = !loaded
        ? workUnavailable
            ? "Work unavailable"
            : "Loading work…"
        : search
          ? "No work matches your search."
          : !work.length
            ? "Create work to give Goblin a goal."
            : "No work in this view.";
    return `<section class="board-workspace" aria-labelledby="board-heading" data-scroll="board">
        <div class="board-inner">
            <div class="board-heading">
                <h1 id="board-heading" tabindex="-1">Work</h1>
                <div class="board-actions">
                    <button class="secondary coworker-ask" data-action="new-chat">${icon("chat")}Ask Goblin</button>
                    ${sidebarCollapsed ? `<button class="primary board-new" data-action="new-work">${icon("plus")}New work</button>` : ""}
                </div>
            </div>
            <section class="coworker-strip" aria-label="Goblin coworker">
                <div class="coworker-identity"><img class="coworker-mark" src="/assets/branding/icon.svg" alt="">
                    <p><strong>${e(agent?.name ?? "Goblin")}</strong><span class="coworker-connection" data-available="${connection?.availability === ConnectionAvailability.Available}">${e(connectionLabel)}</span></p>
                </div>
                ${next ? `<button id="coworker-next" class="coworker-next" data-action="select-work" data-id="${e(next.id)}" title="${e(next.objective)}"><span class="coworker-next-label">${e(workStatus(next))}</span><span class="coworker-next-title">${e(next.objective)}</span>${icon("chevron")}</button>` : ""}
            </section>
            <div class="board-toolbar" ${loaded ? "" : "hidden"}>
                <div class="board-filter"><label class="sr-only" for="board-filter">Filter work</label><select id="board-filter" class="field-control select-control">${Object.entries(
                    filters,
                )
                    .map(
                        ([value, name]) =>
                            `<option value="${value}" ${filter === value ? "selected" : ""}>${name}</option>`,
                    )
                    .join("")}</select><p class="board-count">${count}</p></div>
                <div class="board-layout" role="group" aria-label="Work layout">
                    <button data-action="board-layout" data-value="list" aria-pressed="${layout === "list"}">${icon("list")}List</button>
                    <button data-action="board-layout" data-value="board" aria-pressed="${layout === "board"}">${icon("board")}Board</button>
                </div>
            </div>
            <div class="board-content">
                ${
                    loaded && shown.length
                        ? layout === "board"
                            ? columns(shown)
                            : rows(shown)
                        : `<p class="board-empty">${empty}${loaded && search ? '<button class="text-button" data-action="clear-search">Clear search</button>' : ""}</p>`
                }
            </div>
        </div>
    </section>`;
}
