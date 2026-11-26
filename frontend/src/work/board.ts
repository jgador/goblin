import { AttentionReason, WorkStatus } from "../api/values.js";
import type { View, Work } from "./contracts.js";
import { escapeHtml as e, icon } from "./presentation.js";
import { relativeTime } from "./surface.js";

const listStatuses = {
    all: "All statuses",
    attention: "Needs attention",
    active: "In progress",
    done: "Completed",
} as const;

const filters = {
    ...listStatuses,
    all: "All work",
    assigned: "My work",
} as const;

export type WorkFilter = keyof typeof filters;
export type ListStatus = keyof typeof listStatuses;
export type BoardLayout = "board" | "list";

export function isWorkFilter(value: string): value is WorkFilter {
    return Object.hasOwn(filters, value);
}

export function isListStatus(value: string): value is ListStatus {
    return Object.hasOwn(listStatuses, value);
}

export function workInFilter(work: Work, filter: WorkFilter) {
    switch (filter) {
        case "attention":
            return work.status === WorkStatus.NeedsAttention;
        case "active":
            return laneByStatus[work.status] === "active";
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

export function workStatus(work: Work) {
    if (work.status === WorkStatus.NeedsAttention)
        return work.attention
            ? attentionLabels[work.attention.reason]
            : "Needs attention";
    return work.status === WorkStatus.InProgress ? "In progress" : work.status;
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

function updated(item: View, id: string) {
    return `<span id="board-updated-${id}" class="board-updated"><span class="sr-only">Updated </span><time datetime="${e(item.updatedAt)}">${e(relativeTime(item.updatedAt))}</time></span>`;
}

function card(item: View) {
    const { work } = item;
    const id = e(work.id);
    return `<li><button class="board-card board-entry" data-action="select-work" data-id="${id}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id} board-updated-${id}">
        <span class="board-work-id">Work ${id}</span>
        <span id="board-title-${id}" class="board-card-title" title="${e(work.objective)}">${e(work.objective)}</span>
        <span class="board-card-meta">${status(item)}${updated(item, id)}</span>
    </button></li>`;
}

function rows(items: readonly View[]) {
    return `<div class="board-table"><div class="board-table-heading" aria-hidden="true"><span>Work</span><span>Status</span><span>Updated</span></div><ul class="board-list" role="list" aria-label="Work list">${items
        .map((item) => {
            const id = e(item.work.id);
            const title = e(item.work.objective);
            return `<li><button class="board-entry board-list-item" data-action="select-work" data-id="${id}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id} board-updated-${id}">
            <span class="board-list-copy"><span class="board-list-icon">${icon("work")}</span><span class="board-list-label"><span id="board-title-${id}" class="board-list-title" title="${title}">${title}</span><span class="board-work-id">Work ${id}</span></span></span>
            ${status(item)}${updated(item, id)}<span class="board-list-chevron">${icon("chevron")}</span>
        </button></li>`;
        })
        .join("")}</ul></div>`;
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
            </div><ul role="list">${items.map(card).join("")}</ul></section>`,
            )
            .join("")}
    </div>`;
}

export function renderInboxControls(
    layout: BoardLayout,
    listStatus: ListStatus,
) {
    return `<div class="inbox-controls">
        ${
            layout === "list"
                ? `<div class="inbox-status-control"><label for="inbox-status">Status</label><select id="inbox-status" class="field-control select-control">${Object.entries(
                      listStatuses,
                  )
                      .map(
                          ([value, label]) =>
                              `<option value="${value}" ${listStatus === value ? "selected" : ""}>${label}</option>`,
                      )
                      .join("")}</select></div>`
                : ""
        }
        <div class="board-layout" role="group" aria-label="Work layout">
            <button data-action="board-layout" data-value="board" aria-pressed="${layout === "board"}">${icon("board")}Board</button>
            <button data-action="board-layout" data-value="list" aria-pressed="${layout === "list"}">${icon("list")}List</button>
        </div>
    </div>`;
}

type BoardInput = {
    total: number;
    matches: readonly View[];
    loaded: boolean;
    workUnavailable: boolean;
    listStatus: ListStatus;
    layout: BoardLayout;
    search: string;
    sidebarCollapsed: boolean;
};

export function renderBoard({
    total,
    matches,
    loaded,
    workUnavailable,
    listStatus,
    layout,
    search,
    sidebarCollapsed,
}: BoardInput) {
    const shown =
        layout === "list" && listStatus !== "all"
            ? matches.filter(({ work }) => workInFilter(work, listStatus))
            : matches;
    const count =
        shown.length === total
            ? `${total} ${total === 1 ? "work item" : "work items"}`
            : `${shown.length} of ${total} ${total === 1 ? "work item" : "work items"}`;
    const empty = !loaded
        ? workUnavailable
            ? "Work unavailable"
            : "Loading work…"
        : search && !matches.length
          ? "No work matches your search."
          : !total
            ? "Create work to give Goblin a goal."
            : "No work matches this status.";
    return `<section class="board-workspace" aria-labelledby="board-heading" data-scroll="board">
        <div class="board-inner">
            <div class="board-heading">
                <h1 id="board-heading" tabindex="-1">Work inbox</h1>
                ${
                    sidebarCollapsed
                        ? `<div class="board-actions">
                    <button class="secondary" data-action="new-chat">${icon("chat")}Ask Goblin</button>
                    <button class="primary" data-action="new-work">${icon("plus")}New work</button>
                </div>`
                        : ""
                }
            </div>
            <p class="board-result-count" role="status"><span class="board-count" ${loaded ? "" : "hidden"}>${count}</span></p>
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
