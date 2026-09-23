import { escapeHtml as e, icon } from "./presentation.js";
import type { Work } from "./contracts.js";

type Checkpoint = {
    id: string;
    attemptId: string;
    turnNumber: number;
    branch: string;
    commitSha: string;
    createdAt: string;
};
type Session = {
    id: string;
    attemptId: string;
    checkpointId?: string;
    state: string;
};
type WorkspaceData = {
    checkpoints: Checkpoint[];
    sessions: Session[];
    terminalAvailable: boolean;
};

export class WorkWorkspace {
    private readonly dialog = document.createElement("dialog");
    private work?: Work;
    private data: WorkspaceData = {
        checkpoints: [],
        sessions: [],
        terminalAvailable: false,
    };
    private selected = "";
    private socket?: WebSocket;
    private timer?: ReturnType<typeof setInterval>;
    private generation = 0;
    private opener?: HTMLElement;
    private pending?: { id: string; attemptId: string; checkpointId?: string };
    private opening = false;
    constructor() {
        this.dialog.className = "settings-dialog work-workspace";
        this.dialog.setAttribute("aria-labelledby", "workspace-title");
        document.body.append(this.dialog);
        this.dialog.addEventListener("close", () => {
            this.generation++;
            clearInterval(this.timer);
            this.socket?.close();
            const replacement = document.querySelector<HTMLElement>(
                '[data-action="open-workspace"]',
            );
            (this.opener?.isConnected ? this.opener : replacement)?.focus();
        });
        this.dialog.addEventListener("click", (event) => {
            const button = (event.target as Element).closest<HTMLElement>(
                "[data-workspace]",
            );
            if (button)
                void this.action(
                    button.dataset.workspace!,
                    button.dataset.path,
                );
        });
        this.dialog.addEventListener("change", (event) => {
            if ((event.target as HTMLElement).id === "workspace-checkpoint") {
                this.selected = (event.target as HTMLSelectElement).value;
                void this.loadFiles();
            }
        });
        this.dialog.addEventListener("submit", (event) => {
            if ((event.target as HTMLElement).id !== "workspace-terminal-form")
                return;
            event.preventDefault();
            const input =
                this.dialog.querySelector<HTMLInputElement>(
                    "#workspace-command",
                )!;
            if (this.socket?.readyState === WebSocket.OPEN) {
                this.socket.send(input.value + "\n");
                input.value = "";
            }
        });
    }
    reset() {
        this.dialog.close();
        this.work = undefined;
        this.pending = undefined;
        this.dialog.replaceChildren();
    }
    async open(work: Work) {
        this.opener = document.activeElement as HTMLElement;
        this.work = work;
        this.selected = "";
        this.pending = undefined;
        this.generation++;
        this.dialog.innerHTML = `<header class="settings-header"><div><h2 id="workspace-title">Workspace</h2><p>Work ${e(work.id)} · ${e(work.objective)}</p></div><button class="icon-button" data-workspace="close" aria-label="Close workspace">${icon("close")}</button></header>
            <div class="workspace-content"><p class="workspace-error" role="alert"></p><div class="workspace-toolbar"></div>
            <div class="workspace-files"><nav class="workspace-file-list" aria-label="Workspace files"></nav><section class="workspace-preview" aria-label="File preview"><h3>Saved files</h3><p>Open a checkpoint to inspect its files and changes.</p><pre tabindex="0"></pre></section></div>
            <section class="workspace-inspection" aria-label="Inspection session"><div class="workspace-session"></div><p>Terminal access uses a read-only copy. Closing this panel disconnects the terminal; use Stop workspace to release its compute.</p>
            <pre class="workspace-terminal" role="log" aria-label="Terminal output" tabindex="0"></pre><form id="workspace-terminal-form"><label for="workspace-command">Terminal command</label><div><input id="workspace-command" class="field-control" autocomplete="off" spellcheck="false" disabled><button class="secondary" type="submit" disabled>Run</button><button class="secondary" type="button" data-workspace="interrupt" disabled>Interrupt</button></div></form></section></div>`;
        this.dialog.showModal();
        await this.refresh();
        this.timer = setInterval(() => void this.refresh(), 3000);
    }
    private async request<T>(path: string, body?: unknown): Promise<T> {
        const response = await fetch(
            path.startsWith("/api/")
                ? path
                : `/api/work/${this.work!.id}/workspace${path}`,
            body === undefined
                ? { cache: "no-store" }
                : {
                      method: "POST",
                      headers: { "Content-Type": "application/json" },
                      body: JSON.stringify(body),
                  },
        );
        if (response.status === 401) {
            this.reset();
            window.dispatchEvent(new Event("goblin-workspace-locked"));
            throw new Error("Unlock Goblin to inspect this workspace.");
        }
        const result =
            response.status === 204 ? undefined : await response.json();
        if (!response.ok)
            throw new Error(
                result?.error?.message ??
                    "The workspace request could not be confirmed.",
            );
        return result as T;
    }
    private error(value: unknown) {
        const target = this.dialog.querySelector(".workspace-error");
        if (target)
            target.textContent =
                value instanceof Error ? value.message : String(value);
    }
    private async refresh() {
        const generation = this.generation;
        try {
            const data = await this.request<WorkspaceData>("");
            if (generation !== this.generation || !this.dialog.open) return;
            const changed =
                JSON.stringify(data.checkpoints) !==
                JSON.stringify(this.data.checkpoints);
            this.data = data;
            if (!this.selected && data.checkpoints.length)
                this.selected = data.checkpoints[0]!.id;
            const toolbar = this.dialog.querySelector(".workspace-toolbar")!;
            if (!toolbar.children.length || changed) {
                toolbar.innerHTML = data.checkpoints.length
                    ? `<label for="workspace-checkpoint">Saved checkpoint</label><select id="workspace-checkpoint">${data.checkpoints.map((c) => `<option value="${e(c.id)}" ${c.id === this.selected ? "selected" : ""}>Attempt ${e(c.attemptId)} · Turn ${c.turnNumber} · ${e(c.commitSha.slice(0, 8))}</option>`).join("")}</select><button class="secondary" data-workspace="diff">View changes</button><a class="text-button workspace-download" href="/api/work/${e(this.work!.id)}/workspace/${e(this.selected)}/download">Download files</a>`
                    : `<p>No verified checkpoint yet. A retained workspace can still be inspected after execution stops.</p>`;
                if (this.selected) await this.loadFiles();
            }
            const session = data.sessions.find((s) =>
                [
                    "Queued",
                    "Starting",
                    "Available",
                    "Stopping",
                    "NeedsAttention",
                ].includes(s.state),
            );
            const area = this.dialog.querySelector(".workspace-session")!;
            const html = session
                ? `<strong>${e(({ Queued: "Waiting for capacity", Starting: "Restoring workspace…", Available: "Workspace available", Stopping: "Stopping workspace…", NeedsAttention: "Inspection needs attention" } as Record<string, string>)[session.state] ?? session.state)}</strong>${session.state === "Available" ? '<button class="secondary" data-workspace="connect">Connect terminal</button>' : ""}<button class="secondary" data-workspace="stop" ${session.state === "Stopping" ? "disabled" : ""}>Stop workspace</button>`
                : `<button class="secondary" data-workspace="start" ${data.terminalAvailable ? "" : "disabled"}>${this.pending ? "Resend open request" : "Start inspection"}</button>${!data.terminalAvailable ? "<span>Terminal access requires a Kubernetes execution host.</span>" : ""}`;
            if (area.innerHTML !== html) area.innerHTML = html;
            if (session && session.state !== "Available") this.socket?.close();
        } catch (error) {
            if (generation === this.generation) this.error(error);
        }
    }
    private async loadFiles() {
        const generation = this.generation,
            checkpoint = this.selected;
        try {
            const result = await this.request<{
                files: { path: string; size: number }[];
                truncated: boolean;
            }>(`/${checkpoint}/files`);
            if (generation !== this.generation || checkpoint !== this.selected)
                return;
            this.dialog.querySelector(".workspace-file-list")!.innerHTML =
                result.files
                    .map(
                        (file) =>
                            `<button class="text-button" data-workspace="file" data-path="${e(file.path)}">${e(file.path.replace(/^repository\//, ""))}</button>`,
                    )
                    .join("") +
                (result.truncated
                    ? "<p>Listing limited. Download the checkpoint for all files.</p>"
                    : "");
            const download = this.dialog.querySelector<HTMLAnchorElement>(
                ".workspace-download",
            );
            if (download)
                download.href = `/api/work/${this.work!.id}/workspace/${checkpoint}/download`;
        } catch (error) {
            this.error(error);
        }
    }
    private async action(action: string, path?: string) {
        if (action === "close") {
            this.dialog.close();
            return;
        }
        try {
            this.error("");
            if (action === "file" || action === "diff") {
                const checkpoint = this.selected,
                    generation = this.generation;
                const file = await this.request<{ path: string; text: string }>(
                    `/${checkpoint}/files?path=${encodeURIComponent(action === "diff" ? ".goblin/changes.patch" : path!)}`,
                );
                if (
                    generation !== this.generation ||
                    checkpoint !== this.selected
                )
                    return;
                this.dialog.querySelector(
                    ".workspace-preview h3",
                )!.textContent = file.path;
                this.dialog.querySelector(".workspace-preview p")!.textContent =
                    "Saved checkpoint · Read only";
                this.dialog.querySelector(
                    ".workspace-preview pre",
                )!.textContent = file.text || "No changes in this checkpoint.";
            }
            const session = this.data.sessions.find((s) =>
                [
                    "Queued",
                    "Starting",
                    "Available",
                    "Stopping",
                    "NeedsAttention",
                ].includes(s.state),
            );
            if (action === "start") {
                if (this.opening) return;
                this.opening = true;
                const generation = this.generation;
                try {
                    const checkpoint = this.data.checkpoints.find(
                        (c) => c.id === this.selected,
                    );
                    const attemptId =
                        checkpoint?.attemptId ??
                        this.work!.attempts.filter(
                            (a) => a.target.repository,
                        ).at(-1)?.id;
                    if (!attemptId)
                        throw new Error(
                            "This Work has no repository workspace.",
                        );
                    if (!this.pending) {
                        const { ids } = await this.request<{ ids: string[] }>(
                            "/api/identities",
                            { kinds: ["Inspection"] },
                        );
                        if (generation !== this.generation || !this.dialog.open)
                            return;
                        this.pending = {
                            id: ids[0]!,
                            attemptId,
                            checkpointId: checkpoint?.id,
                        };
                    }
                    await this.request("/sessions", this.pending);
                    if (generation !== this.generation || !this.dialog.open)
                        return;
                    this.pending = undefined;
                    await this.refresh();
                } finally {
                    this.opening = false;
                }
            }
            if (action === "stop" && session) {
                this.socket?.close();
                await this.request(`/sessions/${session.id}/stop`, {});
                await this.refresh();
            }
            if (action === "connect" && session?.state === "Available")
                this.connect(session.id);
            if (
                action === "interrupt" &&
                this.socket?.readyState === WebSocket.OPEN
            )
                this.socket.send("\u0003");
        } catch (error) {
            this.error(error);
        }
    }
    private connect(id: string) {
        this.socket?.close();
        const url = new URL(
            `/api/work/${this.work!.id}/workspace/sessions/${id}/terminal`,
            location.href,
        );
        url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        const socket = (this.socket = new WebSocket(url));
        socket.binaryType = "arraybuffer";
        const decoder = new TextDecoder();
        const output = this.dialog.querySelector<HTMLElement>(
            ".workspace-terminal",
        )!;
        const controls = this.dialog.querySelectorAll<
            HTMLInputElement | HTMLButtonElement
        >("#workspace-terminal-form input, #workspace-terminal-form button");
        socket.onopen = () => {
            if (this.socket !== socket) return;
            controls.forEach((c) => (c.disabled = false));
            this.dialog
                .querySelector<HTMLInputElement>("#workspace-command")!
                .focus();
        };
        socket.onmessage = (event) => {
            if (this.socket !== socket) return;
            const text = (
                typeof event.data === "string"
                    ? event.data
                    : decoder.decode(event.data as ArrayBuffer, {
                          stream: true,
                      })
            )
                .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, "")
                .replace(/\r/g, "");
            output.textContent = ((output.textContent ?? "") + text).slice(
                -100000,
            );
            output.scrollTop = output.scrollHeight;
        };
        socket.onclose = () => {
            if (this.socket !== socket) return;
            controls.forEach((c) => (c.disabled = true));
            output.textContent +=
                "\nTerminal disconnected. The inspection session remains available until stopped.\n";
        };
        socket.onerror = () =>
            this.error(
                "The terminal connection failed. Refresh the session and reconnect.",
            );
    }
}
