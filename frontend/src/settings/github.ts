import { escapeHtml as e } from "../work/presentation.js";
export type GitHubState = {
    configured: boolean;
    login?: string;
    userCode?: string;
    verificationUrl?: string;
    notice?: string;
    status: string;
};
export type Repository = {
    id: string;
    name: string;
    defaultBranch: string;
    enabled?: boolean;
    canPush?: boolean;
};
async function api<T>(path: string, body?: unknown): Promise<T> {
    const response = await fetch(path, {
        method: body === undefined ? "GET" : "POST",
        credentials: "same-origin",
        headers:
            body === undefined ? {} : { "Content-Type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    const value = await response.json();
    if (!response.ok)
        throw new Error(
            value.error?.message ?? "Could not reach Goblin. Try again.",
        );
    return value;
}
export function mountGitHub(root: HTMLElement) {
    let state: GitHubState | null = null,
        busy = false,
        busyAction = "",
        error = "",
        page = 1;
    let enabled: Repository[] = [],
        available: Repository[] = [],
        browsing = false,
        more = false;
    let generation = 0,
        disposed = false;
    let focusedAction = "",
        focusedRepository = "";
    function render() {
        if (disposed) return;
        const active = root.contains(document.activeElement)
            ? (document.activeElement as HTMLElement)
            : null;
        if (active?.dataset.github) {
            focusedAction = active.dataset.github;
            focusedRepository = active.dataset.repository ?? "";
        }
        root.innerHTML = `<h3>GitHub</h3><p class="settings-description">Choose the account and repositories Goblin can use.</p>
          <div class="settings-account" role="status">${e(state?.login ? `@${state.login} · ${state.status}` : (state?.status ?? "Loading connection…"))}</div>
          ${error || state?.notice ? `<p class="settings-notice" role="${error ? "alert" : "status"}">${e(error || state?.notice)}</p>` : ""}
          ${
              !state
                  ? '<p class="settings-description" role="status">Loading GitHub settings…</p>'
                  : state.status === "Connecting"
                    ? `<p>Authorize GitHub CLI using this one-time code.</p>${state.userCode ? `<div class="settings-code"><code>${e(state.userCode)}</code><button data-github="copy">Copy code</button></div><a class="settings-primary" href="https://github.com/login/device" target="_blank" rel="noopener noreferrer">Open GitHub sign-in ↗</a>` : `<p role="status">Getting your sign-in code…</p>`}<p class="settings-description">Waiting for you to finish signing in. You can close Settings and return.</p><button data-github="cancel">Cancel sign-in</button>`
                    : state?.login
                      ? `<div class="settings-actions"><button data-github="check">Check connection</button><button data-github="disconnect">Disconnect GitHub</button></div><p class="settings-description">Disconnect removes Goblin’s saved access. You can revoke the GitHub CLI authorization from GitHub’s application settings.</p>
          <div class="settings-repositories"><h4>Enabled repositories</h4><p class="settings-description">Work inherits access to its assigned branch. Merging stays with you.</p>${
              enabled
                  .filter((r) => r.enabled)
                  .map(
                      (r) =>
                          `<div class="repository-row"><span>${e(r.name)}</span><button data-github="disable" data-repository="${e(r.name)}" aria-label="Disable ${e(r.name)}">Disable</button></div>`,
                  )
                  .join("") || `<p>No repositories enabled yet.</p>`
          }<button data-github="browse">Choose repositories</button>${browsing ? `<div class="repository-picker">${available.map((r) => `<div class="repository-row"><span>${e(r.name)}</span><button data-github="enable" data-repository="${e(r.name)}" ${!r.canPush || enabled.some((x) => x.id === r.id && x.enabled) ? "disabled" : ""}>${enabled.some((x) => x.id === r.id && x.enabled) ? "Enabled" : r.canPush ? "Enable" : "Read only"}</button></div>`).join("") || "No repositories available."}${more ? `<button data-github="more">Load more</button>` : ""}</div>` : ""}</div>`
                      : `<button class="settings-primary" data-github="connect">Connect GitHub</button><p class="settings-description">Sign in on GitHub with a one-time code. No app registration or token copying is needed.</p>`
          }`;
        root.setAttribute("aria-busy", String(busy));
        if (busy) {
            const message = document.createElement("p");
            message.className = "settings-description";
            message.setAttribute("role", "status");
            message.textContent =
                busyAction === "browse" || busyAction === "more"
                    ? "Loading repositories…"
                    : busyAction === "check"
                      ? "Checking connection…"
                      : "Saving changes…";
            root.append(message);
        }
        if (busy)
            for (const button of root.querySelectorAll("button"))
                button.disabled = true;
        if (!busy && (active || document.activeElement === document.body)) {
            const buttons = Array.from(
                root.querySelectorAll<HTMLButtonElement>("[data-github]"),
            ).filter((button) => !button.disabled);
            const replacement =
                buttons.find(
                    (button) =>
                        button.dataset.github === focusedAction &&
                        (button.dataset.repository ?? "") === focusedRepository,
                ) ??
                (focusedRepository
                    ? buttons.find(
                          (button) =>
                              button.dataset.repository === focusedRepository,
                      )
                    : undefined);
            replacement?.focus({ preventScroll: true });
        }
    }
    async function refresh() {
        const version = generation;
        try {
            const next = await api<GitHubState>("/api/github");
            if (version !== generation || busy) return;
            const changed = JSON.stringify(next) !== JSON.stringify(state);
            state = next;
            if (changed) {
                error = "";
                render();
            }
        } catch (failure) {
            if (version === generation && !busy) {
                error = (failure as Error).message;
                render();
            }
        }
    }
    async function loadEnabled() {
        try {
            enabled = await api<Repository[]>("/api/github/repositories");
        } catch {
            enabled = [];
        }
    }
    root.addEventListener("click", async (event) => {
        const button = (event.target as Element).closest<HTMLButtonElement>(
            "button[data-github]",
        );
        if (!button || busy) return;
        const action = button.dataset.github!;
        if (action === "copy") {
            try {
                await navigator.clipboard.writeText(state?.userCode ?? "");
                button.textContent = "Copied";
            } catch {
                error =
                    "Select and copy the code, then paste it on GitHub’s sign-in page.";
                render();
            }
            return;
        }
        busy = true;
        busyAction = action;
        generation++;
        error = "";
        render();
        try {
            if (action === "browse" || action === "more") {
                const nextPage = action === "browse" ? 1 : page + 1;
                const result = await api<Repository[]>(
                    `/api/github/available-repositories?page=${nextPage}`,
                );
                page = nextPage;
                available = page === 1 ? result : [...available, ...result];
                more = result.length === 100;
                browsing = true;
                await loadEnabled();
            } else if (action === "enable" || action === "disable") {
                enabled = await api<Repository[]>("/api/github/repositories", {
                    repository: button.dataset.repository,
                    enabled: String(action === "enable"),
                });
            } else {
                state = await api<GitHubState>(`/api/github/${action}`, {});
                await loadEnabled();
            }
            window.dispatchEvent(new Event("goblin-connections-changed"));
        } catch (failure) {
            error = (failure as Error).message;
        } finally {
            busy = false;
            render();
        }
    });
    render();
    void Promise.all([refresh(), loadEnabled()]).then(render);
    const timer = setInterval(() => {
        if (!document.hidden && !busy) void refresh();
    }, 2000);
    return () => {
        disposed = true;
        generation++;
        clearInterval(timer);
    };
}
