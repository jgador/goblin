import { AttentionReason, WorkStatus } from "../api/values.js";
import type { View, Work } from "./contracts.js";
import { escapeHtml as e, icon, workTitle } from "./presentation.js";
import { relativeTime } from "./surface.js";

const lanes = [
    { id: "ready", name: "Ready", icon: "circle" },
    { id: "active", name: "In progress", icon: "progress" },
    { id: "attention", name: "Needs attention", icon: "wait" },
    { id: "closed", name: "Closed", icon: "circleCheck" },
] as const;

const listStatuses = {
    all: "All statuses",
    ready: lanes[0].name,
    active: lanes[1].name,
    attention: lanes[2].name,
    closed: lanes[3].name,
    done: WorkStatus.Completed,
    cancelled: WorkStatus.Cancelled,
} as const;

export type ListStatus = keyof typeof listStatuses;
export type BoardLayout = "board" | "list";

export function isListStatus(value: string): value is ListStatus {
    return Object.hasOwn(listStatuses, value);
}

function workInFilter(work: Work, filter: ListStatus) {
    switch (filter) {
        case "all":
            return true;
        case "done":
            return work.status === WorkStatus.Completed;
        case "cancelled":
            return work.status === WorkStatus.Cancelled;
        default:
            return laneByStatus[work.status] === filter;
    }
}

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

const attentionActions: Record<AttentionReason, string> = {
    InputRequired: "Reply",
    ResultReview: "Review",
    Failure: "Recover",
    UncertainExecution: "Reconcile",
    CleanupRequired: "Reconcile",
    RepositoryRequired: "Authorize",
};

export function workStatus(work: Work) {
    if (work.status === WorkStatus.NeedsAttention)
        return work.attention
            ? attentionLabels[work.attention.reason]
            : "Needs attention";
    return work.status === WorkStatus.InProgress
        ? listStatuses.active
        : work.status;
}

function status(item: View) {
    const { work } = item;
    const tone =
        work.attention?.reason === AttentionReason.ResultReview
            ? "review"
            : work.status === WorkStatus.Cancelled
              ? "ready"
              : laneByStatus[work.status];
    const reason = work.attention?.reason;
    return `<span id="board-status-${e(work.id)}" class="board-status" data-tone="${tone}">${reason ? `${attentionActions[reason]}<span class="sr-only"> · ${attentionLabels[reason]}</span>` : e(workStatus(work))}</span>`;
}

function updated(item: View, id: string) {
    return `<span id="board-updated-${id}" class="board-updated"><span class="sr-only">Updated </span><time datetime="${e(item.updatedAt)}">${e(relativeTime(item.updatedAt))}</time></span>`;
}

function card(item: View) {
    const { work } = item;
    const id = e(work.id);
    return `<li><button class="board-card board-entry" data-action="select-work" data-id="${id}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id} board-updated-${id}">
        <span class="board-work-id">Work ${id}</span>
        <span id="board-title-${id}" class="board-card-title">${e(workTitle(work))}</span>
        <span class="board-card-meta">${status(item)}${updated(item, id)}</span>
    </button></li>`;
}

function rows(items: readonly View[]) {
    return `<div class="board-table"><div class="board-table-heading" aria-hidden="true"><span>Work</span><span>Status</span><span>Updated</span></div><ul class="board-list" role="list" aria-label="Work list">${items
        .map((item) => {
            const id = e(item.work.id);
            const title = e(workTitle(item.work));
            return `<li><button class="board-entry board-list-item" data-action="select-work" data-id="${id}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id} board-updated-${id}">
            <span class="board-list-copy"><span class="board-list-icon">${icon("work")}</span><span class="board-list-label"><span id="board-title-${id}" class="board-list-title">${title}</span><span class="board-work-id">Work ${id}</span></span></span>
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
};

export function renderBoard({
    total,
    matches,
    loaded,
    workUnavailable,
    listStatus,
    layout,
    search,
}: BoardInput) {
    const shown =
        layout === "list" && listStatus !== "all"
            ? matches.filter(({ work }) => workInFilter(work, listStatus))
            : matches;
    const attentionCount = matches.filter(({ work }) =>
        workInFilter(work, "attention"),
    ).length;
    const count =
        shown.length === total
            ? `${total} ${total === 1 ? "work item" : "work items"}`
            : `${shown.length} of ${total} ${total === 1 ? "work item" : "work items"}`;
    const empty = !loaded
        ? workUnavailable
            ? "Work unavailable"
            : "Loading work…"
        : search.trim() && !matches.length
          ? "No work matches your search."
          : !total
            ? "Create work to give Goblin a goal."
            : "No work matches this status.";
    return `<section class="board-workspace" aria-labelledby="board-heading" data-scroll="board">
        <div class="board-inner">
            <h1 id="board-heading" class="board-heading" tabindex="-1">Work inbox</h1>
            <div class="board-tools">
                <label class="inbox-search">${icon("search")}<span class="sr-only">Filter inbox</span><input id="inbox-search" type="search" placeholder="Filter inbox" value="${e(search)}" autocomplete="off"></label>
                <div class="board-attention">${attentionCount && !(layout === "list" && listStatus === "attention") ? `<button class="secondary" data-action="filter-attention">Needs attention <span>${attentionCount}</span></button>` : ""}</div>
                <p class="board-result-count" role="status"><span class="board-count" ${loaded ? "" : "hidden"}>${count}</span></p>
            </div>
            <div class="board-content">
                ${
                    loaded && shown.length
                        ? layout === "board"
                            ? columns(shown)
                            : rows(shown)
                        : `<div class="board-empty"><p role="status">${empty}</p>${loaded && search.trim() ? '<button class="text-button" data-action="clear-inbox-search">Clear filter</button>' : ""}</div>`
                }
            </div>
        </div>
    </section>`;
}
