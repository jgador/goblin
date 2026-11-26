import { ApiError, errorMessage } from "../api/client.js";
import { hunkRange, parseDiff, type DiffFile } from "./diff.js";
import { escapeHtml as e, icon } from "./presentation.js";

type SavedPatch = {
    checkpoint: { branch: string; commitSha: string };
    baseCommitSha: string;
    patch: string;
};

type DiffPosition = { offset: number; oldLine: number; newLine: number };
const linesPerPage = 400;
const initialLineBudget = 8_000;
const number = new Intl.NumberFormat();

export class WorkChangesPanel {
    readonly element = document.createElement("section");
    private readonly meta: HTMLElement;
    private readonly toolbar: HTMLElement;
    private readonly body: HTMLElement;
    private readonly positions = new WeakMap<
        HTMLDetailsElement,
        DiffPosition
    >();
    private selected?: { workId: string };
    private files: DiffFile[] = [];
    private fileIndex = 0;
    private byFile = false;

    constructor(private readonly request: <T>(path: string) => Promise<T>) {
        this.element.className = "work-changes";
        this.element.id = "work-changes";
        this.element.setAttribute("aria-labelledby", "work-changes-title");
        this.element.innerHTML = `
            <header class="work-changes-header">
                <div class="work-changes-heading">
                    <h2 id="work-changes-title" tabindex="-1">Changes</h2>
                    <div class="work-changes-meta"></div>
                </div>
            </header>
            <div class="work-changes-toolbar" hidden>
                <div class="work-changes-modes" role="group" aria-label="Diff view">
                    <button data-view="all" aria-pressed="true">All files</button>
                    <button data-view="file" aria-pressed="false">By file</button>
                </div>
                <div class="work-changes-totals"></div>
            </div>
            <div class="work-changes-body"></div>`;
        this.meta = this.element.querySelector(".work-changes-meta")!;
        this.toolbar = this.element.querySelector(".work-changes-toolbar")!;
        this.body = this.element.querySelector(".work-changes-body")!;

        this.element.addEventListener("click", (event) => {
            if (!(event.target instanceof Element)) return;
            const button = event.target.closest<HTMLButtonElement>("button");
            if (!button) return;
            if (button.dataset.view) {
                const byFile = button.dataset.view === "file";
                if (this.byFile !== byFile) {
                    this.byFile = byFile;
                    this.renderFiles();
                }
            } else if (button.dataset.file !== undefined) {
                this.selectFile(Number(button.dataset.file));
            } else if (button.hasAttribute("data-more-lines")) {
                this.showLines(
                    button.closest<HTMLDetailsElement>(".diff-file")!,
                );
            }
        });
        this.body.addEventListener("change", (event) => {
            if (event.target instanceof HTMLSelectElement)
                this.selectFile(Number(event.target.value));
        });
        this.body.addEventListener(
            "toggle",
            (event) => {
                const file = event.target;
                if (
                    file instanceof HTMLDetailsElement &&
                    file.open &&
                    !this.positions.has(file)
                )
                    this.showLines(file);
            },
            true,
        );
    }

    get isOpen() {
        return this.selected !== undefined;
    }

    focus() {
        this.element.querySelector<HTMLElement>("#work-changes-title")?.focus();
    }

    async open(workId: string) {
        const selection = { workId };
        this.selected = selection;
        this.toolbar.hidden = true;
        this.meta.replaceChildren();
        this.status("Loading changes…");
        try {
            const saved = await this.request<SavedPatch>(
                `/api/work/${encodeURIComponent(workId)}/changes`,
            );
            if (this.selected !== selection) return;
            const { checkpoint, baseCommitSha, patch } = saved;
            this.meta.innerHTML = `${icon("branch")}<code>${e(checkpoint.branch)}</code><code title="${e(baseCommitSha)} → ${e(checkpoint.commitSha)}">${e(baseCommitSha.slice(0, 7))} → ${e(checkpoint.commitSha.slice(0, 7))}</code>`;
            this.files = parseDiff(patch);
            if (!this.files.length) {
                this.status(
                    patch
                        ? "Could not display the saved diff."
                        : "No changes at this checkpoint.",
                );
                return;
            }
            this.fileIndex = 0;
            this.byFile = false;
            const totals = { added: 0, removed: 0 };
            for (const file of this.files) {
                totals.added += file.added;
                totals.removed += file.removed;
            }
            this.toolbar.querySelector(".work-changes-totals")!.innerHTML =
                `<span>${number.format(this.files.length)} ${this.files.length === 1 ? "file" : "files"} changed</span>${counts(totals)}`;
            this.toolbar.hidden = false;
            this.renderFiles();
        } catch (failure) {
            if (this.selected !== selection) return;
            const message =
                failure instanceof ApiError && failure.status === 404
                    ? "No changes have been saved for review."
                    : errorMessage(failure, "Could not load changes.");
            this.status(message);
        }
    }

    retain(workId?: string) {
        if (this.selected && this.selected.workId !== workId) this.close();
    }

    close() {
        this.selected = undefined;
        this.files = [];
        this.meta.replaceChildren();
        this.toolbar.hidden = true;
        this.body.replaceChildren();
    }

    private status(message: string) {
        this.body.innerHTML = `<p class="work-changes-status" role="status">${e(message)}</p>`;
    }

    private renderFiles() {
        this.toolbar
            .querySelector('[data-view="all"]')!
            .setAttribute("aria-pressed", String(!this.byFile));
        this.toolbar
            .querySelector('[data-view="file"]')!
            .setAttribute("aria-pressed", String(this.byFile));
        if (this.byFile) {
            const entries = this.files
                .map(({ path }, index) => ({ path, index }))
                .sort((a, b) => a.path.localeCompare(b.path));
            this.body.innerHTML = `
                <div class="diff-by-file">
                    <nav class="diff-file-nav" aria-label="Changed files">
                        <ul>${entries.map(({ path, index }) => fileButton(path, index)).join("")}</ul>
                    </nav>
                    <label class="diff-file-picker"><span>Changed file</span>
                        <select class="field-control">${entries
                            .map(
                                ({ path, index }) =>
                                    `<option value="${index}">${e(path)}</option>`,
                            )
                            .join("")}</select>
                    </label>
                    <div class="diff-files"></div>
                </div>`;
            this.selectFile(this.fileIndex);
        } else {
            let remaining = initialLineBudget;
            this.body.innerHTML = `<div class="diff-files">${this.files
                .map((file, index) => {
                    const open = remaining > 0;
                    remaining -= Math.max(
                        1,
                        Math.min(file.lines.length, linesPerPage),
                    );
                    return fileCard(file, index, open);
                })
                .join("")}</div>`;
            for (const file of this.body.querySelectorAll<HTMLDetailsElement>(
                ".diff-file[open]",
            ))
                this.showLines(file);
        }
    }

    private selectFile(index: number) {
        const selected = this.body.querySelector<HTMLElement>(".diff-files")!;
        if (selected.querySelector(`[data-diff-file="${index}"]`)) return;
        this.fileIndex = index;
        this.body
            .querySelector("[aria-current]")
            ?.removeAttribute("aria-current");
        this.body
            .querySelector(`[data-file="${index}"]`)!
            .setAttribute("aria-current", "true");
        this.body.querySelector("select")!.value = String(index);
        selected.innerHTML = fileCard(this.files[index], index, true);
        selected.scrollTop = 0;
        this.showLines(
            selected.querySelector<HTMLDetailsElement>(".diff-file")!,
        );
    }

    private showLines(card: HTMLDetailsElement) {
        const file = this.files[Number(card.dataset.diffFile)];
        const body = card.querySelector<HTMLElement>(".diff-file-body")!;
        let position = this.positions.get(card);
        if (!position) {
            position = { offset: 0, oldLine: 0, newLine: 0 };
            this.positions.set(card, position);
            if (!file.lines.length) {
                if (!file.status)
                    body.innerHTML =
                        '<p class="diff-note">No textual changes.</p>';
                return;
            }
            body.innerHTML = `<div class="diff-scroll" role="region" aria-label="Diff for ${e(file.path)}" tabindex="0"><pre></pre></div>`;
        }
        const pre = body.querySelector("pre")!;
        const previous = pre.lastElementChild;
        const end = Math.min(position.offset + linesPerPage, file.lines.length);
        pre.insertAdjacentHTML(
            "beforeend",
            file.lines
                .slice(position.offset, end)
                .map((line) => renderLine(line, position))
                .join("\n") + "\n",
        );
        position.offset = end;
        const more = body.querySelector<HTMLButtonElement>("[data-more-lines]");
        if (end < file.lines.length) {
            const label = `Show next ${number.format(Math.min(linesPerPage, file.lines.length - end))} lines`;
            if (more) more.textContent = label;
            else
                body.insertAdjacentHTML(
                    "beforeend",
                    `<button class="text-button diff-more" data-more-lines>${label}</button>`,
                );
        } else more?.remove();
        if (previous) {
            const next = previous.nextElementSibling as HTMLElement;
            next.tabIndex = -1;
            next.focus({ preventScroll: true });
        }
    }
}

function counts({ added, removed }: { added: number; removed: number }) {
    return `<span class="diff-counts"><span class="diff-additions" aria-hidden="true">+${number.format(added)}</span><span class="diff-deletions" aria-hidden="true">−${number.format(removed)}</span><span class="sr-only">${number.format(added)} added, ${number.format(removed)} removed</span></span>`;
}

function fileButton(path: string, index: number) {
    const slash = path.lastIndexOf("/");
    return `<li><button data-file="${index}" title="${e(path)}">${icon("file")}<span>
        <span class="diff-filename">${e(path.slice(slash + 1))}</span>
        ${slash > 0 ? `<span class="diff-directory">${e(path.slice(0, slash))}</span>` : ""}
    </span></button></li>`;
}

function fileCard(file: DiffFile, index: number, open: boolean) {
    const path = e(file.path);
    return `<details class="diff-file" data-diff-file="${index}" ${open ? "open" : ""}>
        <summary>
            ${icon("chevron")}<code title="${path}">${path}</code>
            ${file.status ? `<span class="diff-file-status">${file.status}</span>` : ""}
            ${file.added || file.removed ? counts(file) : ""}
        </summary>
        ${file.previousPath ? `<p class="diff-note">From <code>${e(file.previousPath)}</code></p>` : ""}
        <div class="diff-file-body"></div>
    </details>`;
}

function renderLine(line: string, position: DiffPosition) {
    const hunk = hunkRange.exec(line);
    if (hunk) {
        position.oldLine = Number(hunk[1]);
        position.newLine = Number(hunk[2]);
        return `<span class="diff-line diff-hunk">${e(line)}</span>`;
    }
    const marker = line[0];
    const kind = marker === "+" ? "added" : marker === "-" ? "removed" : "";
    const oldLine = marker === "-" || marker === " " ? position.oldLine++ : "";
    const newLine = marker === "+" || marker === " " ? position.newLine++ : "";
    if (oldLine === "" && newLine === "")
        return `<span class="diff-line diff-note-line">${e(line) || " "}</span>`;
    return `<span class="diff-line ${kind ? "diff-" + kind : ""}"><span class="diff-line-number" aria-hidden="true">${oldLine}</span><span class="diff-line-number" aria-hidden="true">${newLine}</span><span class="diff-code">${e(line)}</span></span>`;
}
