import { AttentionReason, WorkStatus } from "../api/values.js";
import type { View, Work } from "./contracts.js";
import { escapeHtml as e, icon } from "./presentation.js";
import { relativeTime } from "./surface.js";

const filters = {
    all: "All work",
    attention: "Needs attention",
    active: "In progress",
    done: "Completed",
    assigned: "My work",
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

function card(item: View, selected: string) {
    const { work } = item;
    const detail = preview(work)?.trim();
    const excerpt =
        detail && (detail.length > 200 ? detail.slice(0, 200) + "…" : detail);
    const id = e(work.id);
    return `<li><button class="board-card board-entry" data-action="select-work" data-id="${id}" aria-current="${work.id === selected}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id}${excerpt ? ` board-preview-${id}` : ""}">
        <span id="board-title-${id}" class="board-card-title" title="${e(work.objective)}">${e(work.objective)}</span>
        ${excerpt ? `<span id="board-preview-${id}" class="board-card-preview">${e(excerpt)}</span>` : ""}
        <span class="board-card-meta">${status(item)}${updated(item)}</span>
    </button></li>`;
}

function rows(items: readonly View[], selected: string) {
    return `<div class="board-table"><div class="board-table-heading" aria-hidden="true"><span>Work</span><span>Status</span><span>Updated</span></div><ul class="board-list" role="list" aria-label="Work list">${items
        .map((item) => {
            const id = e(item.work.id);
            const title = e(item.work.objective);
            return `<li><button class="board-entry board-list-item" data-action="select-work" data-id="${id}" aria-current="${item.work.id === selected}" aria-labelledby="board-title-${id}" aria-describedby="board-status-${id} board-updated-${id}">
            <span class="board-list-copy"><span class="board-list-icon">${icon("work")}</span><span class="board-list-label"><span id="board-title-${id}" class="board-list-title" title="${title}">${title}</span><span class="board-list-id">Work ${id}</span></span></span>
            ${status(item)}<span id="board-updated-${id}"><span class="sr-only">Updated </span>${updated(item)}</span>
        </button></li>`;
        })
        .join("")}</ul></div>`;
}

function columns(items: readonly View[], selected: string) {
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
            </div><ul>${items.map((item) => card(item, selected)).join("")}</ul></section>`,
            )
            .join("")}
    </div>`;
}

const summary = [
    { filter: "attention", icon: "wait" },
    { filter: "active", icon: "progress" },
    { filter: "done", icon: "circleCheck" },
] as const;

type BoardInput = {
    work: readonly View[];
    matches: readonly View[];
    loaded: boolean;
    workUnavailable: boolean;
    filter: WorkFilter;
    layout: BoardLayout;
    search: string;
    selected: string;
    sidebarCollapsed: boolean;
};

export function renderBoard({
    work,
    matches,
    loaded,
    workUnavailable,
    filter,
    layout,
    search,
    selected,
    sidebarCollapsed,
}: BoardInput) {
    const shown = matches.filter(({ work }) => workInFilter(work, filter));
    const counts = workFilters(matches);
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
                <h1 id="board-heading" tabindex="-1">Work inbox</h1>
                <div class="board-actions">
                    ${sidebarCollapsed ? `<button class="secondary" data-action="new-chat">${icon("chat")}Ask Goblin</button>` : ""}
                    <button class="primary" data-action="new-work">${icon("plus")}New work</button>
                </div>
            </div>
            <div class="board-summary" role="group" aria-label="Work status summary" ${loaded ? "" : "hidden"}>
                ${summary
                    .map(({ filter: value, icon: symbol }) => {
                        const total = counts.find(
                            (entry) => entry.value === value,
                        )!.count;
                        return `<button id="inbox-summary-${value}" class="board-summary-card" data-action="board-filter" data-value="${value}" aria-pressed="${filter === value}">
                        <span><span class="board-summary-label">${filters[value]}</span><strong>${total}</strong></span><span class="board-summary-icon">${icon(symbol)}</span>
                    </button>`;
                    })
                    .join("")}
            </div>
            <div class="board-toolbar" ${loaded ? "" : "hidden"}>
                <div class="board-list-heading"><h2>Work items</h2><p class="board-count" role="status">${count}</p></div>
                <div class="board-layout" role="group" aria-label="Work layout">
                    <button data-action="board-layout" data-value="list" aria-pressed="${layout === "list"}">${icon("list")}List</button>
                    <button data-action="board-layout" data-value="board" aria-pressed="${layout === "board"}">${icon("board")}Board</button>
                </div>
                <div class="board-filter" role="group" aria-label="Filter work">
                    ${counts.map(({ value, name, count }) => `<button id="inbox-filter-${value}" class="board-filter-option" data-action="board-filter" data-value="${value}" aria-pressed="${filter === value}">${name}<span>${count}</span></button>`).join("")}
                </div>
            </div>
            <div class="board-content">
                ${
                    loaded && shown.length
                        ? layout === "board"
                            ? columns(shown, selected)
                            : rows(shown, selected)
                        : `<p class="board-empty">${empty}${loaded && search ? '<button class="text-button" data-action="clear-search">Clear search</button>' : ""}</p>`
                }
            </div>
        </div>
    </section>`;
}
