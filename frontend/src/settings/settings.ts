import { mountCodex } from "../connection/codex.js";
import { mountGitHub } from "./github.js";
import { mountSlack } from "./slack.js";
import { icon, escapeHtml as e } from "../work/presentation.js";
import { SystemResources } from "./system.js";
import { mountTimeZonePicker } from "./timezone-picker.js";
import { requestJson } from "../api/client.js";
import { ReadScope } from "../api/read-scope.js";
import type { Agent } from "../api/workspace-contracts.js";

const providers = [
    "agents",
    "connections",
    "codex",
    "github",
    "system",
    "cluster",
    "logs",
    "timezone",
    "integrations",
    "slack",
] as const;
type Provider = (typeof providers)[number];
function providerFrom(value: string): Provider {
    return providers.find((provider) => provider === value) ?? "connections";
}

export class Settings {
    private readonly dialog = document.createElement("dialog");
    private codexMounted = false;
    private githubMounted = false;
    private slackDispose?: () => void;
    private opener: HTMLElement | null = null;
    private openerAction = "settings";
    private generation = 0;
    private codexDispose?: () => void;
    private githubDispose?: () => void;
    private timezoneDispose?: () => void;
    private readonly system: SystemResources;
    private readonly reads = new ReadScope();
    get isOpen() {
        return this.dialog.open;
    }
    reset() {
        this.generation++;
        this.reads.reset();
        this.dialog.close();
        this.codexDispose?.();
        this.githubDispose?.();
        this.slackDispose?.();
        this.slackDispose = undefined;
        this.timezoneDispose?.();
        this.timezoneDispose = undefined;
        this.codexDispose = this.githubDispose = undefined;
        this.codexMounted = this.githubMounted = false;
        for (const panel of this.dialog.querySelectorAll<HTMLElement>(
            "[data-provider-panel]",
        ))
            panel.replaceWith(panel.cloneNode(false));
    }
    constructor(system: SystemResources) {
        this.system = system;
        this.dialog.className = "settings-dialog";
        this.dialog.setAttribute("aria-labelledby", "settings-title");
        this.dialog.innerHTML = `<header class="settings-header"><h2 id="settings-title">Settings</h2><button class="settings-close icon-button" type="button" aria-label="Close settings">${icon("close")}</button></header><div class="settings-layout"><nav class="settings-nav" aria-label="Settings"><p>Workspace</p><button data-provider="connections">${icon("spark")}AI connections</button><button data-provider="github">${icon("branch")}GitHub</button><button data-provider="cluster">${icon("activity")}Cluster</button><button data-provider="logs">${icon("activity")}Logs</button><button class="settings-lock" data-action="lock">${icon("lock")}Lock workspace</button></nav><div class="settings-content"><section data-provider-panel="connections" aria-label="AI connections"></section><section class="codex-settings" data-provider-panel="codex" aria-label="Codex connection"><p>Loading Codex settings…</p></section><section data-provider-panel="github" aria-label="GitHub connection" hidden></section><section data-provider-panel="cluster" aria-label="Cluster" hidden></section><section data-provider-panel="logs" aria-label="Logs" hidden></section></div></div>`;
        document.body.append(this.dialog);
        const integrationsButton = document.createElement("button");
        integrationsButton.dataset.provider = "integrations";
        integrationsButton.innerHTML = `${icon("activity")}Integrations`;
        this.dialog
            .querySelector('[data-provider="github"]')!
            .after(integrationsButton);
        for (const name of ["integrations", "slack"]) {
            const panel = document.createElement("section");
            panel.dataset.providerPanel = name;
            panel.setAttribute(
                "aria-label",
                name === "slack" ? "Slack integration" : "Integrations",
            );
            panel.hidden = true;
            this.dialog.querySelector(".settings-content")!.append(panel);
        }
        const agentsButton = document.createElement("button");
        agentsButton.dataset.provider = "agents";
        agentsButton.innerHTML = `${icon("agents")}Agents`;
        this.dialog
            .querySelector('[data-provider="connections"]')!
            .before(agentsButton);
        const agentsPanel = document.createElement("section");
        agentsPanel.dataset.providerPanel = "agents";
        agentsPanel.setAttribute("aria-label", "Agents");
        agentsPanel.hidden = true;
        this.dialog.querySelector(".settings-content")!.append(agentsPanel);
        const timezoneButton = document.createElement("button");
        timezoneButton.dataset.provider = "timezone";
        timezoneButton.innerHTML = `${icon("clock")}Time & date`;
        this.dialog
            .querySelector('[data-provider="connections"]')!
            .before(timezoneButton);
        const timezonePanel = document.createElement("section");
        timezonePanel.dataset.providerPanel = "timezone";
        timezonePanel.setAttribute("aria-label", "Time & date");
        timezonePanel.hidden = true;
        this.dialog.querySelector(".settings-content")!.append(timezonePanel);
        const systemButton = document.createElement("button");
        systemButton.dataset.provider = "system";
        systemButton.innerHTML = `${icon("activity")}System`;
        this.dialog
            .querySelector('[data-provider="cluster"]')!
            .before(systemButton);
        const systemPanel = document.createElement("section");
        systemPanel.dataset.providerPanel = "system";
        systemPanel.setAttribute("aria-label", "System resources");
        systemPanel.hidden = true;
        this.dialog.querySelector(".settings-content")!.append(systemPanel);
        this.dialog.addEventListener("click", (event) => {
            if (
                event.target instanceof Element &&
                event.target.closest("[data-open-system-cluster]")
            ) {
                this.select("cluster");
                void this.mount("cluster");
            }
        });
        this.dialog
            .querySelector(".settings-close")!
            .addEventListener("click", () => this.dialog.close());
        this.dialog.addEventListener("close", () => {
            this.slackDispose?.();
            this.slackDispose = undefined;
            this.timezoneDispose?.();
            this.timezoneDispose = undefined;
            const replacement = Array.from(
                document.querySelectorAll<HTMLElement>(
                    `[data-action="${this.openerAction}"]`,
                ),
            ).find((element) => element.getClientRects().length > 0);
            (this.opener?.isConnected ? this.opener : replacement)?.focus();
            window.dispatchEvent(new Event("goblin-connections-changed"));
        });
        this.dialog
            .querySelectorAll<HTMLButtonElement>("[data-provider]")
            .forEach((button) =>
                button.addEventListener("click", () => {
                    const provider = providerFrom(
                        button.dataset.provider ?? "",
                    );
                    this.select(provider);
                    void this.mount(provider);
                }),
            );
    }
    private select(provider: Provider) {
        if (provider !== "slack") {
            this.slackDispose?.();
            this.slackDispose = undefined;
        }
        if (provider !== "timezone") {
            this.timezoneDispose?.();
            this.timezoneDispose = undefined;
        }
        this.dialog.querySelector(".settings-content")!.scrollTop = 0;
        this.dialog
            .querySelectorAll<HTMLElement>("[data-provider-panel]")
            .forEach(
                (panel) =>
                    (panel.hidden = panel.dataset.providerPanel !== provider),
            );
        this.dialog
            .querySelectorAll<HTMLButtonElement>("[data-provider]")
            .forEach((button) => {
                button.classList.toggle(
                    "selected",
                    button.dataset.provider ===
                        (provider === "codex"
                            ? "connections"
                            : provider === "slack"
                              ? "integrations"
                              : provider),
                );
                button.setAttribute(
                    "aria-current",
                    button.dataset.provider ===
                        (provider === "codex"
                            ? "connections"
                            : provider === "slack"
                              ? "integrations"
                              : provider)
                        ? "page"
                        : "false",
                );
            });
    }
    async open(value = "connections") {
        const provider = providerFrom(value);
        this.opener =
            document.activeElement instanceof HTMLElement
                ? document.activeElement
                : null;
        this.openerAction = this.opener?.dataset.action ?? "settings";
        this.select(provider);
        if (!this.dialog.open) this.dialog.showModal();
        await this.mount(provider);
    }
    private api<T>(path: string) {
        return requestJson<T>(path, {
            signal: this.reads.signal,
            onUnauthorized: () =>
                window.dispatchEvent(new Event("goblin-workspace-locked")),
        });
    }
    private async mount(provider: Provider) {
        if (provider === "integrations") {
            const panel = this.dialog.querySelector<HTMLElement>(
                '[data-provider-panel="integrations"]',
            )!;
            panel.innerHTML =
                '<h3>Integrations</h3><p class="settings-description">Bring conversations into Goblin from the tools you use.</p><div class="provider-card"><h4>Slack</h4><p class="settings-description">Direct messages and mentions through an app owned by your workspace.</p><button class="settings-primary" data-open-slack>Set up Slack</button></div>';
            panel
                .querySelector("[data-open-slack]")!
                .addEventListener("click", () => {
                    this.select("slack");
                    void this.mount("slack");
                });
            return;
        }
        if (provider === "slack") {
            this.slackDispose?.();
            this.slackDispose = mountSlack(
                this.dialog.querySelector<HTMLElement>(
                    '[data-provider-panel="slack"]',
                )!,
            );
            return;
        }
        if (provider === "agents") {
            const panel = this.dialog.querySelector<HTMLElement>(
                '[data-provider-panel="agents"]',
            )!;
            const generation = this.generation;
            panel.innerHTML =
                '<h3>Agents</h3><p class="settings-description">Loading agents…</p>';
            try {
                const agents = await this.api<Agent[]>("/api/agents");
                if (generation !== this.generation) return;
                panel.innerHTML = `<h3>Agents</h3><p class="settings-description">Your coworkers carry work from its goal through review.</p>${agents.map((agent) => `<div class="provider-card"><div class="provider-heading">${icon("user")}<h4>${e(agent.name)}</h4>${agent.isDefault ? '<span class="provider-default">Default</span>' : ""}</div><p class="settings-description">${agent.model ? e(agent.model) : "Uses the connected runtime’s default model"}</p></div>`).join("") || '<p class="settings-description">No agents are available yet.</p>'}`;
            } catch {
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Agents</h3><p class="settings-notice" role="alert">Agents could not be loaded. Close Settings and try again.</p>';
            }
            return;
        }
        if (provider === "timezone") {
            this.timezoneDispose?.();
            this.timezoneDispose = mountTimeZonePicker(
                this.dialog.querySelector<HTMLElement>(
                    '[data-provider-panel="timezone"]',
                )!,
                "settings",
            );
            return;
        }
        if (provider === "system") {
            this.system.mount(
                this.dialog.querySelector<HTMLElement>(
                    '[data-provider-panel="system"]',
                )!,
            );
            return;
        }
        if (provider === "connections") {
            const panel = this.dialog.querySelector<HTMLElement>(
                '[data-provider-panel="connections"]',
            )!;
            panel.innerHTML = `<h3>AI connections</h3><p class="settings-description">Choose how Goblin connects to the AI that does your work.</p><div class="provider-card"><div class="provider-heading">${icon("spark")}<h4>Codex</h4><span class="provider-default">Default</span></div><p class="settings-description">Connect with your ChatGPT account or an OpenAI API key.</p><button class="settings-primary" data-connect-codex>Manage connection</button></div><p class="settings-description">GitHub repository access is managed separately under GitHub.</p>`;
            panel
                .querySelector("[data-connect-codex]")!
                .addEventListener("click", () => {
                    this.select("codex");
                    void this.mount("codex");
                });
            return;
        }
        if (provider === "cluster") {
            const generation = this.generation;
            const panel = this.dialog.querySelector<HTMLElement>(
                '[data-provider-panel="cluster"]',
            )!;
            panel.innerHTML =
                '<h3>Cluster</h3><p class="settings-description">Loading cluster access…</p>';
            try {
                const cluster = await this.api<{ available: boolean }>(
                    "/api/cluster",
                );
                if (generation !== this.generation) return;
                panel.innerHTML = `<h3>Cluster</h3><p class="settings-description">View pods, logs, events, and storage in Headlamp.</p>${
                    cluster.available
                        ? '<p class="settings-description">Your Goblin login gives you read-only access. Opens in a new tab.</p><a class="settings-primary" href="/headlamp/" target="_blank" rel="noopener">Open cluster</a>'
                        : '<p class="settings-notice">The cluster view is available with Goblin’s Kubernetes installation.</p>'
                }`;
            } catch {
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Cluster</h3><p class="settings-notice">Cluster access could not be loaded. Unlock Goblin and try again.</p>';
            }
            return;
        }
        if (provider === "logs") {
            const panel = this.dialog.querySelector<HTMLElement>(
                '[data-provider-panel="logs"]',
            )!;
            const generation = this.generation;
            panel.innerHTML =
                '<h3>Logs</h3><p class="settings-description">Loading log access…</p>';
            try {
                const logs = await this.api<{ available: boolean }>(
                    "/api/logs",
                );
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Logs</h3><p class="settings-description">Search saved Goblin logs and follow new events.</p>' +
                    (logs.available
                        ? '<p class="settings-description">Opens in a new tab using your Goblin login.</p><a class="settings-primary" href="/logs/" target="_blank" rel="noopener">Open logs</a>'
                        : '<p class="settings-notice">Saved logs are available with Goblin’s Kubernetes installation.</p>');
            } catch {
                if (generation !== this.generation) return;
                panel.innerHTML =
                    '<h3>Logs</h3><p class="settings-notice">Log access could not be loaded. Unlock Goblin and try again.</p>';
            }
            return;
        }
        if (provider === "github") {
            if (!this.githubMounted) {
                this.githubMounted = true;
                this.githubDispose = mountGitHub(
                    this.dialog.querySelector<HTMLElement>(
                        '[data-provider-panel="github"]',
                    )!,
                );
            }
            return;
        }
        if (this.codexMounted) return;
        this.codexMounted = true;
        const panel = this.dialog.querySelector<HTMLElement>(
            '[data-provider-panel="codex"]',
        )!;
        const generation = this.generation;
        try {
            const response = await fetch("/connection/panel.html", {
                signal: this.reads.signal,
            });
            if (!response.ok) throw new Error();
            const html = await response.text();
            if (generation !== this.generation) return;
            panel.innerHTML =
                '<button class="provider-back" type="button">← AI connections</button>' +
                html;
            panel
                .querySelector(".provider-back")!
                .addEventListener("click", () => {
                    this.select("connections");
                    void this.mount("connections");
                });
            const dispose = await mountCodex(
                panel,
                undefined,
                this.reads.signal,
            );
            if (generation !== this.generation) dispose?.();
            else this.codexDispose = dispose;
        } catch {
            if (generation !== this.generation) return;
            panel.innerHTML =
                '<p class="settings-notice" role="alert">Codex settings could not be loaded. Return to AI connections and try again.</p>';
            this.codexMounted = false;
        }
    }
}
