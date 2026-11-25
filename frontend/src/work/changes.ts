import { ApiError, errorMessage } from "../api/client.js";
import { escapeHtml as e, icon } from "./presentation.js";

type SavedPatch = {
    checkpoint: {
        id: string;
        workId: string;
        branch: string;
        commitSha: string;
    };
    baseCommitSha: string;
    patch: string;
};

export class WorkChangesDialog {
    private readonly dialog = document.createElement("dialog");
    private readonly content: HTMLElement;
    private selected?: { workId: string };

    constructor(private readonly request: <T>(path: string) => Promise<T>) {
        this.dialog.className = "work-changes-dialog";
        this.dialog.setAttribute("aria-labelledby", "work-changes-title");
        this.dialog.innerHTML = `<header><h2 id="work-changes-title">Changes</h2><form method="dialog"><button class="icon-button" aria-label="Close changes" autofocus>${icon("close")}</button></form></header><div class="work-changes-content"></div>`;
        this.content = this.dialog.querySelector(".work-changes-content")!;
        this.dialog.addEventListener("close", () => {
            if (!this.dialog.open) this.clear();
        });
        document.body.append(this.dialog);
    }

    async open(workId: string) {
        const selection = { workId };
        this.selected = selection;
        this.content.innerHTML = '<p role="status">Loading changes…</p>';
        if (!this.dialog.open) this.dialog.showModal();
        const url = `/api/work/${encodeURIComponent(workId)}/changes`;
        try {
            const saved = await this.request<SavedPatch>(url);
            if (this.selected !== selection || !this.dialog.open) return;
            this.content.innerHTML = review(saved, url);
        } catch (failure) {
            if (this.selected !== selection || !this.dialog.open) return;
            const message =
                failure instanceof ApiError && failure.status === 404
                    ? "No changes have been saved for review."
                    : errorMessage(failure, "Could not load changes.");
            this.content.innerHTML = `<p role="status">${e(message)}</p>`;
        }
    }

    retain(workId?: string) {
        if (this.selected && this.selected.workId !== workId) {
            if (this.dialog.open) this.dialog.close();
            this.clear();
        }
    }

    private clear() {
        this.selected = undefined;
        this.content.replaceChildren();
    }
}

function review(saved: SavedPatch, url: string) {
    const { checkpoint, baseCommitSha, patch } = saved;
    const branch = `<div class="work-changes-branch"><code>${e(checkpoint.branch)}</code><span title="${e(baseCommitSha)} → ${e(checkpoint.commitSha)}">${e(baseCommitSha.slice(0, 7))} → ${e(checkpoint.commitSha.slice(0, 7))}</span></div>`;
    if (!patch)
        return `${branch}<p role="status">No changes at this checkpoint.</p>`;

    const previewLimit = 160_000;
    const diff = patch.search(/^diff --git /m);
    const content = diff < 0 ? patch : patch.slice(diff);
    const preview = content.slice(0, previewLimit);
    const lines = preview.split("\n").map((line) => {
        const kind =
            line.startsWith("diff --git ") ||
            line.startsWith("+++ ") ||
            line.startsWith("--- ")
                ? "file"
                : line.startsWith("+")
                  ? "added"
                  : line.startsWith("-")
                    ? "removed"
                    : line.startsWith("@@")
                      ? "hunk"
                      : "context";
        return `<span class="patch-${kind}">${e(line) || " "}</span>`;
    });
    const filename = `work-${checkpoint.workId}-${checkpoint.id}.patch`;
    return `<div class="work-changes-toolbar">${branch}<a class="secondary" href="${e(url)}/${encodeURIComponent(checkpoint.id)}.patch" download="${e(filename)}">Download patch</a></div><pre aria-label="Git changes">${lines.join("\n")}</pre>${content.length > previewLimit ? "<p>Download the patch to see the full diff.</p>" : ""}`;
}
